using NfsSharp.Protocol;

namespace NfsSharp.Tests.Protocol;

[TestClass]
public class NfsStatusTests
{
    [TestMethod]
    public void NfsException_StoresStatus()
    {
        var ex = new NfsException(NfsStatus.NoEnt);
        Assert.AreEqual(NfsStatus.NoEnt, ex.Status);
    }

    [TestMethod]
    public void NfsException_MessageContainsStatusName()
    {
        var ex = new NfsException(NfsStatus.Acces);
        StringAssert.Contains(ex.Message, "Acces");
    }

    [TestMethod]
    public void NfsException_WithCustomMessage_ContainsBothParts()
    {
        var ex = new NfsException(NfsStatus.Io, "disk failure");
        StringAssert.Contains(ex.Message, "disk failure");
        StringAssert.Contains(ex.Message, "Io");
    }

    [TestMethod]
    public void NfsException_WithInner_StoresInner()
    {
        var inner = new System.Exception("inner");
        var ex = new NfsException(NfsStatus.ServerFault, "wrapped", inner);
        Assert.AreSame(inner, ex.InnerException);
        Assert.AreEqual(NfsStatus.ServerFault, ex.Status);
    }
}
