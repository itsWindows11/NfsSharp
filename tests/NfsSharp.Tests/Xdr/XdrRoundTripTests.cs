using System;
using System.IO;
using NfsSharp.Xdr;

namespace NfsSharp.Tests.Xdr
{
    public class XdrRoundTripTests
    {
        private static (XdrWriter writer, MemoryStream ms) MakeWriter()
        {
            var ms = new MemoryStream();
            return (new XdrWriter(ms), ms);
        }

        private static XdrReader ReaderFrom(MemoryStream ms)
        {
            ms.Position = 0;
            return new XdrReader(ms);
        }

        [Fact]
        public void Int32_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteInt32(42);
            w.WriteInt32(-1);
            w.WriteInt32(int.MinValue);
            var r = ReaderFrom(ms);
            Assert.Equal(42, r.ReadInt32());
            Assert.Equal(-1, r.ReadInt32());
            Assert.Equal(int.MinValue, r.ReadInt32());
        }

        [Fact]
        public void UInt32_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteUInt32(0);
            w.WriteUInt32(uint.MaxValue);
            var r = ReaderFrom(ms);
            Assert.Equal(0u, r.ReadUInt32());
            Assert.Equal(uint.MaxValue, r.ReadUInt32());
        }

        [Fact]
        public void Int64_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteInt64(long.MinValue);
            w.WriteInt64(long.MaxValue);
            var r = ReaderFrom(ms);
            Assert.Equal(long.MinValue, r.ReadInt64());
            Assert.Equal(long.MaxValue, r.ReadInt64());
        }

        [Fact]
        public void UInt64_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteUInt64(0);
            w.WriteUInt64(ulong.MaxValue);
            var r = ReaderFrom(ms);
            Assert.Equal(0ul, r.ReadUInt64());
            Assert.Equal(ulong.MaxValue, r.ReadUInt64());
        }

        [Fact]
        public void Bool_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteBool(true);
            w.WriteBool(false);
            var r = ReaderFrom(ms);
            Assert.True(r.ReadBool());
            Assert.False(r.ReadBool());
        }

        [Fact]
        public void Float_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteFloat(3.14f);
            w.WriteFloat(float.NegativeInfinity);
            w.WriteFloat(0f);
            var r = ReaderFrom(ms);
            Assert.Equal(3.14f, r.ReadFloat());
            Assert.Equal(float.NegativeInfinity, r.ReadFloat());
            Assert.Equal(0f, r.ReadFloat());
        }

        [Fact]
        public void Double_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteDouble(Math.PI);
            w.WriteDouble(double.NaN);
            var r = ReaderFrom(ms);
            Assert.Equal(Math.PI, r.ReadDouble());
            Assert.True(double.IsNaN(r.ReadDouble()));
        }

        [Fact]
        public void Enum_RoundTrip()
        {
            var (w, ms) = MakeWriter();
            w.WriteEnum(7);
            var r = ReaderFrom(ms);
            Assert.Equal(7, r.ReadEnum());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(8)]
        public void FixedOpaque_RoundTrip(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = (byte)(i + 1);

            var (w, ms) = MakeWriter();
            w.WriteFixedOpaque(data, length);

            // Output should be padded to next 4-byte boundary
            int expectedSize = ((length + 3) / 4) * 4;
            Assert.Equal(expectedSize, ms.Length);

            var r = ReaderFrom(ms);
            var result = r.ReadFixedOpaque(length);
            Assert.Equal(data, result);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void VarOpaque_RoundTrip(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = (byte)(i + 10);

            var (w, ms) = MakeWriter();
            w.WriteVarOpaque(data);

            var r = ReaderFrom(ms);
            var result = r.ReadVarOpaque();
            Assert.Equal(data, result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("hello")]
        [InlineData("hello world this is a test")]
        [InlineData("Unicode: \u4e2d\u6587")]
        public void String_RoundTrip(string value)
        {
            var (w, ms) = MakeWriter();
            w.WriteString(value);
            var r = ReaderFrom(ms);
            Assert.Equal(value, r.ReadString());
        }

        [Fact]
        public void UInt32Array_RoundTrip()
        {
            var arr = new uint[] { 1, 2, 3, 100, uint.MaxValue };
            var (w, ms) = MakeWriter();
            w.WriteUInt32Array(arr);
            var r = ReaderFrom(ms);
            Assert.Equal(arr, r.ReadUInt32Array());
        }

        [Fact]
        public void UInt32Array_Empty_RoundTrip()
        {
            var arr = Array.Empty<uint>();
            var (w, ms) = MakeWriter();
            w.WriteUInt32Array(arr);
            var r = ReaderFrom(ms);
            Assert.Equal(arr, r.ReadUInt32Array());
        }
    }
}
