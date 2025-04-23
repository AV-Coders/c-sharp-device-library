using AVCoders.CommunicationClients;
using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class ColorlightDeviceControlProtocolClassBTest
{
    
    public abstract class StubbedClient : IpComms
    {
        protected StubbedClient(string host, ushort port) : base(host, port, "StubbedClient"){}

        public override void Send(string message){}

        public override void Send(byte[] bytes){}

        public override void SetPort(ushort port){}

        public override void SetHost(string host){}
        public new ConnectionState GetConnectionState() => ConnectionState.Connected;
    }
    
    private readonly Mock<StubbedClient> _mockClient = new("foo", (ushort)1);
    private readonly ColorlightDeviceControlProtocolClassB _display;

    public ColorlightDeviceControlProtocolClassBTest()
    {
        _display = new ColorlightDeviceControlProtocolClassB(_mockClient.Object, "LED Wall");
    }

    [Fact]
    public void Module_RespondsToHeartbeat()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00]);

        _mockClient.Verify(x => x.Send(new byte[] { 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void Module_RespondsToHeartbeatsChainedTogether()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00]);

        _mockClient.Verify(x => x.Send(new byte[] { 0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void PowerOn_DisablesBlackout()
    {
        _display.PowerOn();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void PowerOff_EnablesBlackout()
    {
        _display.PowerOff();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }));
    }

    [Theory]
    [InlineData(0, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(50, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x88, 0x13 })]
    [InlineData(100, new byte[] { 0x50, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x27 })]
    public void SetBrightness_SendsTheCommand(uint brightness, byte[] expectedCommand)
    {
        _display.SetBrightness(brightness);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void SetBrightness_IgnoresValuesOver100()
    {
        
        _display.SetBrightness(101);
        
        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }
}