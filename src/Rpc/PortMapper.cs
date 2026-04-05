using NfsSharp.Auth;

namespace NfsSharp.Rpc;

/// <summary>
/// Client for the ONC Port Mapper / rpcbind service (RFC 1833).
/// Used to discover which TCP port a given RPC program is listening on.
/// </summary>
internal sealed class PortMapper : IDisposable
{
    private readonly RpcClient _rpc;

    /// <param name="host">Hostname or IP address of the NFS server.</param>
    /// <param name="connectTimeout">Timeout for establishing the port mapper connection.</param>
    /// <param name="readTimeout">Timeout for each read operation.</param>
    public PortMapper(string host, TimeSpan connectTimeout, TimeSpan readTimeout)
    {
        _rpc = new RpcClient(host, RpcConstants.PortMapperPort, connectTimeout, readTimeout);
    }

    /// <summary>Connects to the port mapper service.</summary>
    public Task ConnectAsync(CancellationToken ct = default) => _rpc.ConnectAsync(ct);

    /// <summary>
    /// Calls PMAPPROC_GETPORT (procedure 3) to resolve the TCP port of a given program/version.
    /// Returns 0 if the program is not registered.
    /// </summary>
    public async Task<int> GetPortAsync(
        uint              program,
        uint              version,
        CancellationToken ct = default)
    {
        var creds = new AuthNoneCredentials();
        var reader = await _rpc.CallAsync(
            RpcConstants.PortMapperProgram,
            2,
            RpcConstants.PmapProcGetPort,
            creds,
            w =>
            {
                w.WriteUInt32(program);  // prog
                w.WriteUInt32(version);  // vers
                w.WriteUInt32(6);        // prot = IPPROTO_TCP
                w.WriteUInt32(0);        // port (ignored for GETPORT)
            },
            ct).ConfigureAwait(false);

        return (int)reader.ReadUInt32();
    }

    /// <inheritdoc />
    public void Dispose() => _rpc.Dispose();
}
