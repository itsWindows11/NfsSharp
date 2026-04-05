using System;
using System.IO;
using NfsSharp.Auth;
using NfsSharp.Xdr;

namespace NfsSharp.Tests.Auth
{
    public class AuthCredentialTests
    {
        [Fact]
        public void AuthNone_Encode_FlavorZeroBodyLengthZero()
        {
            var cred = new AuthNoneCredentials();
            byte[] encoded = cred.Encode();

            var r = new XdrReader(encoded);
            int flavor = r.ReadInt32();
            uint bodyLen = r.ReadUInt32();

            Assert.Equal(0, flavor);
            Assert.Equal(0u, bodyLen);
        }

        [Fact]
        public void AuthSys_Encode_FlavorOneAndCorrectStructure()
        {
            var cred = new AuthSysCredentials("testhost", uid: 1000, gid: 1000,
                gidList: new uint[] { 100, 200 }, stamp: 12345);
            byte[] encoded = cred.Encode();

            var outer = new XdrReader(encoded);
            int flavor = outer.ReadInt32();
            Assert.Equal(1, flavor); // AUTH_SYS

            // The body is a var_opaque; read length then interpret as XDR
            byte[] body = outer.ReadVarOpaque();
            var inner = new XdrReader(body);

            uint stamp = inner.ReadUInt32();
            string name = inner.ReadString();
            uint uid = inner.ReadUInt32();
            uint gid = inner.ReadUInt32();
            uint[] gids = inner.ReadUInt32Array();

            Assert.Equal(12345u, stamp);
            Assert.Equal("testhost", name);
            Assert.Equal(1000u, uid);
            Assert.Equal(1000u, gid);
            Assert.Equal(new uint[] { 100, 200 }, gids);
        }

        [Fact]
        public void AuthSys_LongMachineName_ThrowsArgumentOutOfRangeException()
        {
            string longName = new string('a', 256);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AuthSysCredentials(longName));
        }

        [Fact]
        public void AuthSys_TooManyGids_ThrowsArgumentOutOfRangeException()
        {
            var tooManyGids = new uint[17];
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AuthSysCredentials("host", gidList: tooManyGids));
        }

        [Fact]
        public void RpcSecGss_Encode_ThrowsNotSupportedException()
        {
            var cred = new RpcSecGssCredentials();
            Assert.Throws<NotSupportedException>(() => cred.Encode());
        }
    }
}
