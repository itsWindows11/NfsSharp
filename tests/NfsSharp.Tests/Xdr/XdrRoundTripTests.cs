using NfsSharp.Xdr;

namespace NfsSharp.Tests.Xdr;

[TestClass]
public class XdrRoundTripTests
{
    private static (XdrWriter writer, MemoryStream ms) MakeWriter()
    {
        var ms = new MemoryStream();
        return (new XdrWriter(ms), ms);
    }

    private static XdrReader ReaderFrom(MemoryStream ms)
    {
        ms.Position = 0;
        return new XdrReader(ms);
    }

    [TestMethod]
    public void Int32_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteInt32(42);
        w.WriteInt32(-1);
        w.WriteInt32(int.MinValue);
        var r = ReaderFrom(ms);
        Assert.AreEqual(42, r.ReadInt32());
        Assert.AreEqual(-1, r.ReadInt32());
        Assert.AreEqual(int.MinValue, r.ReadInt32());
    }

    [TestMethod]
    public void UInt32_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteUInt32(0);
        w.WriteUInt32(uint.MaxValue);
        var r = ReaderFrom(ms);
        Assert.AreEqual(0u, r.ReadUInt32());
        Assert.AreEqual(uint.MaxValue, r.ReadUInt32());
    }

    [TestMethod]
    public void Int64_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteInt64(long.MinValue);
        w.WriteInt64(long.MaxValue);
        var r = ReaderFrom(ms);
        Assert.AreEqual(long.MinValue, r.ReadInt64());
        Assert.AreEqual(long.MaxValue, r.ReadInt64());
    }

    [TestMethod]
    public void UInt64_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteUInt64(0);
        w.WriteUInt64(ulong.MaxValue);
        var r = ReaderFrom(ms);
        Assert.AreEqual(0ul, r.ReadUInt64());
        Assert.AreEqual(ulong.MaxValue, r.ReadUInt64());
    }

    [TestMethod]
    public void Bool_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteBool(true);
        w.WriteBool(false);
        var r = ReaderFrom(ms);
        Assert.IsTrue(r.ReadBool());
        Assert.IsFalse(r.ReadBool());
    }

    [TestMethod]
    public void Float_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteFloat(3.14f);
        w.WriteFloat(float.NegativeInfinity);
        w.WriteFloat(0f);
        var r = ReaderFrom(ms);
        Assert.AreEqual(3.14f, r.ReadFloat());
        Assert.AreEqual(float.NegativeInfinity, r.ReadFloat());
        Assert.AreEqual(0f, r.ReadFloat());
    }

    [TestMethod]
    public void Double_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteDouble(Math.PI);
        w.WriteDouble(double.NaN);
        var r = ReaderFrom(ms);
        Assert.AreEqual(Math.PI, r.ReadDouble());
        Assert.IsTrue(double.IsNaN(r.ReadDouble()));
    }

    [TestMethod]
    public void Enum_RoundTrip()
    {
        var (w, ms) = MakeWriter();
        w.WriteEnum(7);
        var r = ReaderFrom(ms);
        Assert.AreEqual(7, r.ReadEnum());
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(8)]
    public void FixedOpaque_RoundTrip(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i + 1);

        var (w, ms) = MakeWriter();
        w.WriteFixedOpaque(data, length);

        int expectedSize = ((length + 3) / 4) * 4;
        Assert.AreEqual(expectedSize, ms.Length);

        var r = ReaderFrom(ms);
        var result = r.ReadFixedOpaque(length);
        CollectionAssert.AreEqual(data, result);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void VarOpaque_RoundTrip(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i + 10);

        var (w, ms) = MakeWriter();
        w.WriteVarOpaque(data);

        var r = ReaderFrom(ms);
        var result = r.ReadVarOpaque();
        CollectionAssert.AreEqual(data, result);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("hello")]
    [DataRow("hello world this is a test")]
    [DataRow("Unicode: \u4e2d\u6587")]
    public void String_RoundTrip(string value)
    {
        var (w, ms) = MakeWriter();
        w.WriteString(value);
        var r = ReaderFrom(ms);
        Assert.AreEqual(value, r.ReadString());
    }

    [TestMethod]
    public void UInt32Array_RoundTrip()
    {
        var arr = new uint[] { 1, 2, 3, 100, uint.MaxValue };
        var (w, ms) = MakeWriter();
        w.WriteUInt32Array(arr);
        var r = ReaderFrom(ms);
        CollectionAssert.AreEqual(arr, r.ReadUInt32Array());
    }

    [TestMethod]
    public void UInt32Array_Empty_RoundTrip()
    {
        var arr = Array.Empty<uint>();
        var (w, ms) = MakeWriter();
        w.WriteUInt32Array(arr);
        var r = ReaderFrom(ms);
        CollectionAssert.AreEqual(arr, r.ReadUInt32Array());
    }
}
