using System;

namespace NfsSharp.Rpc
{
    /// <summary>Thrown when an ONC RPC layer error occurs (message denial, auth failure, accept error, etc.).</summary>
    public sealed class RpcException : Exception
    {
        /// <inheritdoc />
        public RpcException(string message) : base(message) { }

        /// <inheritdoc />
        public RpcException(string message, Exception inner) : base(message, inner) { }
    }
}
