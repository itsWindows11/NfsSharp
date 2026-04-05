using System.Threading;
using System.Threading.Tasks;

namespace NfsSharp.Protocol
{
    /// <summary>
    /// Internal contract for version-agnostic NFS file I/O operations.
    /// Implemented by <see cref="v2.NfsV2Client"/>, <see cref="v3.NfsV3Client"/>,
    /// and <see cref="v4.NfsV4Client"/>.
    /// </summary>
    internal interface INfsFileOperations
    {
        /// <summary>Reads up to <paramref name="count"/> bytes from <paramref name="handle"/> at <paramref name="offset"/>.</summary>
        Task<NfsReadResult> ReadAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct);

        /// <summary>
        /// Writes <paramref name="count"/> bytes from <paramref name="data"/> (starting at <paramref name="dataOffset"/>)
        /// into <paramref name="handle"/> at the given file <paramref name="offset"/>.
        /// Returns the number of bytes actually written.
        /// </summary>
        Task<int> WriteAsync(NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct);

        /// <summary>Retrieves file attributes for <paramref name="handle"/>.</summary>
        Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct);

        /// <summary>
        /// Flushes any unstable writes to stable storage (COMMIT in NFSv3/v4).
        /// No-op for NFSv2 (all writes are synchronous).
        /// </summary>
        Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct);
    }

    /// <summary>Result of a single NFS READ call.</summary>
    internal readonly struct NfsReadResult
    {
        /// <summary>Data returned from the server (may be fewer bytes than requested).</summary>
        public byte[] Data { get; }

        /// <summary><see langword="true"/> if the server indicated end-of-file.</summary>
        public bool Eof { get; }

        internal NfsReadResult(byte[] data, bool eof) { Data = data; Eof = eof; }
    }
}
