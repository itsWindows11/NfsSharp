using System;

namespace NfsSharp.Xdr
{
    /// <summary>
    /// Thrown when XDR serialization or deserialization fails due to malformed data
    /// or constraint violations (e.g. string/opaque exceeding declared maximum length).
    /// </exception>
    public sealed class XdrException : Exception
    {
        /// <inheritdoc />
        public XdrException(string message) : base(message) { }

        /// <inheritdoc />
        public XdrException(string message, Exception inner) : base(message, inner) { }
    }
}
