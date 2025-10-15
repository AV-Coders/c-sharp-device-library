using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusInterfaceTest
{
    private readonly CBusSerialInterface _interface;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public CBusInterfaceTest()
    {
        _interface = new CBusSerialInterface(_mockClient.Object, true);
    }
    

    [Fact]
    public void GenerateChecksum_AddsAllValues()
    {
        byte[] input = [0x05, 0x38, 0x79, 0x20];  // Turn on Group 32 (0x20

        var method = _interface.GetType()
            .GetMethod("CalculateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_interface, [input]) ?? 0x00);
        Assert.Equal(42, result);
    }

    [Fact]
    public void SendPointToMultipointPayload_SendsTheCommand()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x08], false);
        
        _mockClient.Verify(x => x.Send("\\0538000108BA\r"));
    }
}