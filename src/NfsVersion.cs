namespace NfsSharp;

/// <summary>
/// The NFS protocol version to use when connecting.
/// </summary>
public enum NfsVersion
{
    /// <summary>Automatically negotiate the highest mutually supported version (NFSv4 → NFSv3 → NFSv2).</summary>
    Auto = 0,

    /// <summary>NFS version 2 (RFC 1094). Maximum file size 2 GiB; UDP-only in practice.</summary>
    V2   = 2,

    /// <summary>NFS version 3 (RFC 1813). Stateless, large-file support, strong error detail.</summary>
    V3   = 3,

    /// <summary>NFS version 4 (RFC 7530 / RFC 8881). Stateful, integrated locking, ACLs.</summary>
    V4   = 4,
}
