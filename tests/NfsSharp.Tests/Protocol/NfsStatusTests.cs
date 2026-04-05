using NfsSharp.Protocol;

namespace NfsSharp.Tests.Protocol
{
    public class NfsStatusTests
    {
        [Fact]
        public void NfsException_StoresStatus()
        {
            var ex = new NfsException(NfsStatus.NoEnt);
            Assert.Equal(NfsStatus.NoEnt, ex.Status);
        }

        [Fact]
        public void NfsException_MessageContainsStatusName()
        {
            var ex = new NfsException(NfsStatus.Acces);
            Assert.Contains("Acces", ex.Message);
        }

        [Fact]
        public void NfsException_WithCustomMessage_ContainsBothParts()
        {
            var ex = new NfsException(NfsStatus.Io, "disk failure");
            Assert.Contains("disk failure", ex.Message);
            Assert.Contains("Io", ex.Message);
        }

        [Fact]
        public void NfsException_WithInner_StoresInner()
        {
            var inner = new System.Exception("inner");
            var ex = new NfsException(NfsStatus.ServerFault, "wrapped", inner);
            Assert.Same(inner, ex.InnerException);
            Assert.Equal(NfsStatus.ServerFault, ex.Status);
        }
    }
}
