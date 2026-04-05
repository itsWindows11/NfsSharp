using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NfsSharp.Protocol
{
    /// <summary>
    /// Full abstraction over every NFS protocol version.
    /// All operations exposed by <see cref="NfsSharp.NfsClient"/> and
    /// <see cref="NfsSharp.NfsStream"/> are defined here so that no caller
    /// ever needs to cast to or switch on a concrete version type.
    /// </summary>
    /// <remarks>
    /// Implementations: <see cref="v2.NfsV2Client"/>, <see cref="v3.NfsV3Client"/>,
    /// <see cref="v4.NfsV4Client"/>.
    /// </remarks>
    internal interface INfsProtocolClient : IDisposable
    {
        // ── Connection ────────────────────────────────────────────────────────

        /// <summary>Opens the underlying TCP connection to the NFS server.</summary>
        Task ConnectAsync(CancellationToken ct = default);

        // ── File I/O ──────────────────────────────────────────────────────────

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes from <paramref name="handle"/>
        /// starting at <paramref name="offset"/>.
        /// </summary>
        Task<NfsReadResult> ReadAsync(
            NfsFileHandle handle, long offset, int count, CancellationToken ct);

        /// <summary>
        /// Writes <paramref name="count"/> bytes from <paramref name="data"/> (starting
        /// at <paramref name="dataOffset"/>) into <paramref name="handle"/> at
        /// <paramref name="offset"/>. Returns the number of bytes actually written.
        /// </summary>
        Task<int> WriteAsync(
            NfsFileHandle handle, long offset,
            byte[] data, int dataOffset, int count,
            CancellationToken ct);

        /// <summary>
        /// Commits any unstable (buffered) writes to stable storage.
        /// A no-op for NFSv2 where all writes are synchronous.
        /// </summary>
        Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct);

        // ── Attributes ────────────────────────────────────────────────────────

        /// <summary>Returns the current attributes of <paramref name="handle"/>.</summary>
        Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct);

        /// <summary>
        /// Applies <paramref name="attrs"/> to <paramref name="handle"/>.
        /// <para>NFSv2 does not support size-based truncation via SETATTR;
        /// implementations may throw <see cref="NotSupportedException"/> for
        /// unsupported attribute combinations.</para>
        /// </summary>
        Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct);

        // ── Namespace ─────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves <paramref name="name"/> within <paramref name="dir"/> and returns
        /// both the child's file handle and its current attributes.
        /// </summary>
        Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
            NfsFileHandle dir, string name, CancellationToken ct);

        /// <summary>
        /// Creates a regular file named <paramref name="name"/> inside <paramref name="dir"/>.
        /// Returns the new file's handle.
        /// </summary>
        Task<NfsFileHandle> CreateFileAsync(
            NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct);

        /// <summary>
        /// Creates a directory named <paramref name="name"/> inside <paramref name="dir"/>.
        /// Returns the new directory's handle.
        /// </summary>
        Task<NfsFileHandle> MkDirAsync(
            NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct);

        /// <summary>
        /// Creates a symbolic link named <paramref name="name"/> in <paramref name="dir"/>
        /// pointing to <paramref name="linkTarget"/>.
        /// </summary>
        Task SymLinkAsync(
            NfsFileHandle dir, string name, string linkTarget,
            NfsSetAttributes attrs, CancellationToken ct);

        /// <summary>
        /// Lists the entries of a directory.
        /// Implementations should use READDIRPLUS (NFSv3) or equivalent to populate
        /// <see cref="NfsDirectoryEntry.Attributes"/> and <see cref="NfsDirectoryEntry.FileHandle"/>
        /// when available.
        /// </summary>
        Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(NfsFileHandle dir, CancellationToken ct);

        /// <summary>Reads the target of a symbolic link.</summary>
        Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct);

        /// <summary>Removes a file named <paramref name="name"/> from <paramref name="dir"/>.</summary>
        Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct);

        /// <summary>Removes an empty directory named <paramref name="name"/> from <paramref name="dir"/>.</summary>
        Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct);

        /// <summary>
        /// Renames <paramref name="fromName"/> within <paramref name="fromDir"/>
        /// to <paramref name="toName"/> within <paramref name="toDir"/>.
        /// </summary>
        Task RenameAsync(
            NfsFileHandle fromDir, string fromName,
            NfsFileHandle toDir,   string toName,
            CancellationToken ct);

        /// <summary>Creates a hard link to <paramref name="file"/> inside <paramref name="linkDir"/>.</summary>
        Task LinkAsync(
            NfsFileHandle file,
            NfsFileHandle linkDir, string linkName,
            CancellationToken ct);

        /// <summary>Returns filesystem statistics for the filesystem containing <paramref name="handle"/>.</summary>
        Task<NfsFsStat> FsStatAsync(NfsFileHandle handle, CancellationToken ct);
    }

    /// <summary>Result of a single NFS READ call.</summary>
    internal readonly struct NfsReadResult
    {
        /// <summary>Data returned by the server (may be fewer bytes than requested).</summary>
        public byte[] Data { get; }

        /// <summary><see langword="true"/> if the server signalled end-of-file.</summary>
        public bool Eof { get; }

        internal NfsReadResult(byte[] data, bool eof) { Data = data; Eof = eof; }
    }
}
