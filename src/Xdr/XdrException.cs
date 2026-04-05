namespace NfsSharp.Xdr;

/// <summary>
/// The exception thrown when XDR-encoded data is malformed, truncated, or violates a
/// length constraint (e.g. a variable-length opaque field exceeds its declared maximum).
/// </summary>
public sealed class XdrException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="XdrException"/> with the specified error message.
    /// </summary>
    /// <param name="message">A message that describes the XDR decoding error.</param>
    public XdrException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new <see cref="XdrException"/> with the specified error message
    /// and a reference to the inner exception that caused this exception.
    /// </summary>
    /// <param name="message">A message that describes the XDR decoding error.</param>
    /// <param name="inner">The exception that is the cause of this exception.</param>
    public XdrException(string message, Exception inner) : base(message, inner) { }
}
