using System.Buffers.Binary;
using System.Net.Sockets;
using NfsSharp.Auth;
using NfsSharp.Xdr;

namespace NfsSharp.Rpc;

/// <summary>
/// Low-level ONC RPC client over TCP using the RFC 5531 record-marking standard (§11).
/// Handles connection management, XID tracking, and retry on transient failures.
/// Thread-safe: serialises all in-flight calls with an async semaphore.
/// </summary>
internal sealed class RpcClient : IDisposable
{
    private readonly string _host;
    private readonly int    _port;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _readTimeout;

    private TcpClient?   _tcp;
    private NetworkStream? _stream;
    private uint         _xid;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    private const int MaxRetries        = 3;
    private const int RecordHeaderSize  = 4;   // uint32 record-marking header
    private const int MaxFragmentSize   = 65536;

    public RpcClient(string host, int port, TimeSpan connectTimeout, TimeSpan readTimeout)
    {
        _host           = host           ?? throw new ArgumentNullException(nameof(host));
        _port           = port;
        _connectTimeout = connectTimeout;
        _readTimeout    = readTimeout;
        // Start XIDs from a random offset to reduce collision risk after restart.
        _xid = (uint)Environment.TickCount;
    }

    // ── Connection ───────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Disconnect();
        _tcp = new TcpClient { NoDelay = true };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_connectTimeout);
        try
        {
#if NET5_0_OR_GREATER
            await _tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
#else
            var connectTask = _tcp.ConnectAsync(_host, _port);
            if (await Task.WhenAny(connectTask, Task.Delay(-1, cts.Token)).ConfigureAwait(false) != connectTask)
            {
                _tcp.Close();
                throw new TimeoutException($"Timed out connecting to {_host}:{_port}");
            }
            await connectTask.ConfigureAwait(false);
#endif
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to {_host}:{_port}");
        }

        _stream = _tcp.GetStream();
        _stream.ReadTimeout  = (int)_readTimeout.TotalMilliseconds;
        _stream.WriteTimeout = (int)_readTimeout.TotalMilliseconds;
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp    = null;
    }

    public bool IsConnected => _tcp?.Connected == true;

    // ── RPC call ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends an RPC CALL and returns an <see cref="XdrReader"/> positioned at the reply body.
    /// </summary>
    public async Task<XdrReader> CallAsync(
        uint               program,
        uint               version,
        uint               procedure,
        AuthCredentials    credentials,
        Action<XdrWriter>  writeArgs,
        CancellationToken  ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CallLockedAsync(program, version, procedure, credentials, writeArgs, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<XdrReader> CallLockedAsync(
        uint               program,
        uint               version,
        uint               procedure,
        AuthCredentials    credentials,
        Action<XdrWriter>  writeArgs,
        CancellationToken  ct)
    {
        int attempt = 0;
        while (true)
        {
            if (!IsConnected)
                await ConnectAsync(ct).ConfigureAwait(false);

            uint xid = unchecked(++_xid);
            byte[] payload = BuildPayload(xid, program, version, procedure, credentials, writeArgs);

            try
            {
                await SendRecordAsync(payload, ct).ConfigureAwait(false);
                byte[] replyRecord = await ReceiveRecordAsync(ct).ConfigureAwait(false);
                var reader = new XdrReader(replyRecord);
                RpcMessage.ReadReplyHeader(reader, xid);
                return reader;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetries)
            {
                attempt++;
                Disconnect();
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    // ── Record layer (RFC 5531 §11) ──────────────────────────────────────

    private async Task SendRecordAsync(byte[] payload, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected.");

        // Send as a single last-fragment record.
        var header = new byte[RecordHeaderSize];
        uint fragmentHeader = (uint)payload.Length | RpcConstants.RecordLastFragment;
        BinaryPrimitives.WriteUInt32BigEndian(header, fragmentHeader);

        await _stream.WriteAsync(header, 0, RecordHeaderSize, ct).ConfigureAwait(false);
        await _stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReceiveRecordAsync(CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected.");

        using var ms = new MemoryStream();
        bool lastFragment = false;

        while (!lastFragment)
        {
            byte[] headerBuf = await ReadExactAsync(RecordHeaderSize, ct).ConfigureAwait(false);
            uint   fragHeader = BinaryPrimitives.ReadUInt32BigEndian(headerBuf);
            lastFragment = (fragHeader & RpcConstants.RecordLastFragment) != 0;
            int  fragSize = (int)(fragHeader & ~RpcConstants.RecordLastFragment);

            if (fragSize < 0 || fragSize > RpcConstants.DefaultMaxPayload)
                throw new RpcException($"Invalid RPC fragment size: {fragSize}");

            byte[] fragData = await ReadExactAsync(fragSize, ct).ConfigureAwait(false);
            ms.Write(fragData, 0, fragData.Length);
        }

        return ms.ToArray();
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await _stream!.ReadAsync(buf, offset, count - offset, ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Connection closed by remote host during RPC read.");
            offset += n;
        }
        return buf;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static byte[] BuildPayload(
        uint              xid,
        uint              program,
        uint              version,
        uint              procedure,
        AuthCredentials   credentials,
        Action<XdrWriter> writeArgs)
    {
        using var ms     = new MemoryStream();
        var writer       = new XdrWriter(ms);
        byte[] credBytes = credentials.Encode();
        byte[] verifBytes= AuthCredentials.NullVerifier;

        RpcMessage.WriteCallHeader(writer, xid, program, version, procedure, credBytes, verifBytes);
        writeArgs(writer);
        return ms.ToArray();
    }

    private static bool IsTransient(Exception ex) =>
        ex is SocketException ||
        ex is IOException     ||
        ex is TimeoutException;

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        Disconnect();
        _lock.Dispose();
    }
}
