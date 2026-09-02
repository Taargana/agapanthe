using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>MP-0b W3: <see cref="UniverseId"/> in isolation — its own equality, <c>None</c>, and the hex
/// <c>ToString</c>/<c>Parse</c> round-trip a host uses to put one in a config file.</summary>
public sealed class UniverseIdTests
{
    [Fact]
    public void None_IsAllZero()
    {
        Assert.Equal(new UniverseId(0, 0), UniverseId.None);
    }

    [Fact]
    public void Equality_ComparesBothHalves()
    {
        Assert.Equal(new UniverseId(1, 2), new UniverseId(1, 2));
        Assert.NotEqual(new UniverseId(1, 2), new UniverseId(1, 3));
        Assert.NotEqual(new UniverseId(1, 2), new UniverseId(9, 2));
    }

    [Fact]
    public void ToString_Is32LowercaseHexDigits()
    {
        var id = new UniverseId(0x0123456789ABCDEF, 0xFEDCBA9876543210);
        Assert.Equal("0123456789abcdeffedcba9876543210", id.ToString());
    }

    [Fact]
    public void Parse_RoundTripsToString()
    {
        var id = new UniverseId(0x1122334455667788, 0x99AABBCCDDEEFF00);
        Assert.Equal(id, UniverseId.Parse(id.ToString()));
    }

    [Fact]
    public void Parse_RejectsWrongLength()
    {
        Assert.Throws<FormatException>(() => UniverseId.Parse("00"));
    }
}
