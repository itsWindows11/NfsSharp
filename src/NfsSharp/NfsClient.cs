using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        private readonly int              _customNfsPort;    // 0 = discover via portmapper
        private readonly int              _customMountPort;  // 0 = discover via portmapper

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
        /// <param name="nfsPort">
        /// TCP port the NFS server listens on. When <c>0</c> (default) the port is discovered
        /// automatically via the portmapper service on port 111. Specify a non-zero value to
        /// bypass the portmapper — useful behind firewalls or with non-standard configurations.
        /// </param>
        /// <param name="mountPort">
        /// TCP port the Mount service listens on. When <c>0</c> (default) the port is discovered
        /// via the portmapper. Specify a non-zero value to bypass the portmapper.
        /// </param>
        /// <param name="connectTimeout">Timeout for each connection attempt. Defaults to 30 s.</param>
        /// <param name="readTimeout">Timeout for each read/write RPC. Defaults to 60 s.</param>
        public NfsClient(
            string     server,
            string     exportPath,
            NfsVersion version        = NfsVersion.Auto,
            int        nfsPort        = 0,
            int        mountPort      = 0,
            TimeSpan?  connectTimeout = null,
            TimeSpan?  readTimeout    = null)
            : this(server, exportPath, new AuthNoneCredentials(), version,
                   nfsPort, mountPort, connectTimeout, readTimeout)
        { }

        /// <summary>
        /// Initialises a new <see cref="NfsClient"/> with explicit authentication credentials.
        /// </summary>
        /// <param name="server">Hostname or IP address of the NFS server.</param>
        /// <param name="exportPath">Server-side export path to mount (e.g. <c>/exports/data</c>).</param>
        /// <param name="credentials">Authentication credentials to use for all RPCs.</param>
        /// <param name="version">
        /// NFS version to use; <see cref="NfsVersion.Auto"/> tries NFSv4 → v3 → v2.
        /// </param>
        /// <param name="nfsPort">
        /// TCP port the NFS server listens on. <c>0</c> means discover via portmapper.
        /// </param>
        /// <param name="mountPort">
        /// TCP port the Mount service listens on. <c>0</c> means discover via portmapper.
        /// </param>
        /// <param name="connectTimeout">Timeout for each connection attempt. Defaults to 30 s.</param>
        /// <param name="readTimeout">Timeout for each read/write RPC. Defaults to 60 s.</param>
        public NfsClient(
            string          server,
            string          exportPath,
            AuthCredentials credentials,
            NfsVersion      version        = NfsVersion.Auto,
            int             nfsPort        = 0,
            int             mountPort      = 0,
            TimeSpan?       connectTimeout = null,
            TimeSpan?       readTimeout    = null)
        {
            _server           = server      ?? throw new ArgumentNullException(nameof(server));
            _exportPath       = exportPath  ?? throw new ArgumentNullException(nameof(exportPath));
            _credentials      = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _requestedVersion = version;
            _customNfsPort    = nfsPort;
            _customMountPort  = mountPort;
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

        // ── Streaming directory enumeration ───────────────────────────────────

        /// <summary>
        /// Streams the entries of the directory at <paramref name="path"/> one page at a time.
        /// Each READDIR page is fetched from the server only when the consumer advances the
        /// enumerator past the last already-yielded entry, so memory use is bounded to a
        /// single server response at a time regardless of directory size.
        /// </summary>
        /// <param name="path">Path relative to the mounted export root.</param>
        /// <param name="ct">Token to cancel enumeration mid-stream.</param>
        /// <returns>An <see cref="IAsyncEnumerable{T}"/> of directory entries.</returns>
        /// <example>
        /// <code>
        /// // Process a million-entry directory without ever holding all entries in memory.
        /// await foreach (var entry in nfs.ReadDirStreamAsync("/huge-dir"))
        /// {
        ///     if (entry.Name.EndsWith(".log")) await ProcessAsync(entry);
        /// }
        /// </code>
        /// </example>
        public async IAsyncEnumerable<NfsDirectoryEntry> ReadDirStreamAsync(
            string path,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Directory)
                throw new NfsException(NfsStatus.NotDir, $"'{path}' is not a directory.");
            await foreach (var entry in _protocol!.EnumerateDirAsync(handle, ct).ConfigureAwait(false))
                yield return entry;
        }

        /// <summary>
        /// Streams the entries of the directory identified by <paramref name="handle"/>
        /// one page at a time.
        /// </summary>
        /// <param name="handle">An opaque NFS file handle for the directory.</param>
        /// <param name="ct">Token to cancel enumeration mid-stream.</param>
        /// <returns>An <see cref="IAsyncEnumerable{T}"/> of directory entries.</returns>
        public IAsyncEnumerable<NfsDirectoryEntry> ReadDirStreamAsync(
            NfsFileHandle handle,
            CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            return _protocol!.EnumerateDirAsync(handle, ct);
        }

        // ── Recursive directory enumeration ───────────────────────────────────

        /// <summary>
        /// Recursively and lazily streams every file and directory beneath
        /// <paramref name="path"/> in depth-first order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each entry's <see cref="NfsDirectoryEntry.RelativePath"/> is set to its path
        /// relative to <paramref name="path"/>. <see cref="NfsDirectoryEntry.Name"/> remains
        /// the bare leaf name as returned by the server.
        /// </para>
        /// <para>
        /// On <strong>NFSv3</strong> (READDIRPLUS) each entry already carries a file handle
        /// and attributes so recursive descent requires no extra RPCs.
        /// On <strong>NFSv2 / NFSv4</strong> an additional LOOKUP is issued for each
        /// subdirectory entry to obtain its file handle.
        /// </para>
        /// <para>
        /// The dot (<c>.</c>) and double-dot (<c>..</c>) pseudo-entries are automatically
        /// skipped at every level.
        /// </para>
        /// </remarks>
        /// <param name="path">Path relative to the mounted export root.</param>
        /// <param name="ct">Token to cancel enumeration mid-stream.</param>
        /// <returns>
        /// An <see cref="IAsyncEnumerable{T}"/> that yields every descendant entry,
        /// each annotated with its <see cref="NfsDirectoryEntry.RelativePath"/>.
        /// </returns>
        /// <example>
        /// <code>
        /// // Find all .csv files anywhere under /reports, without loading the full tree.
        /// await foreach (var entry in nfs.ReadDirRecursiveAsync("/reports"))
        /// {
        ///     if (entry.Name.EndsWith(".csv"))
        ///         Console.WriteLine(entry.RelativePath); // e.g. "q4/summary.csv"
        /// }
        /// </code>
        /// </example>
        public async IAsyncEnumerable<NfsDirectoryEntry> ReadDirRecursiveAsync(
            string path,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            var (handle, attrs) = await LookupAsync(path, ct).ConfigureAwait(false);
            if (attrs.Type != NfsFileType.Directory)
                throw new NfsException(NfsStatus.NotDir, $"'{path}' is not a directory.");
            await foreach (var entry in RecurseAsync(handle, string.Empty, ct).ConfigureAwait(false))
                yield return entry;
        }

        /// <summary>
        /// Recursively and lazily streams every file and directory beneath the directory
        /// identified by <paramref name="handle"/> in depth-first order.
        /// </summary>
        /// <param name="handle">An opaque NFS file handle for the starting directory.</param>
        /// <param name="baseRelativePath">
        /// Prefix prepended to each yielded entry's <see cref="NfsDirectoryEntry.RelativePath"/>.
        /// Pass an empty string for the top-level call; the method populates this automatically
        /// during recursion.
        /// </param>
        /// <param name="ct">Token to cancel enumeration mid-stream.</param>
        /// <returns>
        /// An <see cref="IAsyncEnumerable{T}"/> that yields every descendant entry.
        /// </returns>
        public IAsyncEnumerable<NfsDirectoryEntry> ReadDirRecursiveAsync(
            NfsFileHandle handle,
            string baseRelativePath = "",
            CancellationToken ct = default)
        {
            ThrowIfDisposed(); EnsureConnected();
            return RecurseAsync(handle, baseRelativePath, ct);
        }

        /// <summary>
        /// Core depth-first recursive streaming implementation.
        /// Skips <c>.</c> and <c>..</c> at every level, annotates
        /// <see cref="NfsDirectoryEntry.RelativePath"/>, and descends into
        /// every entry whose type is <see cref="NfsFileType.Directory"/>.
        /// </summary>
        private async IAsyncEnumerable<NfsDirectoryEntry> RecurseAsync(
            NfsFileHandle dir,
            string parentRelativePath,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var entry in _protocol!.EnumerateDirAsync(dir, ct).ConfigureAwait(false))
            {
                // Skip the mandatory dot-entries present in every NFS directory.
                if (entry.Name is "." or "..") continue;

                string relPath = string.IsNullOrEmpty(parentRelativePath)
                    ? entry.Name
                    : parentRelativePath + "/" + entry.Name;

                // Yield the entry with its populated RelativePath.
                yield return new NfsDirectoryEntry
                {
                    FileId      = entry.FileId,
                    Name        = entry.Name,
                    Cookie      = entry.Cookie,
                    Attributes  = entry.Attributes,
                    FileHandle  = entry.FileHandle,
                    RelativePath = relPath,
                };

                // Descend into subdirectories.
                bool isDir = entry.Attributes?.Type == NfsFileType.Directory;
                if (!isDir) continue;

                // Prefer the handle carried inside READDIRPLUS results (NFSv3);
                // fall back to a LOOKUP for NFSv2 / NFSv4 entries that lack a handle.
                NfsFileHandle childHandle = entry.FileHandle
                    ?? (await _protocol!.LookupAsync(dir, entry.Name, ct).ConfigureAwait(false)).Handle;

                await foreach (var child in RecurseAsync(childHandle, relPath, ct).ConfigureAwait(false))
                    yield return child;
            }
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
            // Honour the caller-supplied port; skip the portmapper round-trip entirely.
            if (_customNfsPort != 0) return _customNfsPort;

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
            return port != 0 ? port : 2049; // standard NFS port
        }

        private async Task<int> ResolveMountPortAsync(NfsVersion version, CancellationToken ct)
        {
            // Honour the caller-supplied mount port; skip the portmapper round-trip entirely.
            if (_customMountPort != 0) return _customMountPort;

            using var pm = new PortMapper(_server, _connectTimeout, _readTimeout);
            await pm.ConnectAsync(ct).ConfigureAwait(false);
            uint mountVer = version == NfsVersion.V2 ? 1u : 3u;
            int port = await pm.GetPortAsync(RpcConstants.MountProgram, mountVer, ct).ConfigureAwait(false);
            return port != 0 ? port : 635; // standard mount port
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
