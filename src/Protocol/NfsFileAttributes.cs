using NfsSharp.Xdr;

namespace NfsSharp.Protocol;

/// <summary>
/// Post-operation file and directory attributes as returned by NFS GETATTR, LOOKUP,
/// READDIRPLUS, and other operations that include a pre- or post-op attributes field.
/// Corresponds to the NFSv3 <c>fattr3</c> structure (RFC 1813 §2.6).
/// </summary>
public sealed class NfsFileAttributes
{
    /// <summary>Type of the file-system object.</summary>
    public NfsFileType Type { get; init; }

    /// <summary>Protection mode bits (Unix-style).</summary>
    public uint Mode { get; init; }

    /// <summary>Number of hard links to this object.</summary>
    public uint NLink { get; init; }

    /// <summary>User ID of the owner.</summary>
    public uint Uid { get; init; }

    /// <summary>Group ID of the owner.</summary>
    public uint Gid { get; init; }

    /// <summary>File size in bytes.</summary>
    public ulong Size { get; init; }

    /// <summary>Actual bytes used on disk.</summary>
    public ulong Used { get; init; }

    /// <summary>Data used for special device files (rdev).</summary>
    public uint Rdev { get; init; }

    /// <summary>File system identifier (fsid).</summary>
    public ulong FsId { get; init; }

    /// <summary>File identifier (inode number).</summary>
    public ulong FileId { get; init; }

    /// <summary>Last access time.</summary>
    public DateTimeOffset AccessTime { get; init; }

    /// <summary>Last modification time (content).</summary>
    public DateTimeOffset ModifyTime { get; init; }

    /// <summary>Last metadata-change time.</summary>
    public DateTimeOffset ChangeTime { get; init; }

    // ── XDR ──────────────────────────────────────────────────────────────

    /// <summary>Reads NFSv3 fattr3 from <paramref name="reader"/>.</summary>
    public static NfsFileAttributes ReadV3(XdrReader reader)
    {
        var type    = (NfsFileType)reader.ReadInt32();
        uint mode   = reader.ReadUInt32();
        uint nlink  = reader.ReadUInt32();
        uint uid    = reader.ReadUInt32();
        uint gid    = reader.ReadUInt32();
        ulong size  = reader.ReadUInt64();
        ulong used  = reader.ReadUInt64();
        // specdata1 + specdata2 (rdev)
        uint spec1  = reader.ReadUInt32();
        uint spec2  = reader.ReadUInt32();
        ulong fsid  = reader.ReadUInt64();
        ulong fileid= reader.ReadUInt64();
        var atime   = ReadNfsTime(reader);
        var mtime   = ReadNfsTime(reader);
        var ctime   = ReadNfsTime(reader);

        return new NfsFileAttributes
        {
            Type        = type,
            Mode        = mode,
            NLink       = nlink,
            Uid         = uid,
            Gid         = gid,
            Size        = size,
            Used        = used,
            Rdev        = (spec1 << 16) | (spec2 & 0xffff),
            FsId        = fsid,
            FileId      = fileid,
            AccessTime  = atime,
            ModifyTime  = mtime,
            ChangeTime  = ctime,
        };
    }

    private static DateTimeOffset ReadNfsTime(XdrReader r)
    {
        uint seconds     = r.ReadUInt32();
        uint nanoseconds = r.ReadUInt32();
        return DateTimeOffset.FromUnixTimeSeconds(seconds)
               + TimeSpan.FromTicks(nanoseconds / 100L); // ns → 100-ns ticks
    }
}
