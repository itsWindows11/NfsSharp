namespace NfsSharp.Protocol
{
    /// <summary>
    /// NFS status codes used across NFSv2, NFSv3, and (with mapping) NFSv4.
    /// Values match the NFSv3 nfsstat3 enumeration (RFC 1813 §2.6).
    /// </summary>
    public enum NfsStatus
    {
        /// <summary>Indicates success (NFS3_OK).</summary>
        Ok                   = 0,

        /// <summary>Permission denied.</summary>
        Acces                = 13,

        /// <summary>Not a directory or a symbolic link.</summary>
        NotDir               = 20,

        /// <summary>Is a directory.</summary>
        IsDir                = 21,

        /// <summary>Invalid argument.</summary>
        Inval                = 22,

        /// <summary>File too large.</summary>
        FBig                 = 27,

        /// <summary>No space left on device.</summary>
        NoSpc                = 28,

        /// <summary>Read-only file system.</summary>
        RoFs                 = 30,

        /// <summary>Too many hard links.</summary>
        MLink                = 31,

        /// <summary>Operation not permitted.</summary>
        Perm                 = 1,

        /// <summary>No such file or directory.</summary>
        NoEnt                = 2,

        /// <summary>I/O error.</summary>
        Io                   = 5,

        /// <summary>No such device or address.</summary>
        NxIo                 = 6,

        /// <summary>Bad file handle (stale).</summary>
        Stale                = 70,

        /// <summary>Too many levels of remote in path.</summary>
        Remote               = 71,

        /// <summary>Illegal NFS file handle.</summary>
        BadHandle            = 10001,

        /// <summary>Update synchronisation mismatch (detected during SETATTR).</summary>
        NotSync              = 10002,

        /// <summary>READDIR/READDIRPLUS cookie is stale.</summary>
        BadCookie            = 10003,

        /// <summary>Operation not supported.</summary>
        NotSupp              = 10004,

        /// <summary>Buffer or request is too small.</summary>
        TooSmall             = 10005,

        /// <summary>An error occurred on the server which does not map to any NFS error.</summary>
        ServerFault          = 10006,

        /// <summary>Bad type.</summary>
        BadType              = 10007,

        /// <summary>Illegal or unsupported attributes.</summary>
        JukeBox              = 10008,
    }
}
