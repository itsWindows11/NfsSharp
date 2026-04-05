using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NfsSharp.Xdr
{
    /// <summary>
    /// Reads XDR (External Data Representation, RFC 4506) encoded data from a stream or byte array.
    /// All XDR integer types are big-endian and padded to 4-byte multiples.
    /// </summary>
    public sealed class XdrReader
    {
        private readonly Stream _stream;
        private readonly byte[] _int32Buf = new byte[4];
        private readonly byte[] _int64Buf = new byte[8];

        /// <summary>Initializes a new <see cref="XdrReader"/> over the given stream.</summary>
        public XdrReader(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>Initializes a new <see cref="XdrReader"/> over an in-memory byte array.</summary>
        public XdrReader(byte[] data) : this(new MemoryStream(data ?? throw new ArgumentNullException(nameof(data)))) { }

        // ── Primitives ───────────────────────────────────────────────────────

        /// <summary>Reads a signed 32-bit integer.</summary>
        public int ReadInt32()
        {
            ReadFull(_int32Buf, 4);
            return BinaryPrimitives.ReadInt32BigEndian(_int32Buf);
        }

        /// <summary>Reads an unsigned 32-bit integer.</summary>
        public uint ReadUInt32()
        {
            ReadFull(_int32Buf, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(_int32Buf);
        }

        /// <summary>Reads a signed 64-bit integer (hyper).</summary>
        public long ReadInt64()
        {
            ReadFull(_int64Buf, 8);
            return BinaryPrimitives.ReadInt64BigEndian(_int64Buf);
        }

        /// <summary>Reads an unsigned 64-bit integer (unsigned hyper).</summary>
        public ulong ReadUInt64()
        {
            ReadFull(_int64Buf, 8);
            return BinaryPrimitives.ReadUInt64BigEndian(_int64Buf);
        }

        /// <summary>Reads a boolean (XDR TRUE = 1, FALSE = 0).</summary>
        public bool ReadBool() => ReadInt32() != 0;

        /// <summary>Reads a 32-bit IEEE 754 float.</summary>
        public float ReadFloat()
        {
            ReadFull(_int32Buf, 4);
#if NETSTANDARD2_0
            return Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_int32Buf));
#else
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_int32Buf));
#endif
        }

        /// <summary>Reads a 64-bit IEEE 754 double.</summary>
        public double ReadDouble()
        {
            ReadFull(_int64Buf, 8);
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_int64Buf));
        }

#if NETSTANDARD2_0
        private static unsafe float Int32BitsToSingle(int value) => *(float*)&value;
#endif

        /// <summary>Reads an XDR enumeration value as a signed 32-bit integer.</summary>
        public int ReadEnum() => ReadInt32();

        // ── Opaque / String ──────────────────────────────────────────────────

        /// <summary>Reads <paramref name="length"/> bytes of fixed-length opaque data (padded to 4-byte boundary).</summary>
        public byte[] ReadFixedOpaque(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            var buf = new byte[length];
            if (length > 0)
            {
                ReadFull(buf, length);
                SkipPadding(length);
            }
            return buf;
        }

        /// <summary>Reads variable-length opaque data: a 4-byte length prefix followed by data (padded to 4 bytes).</summary>
        public byte[] ReadVarOpaque(int maxLength = int.MaxValue)
        {
            var length = (int)ReadUInt32();
            if (length < 0 || length > maxLength)
                throw new XdrException($"Variable-length opaque exceeds maximum size: {length} > {maxLength}");
            return ReadFixedOpaque(length);
        }

        /// <summary>Reads a UTF-8 encoded XDR string (variable-length opaque interpreted as UTF-8).</summary>
        public string ReadString(int maxLength = int.MaxValue)
        {
            var bytes = ReadVarOpaque(maxLength);
            return Encoding.UTF8.GetString(bytes);
        }

        // ── Arrays ───────────────────────────────────────────────────────────

        /// <summary>Reads a variable-length array of unsigned 32-bit integers.</summary>
        public uint[] ReadUInt32Array()
        {
            var count = (int)ReadUInt32();
            var result = new uint[count];
            for (int i = 0; i < count; i++) result[i] = ReadUInt32();
            return result;
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        private void ReadFull(byte[] buf, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = _stream.Read(buf, offset, count - offset);
                if (n == 0)
                    throw new EndOfStreamException("Unexpected end of XDR data.");
                offset += n;
            }
        }

        private void SkipPadding(int length)
        {
            int pad = (4 - (length & 3)) & 3;
            if (pad > 0)
            {
                // Reuse _int32Buf for discarding padding bytes
                ReadFull(_int32Buf, pad);
            }
        }

        /// <summary>Returns the current position in the underlying stream.</summary>
        public long Position => _stream.Position;
    }
}
