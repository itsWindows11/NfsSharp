using System;

namespace NfsSharp.Protocol
{
    /// <summary>A single entry returned by READDIR or READDIRPLUS.</summary>
    public sealed class NfsDirectoryEntry
    {
        /// <summary>NFS file identifier (cookie-compatible inode number).</summary>
        public ulong FileId { get; init; }

        /// <summary>The file name within the directory (UTF-8).</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Opaque cookie value used for pagination in subsequent READDIR calls.</summary>
        public ulong Cookie { get; init; }

        /// <summary>
        /// Post-operation attributes for this entry.
        /// Present in READDIRPLUS results; <see langword="null"/> for plain READDIR.
        /// </summary>
        public NfsFileAttributes? Attributes { get; init; }

        /// <summary>
        /// File handle for this entry.
        /// Present in READDIRPLUS results; <see langword="null"/> for plain READDIR.
        /// </summary>
        public NfsFileHandle? FileHandle { get; init; }

        /// <inheritdoc />
        public override string ToString() => Name;
    }
}
