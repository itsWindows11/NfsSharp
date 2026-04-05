using NfsSharp.Protocol;
using NfsSharp.Xdr;

namespace NfsSharp.Tests.Protocol;

[TestClass]
public class NfsFileHandleTests
{
    [TestMethod]
    public void SameBytes_AreEqual()
    {
        var a = new NfsFileHandle([1, 2, 3, 4]);
        var b = new NfsFileHandle([1, 2, 3, 4]);
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void DifferentBytes_AreNotEqual()
    {
        var a = new NfsFileHandle([1, 2, 3, 4]);
        var b = new NfsFileHandle([1, 2, 3, 5]);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void DifferentLengths_AreNotEqual()
    {
        var a = new NfsFileHandle([1, 2, 3]);
        var b = new NfsFileHandle([1, 2, 3, 4]);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void WriteTo_ReadFrom_RoundTrip()
    {
        var original = new NfsFileHandle([0xDE, 0xAD, 0xBE, 0xEF, 0x01]);
        using var ms = new MemoryStream();
        var writer = new XdrWriter(ms);
        original.WriteTo(writer);

        ms.Position = 0;
        var reader = new XdrReader(ms);
        var restored = NfsFileHandle.ReadFrom(reader);

        Assert.AreEqual(original, restored);
    }

    [TestMethod]
    public void ToString_IsHexString()
    {
        var handle = new NfsFileHandle([0xAB, 0xCD]);
        string s = handle.ToString();
        Assert.AreEqual("ABCD", s, ignoreCase: true);
    }

    [TestMethod]
    public void Empty_Handle_IsEqual_ToEmpty()
    {
        var a = new NfsFileHandle(Array.Empty<byte>());
        var b = new NfsFileHandle(Array.Empty<byte>());
        Assert.AreEqual(a, b);
    }
}
