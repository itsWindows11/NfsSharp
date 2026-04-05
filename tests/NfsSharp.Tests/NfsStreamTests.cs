using NfsSharp.Tests.Mocks;

namespace NfsSharp.Tests;

[TestClass]
public class NfsStreamTests
{
    private static int s_fileCounter;

    private static NfsStream MakeStream(
        MockProtocolClient ops,
        byte[]? initialData = null,
        long? knownLength = null,
        bool readable = true,
        bool writable = true)
    {
        string name = $"stream-{Interlocked.Increment(ref s_fileCounter)}";
        var handle = ops.CreateRootFile(name, initialData);
        long length = knownLength ?? initialData?.Length ?? 0;
        return new NfsStream(ops, handle, length, readable, writable);
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Read_AssemblesBytesCorrectly()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, initialData: [1, 2, 3, 4, 5], knownLength: 5);
        var buf = new byte[5];
        int n = await stream.ReadAsync(buf, 0, 5, CancellationToken.None);
        Assert.AreEqual(5, n);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, buf);
    }

    [TestMethod]
    public async Task Read_AdvancesPosition()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, initialData: [10, 20, 30], knownLength: 3);
        var buf = new byte[2];
        await stream.ReadAsync(buf, 0, 2, CancellationToken.None);
        Assert.AreEqual(2, stream.Position);
    }

    [TestMethod]
    public async Task Write_CallsWriteAsyncOnMockAndAdvancesPosition()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, writable: true);
        var data = new byte[] { 9, 8, 7 };
        await stream.WriteAsync(data, 0, 3, CancellationToken.None);
        Assert.AreEqual(1, ops.WriteCallCount);
        Assert.AreEqual(3, stream.Position);
    }

    [TestMethod]
    public async Task Flush_CallsCommit_WhenUncommittedWritesPending()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, writable: true);
        await stream.WriteAsync([1], 0, 1, CancellationToken.None);
        await stream.FlushAsync(CancellationToken.None);
        Assert.AreEqual(1, ops.CommitCallCount);
    }

    [TestMethod]
    public async Task Flush_DoesNotCallCommit_WhenNoUncommittedWrites()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops);
        await stream.FlushAsync(CancellationToken.None);
        Assert.AreEqual(0, ops.CommitCallCount);
    }

    [TestMethod]
    public void Seek_Begin()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, initialData: [1, 2, 3, 4, 5], knownLength: 5);
        long pos = stream.Seek(3, SeekOrigin.Begin);
        Assert.AreEqual(3, pos);
        Assert.AreEqual(3, stream.Position);
    }

    [TestMethod]
    public void Seek_Current()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, initialData: [1, 2, 3, 4, 5], knownLength: 5);
        stream.Position = 2;
        long pos = stream.Seek(1, SeekOrigin.Current);
        Assert.AreEqual(3, pos);
    }

    [TestMethod]
    public void Seek_End()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, initialData: [1, 2, 3, 4, 5], knownLength: 5);
        long pos = stream.Seek(-2, SeekOrigin.End);
        Assert.AreEqual(3, pos);
    }

    [TestMethod]
    public async Task SetLengthAsync_CallsSetAttr_WithCorrectSize()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, writable: true);
        await stream.SetLengthAsync(42);
        Assert.AreEqual(1, ops.SetAttrCallCount);
        Assert.IsNotNull(ops.LastSetAttr?.Size);
        Assert.AreEqual(42ul, ops.LastSetAttr!.Size!.Value);
    }

    [TestMethod]
    public void CanRead_ReflectsFlag()
    {
        var ops = new MockProtocolClient();
        using var s1 = MakeStream(ops, readable: true, writable: false);
        using var s2 = MakeStream(ops, readable: false, writable: true);
        Assert.IsTrue(s1.CanRead);
        Assert.IsFalse(s2.CanRead);
    }

    [TestMethod]
    public void CanWrite_ReflectsFlag()
    {
        var ops = new MockProtocolClient();
        using var s1 = MakeStream(ops, readable: true, writable: true);
        using var s2 = MakeStream(ops, readable: true, writable: false);
        Assert.IsTrue(s1.CanWrite);
        Assert.IsFalse(s2.CanWrite);
    }

    [TestMethod]
    public void CanSeek_TrueWhileOpen()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops);
        Assert.IsTrue(stream.CanSeek);
    }

    [TestMethod]
    public void Read_OnWriteOnlyStream_ThrowsNotSupportedException()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, readable: false, writable: true);
        var buf = new byte[4];
        Assert.ThrowsException<NotSupportedException>(() => stream.Read(buf, 0, 4));
    }

    [TestMethod]
    public void Write_OnReadOnlyStream_ThrowsNotSupportedException()
    {
        var ops = new MockProtocolClient();
        using var stream = MakeStream(ops, readable: true, writable: false);
        Assert.ThrowsException<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [TestMethod]
    public async Task Dispose_CommitsUnstableWrites()
    {
        var ops = new MockProtocolClient();
        var stream = MakeStream(ops, writable: true);
        await stream.WriteAsync([42], 0, 1, CancellationToken.None);
        stream.Dispose();
        Assert.AreEqual(1, ops.CommitCallCount);
    }

    [TestMethod]
    public void DoubleDispose_IsSafe()
    {
        var ops = new MockProtocolClient();
        var stream = MakeStream(ops);
        stream.Dispose();
        stream.Dispose();
    }

    [TestMethod]
    public void AfterDispose_CanRead_IsFalse()
    {
        var ops = new MockProtocolClient();
        var stream = MakeStream(ops, readable: true, writable: true);
        stream.Dispose();
        Assert.IsFalse(stream.CanRead);
        Assert.IsFalse(stream.CanWrite);
        Assert.IsFalse(stream.CanSeek);
    }
}
