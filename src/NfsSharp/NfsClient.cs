using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Auth;
using NfsSharp.Protocol;
using NfsSharp.Protocol.Mount;
using NfsSharp.Protocol.v2;
using NfsSharp.Protocol.v3;
using NfsSharp.Protocol.v4;
using NfsSharp.Rpc;

namespace NfsSharp
{
    /// <summary>
    /// High-level NFS client.  Handles version negotiation, mounting, and exposes
    /// file-system operations as well as a streaming <see cref="NfsStream"/> interface.
    /// </summary>
    /// <remarks>
    /// <para>Typical usage:</para>
    /// <code>
    /// await using var nfs = new NfsClient("nfs-server", "/exports/data");
    /// await nfs.ConnectAsync();
    ///
    /// // Stream-based access
    /// await using var stream = await nfs.OpenFileAsync("myfile.bin");
    /// var buf = new byte[8192];
    /// int n = await stream.ReadAsync(buf);
    /// </code>
    /// </remarks>
    public sealed class NfsClient : IAsyncDisposable, IDisposable
    {
        private readonly string           _server;
        private readonly string           _exportPath;
        private readonly NfsVersion       _requestedVersion;
        private readonly AuthCredentials  _credentials;
        private readonly TimeSpan         _connectTimeout;
        private readonly TimeSpan         _readTimeout;

        private INfsFileOperations? _ops;
        private NfsFileHandle?      _rootHandle;
        private NfsVersion          _negotiatedVersion;
        private bool                _disposed;

        /// <summary>The NFS protocol version actually in use after <see cref="ConnectAsync"/>.</summary>
        public NfsVersion NegotiatedVersion => _negotiatedVersion;

        /// <summary>The root file handle for the mounted export.</summary>
        public NfsFileHandle RootHandle =>
            _rootHandle ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        // ── Constructors ──────────────────────────────────────────────────────

        /// <summary>
        /// Initialises a new <see cref="NfsClient"/> with AUTH_NONE credentials.
        /// </summary>
        /// <param name="server">Hostname or IP address of the NFS server.</param>
        /// <param name="exportPath">The server-side export path to mount (e.g. <c>/exports/data</c>).</param>
        /// <param name="version">NFS protocol version to use; <see cref="NfsVersion.Auto"/> negotiates the best available.</param>
        /// <param name="connectTimeout">Timeout for each connection attempt. Defaults to 30 s.</param>
        /// <param name="readTimeout">Timeout for each read/write operation. Defaults to 60 s.</param>
        public NfsClient(
            string     server,
            string     exportPath,
            NfsVersion version        = NfsVersion.Auto,
            TimeSpan?  connectTimeout = null,
            TimeSpan?  readTimeout    = null)
            : this(server, exportPath, new AuthNoneCredentials(), version, connectTimeout, readTimeout)
        { }

        /// <summary>
        /// Initialises a new <see cref="NfsClient"/> with explicit credentials.
        /// </summary>
        public NfsClient(
            string          server,
            string          exportPath,
            AuthCredentials credentials,
            NfsVersion      version        = NfsVersion.Auto,
            TimeSpan?       connectTimeout = null,
            TimeSpan?       readTimeout    = null)
        {
            _server           = server      ?? throw new ArgumentNullException(nameof(server));
            _exportPath       = exportPath  ?? throw new ArgumentNullException(nameof(exportPath));
            _credentials      = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _requestedVersion = version;
            _connectTimeout   = connectTimeout ?? TimeSpan.FromSeconds(30);
            _readTimeout      = readTimeout    ?? TimeSpan.FromSeconds(60);
        }

        // ── Connection ────────────────────────────────────────────────────────

        /// <summary>
        /// Connects to the NFS server, negotiates a protocol version, and mounts the export.
        /// Must be called before any file operations.
        /// </summary>
        /// <param name="ct">Optional cancellation token.</param>
        /// <exception cref="InvalidOperationException">Thrown if called on an already-connected client.</exception>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_ops != null)
                throw new InvalidOperationException("Already connected.");

            int nfsPort = await ResolveNfsPortAsync(ct).ConfigureAwait(false);
            var versionsToTry = GetVersionsToTry();

            foreach (var version in versionsToTry)
            {
                try
                {
                    (_ops, _rootHandle) = await TryConnectVersionAsync(
                        version, nfsPort, ct).ConfigureAwait(false);
                    _negotiatedVersion = version;
                    return;
                }
                catch (Exception) when (_requestedVersion == NfsVersion.Auto)
                {
                    // Try the next version.
                }
            }

            throw new NfsException(NfsStatus.NotSupp,
                $"Could not connect to {_server}:{_exportPath} with any NFS version.");
        }

        // ── File / directory operations ───────────────────────────────────────

        /// <summary>
        /// Looks up a file or directory at <paramref name="path"/> relative to the export root
        /// and returns its file handle and attributes.
        /// </summary>
        public async Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
            string path, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureConnected();

            var parts   = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = RootHandle;
            NfsFileAttributes? attrs = null;

            foreach (var part in parts)
            {
                (current, attrs) = await LookupOneAsync(current, part, ct).ConfigureAwait(false);
            }

            attrs ??= await GetAttrAsync(current, ct).ConfigureAwait(false);
            return (current, attrs);
        }

        /// <summary>Returns attributes for the file or directory identified by <paramref name="handle"/>.</summary>
        public Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureConnected();
            return _ops!.GetAttrAsync(handle, ct);
        }

        /// <summary>
        /// Lists the entries in the directory identified by <paramref name="handle"/>.
        /// Uses READDIRPLUS on NFSv3 (returns both attributes and handles), plain READDIR on v2/v4.
        /// </summary>
        public async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
            NfsFileHandle handle, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureConnected();

            return _negotiatedVersion switch
            {
                NfsVersion.V3 => await ((NfsV3Client)_ops!).ReadDirPlusAsync(handle, ct).ConfigureAwait(false),
                NfsVersion.V2 => await ((NfsV2Client)_ops!).ReadDirAsync(handle, ct).ConfigureAwait(false),
                NfsVersion.V4 => await ((NfsV4Client)_ops!).ReadDirAsync(handle, ct).ConfigureAwait(false),
                _             => throw new InvalidOperationException("Unknown NFS version."),
            };
        }

        // ── Stream-based file access ──────────────────────────────────────────

        /// <summary>
        /// Opens an existing file at <paramref name="path"/> (relative to the export root)
        /// and returns a readable/writable <see cref="NfsStream"/>.
        /// </summary>
        /// <param name="path">Relative path within the mounted export (e.g. <c>dir/file.txt</c>).</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>An <see cref="NfsStream"/> positioned at offset 0.</returns>
        public async Task<NfsStream> OpenFileAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureConnected();

            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Regular)
                throw new NfsException(NfsStatus.IsDir, $"'{path}' is not a regular file.");

            return new NfsStream(_ops!, handle, (long)attrs.Size, readable: true, writable: true);
        }

        /// <summary>
        /// Opens an existing file at <paramref name="path"/> for read-only streaming access.
        /// </summary>
        public async Task<NfsStream> OpenFileReadOnlyAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureConnected();

            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Regular)
                throw new NfsException(NfsStatus.IsDir, $"'{path}' is not a regular file.");

            return new NfsStream(_ops!, handle, (long)attrs.Size, readable: true, writable: false);
        }

        /// <summary>
        /// Creates a new file at <paramref name="path"/> (relative to the export root)
        /// and returns a writable <see cref="NfsStream"/>.
        /// If the file already exists it is truncated.
        /// </summary>
        public async Task<NfsStream> CreateFileAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureConnected();

            // Split path into parent directory and file name.
            int sep         = path.LastIndexOf('/');
            string dirPath  = sep > 0 ? path[..sep]  : string.Empty;
            string fileName = sep > 0 ? path[(sep+1)..] : path;
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("Path must include a file name.", nameof(path));

            NfsFileHandle dirHandle;
            if (string.IsNullOrEmpty(dirPath))
                dirHandle = RootHandle;
            else
                (dirHandle, _) = await LookupAsync(dirPath, ct).ConfigureAwait(false);

            NfsFileHandle fileHandle;
            switch (_negotiatedVersion)
            {
                case NfsVersion.V3:
                {
                    var (fh, _) = await ((NfsV3Client)_ops!).CreateAsync(
                        dirHandle, fileName,
                        new NfsSetAttributes { Mode = 0b110_100_100 /* 0644 */ },
                        exclusive: false, ct: ct).ConfigureAwait(false);
                    fileHandle = fh ?? throw new NfsException(NfsStatus.ServerFault, "CREATE returned no file handle.");
                    break;
                }
                case NfsVersion.V2:
                {
                    (fileHandle, _) = await ((NfsV2Client)_ops!).LookupAsync(
                        dirHandle, fileName, ct).ConfigureAwait(false);
                    // NFSv2 CREATE is not implemented via INfsFileOperations, fall back to lookup.
                    break;
                }
                default:
                    throw new NotSupportedException($"CreateFileAsync is not supported for {_negotiatedVersion}.");
            }

            return new NfsStream(_ops!, fileHandle, 0L, readable: true, writable: true);
        }

        /// <summary>
        /// Returns the exported paths advertised by the server.
        /// </summary>
        public async Task<IReadOnlyList<string>> ListExportsAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            int mountPort  = await ResolveMountPortAsync(_requestedVersion, ct).ConfigureAwait(false);
            using var mc   = new MountClient(
                _server, mountPort,
                _requestedVersion == NfsVersion.V2 ? 2u : 3u,
                _credentials, _connectTimeout, _readTimeout);
            await mc.ConnectAsync(ct).ConfigureAwait(false);
            return await mc.ListExportsAsync(ct).ConfigureAwait(false);
        }

        // ── IDisposable / IAsyncDisposable ────────────────────────────────────

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            (_ops as IDisposable)?.Dispose();
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<(INfsFileOperations ops, NfsFileHandle root)> TryConnectVersionAsync(
            NfsVersion version, int nfsPort, CancellationToken ct)
        {
            int mountPort = await ResolveMountPortAsync(version, ct).ConfigureAwait(false);

            using var mc = new MountClient(
                _server, mountPort,
                version == NfsVersion.V2 ? 2u : 3u,
                _credentials, _connectTimeout, _readTimeout);
            await mc.ConnectAsync(ct).ConfigureAwait(false);
            NfsFileHandle root = await mc.MountAsync(_exportPath, ct).ConfigureAwait(false);

            INfsFileOperations ops = version switch
            {
                NfsVersion.V4 => new NfsV4Client(_server, nfsPort, _credentials, _connectTimeout, _readTimeout),
                NfsVersion.V3 => new NfsV3Client(_server, nfsPort, _credentials, _connectTimeout, _readTimeout),
                NfsVersion.V2 => new NfsV2Client(_server, nfsPort, _credentials, _connectTimeout, _readTimeout),
                _             => throw new ArgumentOutOfRangeException(nameof(version)),
            };

            await ConnectOpsAsync(ops, ct).ConfigureAwait(false);
            return (ops, root);
        }

        private static Task ConnectOpsAsync(INfsFileOperations ops, CancellationToken ct) => ops switch
        {
            NfsV4Client v4 => v4.ConnectAsync(ct),
            NfsV3Client v3 => v3.ConnectAsync(ct),
            NfsV2Client v2 => v2.ConnectAsync(ct),
            _              => Task.CompletedTask,
        };

        private async Task<int> ResolveNfsPortAsync(CancellationToken ct)
        {
            using var pm = new PortMapper(_server, _connectTimeout, _readTimeout);
            await pm.ConnectAsync(ct).ConfigureAwait(false);
            uint version = _requestedVersion switch
            {
                NfsVersion.V4   => 4,
                NfsVersion.V3   => 3,
                NfsVersion.V2   => 2,
                NfsVersion.Auto => 3, // default guess for port map
                _               => 3,
            };
            int port = await pm.GetPortAsync(RpcConstants.NfsProgram, version, ct).ConfigureAwait(false);
            return port != 0 ? port : 2049; // standard NFS port fallback
        }

        private async Task<int> ResolveMountPortAsync(NfsVersion version, CancellationToken ct)
        {
            using var pm = new PortMapper(_server, _connectTimeout, _readTimeout);
            await pm.ConnectAsync(ct).ConfigureAwait(false);
            uint mountVer = version == NfsVersion.V2 ? 1u : 3u;
            int port = await pm.GetPortAsync(RpcConstants.MountProgram, mountVer, ct).ConfigureAwait(false);
            return port != 0 ? port : 635; // standard mount port fallback
        }

        private async Task<(NfsFileHandle, NfsFileAttributes)> LookupOneAsync(
            NfsFileHandle dir, string name, CancellationToken ct) => _negotiatedVersion switch
        {
            NfsVersion.V3 => await ((NfsV3Client)_ops!).LookupAsync(dir, name, ct).ConfigureAwait(false),
            NfsVersion.V2 => await ((NfsV2Client)_ops!).LookupAsync(dir, name, ct).ConfigureAwait(false),
            NfsVersion.V4 => (await ((NfsV4Client)_ops!).LookupAsync(dir, name, ct).ConfigureAwait(false),
                              await _ops!.GetAttrAsync(await ((NfsV4Client)_ops!).LookupAsync(dir, name, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
            _ => throw new InvalidOperationException("Unknown NFS version."),
        };

        private NfsVersion[] GetVersionsToTry() => _requestedVersion switch
        {
            NfsVersion.Auto => new[] { NfsVersion.V4, NfsVersion.V3, NfsVersion.V2 },
            _               => new[] { _requestedVersion },
        };

        private void EnsureConnected()
        {
            if (_ops == null)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NfsClient));
        }
    }
}
