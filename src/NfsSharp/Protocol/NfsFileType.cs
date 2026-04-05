namespace NfsSharp.Protocol
{
    /// <summary>NFS file-system object types (NFSv3 ftype3, RFC 1813 §2.6).</summary>
    public enum NfsFileType
    {
        /// <summary>Regular file.</summary>
        Regular       = 1,

        /// <summary>Directory.</summary>
        Directory     = 2,

        /// <summary>Block special device.</summary>
        BlockDevice   = 3,

        /// <summary>Character special device.</summary>
        CharDevice    = 4,

        /// <summary>Symbolic link.</summary>
        SymbolicLink  = 5,

        /// <summary>Socket.</summary>
        Socket        = 6,

        /// <summary>Named pipe (FIFO).</summary>
        Fifo          = 7,
    }
}
