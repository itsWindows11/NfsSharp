using System.Runtime.CompilerServices;
using NfsSharp.Protocol;

namespace NfsSharp.Tests.Mocks;

/// <summary>
/// In-memory implementation of <see cref="INfsProtocolClient"/> for unit tests.
/// </summary>
internal sealed class MockProtocolClient : INfsProtocolClient
{
    private readonly InMemoryNfsServer _server = new();

    /// <summary>Tracks write RPC calls.</summary>
    public int WriteCallCount { get; private set; }

    /// <summary>Tracks commit RPC calls.</summary>
    public int CommitCallCount { get; private set; }

    /// <summary>Tracks setattr RPC calls.</summary>
    public int SetAttrCallCount { get; private set; }

    /// <summary>Stores the latest attrs provided to <see cref="SetAttrAsync"/>.</summary>
    public NfsSetAttributes? LastSetAttr { get; private set; }

    /// <summary>Limits bytes reported as written per call. Defaults to no limit.</summary>
    public int WriteBytesPerCall { get; set; } = int.MaxValue;

    /// <summary>Gets the root directory handle.</summary>
    public NfsFileHandle RootHandle => _server.RootHandle;

    /// <summary>Creates a file under root and returns its handle.</summary>
    public NfsFileHandle CreateRootFile(string name, byte[]? initialData = null)
        => _server.CreateRootFile(name, initialData);

    public Task<NfsReadResult> ReadAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
        => Task.FromResult(_server.Read(handle, offset, count));

    public Task<int> WriteAsync(NfsFileHandle handle, long offset, byte[] data, int dataOffset, int count, CancellationToken ct)
    {
        WriteCallCount++;
        int chunk = count < WriteBytesPerCall ? count : WriteBytesPerCall;
        return Task.FromResult(_server.Write(handle, offset, data, dataOffset, chunk));
    }

    public Task CommitAsync(NfsFileHandle handle, long offset, int count, CancellationToken ct)
    {
        CommitCallCount++;
        return Task.CompletedTask;
    }

    public Task<NfsFileAttributes> GetAttrAsync(NfsFileHandle handle, CancellationToken ct)
        => Task.FromResult(_server.GetAttributes(handle));

    public Task SetAttrAsync(NfsFileHandle handle, NfsSetAttributes attrs, CancellationToken ct)
    {
        SetAttrCallCount++;
        LastSetAttr = attrs;
        _server.SetAttributes(handle, attrs);
        return Task.CompletedTask;
    }

    public Task<(NfsFileHandle Handle, NfsFileAttributes Attributes)> LookupAsync(NfsFileHandle dir, string name, CancellationToken ct)
        => Task.FromResult(_server.Lookup(dir, name));

    public Task<NfsFileHandle> CreateFileAsync(NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
        => Task.FromResult(_server.CreateFile(dir, name, attrs));

    public Task<NfsFileHandle> MkDirAsync(NfsFileHandle dir, string name, NfsSetAttributes attrs, CancellationToken ct)
        => Task.FromResult(_server.MkDir(dir, name, attrs));

    public Task SymLinkAsync(NfsFileHandle dir, string name, string linkTarget, NfsSetAttributes attrs, CancellationToken ct)
    {
        _server.SymLink(dir, name, linkTarget, attrs);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NfsDirectoryEntry>> ReadDirAsync(NfsFileHandle dir, CancellationToken ct)
        => Task.FromResult(_server.ReadDir(dir));

    public async IAsyncEnumerable<NfsDirectoryEntry> EnumerateDirAsync(NfsFileHandle dir, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in _server.ReadDir(dir))
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
            await Task.Yield();
        }
    }

    public Task<string> ReadLinkAsync(NfsFileHandle handle, CancellationToken ct)
        => Task.FromResult(_server.ReadLink(handle));

    public Task RemoveAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        _server.Remove(dir, name);
        return Task.CompletedTask;
    }

    public Task RmDirAsync(NfsFileHandle dir, string name, CancellationToken ct)
    {
        _server.RmDir(dir, name);
        return Task.CompletedTask;
    }

    public Task RenameAsync(NfsFileHandle fromDir, string fromName, NfsFileHandle toDir, string toName, CancellationToken ct)
    {
        _server.Rename(fromDir, fromName, toDir, toName);
        return Task.CompletedTask;
    }

    public Task LinkAsync(NfsFileHandle file, NfsFileHandle linkDir, string linkName, CancellationToken ct)
    {
        _server.Link(file, linkDir, linkName);
        return Task.CompletedTask;
    }

    public Task<NfsFsStat> FsStatAsync(NfsFileHandle handle, CancellationToken ct)
        => Task.FromResult(_server.FsStat());

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose() { }
}
