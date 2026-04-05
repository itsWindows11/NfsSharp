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
    /// High-level NFS client. Handles version negotiation, mounting, and exposes
    /// file-system operations as well as a streaming <see cref="NfsStream"/> interface.
    /// All operations are delegated to <see cref="Protocol.INfsProtocolClient"/> so that
    /// no version-specific logic leaks into this class.
    /// </summary>
    /// <remarks>
    /// <para>Typical usage:</para>
    /// <code>
    /// await using var nfs = new NfsClient("nfs-server", "/exports/data");
    /// await nfs.ConnectAsync();
    ///
    /// // Stream-based access
    /// await using var stream = await nfs.OpenFileAsync("path/to/file.bin");
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

        // All version knowledge ends here after ConnectAsync completes.
        private INfsProtocolClient? _protocol;
        private NfsFileHandle?      _rootHandle;
        private NfsVersion          _negotiatedVersion;
        private bool                _disposed;

        // ── Public properties ─────────────────────────────────────────────────

        /// <summary>The NFS protocol version negotiated during <see cref="ConnectAsync"/>.</summary>
        public NfsVersion NegotiatedVersion => _negotiatedVersion;

        /// <summary>The root file handle for the mounted export path.</summary>
        public NfsFileHandle RootHandle =>
            _rootHandle ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        // ── Constructors ──────────────────────────────────────────────────────

        /// <summary>
        /// Initialises a new <see cref="NfsClient"/> with AUTH_NONE credentials.
        /// </summary>
        /// <param name="server">Hostname or IP address of the NFS server.</param>
        /// <param name="exportPath">Server-side export path to mount (e.g. <c>/exports/data</c>).</param>
        /// <param name="version">
        /// NFS version to use; <see cref="NfsVersion.Auto"/> tries NFSv4 → v3 → v2.
        /// </param>
        /// <param name="connectTimeout">Timeout for each connection attempt. Defaults to 30 s.</param>
        /// <param name="readTimeout">Timeout for each read/write RPC. Defaults to 60 s.</param>
        public NfsClient(
            string     server,
            string     exportPath,
            NfsVersion version        = NfsVersion.Auto,
            TimeSpan?  connectTimeout = null,
            TimeSpan?  readTimeout    = null)
            : this(server, exportPath, new AuthNoneCredentials(), version, connectTimeout, readTimeout)
        { }

        /// <summary>
        /// Initialises a new <see cref="NfsClient"/> with explicit authentication credentials.
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
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_protocol != null)
                throw new InvalidOperationException("Already connected.");

            int nfsPort = await ResolveNfsPortAsync(ct).ConfigureAwait(false);

            foreach (var version in VersionsToTry())
            {
                try
                {
                    var (proto, root) = await TryConnectVersionAsync(version, nfsPort, ct)
                        .ConfigureAwait(false);
                    _protocol          = proto;
                    _rootHandle        = root;
                    _negotiatedVersion = version;
                    return;
                }
                catch when (_requestedVersion == NfsVersion.Auto)
                {
                    // Fall through to the next version.
                }
            }

            throw new NfsException(NfsStatus.NotSupp,
                $"Could not connect to '{_server}:{_exportPath}' with any supported NFS version.");
        }

        // ── File / directory operations ───────────────────────────────────────

        /// <summary>
        /// Returns the attributes of the file or directory at <paramref name="path"/>
        /// (relative to the export root).
        /// </summary>
        public async Task<NfsFileAttributes> GetAttrAsync(string path, CancellationToken ct = default)
        {
            var (_, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            return attrs;
        }

        /// <summary>
        /// Returns the attributes of the object identified by <paramref name="handle"/>.
        /// </summary>
        public Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            return _protocol!.GetAttrAsync(handle, ct);
        }

        /// <summary>
        /// Resolves <paramref name="path"/> relative to the export root and returns its
        /// file handle and current attributes.
        /// </summary>
        public async Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
            string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var parts   = path.TrimStart('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = RootHandle;
            NfsFileAttributes? attrs = null;

            foreach (var part in parts)
                (current, attrs) = await _protocol!.LookupAsync(current, part, ct).ConfigureAwait(false);

            attrs ??= await _protocol!.GetAttrAsync(current, ct).ConfigureAwait(false);
            return (current, attrs);
        }

        /// <summary>
        /// Lists the entries of the directory at <paramref name="path"/> relative to the export root.
        /// On NFSv3 each entry includes attributes and a file handle (via READDIRPLUS).
        /// </summary>
        public async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
            string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Directory)
                throw new NfsException(NfsStatus.NotDir, $"'{path}' is not a directory.");
            return await _protocol!.ReadDirAsync(handle, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Lists the entries of the directory identified by <paramref name="handle"/>.
        /// </summary>
        public Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
            NfsFileHandle handle, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            return _protocol!.ReadDirAsync(handle, ct);
        }

        /// <summary>
        /// Reads the target of the symbolic link at <paramref name="path"/>.
        /// </summary>
        public async Task<string> ReadLinkAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (handle, _) = await LookupAsync(path, ct).ConfigureAwait(false);
            return await _protocol!.ReadLinkAsync(handle, ct).ConfigureAwait(false);
        }

        /// <summary>Removes the file at <paramref name="path"/>.</summary>
        public async Task RemoveAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (dir, name) = await ResolveParentAsync(path, ct).ConfigureAwait(false);
            await _protocol!.RemoveAsync(dir, name, ct).ConfigureAwait(false);
        }

        /// <summary>Removes the empty directory at <paramref name="path"/>.</summary>
        public async Task RmDirAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (dir, name) = await ResolveParentAsync(path, ct).ConfigureAwait(false);
            await _protocol!.RmDirAsync(dir, name, ct).ConfigureAwait(false);
        }

        /// <summary>Creates a directory at <paramref name="path"/>.</summary>
        public async Task<NfsFileHandle> MkDirAsync(
            string path, NfsSetAttributes? attrs = null, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (dir, name) = await ResolveParentAsync(path, ct).ConfigureAwait(false);
            return await _protocol!.MkDirAsync(dir, name, attrs ?? new NfsSetAttributes { Mode = 0b111_101_101 /* 0755 */ }, ct)
                .ConfigureAwait(false);
        }

        /// <summary>Renames / moves the file or directory at <paramref name="sourcePath"/> to <paramref name="destPath"/>.</summary>
        public async Task RenameAsync(string sourcePath, string destPath, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (fromDir, fromName) = await ResolveParentAsync(sourcePath, ct).ConfigureAwait(false);
            var (toDir,   toName)   = await ResolveParentAsync(destPath,   ct).ConfigureAwait(false);
            await _protocol!.RenameAsync(fromDir, fromName, toDir, toName, ct).ConfigureAwait(false);
        }

        /// <summary>Creates a hard link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.</summary>
        public async Task LinkAsync(string targetPath, string linkPath, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (targetHandle, _)  = await LookupAsync(targetPath, ct).ConfigureAwait(false);
            var (linkDir, linkName) = await ResolveParentAsync(linkPath, ct).ConfigureAwait(false);
            await _protocol!.LinkAsync(targetHandle, linkDir, linkName, ct).ConfigureAwait(false);
        }

        /// <summary>Creates a symbolic link at <paramref name="linkPath"/> pointing to <paramref name="linkTarget"/>.</summary>
        public async Task SymLinkAsync(
            string linkPath, string linkTarget, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (dir, name) = await ResolveParentAsync(linkPath, ct).ConfigureAwait(false);
            await _protocol!.SymLinkAsync(dir, name, linkTarget, new NfsSetAttributes { Mode = 0b111_111_111 /* 0777 */ }, ct)
                .ConfigureAwait(false);
        }

        /// <summary>Returns filesystem statistics for the mounted export.</summary>
        public Task<NfsFsStat> FsStatAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            return _protocol!.FsStatAsync(RootHandle, ct);
        }

        /// <summary>Returns the list of export paths advertised by the server.</summary>
        public async Task<IReadOnlyList<string>> ListExportsAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            int mountPort = await ResolveMountPortAsync(_requestedVersion, ct).ConfigureAwait(false);
            uint mountVer = _requestedVersion == NfsVersion.V2 ? 1u : 3u;
            using var mc  = new MountClient(
                _server, mountPort, mountVer, _credentials, _connectTimeout, _readTimeout);
            await mc.ConnectAsync(ct).ConfigureAwait(false);
            return await mc.ListExportsAsync(ct).ConfigureAwait(false);
        }

        // ── Stream-based file access ──────────────────────────────────────────

        /// <summary>
        /// Opens an existing file at <paramref name="path"/> for reading and writing
        /// and returns a seekable <see cref="NfsStream"/>.
        /// </summary>
        public async Task<NfsStream> OpenFileAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Regular)
                throw new NfsException(NfsStatus.IsDir, $"'{path}' is not a regular file.");
            return new NfsStream(_protocol!, handle, (long)attrs.Size, readable: true, writable: true);
        }

        /// <summary>
        /// Opens an existing file at <paramref name="path"/> for read-only streaming access.
        /// </summary>
        public async Task<NfsStream> OpenFileReadOnlyAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Regular)
                throw new NfsException(NfsStatus.IsDir, $"'{path}' is not a regular file.");
            return new NfsStream(_protocol!, handle, (long)attrs.Size, readable: true, writable: false);
        }

        /// <summary>
        /// Creates a new file at <paramref name="path"/> (or truncates it if it already exists)
        /// and returns a writable <see cref="NfsStream"/>.
        /// </summary>
        public async Task<NfsStream> CreateFileAsync(string path, CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (dir, name) = await ResolveParentAsync(path, ct).ConfigureAwait(false);
            var fh = await _protocol!.CreateFileAsync(
                dir, name,
                new NfsSetAttributes { Mode = 0b110_100_100 /* 0644 */ },
                ct).ConfigureAwait(false);
            return new NfsStream(_protocol!, fh, 0L, readable: true, writable: true);
        }

        // ── IDisposable / IAsyncDisposable ────────────────────────────────────

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _protocol?.Dispose();
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
#if NETSTANDARD2_0
            return new ValueTask();
#else
            return ValueTask.CompletedTask;
#endif
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<(INfsProtocolClient protocol, NfsFileHandle root)> TryConnectVersionAsync(
            NfsVersion version, int nfsPort, CancellationToken ct)
        {
            int mountPort = await ResolveMountPortAsync(version, ct).ConfigureAwait(false);
            uint mountVer = version == NfsVersion.V2 ? 1u : 3u;

            using var mc = new MountClient(
                _server, mountPort, mountVer, _credentials, _connectTimeout, _readTimeout);
            await mc.ConnectAsync(ct).ConfigureAwait(false);
            NfsFileHandle root = await mc.MountAsync(_exportPath, ct).ConfigureAwait(false);

            INfsProtocolClient protocol = version switch
            {
                NfsVersion.V4 => new NfsV4Client(_server, nfsPort, _credentials, _connectTimeout, _readTimeout),
                NfsVersion.V3 => new NfsV3Client(_server, nfsPort, _credentials, _connectTimeout, _readTimeout),
                NfsVersion.V2 => new NfsV2Client(_server, nfsPort, _credentials, _connectTimeout, _readTimeout),
                _             => throw new ArgumentOutOfRangeException(nameof(version)),
            };

            // Version switch is isolated here — ConnectAsync is the only place it lives.
            await protocol.ConnectAsync(ct).ConfigureAwait(false);
            return (protocol, root);
        }

        private async Task<int> ResolveNfsPortAsync(CancellationToken ct)
        {
            using var pm = new PortMapper(_server, _connectTimeout, _readTimeout);
            await pm.ConnectAsync(ct).ConfigureAwait(false);
            uint ver = _requestedVersion switch
            {
                NfsVersion.V4   => 4,
                NfsVersion.V3   => 3,
                NfsVersion.V2   => 2,
                NfsVersion.Auto => 3,
                _               => 3,
            };
            int port = await pm.GetPortAsync(RpcConstants.NfsProgram, ver, ct).ConfigureAwait(false);
            return port != 0 ? port : 2049;
        }

        private async Task<int> ResolveMountPortAsync(NfsVersion version, CancellationToken ct)
        {
            using var pm = new PortMapper(_server, _connectTimeout, _readTimeout);
            await pm.ConnectAsync(ct).ConfigureAwait(false);
            uint mountVer = version == NfsVersion.V2 ? 1u : 3u;
            int port = await pm.GetPortAsync(RpcConstants.MountProgram, mountVer, ct).ConfigureAwait(false);
            return port != 0 ? port : 635;
        }

        /// <summary>
        /// Returns the parent directory handle and the leaf name for <paramref name="path"/>.
        /// </summary>
        private async Task<(NfsFileHandle Dir, string Name)> ResolveParentAsync(
            string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path must not be empty.", nameof(path));

            int sep     = path.TrimEnd('/').LastIndexOf('/');
            string dir  = sep > 0 ? path.Substring(0, sep) : string.Empty;
            string name = sep >= 0 ? path.Substring(sep + 1) : path;
            name = name.TrimEnd('/');

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Path must include a file or directory name.", nameof(path));

            NfsFileHandle dirHandle;
            if (string.IsNullOrEmpty(dir))
                dirHandle = RootHandle;
            else
                (dirHandle, _) = await LookupAsync(dir, ct).ConfigureAwait(false);

            return (dirHandle, name);
        }

        private NfsVersion[] VersionsToTry() => _requestedVersion switch
        {
            NfsVersion.Auto => new[] { NfsVersion.V4, NfsVersion.V3, NfsVersion.V2 },
            _               => new[] { _requestedVersion },
        };

        private void EnsureConnected()
        {
            if (_protocol == null)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NfsClient));
        }
    }
}
