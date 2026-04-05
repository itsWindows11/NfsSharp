using System;
using System.IO;
using NfsSharp.Protocol;
using NfsSharp.Xdr;

namespace NfsSharp.Tests.Protocol
{
    public class NfsFileHandleTests
    {
        [Fact]
        public void SameBytes_AreEqual()
        {
            var a = new NfsFileHandle(new byte[] { 1, 2, 3, 4 });
            var b = new NfsFileHandle(new byte[] { 1, 2, 3, 4 });
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void DifferentBytes_AreNotEqual()
        {
            var a = new NfsFileHandle(new byte[] { 1, 2, 3, 4 });
            var b = new NfsFileHandle(new byte[] { 1, 2, 3, 5 });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DifferentLengths_AreNotEqual()
        {
            var a = new NfsFileHandle(new byte[] { 1, 2, 3 });
            var b = new NfsFileHandle(new byte[] { 1, 2, 3, 4 });
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void WriteTo_ReadFrom_RoundTrip()
        {
            var original = new NfsFileHandle(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 });
            using var ms = new MemoryStream();
            var writer = new XdrWriter(ms);
            original.WriteTo(writer);

            ms.Position = 0;
            var reader = new XdrReader(ms);
            var restored = NfsFileHandle.ReadFrom(reader);

            Assert.Equal(original, restored);
        }

        [Fact]
        public void ToString_IsHexString()
        {
            var handle = new NfsFileHandle(new byte[] { 0xAB, 0xCD });
            string s = handle.ToString();
            Assert.Equal("ABCD", s, ignoreCase: true);
        }

        [Fact]
        public void Empty_Handle_IsEqual_ToEmpty()
        {
            var a = new NfsFileHandle(Array.Empty<byte>());
            var b = new NfsFileHandle(Array.Empty<byte>());
            Assert.Equal(a, b);
        }
    }
}
