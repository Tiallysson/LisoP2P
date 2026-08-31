using System.Net;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class BroadcastAddressCalculatorTests
{
    [Theory]
    [InlineData("192.168.1.50", "255.255.255.0", "192.168.1.255")]
    [InlineData("26.5.10.20", "255.0.0.0", "26.255.255.255")]
    public void Calculate_ReturnsExpectedBroadcastAddress(string ip, string mask, string expected)
    {
        var address = IPAddress.Parse(ip);
        var subnetMask = IPAddress.Parse(mask);

        var broadcast = BroadcastAddressCalculator.Calculate(address, subnetMask);

        Assert.Equal(IPAddress.Parse(expected), broadcast);
    }
}
