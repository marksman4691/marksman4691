using CheckScanBridge.Capture;

namespace CheckScanBridge.Tests;

public class MicrParserTests
{
    [Theory]
    [InlineData("021000021", true)]
    [InlineData("011000015", true)]
    [InlineData("111000025", true)]
    [InlineData("021000022", false)]
    [InlineData("02100002", false)]
    [InlineData("02100002?", false)]
    public void RoutingChecksum(string routing, bool valid) =>
        Assert.Equal(valid, MicrParser.IsValidRouting(routing));

    [Fact]
    public void PersonalCheck()
    {
        var m = MicrParser.Parse("T021000021T 1234567890U 0123");
        Assert.Equal("021000021", m.RoutingNumber);
        Assert.True(m.RoutingValid);
        Assert.Null(m.AuxiliaryOnUs);
        Assert.Equal("1234567890/0123", m.OnUs);
        Assert.Equal("1234567890", m.AccountNumber);
        Assert.Equal("0123", m.SerialNumber);
        Assert.Null(m.AmountCents);
        Assert.False(m.HasUnreadable);
    }

    [Fact]
    public void BusinessCheckWithAuxOnUsAndEpc()
    {
        var m = MicrParser.Parse("U004567U 4 T011000015T 98765432U");
        Assert.Equal("004567", m.AuxiliaryOnUs);
        Assert.Equal("4", m.Epc);
        Assert.Equal("011000015", m.RoutingNumber);
        Assert.Equal("98765432/", m.OnUs);
        Assert.Equal("98765432", m.AccountNumber);
        Assert.Equal("004567", m.SerialNumber);
    }

    [Fact]
    public void EncodedAmount()
    {
        var m = MicrParser.Parse("T111000025T 55500012U 0457 A0000012500A");
        Assert.Equal(12500, m.AmountCents);
        Assert.Equal("55500012/0457", m.OnUs);
    }

    [Fact]
    public void UnreadableCharacters()
    {
        var m = MicrParser.Parse("T0210?0021T 1234567890U");
        Assert.True(m.HasUnreadable);
        Assert.False(m.RoutingValid);
    }

    [Fact]
    public void DashInOnUs()
    {
        var m = MicrParser.Parse("T021000021T 123D456U");
        Assert.Equal("123-456/", m.OnUs);
        Assert.Equal("123456", m.AccountNumber);
    }
}
