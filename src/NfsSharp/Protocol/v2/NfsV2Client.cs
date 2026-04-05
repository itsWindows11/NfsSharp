using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Auth;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol.v2
{
    /// <summary>
    /// NFSv2 protocol client (RFC 1094).
    /// NFSv2 is stateless and uses fixed 32-byte file handles.
    /// All file sizes are limited to 2 GiB.
    /// </summary>
    internal sealed class NfsV2Client : INfsFileOperations, IDisposable
    {
        private readonly RpcClient       _rpc;
        private readonly AuthCredentials _credentials;

        private const uint NfsV2Program = RpcConstants.NfsProgram;
        private const uint NfsV2Version = 2;
        private const int  FhSize       = 32;
        private const int  MaxReadWrite = 8192; // NFSv2 RSIZE/WSIZE limit

        internal NfsV2Client(string host, int port, AuthCredentials credentials,
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
            if (count > MaxReadWrite) count = MaxReadWrite;

            var reader = await CallAsync(6 /* NFSPROC_READ */, w =>
            {
                WriteFixedHandle(w, handle);
                w.WriteUInt32(0);              // totalcount (ignored in v2)
                w.WriteUInt32((uint)offset);
                w.WriteUInt32((uint)count);
                w.WriteUInt32(0);              // totalcount again
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            // fattr
            reader.ReadFixedOpaque(68); // 17 × uint32
            byte[] data = reader.ReadVarOpaque(MaxReadWrite);
            return new NfsReadResult(data, data.Length == 0);
        }

        public async Task<int> WriteAsync(
            NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
        {
            if (count > MaxReadWrite) count = MaxReadWrite;
            var slice = new byte[count];
            Array.Copy(data, dataOffset, slice, 0, count);

            var reader = await CallAsync(8 /* NFSPROC_WRITE */, w =>
            {
                WriteFixedHandle(w, handle);
                w.WriteUInt32(0);              // beginoffset (ignored)
                w.WriteUInt32((uint)offset);
                w.WriteUInt32((uint)count);    // totalcount (ignored)
                w.WriteVarOpaque(slice);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            return count;
        }

        public async Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
        {
            var reader = await CallAsync(1 /* NFSPROC_GETATTR */, w =>
                WriteFixedHandle(w, handle), ct).ConfigureAwait(false);

            CheckStatus(reader);
            return ReadFattr2(reader);
        }

        public Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
        {
            // NFSv2 has no COMMIT; writes are always synchronous.
            return Task.CompletedTask;
        }

        // ── Lookup ────────────────────────────────────────────────────────────

        internal async Task<(NfsFileHandle handle, NfsFileAttributes attrs)> LookupAsync(
            NfsFileHandle dir, string name, CancellationToken ct = default)
        {
            var reader = await CallAsync(4 /* NFSPROC_LOOKUP */, w =>
            {
                WriteFixedHandle(w, dir);
                w.WriteString(name);
            }, ct).ConfigureAwait(false);

            CheckStatus(reader);
            var handle = new NfsFileHandle(reader.ReadFixedOpaque(FhSize));
            var attrs  = ReadFattr2(reader);
            return (handle, attrs);
        }

        // ── ReadDir ──────────────────────────────────────────────────────────

        internal async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
            NfsFileHandle dir, CancellationToken ct = default)
        {
            var results = new List<NfsDirectoryEntry>();
            uint cookie = 0;

            while (true)
            {
                var reader = await CallAsync(16 /* NFSPROC_READDIR */, w =>
                {
                    WriteFixedHandle(w, dir);
                    w.WriteUInt32(cookie);
                    w.WriteUInt32(8192);
                }, ct).ConfigureAwait(false);

                CheckStatus(reader);

                bool any = false;
                while (reader.ReadBool()) // value_follows
                {
                    uint   fileid = reader.ReadUInt32();
                    string name   = reader.ReadString(255);
                    uint   ck     = reader.ReadUInt32();
                    results.Add(new NfsDirectoryEntry { FileId = fileid, Name = name, Cookie = ck });
                    cookie = ck;
                    any = true;
                }

                bool eof = reader.ReadBool();
                if (eof || !any) break;
            }

            return results;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private Task<XdrReader> CallAsync(uint procedure, Action<XdrWriter> args, CancellationToken ct)
            => _rpc.CallAsync(NfsV2Program, NfsV2Version, procedure, _credentials, args, ct);

        private static void WriteFixedHandle(XdrWriter w, NfsFileHandle handle)
        {
            var padded = new byte[FhSize];
            Array.Copy(handle.Data, padded, Math.Min(handle.Data.Length, FhSize));
            w.WriteFixedOpaque(padded, FhSize);
        }

        private static void CheckStatus(XdrReader reader)
        {
            int status = reader.ReadInt32();
            if (status != 0)
                throw new NfsException((NfsStatus)status);
        }

        private static NfsFileAttributes ReadFattr2(XdrReader r)
        {
            var type    = (NfsFileType)r.ReadUInt32();
            uint mode   = r.ReadUInt32();
            uint nlink  = r.ReadUInt32();
            uint uid    = r.ReadUInt32();
            uint gid    = r.ReadUInt32();
            uint size   = r.ReadUInt32();
            r.ReadUInt32(); // blocksize
            uint rdev   = r.ReadUInt32();
            r.ReadUInt32(); // blocks
            r.ReadUInt32(); // fsid
            uint fileid = r.ReadUInt32();
            var atime   = ReadNfsTime2(r);
            var mtime   = ReadNfsTime2(r);
            var ctime   = ReadNfsTime2(r);

            return new NfsFileAttributes
            {
                Type       = type,
                Mode       = mode,
                NLink      = nlink,
                Uid        = uid,
                Gid        = gid,
                Size       = size,
                Used       = size,
                Rdev       = rdev,
                FileId     = fileid,
                AccessTime = atime,
                ModifyTime = mtime,
                ChangeTime = ctime,
            };
        }

        private static DateTimeOffset ReadNfsTime2(XdrReader r)
        {
            uint sec  = r.ReadUInt32();
            uint usec = r.ReadUInt32();
            return DateTimeOffset.FromUnixTimeSeconds(sec)
                   + TimeSpan.FromTicks(usec * 10L); // µs → 100-ns ticks
        }

        public void Dispose() => _rpc.Dispose();
    }
}
