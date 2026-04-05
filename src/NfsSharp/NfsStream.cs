using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NfsSharp.Protocol;

namespace NfsSharp
{
    /// <summary>
    /// A <see cref="Stream"/>-derived class that provides transparent, seekable
    /// streaming read and write access to a single file on an NFS server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NfsStream"/> translates every <see cref="Read"/>, <see cref="Write"/>,
    /// and <see cref="Seek"/> call into one or more NFS READ / WRITE / COMMIT RPCs.
    /// Large transfers are automatically split into server-compatible chunk sizes.
    /// </para>
    /// <para>
    /// Callers should obtain an instance from
    /// <see cref="NfsClient.OpenFileAsync"/> or <see cref="NfsClient.CreateFileAsync"/>
    /// rather than constructing one directly.
    /// </para>
    /// <para>
    /// <strong>Thread safety</strong>: a single <see cref="NfsStream"/> instance must not
    /// be used concurrently from multiple threads.  Create one stream per logical reader/writer.
    /// </para>
    /// </remarks>
    public sealed class NfsStream : Stream
    {
        private readonly INfsFileOperations _ops;
        private readonly NfsFileHandle      _handle;
        private readonly bool               _readable;
        private readonly bool               _writable;

        private long  _position;
        private long? _cachedLength;   // lazily populated by GetLength()
        private bool  _hasUncommitted; // true if UNSTABLE writes are pending
        private bool  _disposed;

        /// <summary>Maximum bytes to transfer in a single NFS READ or WRITE call (1 MiB).</summary>
        private const int ChunkSize = 1 * 1024 * 1024;

        // ── Constructor ───────────────────────────────────────────────────────

        internal NfsStream(
            INfsFileOperations ops,
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

        /// <inheritdoc />
        public override bool CanRead  => _readable && !_disposed;

        /// <inheritdoc />
        public override bool CanWrite => _writable && !_disposed;

        /// <inheritdoc />
        public override bool CanSeek  => !_disposed;

        /// <inheritdoc />
        public override bool CanTimeout => true;

        // ── Length / Position ─────────────────────────────────────────────────

        /// <inheritdoc />
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                if (!_cachedLength.HasValue)
                    // Synchronous fallback — callers should prefer GetLengthAsync().
                    _cachedLength = GetLengthCoreAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                return _cachedLength.Value;
            }
        }

        /// <summary>Asynchronously returns the current file size in bytes.</summary>
        public async Task<long> GetLengthAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            _cachedLength = await GetLengthCoreAsync(ct).ConfigureAwait(false);
            return _cachedLength.Value;
        }

        /// <inheritdoc />
        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Position must be non-negative.");
                _position = value;
            }
        }

        // ── Seek ──────────────────────────────────────────────────────────────

        /// <inheritdoc />
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
                throw new IOException("Attempted to seek before the beginning of the stream.");
            _position = newPos;
            return _position;
        }

        // ── Read ──────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsyncCore(buffer, offset, count, ct);

#if NETSTANDARD2_0
        // ReadAsync(Memory<byte>, ...) is not available in netstandard2.0; fall back to array overload.
#else
        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // Rent a temporary array if the buffer is not backed by an array.
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var seg))
                return await ReadAsyncCore(seg.Array!, seg.Offset, seg.Count, ct).ConfigureAwait(false);

            var tmp = new byte[buffer.Length];
            int n   = await ReadAsyncCore(tmp, 0, tmp.Length, ct).ConfigureAwait(false);
            tmp.AsMemory(0, n).CopyTo(buffer);
            return n;
        }
#endif

        private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ValidateReadWriteArgs(buffer, offset, count);
            ThrowIfNotReadable();
            if (count == 0) return 0;

            int totalRead = 0;
            while (count > 0)
            {
                int chunk = Math.Min(count, ChunkSize);
                var result = await _ops.ReadAsync(_handle, _position, chunk, ct).ConfigureAwait(false);

                if (result.Data.Length == 0)
                    break; // EOF

                Array.Copy(result.Data, 0, buffer, offset, result.Data.Length);
                _position += result.Data.Length;
                offset    += result.Data.Length;
                count     -= result.Data.Length;
                totalRead += result.Data.Length;

                if (result.Eof) break;
            }
            return totalRead;
        }

        // ── Write ─────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsyncCore(buffer, offset, count, ct);

#if !NETSTANDARD2_0
        /// <inheritdoc />
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var seg))
            {
                await WriteAsyncCore(seg.Array!, seg.Offset, seg.Count, ct).ConfigureAwait(false);
                return;
            }
            var tmp = buffer.ToArray();
            await WriteAsyncCore(tmp, 0, tmp.Length, ct).ConfigureAwait(false);
        }
#endif

        private async Task WriteAsyncCore(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ValidateReadWriteArgs(buffer, offset, count);
            ThrowIfNotWritable();
            if (count == 0) return;

            while (count > 0)
            {
                int chunk   = Math.Min(count, ChunkSize);
                int written = await _ops.WriteAsync(_handle, _position, buffer, offset, chunk, ct).ConfigureAwait(false);
                if (written <= 0)
                    throw new IOException("NFS WRITE returned zero bytes written.");

                _position    += written;
                offset       += written;
                count        -= written;
                _hasUncommitted = true;

                // Invalidate cached length if we've extended the file.
                if (_cachedLength.HasValue && _position > _cachedLength.Value)
                    _cachedLength = _position;
            }
        }

        // ── Flush / SetLength ─────────────────────────────────────────────────

        /// <summary>
        /// Commits any unstable (buffered) writes to stable storage on the server.
        /// For NFSv2 this is a no-op (all writes are already synchronous).
        /// </summary>
        public override void Flush()
            => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        /// <inheritdoc />
        public override async Task FlushAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            if (!_hasUncommitted) return;
            await _ops.CommitAsync(_handle, 0, 0, ct).ConfigureAwait(false);
            _hasUncommitted = false;
        }

        /// <summary>
        /// Truncates or extends the file to <paramref name="value"/> bytes.
        /// Implemented via NFS SETATTR on NFSv3 / NFSv4.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Thrown for NFSv2, which does not support arbitrary truncation via SETATTR.
        /// </exception>
        public override void SetLength(long value)
            => SetLengthAsync(value, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>Asynchronously truncates or extends the file to <paramref name="value"/> bytes.</summary>
        public async Task SetLengthAsync(long value, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            ThrowIfNotWritable();
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

            // Delegate to protocol-specific SETATTR.
            if (_ops is Protocol.v3.NfsV3Client v3)
            {
                await v3.SetAttrAsync(_handle,
                    new Protocol.NfsSetAttributes { Size = (ulong)value }, ct).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException(
                    "SetLength is not supported for this NFS protocol version.");
            }

            _cachedLength = value;
            if (_position > value) _position = value;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                if (_hasUncommitted)
                {
                    try { _ops.CommitAsync(_handle, 0, 0, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch { /* best-effort */ }
                }
            }
            _disposed = true;
            base.Dispose(disposing);
        }

#if !NETSTANDARD2_0
        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                if (_hasUncommitted)
                {
                    try { await _ops.CommitAsync(_handle, 0, 0, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best-effort */ }
                }
                _disposed = true;
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<long> GetLengthCoreAsync(CancellationToken ct)
        {
            var attrs = await _ops.GetAttrAsync(_handle, ct).ConfigureAwait(false);
            return (long)attrs.Size;
        }

        private static void ValidateReadWriteArgs(byte[] buffer, int offset, int count)
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
                throw new NotSupportedException("This NfsStream is not opened for reading.");
        }

        private void ThrowIfNotWritable()
        {
            if (!_writable)
                throw new NotSupportedException("This NfsStream is not opened for writing.");
        }
    }
}
