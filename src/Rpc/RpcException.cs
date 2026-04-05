namespace NfsSharp.Rpc;

/// <summary>
/// The exception thrown when an ONC RPC call fails at the transport or protocol level
/// (e.g. XID mismatch, version mismatch, authentication error, or a denied reply).
/// For NFS-level errors use <see cref="NfsSharp.NfsException"/> instead.
/// </summary>
public sealed class RpcException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="RpcException"/> with the specified error message.
    /// </summary>
    /// <param name="message">A message that describes the RPC failure.</param>
    public RpcException(string message) : base(message) { }

    /// <summary>
    /// Initialises a new <see cref="RpcException"/> with the specified error message
    /// and a reference to the inner exception that caused this exception.
    /// </summary>
    /// <param name="message">A message that describes the RPC failure.</param>
    /// <param name="inner">The exception that is the cause of this exception.</param>
    public RpcException(string message, Exception inner) : base(message, inner) { }
}
