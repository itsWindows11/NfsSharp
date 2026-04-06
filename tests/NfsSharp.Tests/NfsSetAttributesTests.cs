using NfsSharp.Protocol;
using NfsSharp.Tests.Mocks;

namespace NfsSharp.Tests;

[TestClass]
public class NfsSetAttributesTests
{
    // ── AccessTime ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SetAttrAsync_SetsAccessTime_WhenProvided()
    {
        var ops = new MockProtocolClient();
        var handle = ops.CreateRootFile("test-atime.txt");
        var target = new DateTimeOffset(2020, 6, 15, 10, 30, 0, TimeSpan.Zero);

        await ops.SetAttrAsync(handle, new NfsSetAttributes { AccessTime = target }, default);

        var attrs = await ops.GetAttrAsync(handle, default);
        Assert.AreEqual(target, attrs.AccessTime);
    }

    [TestMethod]
    public async Task SetAttrAsync_SetsModifyTime_WhenProvided()
    {
        var ops = new MockProtocolClient();
        var handle = ops.CreateRootFile("test-mtime.txt");
        var target = new DateTimeOffset(2021, 3, 1, 8, 0, 0, TimeSpan.Zero);

        await ops.SetAttrAsync(handle, new NfsSetAttributes { ModifyTime = target }, default);

        var attrs = await ops.GetAttrAsync(handle, default);
        Assert.AreEqual(target, attrs.ModifyTime);
    }

    [TestMethod]
    public async Task SetAttrAsync_SetsBothTimes_WhenBothProvided()
    {
        var ops = new MockProtocolClient();
        var handle = ops.CreateRootFile("test-both-times.txt");
        var atime = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mtime = new DateTimeOffset(2022, 12, 31, 23, 59, 59, TimeSpan.Zero);

        await ops.SetAttrAsync(handle, new NfsSetAttributes { AccessTime = atime, ModifyTime = mtime }, default);

        var attrs = await ops.GetAttrAsync(handle, default);
        Assert.AreEqual(atime, attrs.AccessTime);
        Assert.AreEqual(mtime, attrs.ModifyTime);
    }

    [TestMethod]
    public async Task SetAttrAsync_DoesNotChangeAccessTime_WhenNotProvided()
    {
        var ops = new MockProtocolClient();
        var handle = ops.CreateRootFile("test-no-atime.txt");

        // Record the access time after creation.
        var attrsBefore = await ops.GetAttrAsync(handle, default);
        var originalAtime = attrsBefore.AccessTime;

        // SetAttr without touching AccessTime.
        await ops.SetAttrAsync(handle, new NfsSetAttributes { Mode = 0b110_110_100u }, default);

        var attrsAfter = await ops.GetAttrAsync(handle, default);
        Assert.AreEqual(originalAtime, attrsAfter.AccessTime);
    }

    [TestMethod]
    public async Task SetAttrAsync_DoesNotChangeModifyTime_WhenNotProvided()
    {
        var ops = new MockProtocolClient();
        var handle = ops.CreateRootFile("test-no-mtime.txt");

        var attrsBefore = await ops.GetAttrAsync(handle, default);
        var originalMtime = attrsBefore.ModifyTime;

        // SetAttr without touching ModifyTime.
        await ops.SetAttrAsync(handle, new NfsSetAttributes { Mode = 0b110_110_100u }, default);

        var attrsAfter = await ops.GetAttrAsync(handle, default);
        Assert.AreEqual(originalMtime, attrsAfter.ModifyTime);
    }

    // ── NfsSetAttributes defaults ─────────────────────────────────────────

    [TestMethod]
    public void NfsSetAttributes_DefaultsToNullTimestamps()
    {
        var attrs = new NfsSetAttributes();
        Assert.IsNull(attrs.AccessTime);
        Assert.IsNull(attrs.ModifyTime);
    }

    [TestMethod]
    public void NfsSetAttributes_CanSetAndReadTimestamps()
    {
        var atime = DateTimeOffset.UtcNow;
        var mtime = atime.AddHours(-1);
        var attrs = new NfsSetAttributes { AccessTime = atime, ModifyTime = mtime };
        Assert.AreEqual(atime, attrs.AccessTime);
        Assert.AreEqual(mtime, attrs.ModifyTime);
    }
}
