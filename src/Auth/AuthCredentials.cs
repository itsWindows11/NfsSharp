using NfsSharp.Xdr;

namespace NfsSharp.Auth;

/// <summary>
/// Base class for ONC RPC authentication credentials.
/// Subclasses encode their flavor and opaque body in <see cref="Encode"/>.
/// </summary>
public abstract class AuthCredentials
{
    /// <summary>The authentication flavour of these credentials.</summary>
    public abstract AuthType Flavor { get; }

    /// <summary>
    /// Encodes the credentials as an XDR auth-body: [flavor (int32)][opaque body (variable-length)].
    /// The returned bytes can be embedded directly in an RPC call header.
    /// </summary>
    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        var w = new XdrWriter(ms);
        w.WriteInt32((int)Flavor);
        EncodeBody(w);
        return ms.ToArray();
    }

    /// <summary>Encodes only the variable-length opaque body for this credential type.</summary>
    protected abstract void EncodeBody(XdrWriter writer);

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// A pre-encoded AUTH_NONE verifier ([0][0]) to use as the call verifier
    /// (clients always send an empty verifier).
    /// </summary>
    internal static readonly byte[] NullVerifier;

    static AuthCredentials()
    {
        using var ms = new MemoryStream();
        var w = new XdrWriter(ms);
        w.WriteInt32((int)AuthType.None);
        w.WriteUInt32(0);
        NullVerifier = ms.ToArray();
    }
}
