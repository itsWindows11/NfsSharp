using System;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol
{
    /// <summary>
    /// An opaque NFS file handle (nfs_fh3 / nfs_fh4).
    /// Treated as a byte sequence of at most 64 bytes (NFSv3) or 128 bytes (NFSv4).
    /// </summary>
    public sealed class NfsFileHandle : IEquatable<NfsFileHandle>
    {
        /// <summary>Maximum file handle size for NFSv3 (bytes).</summary>
        public const int MaxSizeV3 = 64;

        /// <summary>Maximum file handle size for NFSv4 (bytes).</summary>
        public const int MaxSizeV4 = 128;

        /// <summary>The raw opaque bytes of this file handle.</summary>
        public byte[] Data { get; }

        /// <summary>Creates a file handle from raw opaque bytes.</summary>
        public NfsFileHandle(byte[] data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }

        // ── XDR ──────────────────────────────────────────────────────────────

        /// <summary>Reads a variable-length file handle (NFSv3 / NFSv4 format) from <paramref name="reader"/>.</summary>
        public static NfsFileHandle ReadFrom(XdrReader reader, int maxSize = MaxSizeV3)
        {
            byte[] data = reader.ReadVarOpaque(maxSize);
            return new NfsFileHandle(data);
        }

        /// <summary>Writes a variable-length file handle to <paramref name="writer"/>.</summary>
        public void WriteTo(XdrWriter writer)
        {
            writer.WriteVarOpaque(Data);
        }

        // ── Equality ─────────────────────────────────────────────────────────

        /// <inheritdoc />
        public bool Equals(NfsFileHandle? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (Data.Length != other.Data.Length) return false;
            return Data.AsSpan().SequenceEqual(other.Data.AsSpan());
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as NfsFileHandle);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (byte b in Data) hash.Add(b);
            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public override string ToString() => Convert.ToHexString(Data);
    }
}
