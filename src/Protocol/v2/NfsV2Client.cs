using NfsSharp.Auth;
using NfsSharp.Rpc;
using NfsSharp.Xdr;

namespace NfsSharp.Protocol.v2;

/// <summary>
/// NFSv2 protocol client (RFC 1094).
/// NFSv2 is stateless; file handles are fixed 32 bytes; max file size 2 GiB.
/// </summary>
internal sealed class NfsV2Client : INfsProtocolClient
{
    private readonly RpcClient       _rpc;
    private readonly AuthCredentials _credentials;

    private const uint NfsV2Program = RpcConstants.NfsProgram;
    private const uint NfsV2Version = 2;
    private const int  FhSize       = 32;
    private const int  MaxReadWrite = 8192;

    // NFSv2 procedure numbers (RFC 1094 §2.2)
    private const uint ProcNull    = 0;
    private const uint ProcGetAttr = 1;
    private const uint ProcSetAttr = 2;
    private const uint ProcLookup  = 4;
    private const uint ProcReadLink= 5;
    private const uint ProcRead    = 6;
    private const uint ProcWrite   = 8;
    private const uint ProcCreate  = 9;
    private const uint ProcRemove  = 12;
    private const uint ProcRename  = 16;
    private const uint ProcLink    = 17;
    private const uint ProcSymLink = 10;
    private const uint ProcMkDir   = 14;
    private const uint ProcRmDir   = 15;
    private const uint ProcReadDir = 16;
    private const uint ProcStatFs  = 17;

    internal NfsV2Client(string host, int port, AuthCredentials credentials,
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
        if (count > MaxReadWrite) count = MaxReadWrite;
        var reader = await CallAsync(ProcRead, w =>
        {
            WriteHandle(w, handle);
            w.WriteUInt32(0);
            w.WriteUInt32((uint)offset);
            w.WriteUInt32((uint)count);
            w.WriteUInt32(0);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
        reader.ReadFixedOpaque(68); // fattr (17 × uint32)
        byte[] data = reader.ReadVarOpaque(MaxReadWrite);
        return new NfsReadResult(data, data.Length == 0);
    }

    public async Task<int> WriteAsync(
        NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
    {
        if (count > MaxReadWrite) count = MaxReadWrite;
        var slice = new byte[count];
        Array.Copy(data, dataOffset, slice, 0, count);
        var reader = await CallAsync(ProcWrite, w =>
        {
            WriteHandle(w, handle);
            w.WriteUInt32(0);
            w.WriteUInt32((uint)offset);
            w.WriteUInt32((uint)count);
            w.WriteVarOpaque(slice);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
        return count;
    }

    public Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
        => Task.CompletedTask; // NFSv2 writes are always synchronous

    // ── INfsProtocolClient: Attributes ────────────────────────────────────

    public async Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
    {
        var reader = await CallAsync(ProcGetAttr, w => WriteHandle(w, handle), ct).ConfigureAwait(false);
        CheckStatus(reader);
        return ReadFattr2(reader);
    }

    public async Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct)
    {
        if (attrs.Size.HasValue)
            throw new NotSupportedException(
                "NFSv2 SETATTR does not support size-based truncation reliably. " +
                "Use NFSv3 or later for SetLength support.");

        var reader = await CallAsync(ProcSetAttr, w =>
        {
            WriteHandle(w, handle);
            WriteSattr2(w, attrs);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
    }

    // ── INfsProtocolClient: Namespace ─────────────────────────────────────

    public async Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(
        NfsFileHandle dir, string name, CancellationToken ct)
    {
        var reader = await CallAsync(ProcLookup, w =>
        {
            WriteHandle(w, dir);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
        var fh    = new NfsFileHandle(reader.ReadFixedOpaque(FhSize));
        var attrs = ReadFattr2(reader);
        return (fh, attrs);
    }

    public async Task<NfsFileHandle> CreateFileAsync(
        NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
    {
        var reader = await CallAsync(ProcCreate, w =>
        {
            WriteHandle(w, dir);
            w.WriteString(name);
            WriteSattr2(w, attrs);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
        return new NfsFileHandle(reader.ReadFixedOpaque(FhSize));
    }

    public async Task<NfsFileHandle> MkDirAsync(
        NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
    {
        var reader = await CallAsync(ProcMkDir, w =>
        {
            WriteHandle(w, dir);
            w.WriteString(name);
            WriteSattr2(w, attrs);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
        return new NfsFileHandle(reader.ReadFixedOpaque(FhSize));
    }

    public async Task SymLinkAsync(
        NfsFileHandle dir, string name, string linkTarget, NfsSetAttributes attrs, CancellationToken ct)
    {
        var reader = await CallAsync(ProcSymLink, w =>
        {
            WriteHandle(w, dir);
            w.WriteString(name);
            w.WriteString(linkTarget);
            WriteSattr2(w, attrs);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
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
        // NFSv2 READDIR pages through entries using a uint32 cookie.
        // Each page is fetched only when the consumer advances past the last entry
        // of the current page, giving true lazy streaming semantics.
        uint cookie = 0;
        while (true)
        {
            var reader = await CallAsync(ProcReadDir, w =>
            {
                WriteHandle(w, dir);
                w.WriteUInt32(cookie);
                w.WriteUInt32(8192); // count hint
            }, ct).ConfigureAwait(false);
            CheckStatus(reader);

            bool anyInPage = false;
            while (reader.ReadBool()) // value_follows
            {
                uint   fileid = reader.ReadUInt32();
                string name   = reader.ReadString(255);
                uint   ck     = reader.ReadUInt32();
                yield return new NfsDirectoryEntry { FileId = fileid, Name = name, Cookie = ck };
                cookie     = ck;
                anyInPage  = true;
            }

            bool eof = reader.ReadBool();
            if (eof || !anyInPage) yield break;
        }
    }

    public async Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct)
    {
        var reader = await CallAsync(ProcReadLink, w => WriteHandle(w, handle), ct).ConfigureAwait(false);
        CheckStatus(reader);
        return reader.ReadString(4096);
    }

    public async Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        var reader = await CallAsync(ProcRemove, w =>
        {
            WriteHandle(w, dir);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
    }

    public async Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        var reader = await CallAsync(ProcRmDir, w =>
        {
            WriteHandle(w, dir);
            w.WriteString(name);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
    }

    public async Task RenameAsync(
        NfsFileHandle fromDir, string fromName,
        NfsFileHandle toDir,   string toName,
        CancellationToken ct)
    {
        var reader = await CallAsync(ProcRename, w =>
        {
            WriteHandle(w, fromDir);
            w.WriteString(fromName);
            WriteHandle(w, toDir);
            w.WriteString(toName);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
    }

    public async Task LinkAsync(
        NfsFileHandle file, NfsFileHandle linkDir, string linkName, CancellationToken ct)
    {
        var reader = await CallAsync(ProcLink, w =>
        {
            WriteHandle(w, file);
            WriteHandle(w, linkDir);
            w.WriteString(linkName);
        }, ct).ConfigureAwait(false);
        CheckStatus(reader);
    }

    public async Task<NfsFsStat> FsStatAsync(NfsFileHandle handle, CancellationToken ct)
    {
        var reader = await CallAsync(ProcStatFs, w => WriteHandle(w, handle), ct).ConfigureAwait(false);
        CheckStatus(reader);
        reader.ReadUInt32(); // tsize (transfer size)
        uint bsize  = reader.ReadUInt32();
        uint blocks = reader.ReadUInt32();
        uint bfree  = reader.ReadUInt32();
        uint bavail = reader.ReadUInt32();
        ulong total  = (ulong)blocks * bsize;
        ulong free   = (ulong)bfree  * bsize;
        ulong avail  = (ulong)bavail * bsize;
        return new NfsFsStat
        {
            TotalBytes = total, FreeBytes = free, AvailBytes = avail,
            TotalFiles = 0,     FreeFiles  = 0,   AvailFiles  = 0,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private Task<XdrReader> CallAsync(uint proc, Action<XdrWriter> args, CancellationToken ct)
        => _rpc.CallAsync(NfsV2Program, NfsV2Version, proc, _credentials, args, ct);

    private static void WriteHandle(XdrWriter w, NfsFileHandle handle)
    {
        var padded = new byte[FhSize];
        Array.Copy(handle.Data, padded, Math.Min(handle.Data.Length, FhSize));
        w.WriteFixedOpaque(padded, FhSize);
    }

    private static void CheckStatus(XdrReader reader)
    {
        int status = reader.ReadInt32();
        if (status != 0) throw new NfsException((NfsStatus)status);
    }

    private static void WriteSattr2(XdrWriter w, NfsSetAttributes a)
    {
        w.WriteUInt32(a.Mode ?? 0b110_100_100u); // 0644 rw-r--r--
        w.WriteUInt32(a.Uid  ?? 0xFFFFFFFF); // uid  (0xFFFF = no change)
        w.WriteUInt32(a.Gid  ?? 0xFFFFFFFF); // gid
        w.WriteUInt32(a.Size.HasValue ? (uint)a.Size.Value : 0xFFFFFFFF); // size
        w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0); // atime (no change)
        w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0); // mtime (no change)
    }

    private static NfsFileAttributes ReadFattr2(XdrReader r)
    {
        var  type   = (NfsFileType)r.ReadUInt32();
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
        var  atime  = ReadTime2(r);
        var  mtime  = ReadTime2(r);
        var  ctime  = ReadTime2(r);
        return new NfsFileAttributes
        {
            Type = type, Mode = mode, NLink = nlink,
            Uid = uid, Gid = gid, Size = size, Used = size,
            Rdev = rdev, FileId = fileid,
            AccessTime = atime, ModifyTime = mtime, ChangeTime = ctime,
        };
    }

    private static DateTimeOffset ReadTime2(XdrReader r)
    {
        uint sec = r.ReadUInt32(); uint usec = r.ReadUInt32();
        return DateTimeOffset.FromUnixTimeSeconds(sec) + TimeSpan.FromTicks(usec * 10L);
    }

    public void Dispose() => _rpc.Dispose();
}
