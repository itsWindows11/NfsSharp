using System;
using NfsSharp.Protocol;

namespace NfsSharp
{
    /// <summary>
    /// Thrown when the NFS server returns an error status, or when a client-side
    /// NFS protocol violation is detected.
    /// </summary>
    public class NfsException : Exception
    {
        /// <summary>The NFS status code returned by the server.</summary>
        public NfsStatus Status { get; }

        /// <summary>Initialises a new <see cref="NfsException"/> with the given status code.</summary>
        public NfsException(NfsStatus status)
            : base(BuildMessage(status, null))
        {
            Status = status;
        }

        /// <summary>Initialises a new <see cref="NfsException"/> with a custom message.</summary>
        public NfsException(NfsStatus status, string message)
            : base(BuildMessage(status, message))
        {
            Status = status;
        }

        /// <summary>Initialises a new <see cref="NfsException"/> with a custom message and inner exception.</summary>
        public NfsException(NfsStatus status, string message, Exception inner)
            : base(BuildMessage(status, message), inner)
        {
            Status = status;
        }

        private static string BuildMessage(NfsStatus status, string? extra)
        {
            var msg = $"NFS error: {status} ({(int)status})";
            return extra != null ? $"{extra} [{msg}]" : msg;
        }
    }
}
