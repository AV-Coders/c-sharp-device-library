using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusInterfaceTest
{
    private readonly CBusInterface _interface;
    private readonly Mock<CommunicationClient> _mockClient = new("foo", "bar", (ushort)1);

    public CBusInterfaceTest()
    {
        _interface = new CBusInterface(_mockClient.Object, true);
    }
    

    [Fact]
    public void GenerateChecksum_AddsAllValues()
    {
        byte[] input = [0x05, 0x38, 0x79, 0x20];  // Turn on Group 32 (0x20

        var method = _interface.GetType()
            .GetMethod("CalculateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_interface, [input]) ?? 0x00);
        Assert.Equal(0x2a, result);
    }

    [Fact]
    public void SendPointToMultipointPayload_SendsTheCommand()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x08]);
        
        _mockClient.Verify(x => x.Send(new byte[]{ 0x5c, 0x05, 0x38, 0x00, 0x01, 0x08, 0xba, 0x0d }));
    }
}