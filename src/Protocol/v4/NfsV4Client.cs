using NfsSharp.Auth;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol.v4;

/// <summary>
/// NFSv4 protocol client (RFC 7530).
/// Uses COMPOUND operations; operates with anonymous (stateless) stateids.
/// </summary>
internal sealed class NfsV4Client : INfsProtocolClient
{
    private readonly RpcClient       _rpc;
    private readonly AuthCredentials _credentials;

    private const uint Prog    = RpcConstants.NfsProgram;
    private const uint Version = 4;
    private const int  MaxRW   = 1 * 1024 * 1024;

    // NFSv4 op codes (RFC 7530 §14)
    private const int Op_Commit   = 5;
    private const int Op_Create   = 6;
    private const int Op_GetAttr  = 9;
    private const int Op_GetFh    = 10;
    private const int Op_Link     = 11;
    private const int Op_Lookup   = 15;
    private const int Op_PutFh    = 22;
    private const int Op_Read     = 25;
    private const int Op_ReadDir  = 26;
    private const int Op_ReadLink = 27;
    private const int Op_Remove   = 28;
    private const int Op_Rename   = 29;
    private const int Op_SaveFh   = 32;
    private const int Op_SetAttr  = 34;
    private const int Op_Write    = 38;

    // NFSv4 attribute bitmap bits
    private const uint AttrBit0_Type = 0x00000001;
    private const uint AttrBit0_Size = 0x00000800;
    private const uint AttrBit1_Mode          = 0x00000002; // attribute 33
    private const uint AttrBit1_TimeAccessSet = 0x00010000; // attribute 48
    private const uint AttrBit1_TimeModifySet = 0x00400000; // attribute 54

    // NFSv4 write stability
    private const int Unstable4 = 0;

    internal NfsV4Client(string host, int port, AuthCredentials credentials,
        TimeSpan connectTimeout, TimeSpan readTimeout)
    {
        _rpc         = new RpcClient(host, port, connectTimeout, readTimeout);
        _credentials = credentials;
    }

    // ── INfsProtocolClient: Connection ────────────────────────────────────

    public Task ConnectAsync(CancellationToken ct = default) => _rpc.ConnectAsync(ct);

    // ── INfsProtocolClient: File I/O ──────────────────────────────────────

    public async Task<NfsReadResult> ReadAsync(
        NfsFileHandle handle, long offset, int count, CancellationToken ct)
    {
        if (count > MaxRW) count = MaxRW;
        // COMPOUND (2): PUTFH + READ
        var r = await Compound("read", 2, w =>
        {
            PutFh(w, handle);
            w.WriteInt32(Op_Read);
            AnonStateId(w);
            w.WriteUInt64((ulong)offset);
            w.WriteUInt32((uint)count);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Read);
        bool   eof  = r.ReadBool();
        byte[] data = r.ReadVarOpaque(MaxRW);
        return new NfsReadResult(data, eof);
    }

    public async Task<int> WriteAsync(
        NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
    {
        if (count > MaxRW) count = MaxRW;
        var slice = new byte[count];
        Array.Copy(data, dataOffset, slice, 0, count);
        // COMPOUND (2): PUTFH + WRITE
        var r = await Compound("write", 2, w =>
        {
            PutFh(w, handle);
            w.WriteInt32(Op_Write);
            AnonStateId(w);
            w.WriteUInt64((ulong)offset);
            w.WriteInt32(Unstable4);
            w.WriteVarOpaque(slice);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Write);
        uint written = r.ReadUInt32();
        r.ReadInt32();        // committed
        r.ReadFixedOpaque(8); // writeverf4
        return (int)written;
    }

    public async Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
    {
        // COMPOUND (2): PUTFH + COMMIT
        var r = await Compound("commit", 2, w =>
        {
            PutFh(w, handle);
            w.WriteInt32(Op_Commit);
            w.WriteUInt64((ulong)offset);
            w.WriteUInt32((uint)count);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Commit);
        r.ReadFixedOpaque(8); // writeverf4
    }

    // ── INfsProtocolClient: Attributes ────────────────────────────────────

    public async Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
    {
        // COMPOUND (2): PUTFH + GETATTR
        var r = await Compound("getattr", 2, w =>
        {
            PutFh(w, handle);
            w.WriteInt32(Op_GetAttr);
            w.WriteUInt32(2);
            w.WriteUInt32(AttrBit0_Type | AttrBit0_Size);
            w.WriteUInt32(AttrBit1_Mode);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_GetAttr);
        return ParseGetAttr(r);
    }

    public async Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct)
    {
        // Build attrlist4 for the attributes we want to set.
        byte[] attrBlob = BuildSetAttrBlob(attrs);

        // COMPOUND (2): PUTFH + SETATTR
        var r = await Compound("setattr", 2, w =>
        {
            PutFh(w, handle);
            w.WriteInt32(Op_SetAttr);
            AnonStateId(w);
            w.WriteVarOpaque(attrBlob);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_SetAttr);
        r.ReadUInt32(); r.ReadUInt32(); // attrsset bitmap (2 words) — discard
    }

    // ── INfsProtocolClient: Namespace ─────────────────────────────────────

    public async Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
        NfsFileHandle dir, string name, CancellationToken ct)
    {
        // COMPOUND (4): PUTFH + LOOKUP + GETFH + GETATTR
        var r = await Compound("lookup", 4, w =>
        {
            PutFh(w, dir);
            w.WriteInt32(Op_Lookup);
            w.WriteString(name);
            w.WriteInt32(Op_GetFh);
            w.WriteInt32(Op_GetAttr);
            w.WriteUInt32(2);
            w.WriteUInt32(AttrBit0_Type | AttrBit0_Size);
            w.WriteUInt32(AttrBit1_Mode);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh);
        ChkOp(r, Op_Lookup);
        ChkOp(r, Op_GetFh);
        var fh = NfsFileHandle.ReadFrom(r, NfsFileHandle.MaxSizeV4);
        ChkOp(r, Op_GetAttr);
        var attrs = ParseGetAttr(r);
        return (fh, attrs);
    }

    public async Task<NfsFileHandle> CreateFileAsync(
        NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
    {
        // COMPOUND (3): PUTFH + CREATE(NF4REG) + GETFH
        byte[] attrBlob = BuildSetAttrBlob(attrs);
        var r = await Compound("createfile", 3, w =>
        {
            PutFh(w, dir);
            w.WriteInt32(Op_Create);
            w.WriteInt32(1); // NF4REG = regular file
            w.WriteString(name);
            w.WriteVarOpaque(attrBlob);
            w.WriteInt32(Op_GetFh);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Create);
        SkipChangeInfo(r);
        r.ReadUInt32(); r.ReadUInt32(); // attrset bitmap
        ChkOp(r, Op_GetFh);
        return NfsFileHandle.ReadFrom(r, NfsFileHandle.MaxSizeV4);
    }

    public async Task<NfsFileHandle> MkDirAsync(
        NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
    {
        // COMPOUND (3): PUTFH + CREATE(NF4DIR) + GETFH
        byte[] attrBlob = BuildSetAttrBlob(attrs);
        var r = await Compound("mkdir", 3, w =>
        {
            PutFh(w, dir);
            w.WriteInt32(Op_Create);
            w.WriteInt32(2); // NF4DIR = directory
            w.WriteString(name);
            w.WriteVarOpaque(attrBlob);
            w.WriteInt32(Op_GetFh);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Create);
        SkipChangeInfo(r);
        r.ReadUInt32(); r.ReadUInt32();
        ChkOp(r, Op_GetFh);
        return NfsFileHandle.ReadFrom(r, NfsFileHandle.MaxSizeV4);
    }

    public async Task SymLinkAsync(
        NfsFileHandle dir, string name, string linkTarget, NfsSetAttributes attrs, CancellationToken ct)
    {
        byte[] attrBlob = BuildSetAttrBlob(attrs);
        // COMPOUND (2): PUTFH + CREATE(NF4LNK)
        var r = await Compound("symlink", 2, w =>
        {
            PutFh(w, dir);
            w.WriteInt32(Op_Create);
            w.WriteInt32(5); // NF4LNK = symlink
            w.WriteString(linkTarget); // linkdata
            w.WriteString(name);
            w.WriteVarOpaque(attrBlob);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Create);
    }

    public async Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(
        NfsFileHandle dir, CancellationToken ct)
    {
        var results = new List<NfsDirectoryEntry>();
        await foreach (var entry in EnumerateDirAsync(dir, ct).ConfigureAwait(false))
            results.Add(entry);
        return results;
    }

    /// <inheritdoc cref="INfsProtocolClient.EnumerateDirAsync"/>
    public async IAsyncEnumerable<NfsDirectoryEntry> EnumerateDirAsync(
        NfsFileHandle dir,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // NFSv4 COMPOUND: PUTFH + READDIR.
        // We request the 'type' attribute (word0 bit 0) so that the recursive
        // enumerator can identify subdirectories without extra GETATTR RPCs.
        // Pages are fetched lazily — only when the consumer requests more entries.
        ulong  cookie   = 0;
        byte[] verifier = new byte[8];

        while (true)
        {
            var r = await Compound("readdir", 2, w =>
            {
                PutFh(w, dir);
                w.WriteInt32(Op_ReadDir);
                w.WriteUInt64(cookie);
                w.WriteFixedOpaque(verifier, 8);
                w.WriteUInt32(4096);          // dircount
                w.WriteUInt32(65536);         // maxcount
                w.WriteUInt32(2);             // bitmap4 length = 2 words
                w.WriteUInt32(AttrBit0_Type); // word0: request 'type'
                w.WriteUInt32(0);             // word1: nothing
            }, ct).ConfigureAwait(false);
            ChkOp(r, Op_PutFh);
            ChkOp(r, Op_ReadDir);
            verifier = r.ReadFixedOpaque(8); // cookieverf4

            bool anyInPage = false;
            while (r.ReadBool()) // entry4 value_follows
            {
                // RFC 7530 §14.2.22: entry4 = { cookie, name, fattr4 }
                ulong  ck   = r.ReadUInt64();    // nfs_cookie4
                string name = r.ReadString(255); // component4

                // fattr4 = { bitmap4 attrmask, opaque attrvals }
                NfsFileType? type = ParseV4EntryType(r);

                yield return new NfsDirectoryEntry
                {
                    Name       = name,
                    Cookie     = ck,
                    Attributes = type.HasValue
                        ? new NfsFileAttributes { Type = type.Value }
                        : null,
                };
                cookie    = ck;
                anyInPage = true;
            }

            bool eof = r.ReadBool();
            if (eof || !anyInPage) yield break;
        }
    }

    /// <summary>
    /// Parses an <c>fattr4</c> from the current position and returns the <c>type</c>
    /// attribute value if it was included in the attrmask, or <see langword="null"/> otherwise.
    /// Consumes the full <c>fattr4</c> regardless of which attributes are present.
    /// </summary>
    private static NfsFileType? ParseV4EntryType(XdrReader r)
    {
        // bitmap4: count (uint32) + count × uint32 words
        uint maskCount = r.ReadUInt32();
        uint word0     = maskCount > 0 ? r.ReadUInt32() : 0;
        for (uint i = 1; i < maskCount; i++) r.ReadUInt32(); // discard remaining words

        // attrlist4: opaque<> (length-prefixed)
        byte[] attrvals = r.ReadVarOpaque();

        if ((word0 & AttrBit0_Type) != 0 && attrvals.Length >= 4)
        {
            var inner = new XdrReader(attrvals);
            return (NfsFileType)inner.ReadInt32();
        }
        return null;
    }

    public async Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct)
    {
        // COMPOUND (2): PUTFH + READLINK
        var r = await Compound("readlink", 2, w =>
        {
            PutFh(w, handle);
            w.WriteInt32(Op_ReadLink);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_ReadLink);
        return r.ReadString(4096);
    }

    public async Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        // COMPOUND (2): PUTFH + REMOVE
        var r = await Compound("remove", 2, w =>
        {
            PutFh(w, dir);
            w.WriteInt32(Op_Remove);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_Remove);
        SkipChangeInfo(r);
    }

    // NFSv4 uses REMOVE for both files and directories — no async/await needed.
    public Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct)
        => RemoveAsync(dir, name, ct);

    public async Task RenameAsync(
        NfsFileHandle fromDir, string fromName,
        NfsFileHandle toDir,   string toName,
        CancellationToken ct)
    {
        // COMPOUND (4): PUTFH(from) + SAVEFH + PUTFH(to) + RENAME
        var r = await Compound("rename", 4, w =>
        {
            PutFh(w, fromDir);
            w.WriteInt32(Op_SaveFh);
            PutFh(w, toDir);
            w.WriteInt32(Op_Rename);
            w.WriteString(fromName);
            w.WriteString(toName);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_SaveFh); ChkOp(r, Op_PutFh); ChkOp(r, Op_Rename);
    }

    public async Task LinkAsync(
        NfsFileHandle file, NfsFileHandle linkDir, string linkName, CancellationToken ct)
    {
        // COMPOUND (4): PUTFH(file) + SAVEFH + PUTFH(linkDir) + LINK
        var r = await Compound("link", 4, w =>
        {
            PutFh(w, file);
            w.WriteInt32(Op_SaveFh);
            PutFh(w, linkDir);
            w.WriteInt32(Op_Link);
            w.WriteString(linkName);
        }, ct).ConfigureAwait(false);
        ChkOp(r, Op_PutFh); ChkOp(r, Op_SaveFh); ChkOp(r, Op_PutFh); ChkOp(r, Op_Link);
    }

    public async Task<NfsFsStat> FsStatAsync(NfsFileHandle handle, CancellationToken ct)
    {
        // Approximate via GETATTR: space_avail, space_free, space_total (bits 35,36,37 = word1 bits 3,4,5)
        // For simplicity request size (word0 bit11) and return zeros for files.
        var attrs = await GetAttrAsync(handle, ct).ConfigureAwait(false);
        return new NfsFsStat { TotalBytes = attrs.Size };
    }

    // ── COMPOUND dispatcher ───────────────────────────────────────────────

    private async Task<XdrReader> Compound(
        string tag, int opCount, Action<XdrWriter> writeOps, CancellationToken ct)
    {
        var reader = await _rpc.CallAsync(Prog, Version, 0 /* NFSPROC4_COMPOUND */,
            _credentials, w =>
            {
                w.WriteString(tag);
                w.WriteUInt32(0);            // minorversion (NFSv4.0)
                w.WriteUInt32((uint)opCount);
                writeOps(w);
            }, ct).ConfigureAwait(false);

        int status = reader.ReadInt32();
        reader.ReadString(256); // tag
        reader.ReadUInt32();    // resultcount

        if (status != 0)
            throw new NfsException(MapV4Status(status),
                $"NFSv4 COMPOUND failed (nfsstat4={status})");
        return reader;
    }

    // ── XDR write helpers ─────────────────────────────────────────────────

    private static void PutFh(XdrWriter w, NfsFileHandle fh)
    {
        w.WriteInt32(Op_PutFh);
        fh.WriteTo(w);
    }

    /// <summary>Anonymous (all-zeros) stateid4: seqid=0 + 12-byte other.</summary>
    private static void AnonStateId(XdrWriter w)
    {
        w.WriteUInt32(0);
        w.WriteFixedOpaque(new byte[12], 12);
    }

    private static byte[] BuildSetAttrBlob(NfsSetAttributes a)
    {
        using var ms = new MemoryStream();
        var w = new XdrWriter(ms);
        // bitmap4: 2 words encoding which attrs we set
        uint word0 = 0, word1 = 0;
        if (a.Size.HasValue)        word0 |= AttrBit0_Size;
        if (a.Mode.HasValue)        word1 |= AttrBit1_Mode;
        if (a.AccessTime.HasValue)  word1 |= AttrBit1_TimeAccessSet;
        if (a.ModifyTime.HasValue)  word1 |= AttrBit1_TimeModifySet;
        w.WriteUInt32(2); w.WriteUInt32(word0); w.WriteUInt32(word1);
        // Attribute values must be written in ascending attribute-number order.
        if (a.Size.HasValue)       w.WriteUInt64(a.Size.Value);
        if (a.Mode.HasValue)       w.WriteUInt32(a.Mode.Value);
        if (a.AccessTime.HasValue) WriteV4SetTime(w, a.AccessTime.Value);
        if (a.ModifyTime.HasValue) WriteV4SetTime(w, a.ModifyTime.Value);
        return ms.ToArray();
    }

    // NFSv4 settime4: SET_TO_SERVER_TIME=1, SET_TO_CLIENT_TIME=2 + nfstime4 (int64 secs, uint32 nsecs)
    private static void WriteV4SetTime(XdrWriter w, DateTimeOffset time)
    {
        w.WriteInt32(2); // SET_TO_CLIENT_TIME
        w.WriteInt64(time.ToUnixTimeSeconds());
        w.WriteUInt32((uint)((time.Ticks % TimeSpan.TicksPerSecond) * 100L)); // 100-ns ticks → ns
    }

    // ── XDR read helpers ──────────────────────────────────────────────────

    private static void ChkOp(XdrReader r, int expectedOp)
    {
        int resop  = r.ReadInt32();
        int status = r.ReadInt32();
        if (status != 0)
            throw new NfsException(MapV4Status(status),
                $"NFSv4 op {resop} (expected {expectedOp}) failed (nfsstat4={status})");
    }

    private static NfsFileAttributes ParseGetAttr(XdrReader r)
    {
        byte[] blob  = r.ReadVarOpaque();
        var inner    = new XdrReader(blob);
        uint w0 = inner.ReadUInt32();
        uint w1 = inner.ReadUInt32();
        NfsFileType type = NfsFileType.Regular;
        ulong size = 0; uint mode = 0;
        if ((w0 & AttrBit0_Type) != 0) type = (NfsFileType)inner.ReadInt32();
        if ((w0 & AttrBit0_Size) != 0) size = inner.ReadUInt64();
        if ((w1 & AttrBit1_Mode) != 0) mode = inner.ReadUInt32();
        return new NfsFileAttributes { Type = type, Size = size, Mode = mode };
    }

    private static void SkipChangeInfo(XdrReader r)
    {
        r.ReadBool(); r.ReadUInt64(); r.ReadUInt64(); // atomic + before + after
    }

    private static NfsStatus MapV4Status(int s) => s switch
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
        _     => NfsStatus.ServerFault,
    };

    public void Dispose() => _rpc.Dispose();
}
