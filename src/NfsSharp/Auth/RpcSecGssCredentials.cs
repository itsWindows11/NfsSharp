using System;
using NfsSharp.Xdr;

namespace NfsSharp.Auth
{
    /// <summary>
    /// Stub for RPCSEC_GSS credentials (RFC 2203 / RFC 5403).
    /// Full Kerberos/GSSAPI support requires a GSSAPI binding not included in this build.
    /// Using this type causes an <see cref="NotSupportedException"/> at encode time unless
    /// a concrete subclass overrides <see cref="EncodeBody"/>.
    /// </summary>
    public class RpcSecGssCredentials : AuthCredentials
    {
        /// <inheritdoc />
        public override AuthType Flavor => AuthType.Gss;

        /// <inheritdoc />
        protected override void EncodeBody(XdrWriter writer)
        {
            throw new NotSupportedException(
                "RPCSEC_GSS requires a GSSAPI binding. " +
                "Derive from RpcSecGssCredentials and override EncodeBody, " +
                "or use AuthNoneCredentials / AuthSysCredentials instead.");
        }
    }
}
