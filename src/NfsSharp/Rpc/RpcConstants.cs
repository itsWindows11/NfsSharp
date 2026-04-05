namespace NfsSharp.Rpc
{
    /// <summary>Constants for the ONC RPC protocol (RFC 5531).</summary>
    internal static class RpcConstants
    {
        // ── Port numbers ─────────────────────────────────────────────────────
        public const int PortMapperPort = 111;

        // ── Protocol versions ────────────────────────────────────────────────
        public const uint RpcVersion = 2;

        // ── Message types ────────────────────────────────────────────────────
        public const int MsgTypeCall  = 0;
        public const int MsgTypeReply = 1;

        // ── Reply stats ──────────────────────────────────────────────────────
        public const int ReplyStatMsgAccepted = 0;
        public const int ReplyStatMsgDenied   = 1;

        // ── Accept stats ─────────────────────────────────────────────────────
        public const int AcceptStatSuccess      = 0;
        public const int AcceptStatProgUnavail  = 1;
        public const int AcceptStatProgMismatch = 2;
        public const int AcceptStatProcUnavail  = 3;
        public const int AcceptStatGarbageArgs  = 4;
        public const int AcceptStatSystemErr    = 5;

        // ── Reject stats ─────────────────────────────────────────────────────
        public const int RejectStatRpcMismatch = 0;
        public const int RejectStatAuthError   = 1;

        // ── Authentication flavours ──────────────────────────────────────────
        public const int AuthFlavorNone    = 0;
        public const int AuthFlavorSys     = 1;
        public const int AuthFlavorShort   = 2;
        public const int AuthFlavorGss     = 6;

        // ── Program numbers ──────────────────────────────────────────────────
        public const uint PortMapperProgram = 100000;
        public const uint MountProgram      = 100005;
        public const uint NfsProgram        = 100003;

        // ── Port mapper procedure numbers ────────────────────────────────────
        public const uint PmapProcNull    = 0;
        public const uint PmapProcGetPort = 3;

        // ── Mount procedure numbers ──────────────────────────────────────────
        public const uint MntProcNull   = 0;
        public const uint MntProcMnt    = 1;
        public const uint MntProcUmnt   = 3;
        public const uint MntProcExport = 5;

        // ── NFS procedure numbers (shared across v2 and v3 where identical) ──
        public const uint NfsProcNull    = 0;
        public const uint NfsProcGetAttr = 1;
        public const uint NfsProcSetAttr = 2;
        public const uint NfsProcLookup  = 3;
        public const uint NfsProcAccess  = 4;   // v3+
        public const uint NfsProcReadLink = 5;
        public const uint NfsProcRead    = 6;
        public const uint NfsProcWrite   = 8;
        public const uint NfsProcCreate  = 9;
        public const uint NfsProcMkDir   = 14;
        public const uint NfsProcSymLink = 10;
        public const uint NfsProcMkNod   = 11;  // v3+
        public const uint NfsProcRemove  = 12;
        public const uint NfsProcRmDir   = 15;
        public const uint NfsProcRename  = 16;
        public const uint NfsProcLink    = 17;
        public const uint NfsProcReadDir = 16;
        public const uint NfsProcReadDirPlus = 17; // v3+
        public const uint NfsProcFsStat  = 18;
        public const uint NfsProcFsInfo  = 19;  // v3+
        public const uint NfsProcPathConf = 20; // v3+
        public const uint NfsProcCommit  = 21;  // v3+

        // NFSv2 procedure numbers (differ for some ops)
        public const uint Nfs2ProcReadDir = 16;
        public const uint Nfs2ProcStatFs  = 17;

        // NFSv3 procedure numbers
        public const uint Nfs3ProcNull       = 0;
        public const uint Nfs3ProcGetAttr    = 1;
        public const uint Nfs3ProcSetAttr    = 2;
        public const uint Nfs3ProcLookup     = 3;
        public const uint Nfs3ProcAccess     = 4;
        public const uint Nfs3ProcReadLink   = 5;
        public const uint Nfs3ProcRead       = 6;
        public const uint Nfs3ProcWrite      = 7;
        public const uint Nfs3ProcCreate     = 8;
        public const uint Nfs3ProcMkDir      = 9;
        public const uint Nfs3ProcSymLink    = 10;
        public const uint Nfs3ProcMkNod      = 11;
        public const uint Nfs3ProcRemove     = 12;
        public const uint Nfs3ProcRmDir      = 13;
        public const uint Nfs3ProcRename     = 14;
        public const uint Nfs3ProcLink       = 15;
        public const uint Nfs3ProcReadDir    = 16;
        public const uint Nfs3ProcReadDirPlus = 17;
        public const uint Nfs3ProcFsStat     = 18;
        public const uint Nfs3ProcFsInfo     = 19;
        public const uint Nfs3ProcPathConf   = 20;
        public const uint Nfs3ProcCommit     = 21;

        // ── Miscellaneous ────────────────────────────────────────────────────
        /// <summary>TCP record-marking layer fragment header high bit (last fragment flag).</summary>
        public const uint RecordLastFragment = 0x80000000u;

        /// <summary>Default maximum RPC payload size (1 MiB).</summary>
        public const int DefaultMaxPayload = 1 * 1024 * 1024;
    }
}
