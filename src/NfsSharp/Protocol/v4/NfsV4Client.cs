using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Auth;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol.v4
{
    /// <summary>
    /// NFSv4 protocol client (RFC 7530).
    /// Uses COMPOUND operations to batch multiple NFS operations in a single RPC call.
    /// Operates statelessly (anonymous stateids) for reads and writes.
    /// </summary>
    internal sealed class NfsV4Client : INfsFileOperations, IDisposable
    {
        private readonly RpcClient       _rpc;
        private readonly AuthCredentials _credentials;

        private const uint NfsV4Program = RpcConstants.NfsProgram;
        private const uint NfsV4Version = 4;
        private const int  MaxRW        = 1 * 1024 * 1024; // 1 MiB per op

        // NFSv4 operation codes (RFC 7530 §14)
        private const int Op_Access     = 3;
        private const int Op_Close      = 4;
        private const int Op_Commit     = 5;
        private const int Op_Create     = 6;
        private const int Op_GetAttr    = 9;
        private const int Op_GetFh      = 10;
        private const int Op_Link       = 11;
        private const int Op_Lookup     = 15;
        private const int Op_Open       = 18;
        private const int Op_PutFh      = 22;
        private const int Op_PutRootFh  = 24;
        private const int Op_Read       = 25;
        private const int Op_ReadDir    = 26;
        private const int Op_Remove     = 28;
        private const int Op_Rename     = 29;
        private const int Op_SaveFh     = 32;
        private const int Op_SetAttr    = 34;
        private const int Op_Write      = 38;

        // NFSv4 write stability
        private const int Unstable4 = 0;

        // NFSv4 attribute bitmask bits (word 0 of the two-word bitmap)
        private const uint AttrBit0_Type = 0x00000001;
        private const uint AttrBit0_Size = 0x00000800;
        // word 1
        private const uint AttrBit1_Mode = 0x00000002;

        internal NfsV4Client(string host, int port, AuthCredentials credentials,
            TimeSpan connectTimeout, TimeSpan readTimeout)
        {
            _rpc         = new RpcClient(host, port, connectTimeout, readTimeout);
            _credentials = credentials;
        }

        internal Task ConnectAsync(CancellationToken ct = default) => _rpc.ConnectAsync(ct);

        // ── INfsFileOperations ────────────────────────────────────────────────

        public async Task<NfsReadResult> ReadAsync(
            NfsFileHandle handle, long offset, int count, CancellationToken ct)
        {
            if (count > MaxRW) count = MaxRW;

            // COMPOUND (2 ops): PUTFH + READ
            var reader = await CompoundAsync("read", 2, w =>
            {
                WritePutFh(w, handle);
                w.WriteInt32(Op_Read);
                WriteAnonymousStateId(w);
                w.WriteUInt64((ulong)offset);
                w.WriteUInt32((uint)count);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_Read);
            bool   eof  = reader.ReadBool();
            byte[] data = reader.ReadVarOpaque(MaxRW);
            return new NfsReadResult(data, eof);
        }

        public async Task<int> WriteAsync(
            NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
        {
            if (count > MaxRW) count = MaxRW;
            var slice = new byte[count];
            Array.Copy(data, dataOffset, slice, 0, count);

            // COMPOUND (2 ops): PUTFH + WRITE
            var reader = await CompoundAsync("write", 2, w =>
            {
                WritePutFh(w, handle);
                w.WriteInt32(Op_Write);
                WriteAnonymousStateId(w);
                w.WriteUInt64((ulong)offset);
                w.WriteInt32(Unstable4);
                w.WriteVarOpaque(slice);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_Write);
            uint written = reader.ReadUInt32();
            reader.ReadInt32();        // committed
            reader.ReadFixedOpaque(8); // writeverf4
            return (int)written;
        }

        public async Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
        {
            // COMPOUND (2 ops): PUTFH + GETATTR
            var reader = await CompoundAsync("getattr", 2, w =>
            {
                WritePutFh(w, handle);
                w.WriteInt32(Op_GetAttr);
                // attrrequest: 2 words → [type|size] [mode]
                w.WriteUInt32(2);
                w.WriteUInt32(AttrBit0_Type | AttrBit0_Size);
                w.WriteUInt32(AttrBit1_Mode);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_GetAttr);
            return ReadGetAttrResult(reader);
        }

        public async Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
        {
            // COMPOUND (2 ops): PUTFH + COMMIT
            var reader = await CompoundAsync("commit", 2, w =>
            {
                WritePutFh(w, handle);
                w.WriteInt32(Op_Commit);
                w.WriteUInt64((ulong)offset);
                w.WriteUInt32((uint)count);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_Commit);
            reader.ReadFixedOpaque(8); // writeverf4
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        internal async Task<NfsFileHandle> LookupAsync(
            NfsFileHandle dir, string name, CancellationToken ct = default)
        {
            // COMPOUND (3 ops): PUTFH + LOOKUP + GETFH
            var reader = await CompoundAsync("lookup", 3, w =>
            {
                WritePutFh(w, dir);
                w.WriteInt32(Op_Lookup);
                w.WriteString(name);
                w.WriteInt32(Op_GetFh);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_Lookup);
            CheckOpStatus(reader, Op_GetFh);
            return NfsFileHandle.ReadFrom(reader, NfsFileHandle.MaxSizeV4);
        }

        // ── ReadDir ──────────────────────────────────────────────────────────

        internal async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
            NfsFileHandle dir, CancellationToken ct = default)
        {
            var results    = new List<NfsDirectoryEntry>();
            ulong cookie   = 0;
            byte[] cookieVerf = new byte[8];

            while (true)
            {
                // COMPOUND (2 ops): PUTFH + READDIR
                var reader = await CompoundAsync("readdir", 2, w =>
                {
                    WritePutFh(w, dir);
                    w.WriteInt32(Op_ReadDir);
                    w.WriteUInt64(cookie);
                    w.WriteFixedOpaque(cookieVerf, 8);
                    w.WriteUInt32(4096);   // dircount
                    w.WriteUInt32(65536);  // maxcount
                    // attrrequest: 2 zero-words (no attributes)
                    w.WriteUInt32(2);
                    w.WriteUInt32(0);
                    w.WriteUInt32(0);
                }, ct).ConfigureAwait(false);

                CheckOpStatus(reader, Op_PutFh);
                CheckOpStatus(reader, Op_ReadDir);
                cookieVerf = reader.ReadFixedOpaque(8);

                bool any = false;
                while (reader.ReadBool()) // value_follows
                {
                    ulong  fileid = reader.ReadUInt64();
                    string name   = reader.ReadString(255);
                    ulong  ck     = reader.ReadUInt64();
                    // attrlist4 (length-prefixed, discard)
                    reader.ReadVarOpaque();
                    results.Add(new NfsDirectoryEntry { FileId = fileid, Name = name, Cookie = ck });
                    cookie = ck;
                    any = true;
                }

                bool eof = reader.ReadBool();
                if (eof || !any) break;
            }

            return results;
        }

        // ── Remove ────────────────────────────────────────────────────────────

        internal async Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct = default)
        {
            // COMPOUND (2 ops): PUTFH + REMOVE
            var reader = await CompoundAsync("remove", 2, w =>
            {
                WritePutFh(w, dir);
                w.WriteInt32(Op_Remove);
                w.WriteString(name);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_Remove);
            SkipChangeInfo(reader);
        }

        // ── Rename ────────────────────────────────────────────────────────────

        internal async Task RenameAsync(
            NfsFileHandle fromDir, string fromName,
            NfsFileHandle toDir,   string toName,
            CancellationToken ct = default)
        {
            // COMPOUND (4 ops): PUTFH(fromDir) + SAVEFH + PUTFH(toDir) + RENAME
            var reader = await CompoundAsync("rename", 4, w =>
            {
                WritePutFh(w, fromDir);
                w.WriteInt32(Op_SaveFh);
                WritePutFh(w, toDir);
                w.WriteInt32(Op_Rename);
                w.WriteString(fromName);
                w.WriteString(toName);
            }, ct).ConfigureAwait(false);

            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_SaveFh);
            CheckOpStatus(reader, Op_PutFh);
            CheckOpStatus(reader, Op_Rename);
        }

        // ── Core COMPOUND dispatcher ──────────────────────────────────────────

        /// <summary>
        /// Sends an NFSv4 COMPOUND RPC with <paramref name="opCount"/> operations
        /// encoded by <paramref name="writeOps"/>, and returns a reader positioned
        /// at the start of the reply results array.
        /// </summary>
        private async Task<XdrReader> CompoundAsync(
            string            tag,
            int               opCount,
            Action<XdrWriter> writeOps,
            CancellationToken ct)
        {
            var reader = await _rpc.CallAsync(
                NfsV4Program, NfsV4Version,
                0 /* NFSPROC4_COMPOUND */,
                _credentials,
                w =>
                {
                    w.WriteString(tag);
                    w.WriteUInt32(0);            // minorversion (NFSv4.0)
                    w.WriteUInt32((uint)opCount);
                    writeOps(w);
                },
                ct).ConfigureAwait(false);

            // COMPOUND reply header: status + tag + resultcount
            int status = reader.ReadInt32();
            reader.ReadString(256);       // tag (discard)
            reader.ReadUInt32();          // resultcount

            if (status != 0)
                throw new NfsException(MapV4Status(status),
                    $"NFSv4 COMPOUND failed (nfsstat4={status})");

            return reader;
        }

        // ── XDR helpers ───────────────────────────────────────────────────────

        private static void WritePutFh(XdrWriter w, NfsFileHandle fh)
        {
            w.WriteInt32(Op_PutFh);
            fh.WriteTo(w);
        }

        /// <summary>Writes an anonymous (all-zeros) stateid4 (seqid=0, other=12×0).</summary>
        private static void WriteAnonymousStateId(XdrWriter w)
        {
            w.WriteUInt32(0);                   // seqid = 0 (anonymous)
            w.WriteFixedOpaque(new byte[12], 12); // other
        }

        private static void CheckOpStatus(XdrReader reader, int expectedOp)
        {
            int resop  = reader.ReadInt32();
            int status = reader.ReadInt32();
            if (status != 0)
                throw new NfsException(MapV4Status(status),
                    $"NFSv4 op {resop} (expected {expectedOp}) failed (nfsstat4={status})");
        }

        private static NfsFileAttributes ReadGetAttrResult(XdrReader reader)
        {
            // attrlist4: length-prefixed XDR blob containing the requested attributes
            byte[] blob  = reader.ReadVarOpaque();
            var inner    = new XdrReader(blob);

            // bitmap4: 2 words
            uint w0 = inner.ReadUInt32();
            uint w1 = inner.ReadUInt32();

            NfsFileType type = NfsFileType.Regular;
            ulong       size = 0;
            uint        mode = 0;

            if ((w0 & AttrBit0_Type) != 0) type = (NfsFileType)inner.ReadInt32();
            if ((w0 & AttrBit0_Size) != 0) size = inner.ReadUInt64();
            if ((w1 & AttrBit1_Mode) != 0) mode = inner.ReadUInt32();

            return new NfsFileAttributes { Type = type, Size = size, Mode = mode };
        }

        private static void SkipChangeInfo(XdrReader reader)
        {
            reader.ReadBool();    // atomic
            reader.ReadUInt64(); // before
            reader.ReadUInt64(); // after
        }

        /// <summary>Maps an NFSv4 nfsstat4 value to the common <see cref="NfsStatus"/> enum.</summary>
        private static NfsStatus MapV4Status(int v4status) => v4status switch
        {
            0     => NfsStatus.Ok,
            1     => NfsStatus.Perm,
            2     => NfsStatus.NoEnt,
            5     => NfsStatus.Io,
            13    => NfsStatus.Acces,
            20    => NfsStatus.NotDir,
            21    => NfsStatus.IsDir,
            22    => NfsStatus.Inval,
            27    => NfsStatus.FBig,
            28    => NfsStatus.NoSpc,
            30    => NfsStatus.RoFs,
            70    => NfsStatus.Stale,
            10001 => NfsStatus.BadHandle,
            10003 => NfsStatus.BadCookie,
            10004 => NfsStatus.NotSupp,
            10006 => NfsStatus.ServerFault,
            _     => NfsStatus.ServerFault,
        };

        public void Dispose() => _rpc.Dispose();
    }
}
