using System.Reflection;

namespace AVCoders.CommunicationClients.Tests;

/// <summary>
/// Wake() sends to the network broadcast address, which cannot be observed on
/// loopback, so these tests verify the packet construction logic via reflection.
/// </summary>
public class AvCodersWakeOnLanTest
{
    private static byte[] ParseMacAddress(string mac)
    {
        var method = typeof(AvCodersWakeOnLan)
            .GetMethod("ParseMacAddress", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (byte[])method.Invoke(null, new object[] { mac })!;
    }

    private static byte[] BuildMagicPacket(byte[] macBytes)
    {
        var method = typeof(AvCodersWakeOnLan)
            .GetMethod("BuildMagicPacket", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            return (byte[])method.Invoke(new AvCodersWakeOnLan(), new object[] { macBytes })!;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    public void ParseMacAddress_SupportsColonAndDashSeparators(string mac)
    {
        var expected = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        Assert.Equal(expected, ParseMacAddress(mac));
    }

    [Fact]
    public void BuildMagicPacket_Is6XFF_Then16RepetitionsOfTheMac()
    {
        var mac = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var packet = BuildMagicPacket(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var repetition = 0; repetition < 16; repetition++)
            Assert.Equal(mac, packet.Skip(6 + repetition * 6).Take(6).ToArray());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void BuildMagicPacket_RejectsMacsThatAreNot6Bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => BuildMagicPacket(new byte[length]));
    }
}
