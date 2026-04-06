namespace NfsSharp.Protocol;

/// <summary>
/// Describes the attributes to change on a file or directory via NFS SETATTR.
/// Only properties set to a non-<see langword="null"/> value are applied;
/// leave a property <see langword="null"/> to keep the server's current value.
/// </summary>
public sealed class NfsSetAttributes
{
    /// <summary>New Unix permission bits, or <see langword="null"/> to leave unchanged.</summary>
    public uint? Mode { get; set; }

    /// <summary>New owner UID, or <see langword="null"/> to leave unchanged.</summary>
    public uint? Uid { get; set; }

    /// <summary>New owner GID, or <see langword="null"/> to leave unchanged.</summary>
    public uint? Gid { get; set; }

    /// <summary>Truncate file to this size in bytes, or <see langword="null"/> to leave unchanged.</summary>
    public ulong? Size { get; set; }

    /// <summary>New last-access time, or <see langword="null"/> to leave unchanged.</summary>
    public DateTimeOffset? AccessTime { get; set; }

    /// <summary>New last-modification time, or <see langword="null"/> to leave unchanged.</summary>
    public DateTimeOffset? ModifyTime { get; set; }
}

/// <summary>Filesystem statistics returned by FSSTAT.</summary>
public sealed class NfsFsStat
{
    /// <summary>Total bytes on the filesystem.</summary>
    public ulong TotalBytes { get; init; }

    /// <summary>Free bytes on the filesystem.</summary>
    public ulong FreeBytes { get; init; }

    /// <summary>Available bytes for unprivileged users.</summary>
    public ulong AvailBytes { get; init; }

    /// <summary>Total file slots (inodes).</summary>
    public ulong TotalFiles { get; init; }

    /// <summary>Free file slots.</summary>
    public ulong FreeFiles { get; init; }

    /// <summary>Available file slots for unprivileged users.</summary>
    public ulong AvailFiles { get; init; }
}
