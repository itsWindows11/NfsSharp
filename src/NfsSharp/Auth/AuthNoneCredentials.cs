using NfsSharp.Xdr;

namespace NfsSharp.Auth
{
    /// <summary>
    /// AUTH_NONE credentials — no authentication (RFC 5531 §8.2.1).
    /// Sends a zero-length opaque body.
    /// </summary>
    public sealed class AuthNoneCredentials : AuthCredentials
    {
        /// <inheritdoc />
        public override AuthType Flavor => AuthType.None;

        /// <inheritdoc />
        protected override void EncodeBody(XdrWriter writer)
        {
            // AUTH_NONE body is a zero-length variable-length opaque.
            writer.WriteUInt32(0);
        }
    }
}
