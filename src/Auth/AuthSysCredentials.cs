using NfsSharp.Xdr;

namespace NfsSharp.Auth;

/// <summary>
/// AUTH_SYS (Unix) credentials (RFC 5531 §8.2.2).
/// Contains a machine name, UID, GID, and a list of supplemental GIDs.
/// </summary>
public sealed class AuthSysCredentials : AuthCredentials
{
    private const int MaxMachineName  = 255;
    private const int MaxGids         = 16;

    /// <summary>Arbitrary stamp (usually seconds since epoch or process start time).</summary>
    public uint Stamp { get; set; }

    /// <summary>Client machine name (max 255 characters).</summary>
    public string MachineName { get; set; }

    /// <summary>Effective user ID.</summary>
    public uint Uid { get; set; }

    /// <summary>Effective group ID.</summary>
    public uint Gid { get; set; }

    /// <summary>Supplemental group IDs (max 16 entries).</summary>
    public uint[] GidList { get; set; }

    /// <summary>
    /// Initialises AUTH_SYS credentials.
    /// </summary>
    /// <param name="machineName">Client machine name (max 255 chars).</param>
    /// <param name="uid">Effective user ID.</param>
    /// <param name="gid">Effective group ID.</param>
    /// <param name="gidList">Supplemental group IDs (max 16). Pass <see langword="null"/> for none.</param>
    /// <param name="stamp">Opaque identifier; defaults to current Unix timestamp if zero.</param>
    public AuthSysCredentials(
        string machineName,
        uint   uid     = 0,
        uint   gid     = 0,
        uint[]? gidList = null,
        uint   stamp   = 0)
    {
        if (machineName == null) throw new ArgumentNullException(nameof(machineName));
        if (machineName.Length > MaxMachineName)
            throw new ArgumentOutOfRangeException(nameof(machineName),
                $"Machine name must be at most {MaxMachineName} characters.");

        gidList ??= Array.Empty<uint>();
        if (gidList.Length > MaxGids)
            throw new ArgumentOutOfRangeException(nameof(gidList),
                $"GID list must contain at most {MaxGids} entries.");

        MachineName = machineName;
        Uid         = uid;
        Gid         = gid;
        GidList     = gidList;
        Stamp       = stamp != 0 ? stamp : (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <inheritdoc />
    public override AuthType Flavor => AuthType.Sys;

    /// <inheritdoc />
    protected override void EncodeBody(XdrWriter outerWriter)
    {
        // The AUTH_SYS opaque body is itself XDR-encoded.
        using var ms   = new MemoryStream();
        var inner      = new XdrWriter(ms);
        inner.WriteUInt32(Stamp);
        inner.WriteString(MachineName);
        inner.WriteUInt32(Uid);
        inner.WriteUInt32(Gid);
        inner.WriteUInt32Array(GidList);

        byte[] body = ms.ToArray();
        outerWriter.WriteVarOpaque(body);
    }
}
