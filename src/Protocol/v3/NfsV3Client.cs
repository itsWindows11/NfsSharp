using NfsSharp.Auth;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol.v3;

/// <summary>
/// NFSv3 protocol client (RFC 1813). Implements all 22 procedures.
/// </summary>
internal sealed class NfsV3Client : INfsProtocolClient
{
    private readonly RpcClient       _rpc;
    private readonly AuthCredentials _credentials;

    private const uint Prog    = RpcConstants.NfsProgram;
    private const uint Version = 3;
    private const int  MaxRW   = 1 * 1024 * 1024;

    internal NfsV3Client(string host, int port, AuthCredentials credentials,
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
        var r = await Call(RpcConstants.Nfs3ProcRead, w =>
        {
            handle.WriteTo(w);
            w.WriteUInt64((ulong)offset);
            w.WriteUInt32((uint)count);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        SkipPostOpAttr(r);
        uint bytesRead = r.ReadUInt32();
        bool eof       = r.ReadBool();
        byte[] data    = r.ReadVarOpaque(MaxRW);
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
        var r = await Call(RpcConstants.Nfs3ProcWrite, w =>
        {
            handle.WriteTo(w);
            w.WriteUInt64((ulong)offset);
            w.WriteUInt32((uint)count);
            w.WriteInt32(0); // UNSTABLE
            w.WriteVarOpaque(slice);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        SkipWccData(r);
        uint written = r.ReadUInt32();
        r.ReadInt32();        // committed
        r.ReadFixedOpaque(8); // write verifier
        return (int)written;
    }

    public async Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcCommit, w =>
        {
            handle.WriteTo(w);
            w.WriteUInt64((ulong)offset);
            w.WriteUInt32((uint)count);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        SkipWccData(r);
        r.ReadFixedOpaque(8); // write verifier
    }

    // ── INfsProtocolClient: Attributes ────────────────────────────────────

    public async Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcGetAttr, w => handle.WriteTo(w), ct).ConfigureAwait(false);
        CheckStatus(r);
        return NfsFileAttributes.ReadV3(r);
    }

    public async Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcSetAttr, w =>
        {
            handle.WriteTo(w);
            WriteSetAttr(w, attrs);
            w.WriteBool(false); // no sattrguard3
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        SkipWccData(r);
    }

    // ── INfsProtocolClient: Namespace ─────────────────────────────────────

    public async Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
        NfsFileHandle dir, string name, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcLookup, w =>
        {
            dir.WriteTo(w);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        var fh    = NfsFileHandle.ReadFrom(r);
        var attrs = ReadPostOpAttr(r)!;
        SkipPostOpAttr(r); // dir attrs
        return (fh, attrs!);
    }

    public async Task<NfsFileHandle> CreateFileAsync(
        NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcCreate, w =>
        {
            dir.WriteTo(w);
            w.WriteString(name);
            w.WriteInt32(0); // UNCHECKED
            WriteSetAttr(w, attrs);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        var (fh, _) = ReadPostOpFhAttr(r);
        return fh ?? throw new NfsException(NfsStatus.ServerFault, "CREATE returned no file handle.");
    }

    public async Task<NfsFileHandle> MkDirAsync(
        NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcMkDir, w =>
        {
            dir.WriteTo(w);
            w.WriteString(name);
            WriteSetAttr(w, attrs);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
        var (fh, _) = ReadPostOpFhAttr(r);
        return fh ?? throw new NfsException(NfsStatus.ServerFault, "MKDIR returned no file handle.");
    }

    public async Task SymLinkAsync(
        NfsFileHandle dir, string name, string linkTarget, NfsSetAttributes attrs, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcSymLink, w =>
        {
            dir.WriteTo(w);
            w.WriteString(name);
            WriteSetAttr(w, attrs);
            w.WriteString(linkTarget);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
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
        // NFSv3 READDIRPLUS pages through entries using a uint64 cookie + cookieverifier.
        // Each READDIRPLUS response includes per-entry attributes and file handles,
        // so recursive traversal requires zero additional RPCs.
        // Pages are fetched lazily — only when the consumer requests more entries.
        ulong  cookie   = 0;
        byte[] verifier = new byte[8];

        while (true)
        {
            var r = await Call(RpcConstants.Nfs3ProcReadDirPlus, w =>
            {
                dir.WriteTo(w);
                w.WriteUInt64(cookie);
                w.WriteFixedOpaque(verifier, 8);
                w.WriteUInt32(4096);   // dircount  — byte budget for names only
                w.WriteUInt32(65536);  // maxcount  — byte budget for full reply
            }, ct).ConfigureAwait(false);
            CheckStatus(r);
            SkipPostOpAttr(r);
            verifier = r.ReadFixedOpaque(8);

            bool anyInPage = false;
            while (r.ReadBool()) // value_follows
            {
                ulong              fileid = r.ReadUInt64();
                string             name   = r.ReadString(255);
                ulong              ck     = r.ReadUInt64();
                NfsFileAttributes? attrs  = ReadPostOpAttr(r);
                NfsFileHandle?     fh     = r.ReadBool() ? NfsFileHandle.ReadFrom(r) : null;

                yield return new NfsDirectoryEntry
                {
                    FileId = fileid, Name = name, Cookie = ck,
                    Attributes = attrs, FileHandle = fh,
                };
                cookie    = ck;
                anyInPage = true;
            }

            bool eof = r.ReadBool();
            if (eof || !anyInPage) yield break;
        }
    }

    public async Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcReadLink, w => handle.WriteTo(w), ct).ConfigureAwait(false);
        CheckStatus(r);
        SkipPostOpAttr(r);
        return r.ReadString(4096);
    }

    public async Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcRemove, w =>
        {
            dir.WriteTo(w);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
    }

    public async Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcRmDir, w =>
        {
            dir.WriteTo(w);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
    }

    public async Task RenameAsync(
        NfsFileHandle fromDir, string fromName,
        NfsFileHandle toDir,   string toName,
        CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcRename, w =>
        {
            fromDir.WriteTo(w);
            w.WriteString(fromName);
            toDir.WriteTo(w);
            w.WriteString(toName);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
    }

    public async Task LinkAsync(
        NfsFileHandle file, NfsFileHandle linkDir, string linkName, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcLink, w =>
        {
            file.WriteTo(w);
            linkDir.WriteTo(w);
            w.WriteString(linkName);
        }, ct).ConfigureAwait(false);
        CheckStatus(r);
    }

    public async Task<NfsFsStat> FsStatAsync(NfsFileHandle handle, CancellationToken ct)
    {
        var r = await Call(RpcConstants.Nfs3ProcFsStat, w => handle.WriteTo(w), ct).ConfigureAwait(false);
        CheckStatus(r);
        SkipPostOpAttr(r);
        return new NfsFsStat
        {
            TotalBytes = r.ReadUInt64(), FreeBytes  = r.ReadUInt64(), AvailBytes  = r.ReadUInt64(),
            TotalFiles = r.ReadUInt64(), FreeFiles  = r.ReadUInt64(), AvailFiles  = r.ReadUInt64(),
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private Task<XdrReader> Call(uint proc, Action<XdrWriter> args, CancellationToken ct)
        => _rpc.CallAsync(Prog, Version, proc, _credentials, args, ct);

    private static void CheckStatus(XdrReader r)
    {
        int s = r.ReadInt32();
        if (s != 0) throw new NfsException((NfsStatus)s);
    }

    private static void SkipPostOpAttr(XdrReader r)
    { if (r.ReadBool()) NfsFileAttributes.ReadV3(r); }

    private static NfsFileAttributes? ReadPostOpAttr(XdrReader r)
        => r.ReadBool() ? NfsFileAttributes.ReadV3(r) : null;

    private static (NfsFileHandle? fh, NfsFileAttributes? attrs) ReadPostOpFhAttr(XdrReader r)
    {
        NfsFileHandle? fh = r.ReadBool() ? NfsFileHandle.ReadFrom(r) : null;
        NfsFileAttributes? attrs = ReadPostOpAttr(r);
        SkipWccData(r); // dir_wcc
        return (fh, attrs);
    }

    private static void SkipWccData(XdrReader r)
    {
        if (r.ReadBool()) // pre_op_attr
        { r.ReadUInt64(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); }
        SkipPostOpAttr(r);
    }

    private static void WriteSetAttr(XdrWriter w, NfsSetAttributes a)
    {
        w.WriteBool(a.Mode.HasValue); if (a.Mode.HasValue) w.WriteUInt32(a.Mode.Value);
        w.WriteBool(a.Uid.HasValue);  if (a.Uid.HasValue)  w.WriteUInt32(a.Uid.Value);
        w.WriteBool(a.Gid.HasValue);  if (a.Gid.HasValue)  w.WriteUInt32(a.Gid.Value);
        w.WriteBool(a.Size.HasValue); if (a.Size.HasValue) w.WriteUInt64(a.Size.Value);
        w.WriteInt32(1); // SET_TO_SERVER_TIME for atime
        w.WriteInt32(1); // SET_TO_SERVER_TIME for mtime
    }

    public void Dispose() => _rpc.Dispose();
}
