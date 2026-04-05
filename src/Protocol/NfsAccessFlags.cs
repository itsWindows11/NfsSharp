namespace NfsSharp.Protocol;

/// <summary>
/// Access permission bits for NFS ACCESS calls (NFSv3 RFC 1813 §2.6 / NFSv4 RFC 7530 §6.2.1.3).
/// </summary>
[Flags]
public enum NfsAccessFlags : uint
{
    /// <summary>No access.</summary>
    None    = 0x00,

    /// <summary>Read data from file or read directory.</summary>
    Read    = 0x01,

    /// <summary>Look up a name in a directory.</summary>
    Lookup  = 0x02,

    /// <summary>Rewrite existing file data or modify existing directory entries.</summary>
    Modify  = 0x04,

    /// <summary>Write new data or add directory entries.</summary>
    Extend  = 0x08,

    /// <summary>Delete an existing directory entry.</summary>
    Delete  = 0x10,

    /// <summary>Execute file or search a directory.</summary>
    Execute = 0x20,
}
