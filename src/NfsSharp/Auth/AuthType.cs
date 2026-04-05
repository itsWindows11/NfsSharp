namespace NfsSharp.Auth
{
    /// <summary>ONC RPC authentication flavour identifiers (RFC 5531 §8.2).</summary>
    public enum AuthType
    {
        /// <summary>No authentication (AUTH_NONE).</summary>
        None  = 0,

        /// <summary>Unix / System credentials (AUTH_SYS).</summary>
        Sys   = 1,

        /// <summary>Short-hand credentials (AUTH_SHORT); server-assigned.</summary>
        Short = 2,

        /// <summary>RPCSEC_GSS (Kerberos / GSSAPI, RFC 2203).</summary>
        Gss   = 6,
    }
}
