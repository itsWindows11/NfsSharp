using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NfsSharp.Xdr
{
    /// <summary>
    /// Writes XDR (External Data Representation, RFC 4506) encoded data to a stream.
    /// All XDR integer types are big-endian and padded to 4-byte multiples.
    /// </summary>
    public sealed class XdrWriter
    {
        private readonly Stream _stream;
        private readonly byte[] _int32Buf = new byte[4];
        private readonly byte[] _int64Buf = new byte[8];
        private static readonly byte[] s_padding = new byte[3];

        /// <summary>Initializes a new <see cref="XdrWriter"/> over the given stream.</summary>
        public XdrWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        // ── Primitives ───────────────────────────────────────────────────────

        /// <summary>Writes a signed 32-bit integer.</summary>
        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(_int32Buf, value);
            _stream.Write(_int32Buf, 0, 4);
        }

        /// <summary>Writes an unsigned 32-bit integer.</summary>
        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_int32Buf, value);
            _stream.Write(_int32Buf, 0, 4);
        }

        /// <summary>Writes a signed 64-bit integer (hyper).</summary>
        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64BigEndian(_int64Buf, value);
            _stream.Write(_int64Buf, 0, 8);
        }

        /// <summary>Writes an unsigned 64-bit integer (unsigned hyper).</summary>
        public void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(_int64Buf, value);
            _stream.Write(_int64Buf, 0, 8);
        }

        /// <summary>Writes a boolean (XDR TRUE = 1, FALSE = 0).</summary>
        public void WriteBool(bool value) => WriteInt32(value ? 1 : 0);

        /// <summary>Writes a 32-bit IEEE 754 float.</summary>
        public void WriteFloat(float value)
        {
            BinaryPrimitives.WriteInt32BigEndian(_int32Buf, BitConverter.SingleToInt32Bits(value));
            _stream.Write(_int32Buf, 0, 4);
        }

        /// <summary>Writes a 64-bit IEEE 754 double.</summary>
        public void WriteDouble(double value)
        {
            BinaryPrimitives.WriteInt64BigEndian(_int64Buf, BitConverter.DoubleToInt64Bits(value));
            _stream.Write(_int64Buf, 0, 8);
        }

        /// <summary>Writes an XDR enumeration value as a signed 32-bit integer.</summary>
        public void WriteEnum(int value) => WriteInt32(value);

        // ── Opaque / String ──────────────────────────────────────────────────

        /// <summary>
        /// Writes exactly <paramref name="length"/> bytes of fixed-length opaque data
        /// (with trailing padding to 4-byte boundary).
        /// </summary>
        public void WriteFixedOpaque(byte[] data, int length)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (data.Length < length) throw new ArgumentException("Buffer too short for specified length.", nameof(data));

            _stream.Write(data, 0, length);
            WritePadding(length);
        }

        /// <summary>
        /// Writes variable-length opaque data: a 4-byte length prefix followed by data (padded to 4 bytes).
        /// </summary>
        public void WriteVarOpaque(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            WriteUInt32((uint)data.Length);
            if (data.Length > 0)
            {
                _stream.Write(data, 0, data.Length);
                WritePadding(data.Length);
            }
        }

        /// <summary>Writes a UTF-8 encoded XDR string.</summary>
        public void WriteString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            WriteVarOpaque(Encoding.UTF8.GetBytes(value));
        }

        /// <summary>Writes a variable-length array of unsigned 32-bit integers.</summary>
        public void WriteUInt32Array(uint[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            WriteUInt32((uint)values.Length);
            foreach (var v in values) WriteUInt32(v);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void WritePadding(int length)
        {
            int pad = (4 - (length & 3)) & 3;
            if (pad > 0)
                _stream.Write(s_padding, 0, pad);
        }

        /// <summary>Returns a <see cref="MemoryStream"/> with all written data.</summary>
        public static byte[] Encode(Action<XdrWriter> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            using var ms = new MemoryStream();
            var writer = new XdrWriter(ms);
            action(writer);
            return ms.ToArray();
        }

        /// <summary>Current position in the underlying stream.</summary>
        public long Position => _stream.Position;
    }
}
