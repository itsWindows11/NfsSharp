using NfsSharp.Protocol;

namespace NfsSharp;

/// <summary>
/// The exception thrown when an NFS server returns an error status code.
/// Carries the raw <see cref="NfsStatus"/> so callers can react to specific conditions
/// (e.g. <see cref="NfsStatus.NoEnt"/>, <see cref="NfsStatus.Acces"/>) without parsing
/// the message string.
/// </summary>
public class NfsException : Exception
{
    /// <summary>The NFS status code returned by the server.</summary>
    public NfsStatus Status { get; }

    /// <summary>
    /// Initialises a new <see cref="NfsException"/> with the given NFS status code.
    /// </summary>
    /// <param name="status">The NFS error status reported by the server.</param>
    public NfsException(NfsStatus status)
        : base(BuildMessage(status, null))
    {
        Status = status;
    }

    /// <summary>
    /// Initialises a new <see cref="NfsException"/> with the given NFS status code and a
    /// human-readable description of the failing operation.
    /// </summary>
    /// <param name="status">The NFS error status reported by the server.</param>
    /// <param name="message">Additional context about the operation that failed.</param>
    public NfsException(NfsStatus status, string message)
        : base(BuildMessage(status, message))
    {
        Status = status;
    }

    /// <summary>
    /// Initialises a new <see cref="NfsException"/> with the given NFS status code, a
    /// human-readable description, and an inner exception that caused this one.
    /// </summary>
    /// <param name="status">The NFS error status reported by the server.</param>
    /// <param name="message">Additional context about the operation that failed.</param>
    /// <param name="inner">The exception that is the cause of this exception.</param>
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
