using System;
using System.IO;
using NfsSharp.Xdr;

namespace NfsSharp.Rpc
{
    /// <summary>
    /// Encodes and decodes ONC RPC call and reply message headers (RFC 5531 §8).
    /// </summary>
    internal static class RpcMessage
    {
        // ── Call ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes an RPC CALL header to <paramref name="writer"/>.
        /// </summary>
        internal static void WriteCallHeader(
            XdrWriter writer,
            uint      xid,
            uint      program,
            uint      version,
            uint      procedure,
            byte[]    credentialsOpaque,
            byte[]    verifierOpaque)
        {
            writer.WriteUInt32(xid);
            writer.WriteInt32(RpcConstants.MsgTypeCall);
            writer.WriteUInt32(RpcConstants.RpcVersion);
            writer.WriteUInt32(program);
            writer.WriteUInt32(version);
            writer.WriteUInt32(procedure);

            // Credentials (opaque auth body prefixed by flavor)
            WriteAuth(writer, credentialsOpaque);

            // Verifier (always AUTH_NONE for client → server)
            WriteAuth(writer, verifierOpaque);
        }

        // ── Reply ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads and validates an RPC REPLY header. Throws on error replies.
        /// Returns the XDR reader positioned at the start of the reply body.
        /// </summary>
        internal static void ReadReplyHeader(XdrReader reader, uint expectedXid)
        {
            uint xid = reader.ReadUInt32();
            if (xid != expectedXid)
                throw new RpcException($"XID mismatch: expected {expectedXid}, got {xid}");

            int msgType = reader.ReadInt32();
            if (msgType != RpcConstants.MsgTypeReply)
                throw new RpcException($"Expected REPLY message type ({RpcConstants.MsgTypeReply}), got {msgType}");

            int replyStat = reader.ReadInt32();
            if (replyStat == RpcConstants.ReplyStatMsgDenied)
            {
                int rejectStat = reader.ReadInt32();
                if (rejectStat == RpcConstants.RejectStatRpcMismatch)
                {
                    uint low  = reader.ReadUInt32();
                    uint high = reader.ReadUInt32();
                    throw new RpcException($"RPC version mismatch: server supports {low}-{high}");
                }
                int authStat = reader.ReadInt32();
                throw new RpcException($"RPC authentication error: auth_stat={authStat}");
            }

            if (replyStat != RpcConstants.ReplyStatMsgAccepted)
                throw new RpcException($"Unknown reply_stat: {replyStat}");

            // Read verifier (discard)
            ReadAuth(reader);

            int acceptStat = reader.ReadInt32();
            switch (acceptStat)
            {
                case RpcConstants.AcceptStatSuccess:
                    return;
                case RpcConstants.AcceptStatProgUnavail:
                    throw new RpcException("Program not available on server.");
                case RpcConstants.AcceptStatProgMismatch:
                {
                    uint low  = reader.ReadUInt32();
                    uint high = reader.ReadUInt32();
                    throw new RpcException($"Program version mismatch: server supports {low}-{high}");
                }
                case RpcConstants.AcceptStatProcUnavail:
                    throw new RpcException("Procedure not available on server.");
                case RpcConstants.AcceptStatGarbageArgs:
                    throw new RpcException("Server could not decode call arguments.");
                case RpcConstants.AcceptStatSystemErr:
                    throw new RpcException("Remote system error on server.");
                default:
                    throw new RpcException($"Unknown accept_stat: {acceptStat}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void WriteAuth(XdrWriter writer, byte[] body)
        {
            // body is already fully XDR-encoded: [flavor int32][opaque body (var_opaque)]
            // Write it verbatim — no additional length prefix or padding needed.
            writer.WriteRaw(body);
        }

        private static void ReadAuth(XdrReader reader)
        {
            // flavor + variable-length opaque body
            reader.ReadInt32();        // flavor
            reader.ReadVarOpaque();    // body
        }
    }
}
