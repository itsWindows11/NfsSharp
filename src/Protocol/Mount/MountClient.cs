using NfsSharp.Auth;
using NfsSharp.Rpc;

namespace NfsSharp.Protocol.Mount;

/// <summary>Mount protocol client (RFC 1813 Appendix I / RFC 1094 §A.3).</summary>
internal sealed class MountClient : IDisposable
{
    private readonly RpcClient     _rpc;
    private readonly AuthCredentials _credentials;
    private readonly uint          _mountVersion;

    // Mount v1 = NFSv2 era; Mount v3 = NFSv3 era; NFSv4 uses its own auth.
    private const uint MountV1 = 1;
    private const uint MountV3 = 3;

    internal MountClient(string host, int port, uint nfsVersion, AuthCredentials credentials,
        TimeSpan connectTimeout, TimeSpan readTimeout)
    {
        _rpc          = new RpcClient(host, port, connectTimeout, readTimeout);
        _credentials  = credentials;
        _mountVersion = nfsVersion == 2 ? MountV1 : MountV3;
    }

    internal Task ConnectAsync(CancellationToken ct = default) => _rpc.ConnectAsync(ct);

    // ── Mount ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Mounts the given export path and returns the root file handle.
    /// Throws <see cref="NfsException"/> on server-side errors.
    /// </summary>
    internal async Task<NfsFileHandle> MountAsync(string exportPath, CancellationToken ct = default)
    {
        var reader = await _rpc.CallAsync(
            RpcConstants.MountProgram,
            _mountVersion,
            RpcConstants.MntProcMnt,
            _credentials,
            w => w.WriteString(exportPath),
            ct).ConfigureAwait(false);

        int status = reader.ReadInt32();
        if (status != 0)
            throw new NfsException((NfsStatus)status, $"Mount failed for '{exportPath}'.");

        // NFSv3 mount returns a variable-length file handle; NFSv2 returns fixed 32 bytes.
        NfsFileHandle handle;
        if (_mountVersion == MountV3)
            handle = NfsFileHandle.ReadFrom(reader, NfsFileHandle.MaxSizeV3);
        else
            handle = new NfsFileHandle(reader.ReadFixedOpaque(32));

        return handle;
    }

    // ── Unmount ───────────────────────────────────────────────────────────

    /// <summary>Informs the server that the client has unmounted the export.</summary>
    internal async Task UnmountAsync(string exportPath, CancellationToken ct = default)
    {
        await _rpc.CallAsync(
            RpcConstants.MountProgram,
            _mountVersion,
            RpcConstants.MntProcUmnt,
            _credentials,
            w => w.WriteString(exportPath),
            ct).ConfigureAwait(false);
    }

    // ── Export list ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of exported paths from the server.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ListExportsAsync(CancellationToken ct = default)
    {
        var reader = await _rpc.CallAsync(
            RpcConstants.MountProgram,
            _mountVersion,
            RpcConstants.MntProcExport,
            _credentials,
            _ => { },   // no arguments
            ct).ConfigureAwait(false);

        var exports = new List<string>();
        while (reader.ReadBool()) // value_follows
        {
            string path = reader.ReadString(1024);
            // Read (and discard) the groups list.
            while (reader.ReadBool()) // group value_follows
                reader.ReadString(1024);

            exports.Add(path);
        }
        return exports;
    }

    public void Dispose() => _rpc.Dispose();
}
