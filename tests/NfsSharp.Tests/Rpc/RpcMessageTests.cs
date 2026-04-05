using System;
using System.IO;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Tests.Rpc
{
    public class RpcMessageTests
    {
        // Builds a minimal accepted-reply byte array for a given XID.
        private static byte[] BuildAcceptedReply(uint xid, int acceptStat = 0 /* SUCCESS */)
        {
            using var ms = new MemoryStream();
            var w = new XdrWriter(ms);
            w.WriteUInt32(xid);           // XID
            w.WriteInt32(1);              // MsgTypeReply = 1
            w.WriteInt32(0);              // ReplyStatMsgAccepted = 0
            // Verifier: flavor=AUTH_NONE, body=empty
            w.WriteInt32(0);             // flavor
            w.WriteUInt32(0);            // body length
            w.WriteInt32(acceptStat);    // accept_stat
            return ms.ToArray();
        }

        private static byte[] BuildMsgDeniedReply(uint xid, int rejectStat, int detail)
        {
            using var ms = new MemoryStream();
            var w = new XdrWriter(ms);
            w.WriteUInt32(xid);
            w.WriteInt32(1);    // reply
            w.WriteInt32(1);    // MSG_DENIED
            w.WriteInt32(rejectStat);
            if (rejectStat == 0) // RPC_MISMATCH
            {
                w.WriteUInt32(2); // low
                w.WriteUInt32(2); // high
            }
            else
            {
                w.WriteInt32(detail); // auth_stat
            }
            return ms.ToArray();
        }

        [Fact]
        public void WriteCallHeader_Then_ReadReplyHeader_Success()
        {
            // Build a CALL, then build a synthetic accepted reply
            uint xid = 0xDEADBEEFu;

            using var callMs = new MemoryStream();
            var callWriter = new XdrWriter(callMs);
            RpcMessage.WriteCallHeader(callWriter, xid, 100003, 3, 6,
                new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 },  // AUTH_NONE cred bytes
                new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }); // AUTH_NONE verifier bytes

            byte[] replyBytes = BuildAcceptedReply(xid, 0);
            var reader = new XdrReader(replyBytes);
            // Should not throw
            RpcMessage.ReadReplyHeader(reader, xid);
        }

        [Fact]
        public void ReadReplyHeader_MsgDenied_ThrowsRpcException()
        {
            uint xid = 42u;
            byte[] replyBytes = BuildMsgDeniedReply(xid, rejectStat: 1 /* AUTH_ERROR */, detail: 0);
            var reader = new XdrReader(replyBytes);
            Assert.Throws<RpcException>(() => RpcMessage.ReadReplyHeader(reader, xid));
        }

        [Fact]
        public void ReadReplyHeader_ProgUnavail_ThrowsRpcException()
        {
            uint xid = 99u;
            byte[] replyBytes = BuildAcceptedReply(xid, acceptStat: 1 /* PROG_UNAVAIL */);
            var reader = new XdrReader(replyBytes);
            Assert.Throws<RpcException>(() => RpcMessage.ReadReplyHeader(reader, xid));
        }

        [Fact]
        public void ReadReplyHeader_XidMismatch_ThrowsRpcException()
        {
            uint xid = 100u;
            byte[] replyBytes = BuildAcceptedReply(xid + 1, acceptStat: 0);
            var reader = new XdrReader(replyBytes);
            Assert.Throws<RpcException>(() => RpcMessage.ReadReplyHeader(reader, xid));
        }

        [Fact]
        public void ReadReplyHeader_RpcMismatch_ThrowsRpcException()
        {
            uint xid = 55u;
            byte[] replyBytes = BuildMsgDeniedReply(xid, rejectStat: 0 /* RPC_MISMATCH */, detail: 0);
            var reader = new XdrReader(replyBytes);
            Assert.Throws<RpcException>(() => RpcMessage.ReadReplyHeader(reader, xid));
        }
    }
}
