using System.Runtime.InteropServices;
using NfsSharp.Protocol;

namespace NfsSharp;

/// <summary>
/// A <see cref="Stream"/>-derived class that provides transparent, seekable, readable,
/// and writable access to a single file on an NFS server.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NfsStream"/> translates every <see cref="Stream.ReadAsync(byte[],int,int,CancellationToken)"/>,
/// <see cref="Stream.WriteAsync(byte[],int,int,CancellationToken)"/>, and <see cref="Seek"/> call into
/// one or more NFS READ / WRITE / COMMIT RPCs against the underlying server. Large transfers are
/// automatically split into server-compatible chunk sizes (up to 1 MiB per call by default).
/// </para>
/// <para>
/// Because <see cref="NfsStream"/> is a plain <see cref="Stream"/>, it integrates naturally with the
/// broader .NET I/O ecosystem:
/// </para>
/// <code>
/// await using NfsStream stream = await nfs.OpenFileAsync("data/report.csv");
///
/// // Read with StreamReader
/// using var reader = new StreamReader(stream);
/// string text = await reader.ReadToEndAsync();
///
/// // Copy to a local file
/// using var local = File.Create("report.csv");
/// await stream.CopyToAsync(local);
///
/// // Deserialize JSON directly
/// var obj = await JsonSerializer.DeserializeAsync&lt;MyType&gt;(stream);
/// </code>
/// <para>
/// Obtain an instance via <see cref="NfsClient.OpenFileAsync(string,System.IO.FileAccess,bool,System.Threading.CancellationToken)"/>
/// rather than constructing one directly.
/// </para>
/// <para>
/// <strong>Thread safety:</strong> A single <see cref="NfsStream"/> instance must not be used
/// concurrently from multiple threads. Create one stream per logical reader/writer.
/// </para>
/// </remarks>
public sealed class NfsStream : Stream
{
    private readonly INfsProtocolClient _ops;
    private readonly NfsFileHandle      _handle;
    private readonly bool               _readable;
    private readonly bool               _writable;

    private long  _position;
    private long? _cachedLength;      // lazily populated on first Length access
    private bool  _hasUncommitted;    // true when UNSTABLE writes are pending COMMIT
    private bool  _disposed;

    /// <summary>Maximum bytes transferred in a single NFS READ or WRITE call (1 MiB).</summary>
    private const int ChunkSize = 1 * 1024 * 1024;

    // ── Constructor (internal) ────────────────────────────────────────────

    /// <summary>
    /// Initialises a new <see cref="NfsStream"/> backed by an already-resolved NFS file handle.
    /// </summary>
    /// <param name="ops">The version-agnostic protocol client that performs the actual RPCs.</param>
    /// <param name="handle">The opaque NFS file handle identifying the remote file.</param>
    /// <param name="knownLength">
    /// The file size in bytes at open time, used to pre-populate <see cref="Length"/>
    /// without an extra GETATTR round-trip. Pass a negative value if unknown.
    /// </param>
    /// <param name="readable">Whether this stream allows <see cref="Read"/>.</param>
    /// <param name="writable">Whether this stream allows <see cref="Write"/>.</param>
    internal NfsStream(
        INfsProtocolClient ops,
        NfsFileHandle      handle,
        long               knownLength,
        bool               readable,
        bool               writable)
    {
        _ops          = ops    ?? throw new ArgumentNullException(nameof(ops));
        _handle       = handle ?? throw new ArgumentNullException(nameof(handle));
        _readable     = readable;
        _writable     = writable;
        _cachedLength = knownLength >= 0 ? knownLength : (long?)null;
    }

    // ── Stream capability properties ──────────────────────────────────────

    /// <summary>
    /// Gets a value indicating whether the current stream supports reading.
    /// <see langword="false"/> after the stream has been disposed.
    /// </summary>
    public override bool CanRead => _readable && !_disposed;

    /// <summary>
    /// Gets a value indicating whether the current stream supports writing.
    /// <see langword="false"/> after the stream has been disposed, or when the stream
    /// was opened with read-only access via
    /// <see cref="NfsClient.OpenFileAsync(string,System.IO.FileAccess,bool,System.Threading.CancellationToken)"/>.
    /// </summary>
    public override bool CanWrite => _writable && !_disposed;

    /// <summary>
    /// Gets a value indicating whether the current stream supports seeking.
    /// Always <see langword="true"/> while the stream is open; NFS is a random-access protocol.
    /// </summary>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanTimeout => true;

    // ── Length / Position ─────────────────────────────────────────────────

    /// <summary>
    /// Gets the current file size in bytes.
    /// On the first access this issues a GETATTR RPC to the server; subsequent accesses
    /// return a cached value that is updated after every write.
    /// Prefer <see cref="GetLengthAsync"/> in async contexts to avoid a synchronous block.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            if (!_cachedLength.HasValue)
                _cachedLength = GetLengthCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            return _cachedLength.Value;
        }
    }

    /// <summary>
    /// Asynchronously returns the current file size in bytes, issuing a GETATTR RPC
    /// to the server if the size has not been cached since the last write.
    /// </summary>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>The file size in bytes.</returns>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public async Task<long> GetLengthAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _cachedLength = await GetLengthCoreAsync(ct).ConfigureAwait(false);
        return _cachedLength.Value;
    }

    /// <summary>
    /// Gets or sets the current byte position within the stream.
    /// Setting the position does not issue any RPC; NFS supports random access natively.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override long Position
    {
        get { ThrowIfDisposed(); return _position; }
        set
        {
            ThrowIfDisposed();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Position must be non-negative.");
            _position = value;
        }
    }

    // ── Seek ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the position within the current stream.
    /// No RPC is issued; the position is tracked client-side.
    /// </summary>
    /// <param name="offset">A byte offset relative to <paramref name="origin"/>.</param>
    /// <param name="origin">
    /// A value of type <see cref="SeekOrigin"/> indicating the reference point used to
    /// obtain the new position.
    /// </param>
    /// <returns>The new position within the stream.</returns>
    /// <exception cref="IOException">The computed position would be before the beginning of the stream.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        long newPos = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => Length + offset,
            _                  => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (newPos < 0)
            throw new IOException("Cannot seek before the beginning of the stream.");
        _position = newPos;
        return _position;
    }

    // ── Read ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a sequence of bytes from the current stream and advances the position.
    /// Blocks the calling thread; prefer <see cref="ReadAsync(byte[],int,int,CancellationToken)"/>
    /// in async contexts.
    /// </summary>
    /// <param name="buffer">An array of bytes to read data into.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <returns>
    /// The total number of bytes read. This may be less than <paramref name="count"/> if fewer bytes
    /// are available, and zero if end-of-file has been reached.
    /// </returns>
    /// <exception cref="NotSupportedException">The stream does not support reading.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously reads a sequence of bytes from the current stream and advances the position.
    /// Issues one or more NFS READ RPCs, splitting the request into chunks as necessary.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>
    /// The total number of bytes read into the buffer. May be less than <paramref name="count"/>
    /// at end-of-file.
    /// </returns>
    /// <exception cref="NotSupportedException">The stream does not support reading.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadCoreAsync(buffer, offset, count, ct);

#if !NETSTANDARD2_0
    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg))
            return await ReadCoreAsync(seg.Array!, seg.Offset, seg.Count, ct).ConfigureAwait(false);

        var tmp = new byte[buffer.Length];
        int n   = await ReadCoreAsync(tmp, 0, tmp.Length, ct).ConfigureAwait(false);
        tmp.AsMemory(0, n).CopyTo(buffer);
        return n;
    }
#endif

    private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ValidateBufferArgs(buffer, offset, count);
        ThrowIfNotReadable();
        if (count == 0) return 0;

        int totalRead = 0;
        while (count > 0)
        {
            int chunk = Math.Min(count, ChunkSize);
            NfsReadResult result = await _ops.ReadAsync(_handle, _position, chunk, ct).ConfigureAwait(false);

            if (result.Data.Length == 0) break; // EOF

            Array.Copy(result.Data, 0, buffer, offset, result.Data.Length);
            int n   = result.Data.Length;
            _position  += n;
            offset     += n;
            count      -= n;
            totalRead  += n;

            if (result.Eof) break;
        }
        return totalRead;
    }

    // ── Write ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a sequence of bytes to the current stream and advances the position.
    /// Blocks the calling thread; prefer <see cref="WriteAsync(byte[],int,int,CancellationToken)"/>
    /// in async contexts.
    /// </summary>
    /// <param name="buffer">An array of bytes to write from.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin reading data.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <exception cref="NotSupportedException">The stream does not support writing.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously writes a sequence of bytes to the current stream and advances the position.
    /// Issues one or more NFS WRITE RPCs, splitting large payloads into chunks as necessary.
    /// Writes are sent as <c>UNSTABLE</c>; call <see cref="FlushAsync"/> to commit to stable storage.
    /// </summary>
    /// <param name="buffer">The buffer containing the data to write.</param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> from which to read data.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <exception cref="NotSupportedException">The stream does not support writing.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteCoreAsync(buffer, offset, count, ct);

#if !NETSTANDARD2_0
    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> seg))
        {
            await WriteCoreAsync(seg.Array!, seg.Offset, seg.Count, ct).ConfigureAwait(false);
            return;
        }
        byte[] tmp = buffer.ToArray();
        await WriteCoreAsync(tmp, 0, tmp.Length, ct).ConfigureAwait(false);
    }
#endif

    private async Task WriteCoreAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ValidateBufferArgs(buffer, offset, count);
        ThrowIfNotWritable();
        if (count == 0) return;

        while (count > 0)
        {
            int chunk   = Math.Min(count, ChunkSize);
            int written = await _ops.WriteAsync(_handle, _position, buffer, offset, chunk, ct).ConfigureAwait(false);
            if (written <= 0)
                throw new IOException("NFS WRITE returned zero bytes written.");

            _position       += written;
            offset          += written;
            count           -= written;
            _hasUncommitted  = true;

            // Extend cached length if we've written past the previous end of file.
            if (_cachedLength.HasValue && _position > _cachedLength.Value)
                _cachedLength = _position;
        }
    }

    // ── Flush ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Commits any unstable (server-buffered) writes to stable storage by issuing
    /// an NFS COMMIT RPC. For NFSv2, where all writes are already synchronous, this
    /// is a no-op.
    /// Blocks the calling thread; prefer <see cref="FlushAsync"/> in async contexts.
    /// </summary>
    public override void Flush()
        => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously commits any unstable (server-buffered) writes to stable storage
    /// by issuing an NFS COMMIT RPC. For NFSv2 this is a no-op.
    /// </summary>
    /// <param name="ct">Token to cancel the operation.</param>
    public override async Task FlushAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!_hasUncommitted) return;
        await _ops.CommitAsync(_handle, 0, 0, ct).ConfigureAwait(false);
        _hasUncommitted = false;
    }

    // ── SetLength ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the length of the current stream by truncating or extending the remote file via
    /// NFS SETATTR. The stream position is clamped to <paramref name="value"/> if it would
    /// otherwise exceed the new length.
    /// Blocks the calling thread; prefer <see cref="SetLengthAsync"/> in async contexts.
    /// </summary>
    /// <param name="value">The desired length of the stream in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    /// <exception cref="NotSupportedException">The stream is read-only.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public override void SetLength(long value)
        => SetLengthAsync(value, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously sets the length of the current stream by truncating or extending the
    /// remote file via NFS SETATTR.
    /// </summary>
    /// <param name="value">The desired length of the stream in bytes.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    /// <exception cref="NotSupportedException">The stream is read-only or the NFS version does not support size-based SETATTR.</exception>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public async Task SetLengthAsync(long value, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfNotWritable();
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

        await _ops.SetAttrAsync(_handle, new NfsSetAttributes { Size = (ulong)value }, ct).ConfigureAwait(false);
        _cachedLength = value;
        if (_position > value) _position = value;
    }

    // ── IDisposable / IAsyncDisposable ────────────────────────────────────

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing && _hasUncommitted)
        {
            try
            { _ops.CommitAsync(_handle, 0, 0, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { /* best-effort on dispose */ }
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc />
#if !NETSTANDARD2_0
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed && _hasUncommitted)
        {
            try { await _ops.CommitAsync(_handle, 0, 0, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort on dispose */ }
        }
        _disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
    }
#endif

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<long> GetLengthCoreAsync(CancellationToken ct)
    {
        NfsFileAttributes attrs = await _ops.GetAttrAsync(_handle, ct).ConfigureAwait(false);
        return (long)attrs.Size;
    }

    private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
    {
        if (buffer == null)  throw new ArgumentNullException(nameof(buffer));
        if (offset < 0)      throw new ArgumentOutOfRangeException(nameof(offset));
        if (count  < 0)      throw new ArgumentOutOfRangeException(nameof(count));
        if (offset + count > buffer.Length)
            throw new ArgumentException("offset + count exceeds buffer length.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NfsStream));
    }

    private void ThrowIfNotReadable()
    {
        if (!_readable)
            throw new NotSupportedException("This NfsStream was not opened for reading.");
    }

    private void ThrowIfNotWritable()
    {
        if (!_writable)
            throw new NotSupportedException("This NfsStream was not opened for writing.");
    }
}
