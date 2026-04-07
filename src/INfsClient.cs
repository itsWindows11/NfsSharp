using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Protocol;

namespace NfsSharp;

/// <summary>
/// Abstraction over <see cref="NfsClient"/> that exposes all high-level NFS operations.
/// Implement this interface (or use <see cref="NfsClient"/> directly) to enable mocking
/// in unit tests or other scenarios that require a substitute implementation.
/// </summary>
public interface INfsClient : IAsyncDisposable, IDisposable
{
    // ── Properties ────────────────────────────────────────────────────────

    /// <summary>The NFS protocol version negotiated during <see cref="ConnectAsync"/>.</summary>
    NfsVersion NegotiatedVersion { get; }

    /// <summary>The root file handle for the mounted export path.</summary>
    NfsFileHandle RootHandle { get; }

    /// <summary>
    /// The mounted export path currently used by this client.
    /// If no export path was provided to the constructor, this is populated during
    /// <see cref="ConnectAsync"/> after export discovery.
    /// </summary>
    string? ExportPath { get; }

    // ── Connection ────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the NFS server, negotiates the protocol version, and mounts the export.
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);

    // ── Attributes ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the attributes of the file or directory at <paramref name="path"/>
    /// (relative to the export root).
    /// </summary>
    Task<NfsFileAttributes> GetAttrAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns the attributes of the object identified by <paramref name="handle"/>.
    /// </summary>
    Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="attrs"/> to the file or directory at <paramref name="path"/>.
    /// Only non-<see langword="null"/> fields in <paramref name="attrs"/> are changed;
    /// leave a field <see langword="null"/> to keep its current value.
    /// </summary>
    Task SetAttrAsync(string path, NfsSetAttributes attrs, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="attrs"/> to the object identified by <paramref name="handle"/>.
    /// Only non-<see langword="null"/> fields in <paramref name="attrs"/> are changed;
    /// leave a field <see langword="null"/> to keep its current value.
    /// </summary>
    Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct = default);

    // ── Existence ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> if a file, directory, or any other object exists
    /// at <paramref name="path"/> on the server; <see langword="false"/> if it does not.
    /// </summary>
    /// <remarks>
    /// All NFS errors other than <see cref="NfsStatus.NoEnt"/> are still propagated as
    /// <see cref="NfsException"/> so that permission errors and server faults are not
    /// silently swallowed.
    /// </remarks>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns <see langword="true"/> if the object identified by <paramref name="handle"/>
    /// still exists on the server; <see langword="false"/> if the server reports
    /// <see cref="NfsStatus.NoEnt"/> (stale handle or deleted object).
    /// </summary>
    /// <remarks>
    /// All NFS errors other than <see cref="NfsStatus.NoEnt"/> are still propagated as
    /// <see cref="NfsException"/>.
    /// </remarks>
    Task<bool> ExistsAsync(NfsFileHandle handle, CancellationToken ct = default);

    // ── Namespace ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="path"/> relative to the export root and returns its
    /// file handle and current attributes.
    /// </summary>
    Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
        string path, CancellationToken ct = default);

    /// <summary>
    /// Lists the entries of the directory at <paramref name="path"/> relative to the export root.
    /// On NFSv3 each entry includes attributes and a file handle (via READDIRPLUS).
    /// </summary>
    Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Lists the entries of the directory identified by <paramref name="handle"/>.
    /// </summary>
    Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(NfsFileHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Streams the entries of the directory at <paramref name="path"/> one page at a time.
    /// Each READDIR page is fetched from the server only when the consumer advances the
    /// enumerator past the last already-yielded entry.
    /// </summary>
    IAsyncEnumerable<NfsDirectoryEntry> ReadDirStreamAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Streams the entries of the directory identified by <paramref name="handle"/>
    /// one page at a time.
    /// </summary>
    IAsyncEnumerable<NfsDirectoryEntry> ReadDirStreamAsync(NfsFileHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Recursively and lazily streams every file and directory beneath
    /// <paramref name="path"/> in depth-first order.
    /// </summary>
    IAsyncEnumerable<NfsDirectoryEntry> ReadDirRecursiveAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Recursively and lazily streams every file and directory beneath the directory
    /// identified by <paramref name="handle"/> in depth-first order.
    /// </summary>
    IAsyncEnumerable<NfsDirectoryEntry> ReadDirRecursiveAsync(
        NfsFileHandle handle, string baseRelativePath = "", CancellationToken ct = default);

    /// <summary>Reads the target of the symbolic link at <paramref name="path"/>.</summary>
    Task<string> ReadLinkAsync(string path, CancellationToken ct = default);

    /// <summary>Removes the file at <paramref name="path"/>.</summary>
    Task RemoveAsync(string path, CancellationToken ct = default);

    /// <summary>Removes the empty directory at <paramref name="path"/>.</summary>
    Task RmDirAsync(string path, CancellationToken ct = default);

    /// <summary>Creates a directory at <paramref name="path"/>.</summary>
    Task<NfsFileHandle> MkDirAsync(string path, NfsSetAttributes? attrs = null, CancellationToken ct = default);

    /// <summary>Renames / moves the file or directory at <paramref name="sourcePath"/> to <paramref name="destPath"/>.</summary>
    Task RenameAsync(string sourcePath, string destPath, CancellationToken ct = default);

    /// <summary>Creates a hard link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.</summary>
    Task LinkAsync(string targetPath, string linkPath, CancellationToken ct = default);

    /// <summary>Creates a symbolic link at <paramref name="linkPath"/> pointing to <paramref name="linkTarget"/>.</summary>
    Task SymLinkAsync(string linkPath, string linkTarget, CancellationToken ct = default);

    /// <summary>Returns filesystem statistics for the mounted export.</summary>
    Task<NfsFsStat> FsStatAsync(CancellationToken ct = default);

    /// <summary>Returns the list of export paths advertised by the server.</summary>
    Task<IReadOnlyList<string>> ListExportsAsync(CancellationToken ct = default);

    // ── Stream-based file access ──────────────────────────────────────────

    /// <summary>
    /// Opens a file at <paramref name="path"/> and returns a seekable <see cref="NfsStream"/>
    /// configured with the requested <paramref name="access"/> mode.
    /// </summary>
    Task<NfsStream> OpenFileAsync(
        string path,
        FileAccess access = FileAccess.ReadWrite,
        bool create = false,
        CancellationToken ct = default);

    // ── Parallel local file transfer fast paths ──────────────────────────

    /// <summary>
    /// Downloads a remote file to a local path using parallel ranged NFS READ calls.
    /// </summary>
    Task DownloadFileToLocalAsync(
        string remotePath,
        string localPath,
        int degreeOfParallelism = 4,
        int chunkSize = 4 * 1024 * 1024,
        IProgress<long>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Uploads a local file to a remote path using parallel ranged NFS WRITE calls.
    /// </summary>
    Task UploadFileFromLocalAsync(
        string localPath,
        string remotePath,
        int degreeOfParallelism = 4,
        int chunkSize = 4 * 1024 * 1024,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
