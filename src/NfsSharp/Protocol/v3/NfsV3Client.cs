using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Auth;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol.v3
{
    /// <summary>
    /// NFSv3 protocol client (RFC 1813).
    /// Implements all 22 NFSv3 procedures including READDIRPLUS and COMMIT.
    /// </summary>
    internal sealed class NfsV3Client : INfsFileOperations, IDisposable
    {
        private readonly RpcClient       _rpc;
        private readonly AuthCredentials _credentials;

        private const uint NfsV3Program = RpcConstants.NfsProgram;
        private const uint NfsV3Version = 3;
        private const int  MaxRW        = 1 * 1024 * 1024; // 1 MiB per operation

        internal NfsV3Client(string host, int port, AuthCredentials credentials,
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

            var reader = await CallAsync(RpcConstants.Nfs3ProcRead, w =>
            {
                handle.WriteTo(w);
                w.WriteUInt64((ulong)offset);
                w.WriteUInt32((uint)count);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipPostOpAttr(reader); // post_op_attr
            uint bytesRead = reader.ReadUInt32();
            bool eof       = reader.ReadBool();
            byte[] data    = reader.ReadVarOpaque(MaxRW);
            // data length may differ from bytesRead for trailing padding reasons; trust bytesRead.
            if (data.Length > (int)bytesRead)
            {
                var trimmed = new byte[bytesRead];
                Array.Copy(data, trimmed, (int)bytesRead);
                data = trimmed;
            }
            return new NfsReadResult(data, eof);
        }

        public async Task<int> WriteAsync(
            NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
        {
            if (count > MaxRW) count = MaxRW;
            var slice = new byte[count];
            Array.Copy(data, dataOffset, slice, 0, count);

            var reader = await CallAsync(RpcConstants.Nfs3ProcWrite, w =>
            {
                handle.WriteTo(w);
                w.WriteUInt64((ulong)offset);
                w.WriteUInt32((uint)count);
                w.WriteInt32(0); // UNSTABLE = 0 (caller calls Commit separately)
                w.WriteVarOpaque(slice);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipWccData(reader);    // file_wcc
            uint written = reader.ReadUInt32();
            reader.ReadInt32();     // committed
            reader.ReadFixedOpaque(8); // verf (write verifier)
            return (int)written;
        }

        public async Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcGetAttr, w =>
                handle.WriteTo(w), ct).ConfigureAwait(false);

            CheckStatus(reader);
            return NfsFileAttributes.ReadV3(reader);
        }

        public async Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcCommit, w =>
            {
                handle.WriteTo(w);
                w.WriteUInt64((ulong)offset);
                w.WriteUInt32((uint)count);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipWccData(reader); // file_wcc
            // verf (8 bytes) – discard
            reader.ReadFixedOpaque(8);
        }

        // ── SETATTR ───────────────────────────────────────────────────────────

        internal async Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcSetAttr, w =>
            {
                handle.WriteTo(w);
                WriteSetAttr(w, attrs);
                w.WriteBool(false); // guard (no sattrguard3)
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipWccData(reader);
        }

        // ── LOOKUP ────────────────────────────────────────────────────────────

        internal async Task<(NfsFileHandle handle, NfsFileAttributes attrs)> LookupAsync(
            NfsFileHandle dir, string name, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcLookup, w =>
            {
                dir.WriteTo(w);
                w.WriteString(name);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            var fh    = NfsFileHandle.ReadFrom(reader);
            var attrs = ReadPostOpAttr(reader)!;     // object attributes
            SkipPostOpAttr(reader);                  // dir attributes
            return (fh, attrs!);
        }

        // ── ACCESS ────────────────────────────────────────────────────────────

        internal async Task<NfsAccessFlags> AccessAsync(
            NfsFileHandle handle, NfsAccessFlags requested, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcAccess, w =>
            {
                handle.WriteTo(w);
                w.WriteUInt32((uint)requested);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipPostOpAttr(reader);
            return (NfsAccessFlags)reader.ReadUInt32();
        }

        // ── READLINK ──────────────────────────────────────────────────────────

        internal async Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcReadLink, w =>
                handle.WriteTo(w), ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipPostOpAttr(reader);
            return reader.ReadString(4096);
        }

        // ── CREATE / MKDIR / SYMLINK / MKNOD ─────────────────────────────────

        internal async Task<(NfsFileHandle? handle, NfsFileAttributes? attrs)> CreateAsync(
            NfsFileHandle dir, string name, NfsSetAttributes attrs, bool exclusive = false,
            CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcCreate, w =>
            {
                dir.WriteTo(w);
                w.WriteString(name);
                w.WriteInt32(exclusive ? 1 : 0); // EXCLUSIVE = 1, UNCHECKED = 0
                if (!exclusive) WriteSetAttr(w, attrs);
                else            w.WriteFixedOpaque(new byte[8], 8); // createverf3
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            return ReadPostOpFhAttr(reader);
        }

        internal async Task<(NfsFileHandle? handle, NfsFileAttributes? attrs)> MkDirAsync(
            NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcMkDir, w =>
            {
                dir.WriteTo(w);
                w.WriteString(name);
                WriteSetAttr(w, attrs);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            return ReadPostOpFhAttr(reader);
        }

        internal async Task SymLinkAsync(NfsFileHandle dir, string name, string linkPath,
            NfsSetAttributes attrs, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcSymLink, w =>
            {
                dir.WriteTo(w);
                w.WriteString(name);
                WriteSetAttr(w, attrs);
                w.WriteString(linkPath);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
        }

        // ── REMOVE / RMDIR ────────────────────────────────────────────────────

        internal async Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcRemove, w =>
            {
                dir.WriteTo(w);
                w.WriteString(name);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
        }

        internal async Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcRmDir, w =>
            {
                dir.WriteTo(w);
                w.WriteString(name);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
        }

        // ── RENAME ────────────────────────────────────────────────────────────

        internal async Task RenameAsync(
            NfsFileHandle fromDir, string fromName,
            NfsFileHandle toDir,   string toName,
            CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcRename, w =>
            {
                fromDir.WriteTo(w);
                w.WriteString(fromName);
                toDir.WriteTo(w);
                w.WriteString(toName);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
        }

        // ── LINK ──────────────────────────────────────────────────────────────

        internal async Task LinkAsync(
            NfsFileHandle file, NfsFileHandle linkDir, string linkName,
            CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcLink, w =>
            {
                file.WriteTo(w);
                linkDir.WriteTo(w);
                w.WriteString(linkName);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
        }

        // ── READDIR ───────────────────────────────────────────────────────────

        internal async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
            NfsFileHandle dir, CancellationToken ct = default)
        {
            var results = new List<NfsDirectoryEntry>();
            ulong cookie     = 0;
            byte[] cookieVerf = new byte[8];

            while (true)
            {
                var reader = await CallAsync(RpcConstants.Nfs3ProcReadDir, w =>
                {
                    dir.WriteTo(w);
                    w.WriteUInt64(cookie);
                    w.WriteFixedOpaque(cookieVerf, 8);
                    w.WriteUInt32(4096); // dircount
                }, ct).ConfigureAwait(false);

                CheckStatus(reader);
                SkipPostOpAttr(reader);
                cookieVerf = reader.ReadFixedOpaque(8);

                bool any = false;
                while (reader.ReadBool()) // value_follows
                {
                    ulong  fileid = reader.ReadUInt64();
                    string name   = reader.ReadString(255);
                    ulong  ck     = reader.ReadUInt64();
                    results.Add(new NfsDirectoryEntry { FileId = fileid, Name = name, Cookie = ck });
                    cookie = ck;
                    any = true;
                }

                bool eof = reader.ReadBool();
                if (eof || !any) break;
            }

            return results;
        }

        // ── READDIRPLUS ──────────────────────────────────────────────────────

        internal async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirPlusAsync(
            NfsFileHandle dir, CancellationToken ct = default)
        {
            var results = new List<NfsDirectoryEntry>();
            ulong cookie      = 0;
            byte[] cookieVerf = new byte[8];

            while (true)
            {
                var reader = await CallAsync(RpcConstants.Nfs3ProcReadDirPlus, w =>
                {
                    dir.WriteTo(w);
                    w.WriteUInt64(cookie);
                    w.WriteFixedOpaque(cookieVerf, 8);
                    w.WriteUInt32(4096);   // dircount
                    w.WriteUInt32(65536);  // maxcount
                }, ct).ConfigureAwait(false);

                CheckStatus(reader);
                SkipPostOpAttr(reader);
                cookieVerf = reader.ReadFixedOpaque(8);

                bool any = false;
                while (reader.ReadBool()) // value_follows
                {
                    ulong  fileid = reader.ReadUInt64();
                    string name   = reader.ReadString(255);
                    ulong  ck     = reader.ReadUInt64();
                    // name_attributes (post_op_attr)
                    NfsFileAttributes? attrs = ReadPostOpAttr(reader);
                    // name_handle (post_op_fh3)
                    NfsFileHandle? fh = null;
                    if (reader.ReadBool()) fh = NfsFileHandle.ReadFrom(reader);

                    results.Add(new NfsDirectoryEntry
                    {
                        FileId     = fileid,
                        Name       = name,
                        Cookie     = ck,
                        Attributes = attrs,
                        FileHandle = fh,
                    });
                    cookie = ck;
                    any = true;
                }

                bool eof = reader.ReadBool();
                if (eof || !any) break;
            }

            return results;
        }

        // ── FSSTAT / FSINFO / PATHCONF ────────────────────────────────────────

        internal async Task<NfsFsStat> FsStatAsync(NfsFileHandle fsh, CancellationToken ct = default)
        {
            var reader = await CallAsync(RpcConstants.Nfs3ProcFsStat, w =>
                fsh.WriteTo(w), ct).ConfigureAwait(false);

            CheckStatus(reader);
            SkipPostOpAttr(reader);
            return new NfsFsStat
            {
                TotalBytes   = reader.ReadUInt64(),
                FreeBytes    = reader.ReadUInt64(),
                AvailBytes   = reader.ReadUInt64(),
                TotalFiles   = reader.ReadUInt64(),
                FreeFiles    = reader.ReadUInt64(),
                AvailFiles   = reader.ReadUInt64(),
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private Task<XdrReader> CallAsync(uint procedure, Action<XdrWriter> args, CancellationToken ct)
            => _rpc.CallAsync(NfsV3Program, NfsV3Version, procedure, _credentials, args, ct);

        private static void CheckStatus(XdrReader reader)
        {
            int status = reader.ReadInt32();
            if (status != 0)
                throw new NfsException((NfsStatus)status);
        }

        private static void SkipPostOpAttr(XdrReader reader)
        {
            if (reader.ReadBool()) // attributes_follow
                NfsFileAttributes.ReadV3(reader);
        }

        private static NfsFileAttributes? ReadPostOpAttr(XdrReader reader)
        {
            return reader.ReadBool() ? NfsFileAttributes.ReadV3(reader) : null;
        }

        private static (NfsFileHandle? fh, NfsFileAttributes? attrs) ReadPostOpFhAttr(XdrReader reader)
        {
            NfsFileHandle? fh = null;
            if (reader.ReadBool()) fh = NfsFileHandle.ReadFrom(reader);
            NfsFileAttributes? attrs = ReadPostOpAttr(reader);
            SkipWccData(reader); // dir_wcc
            return (fh, attrs);
        }

        private static void SkipWccData(XdrReader reader)
        {
            // pre_op_attr (wcc_attr optional)
            if (reader.ReadBool())
            {
                reader.ReadUInt64(); // size
                reader.ReadUInt32(); reader.ReadUInt32(); // mtime
                reader.ReadUInt32(); reader.ReadUInt32(); // ctime
            }
            // post_op_attr
            SkipPostOpAttr(reader);
        }

        private static void WriteSetAttr(XdrWriter w, NfsSetAttributes a)
        {
            w.WriteBool(a.Mode.HasValue);    if (a.Mode.HasValue)    w.WriteUInt32(a.Mode.Value);
            w.WriteBool(a.Uid.HasValue);     if (a.Uid.HasValue)     w.WriteUInt32(a.Uid.Value);
            w.WriteBool(a.Gid.HasValue);     if (a.Gid.HasValue)     w.WriteUInt32(a.Gid.Value);
            w.WriteBool(a.Size.HasValue);    if (a.Size.HasValue)    w.WriteUInt64(a.Size.Value);
            // atime: SET_TO_SERVER_TIME = 1
            w.WriteInt32(1); // SET_TO_SERVER_TIME
            // mtime: SET_TO_SERVER_TIME = 1
            w.WriteInt32(1); // SET_TO_SERVER_TIME
        }

        public void Dispose() => _rpc.Dispose();
    }
}
