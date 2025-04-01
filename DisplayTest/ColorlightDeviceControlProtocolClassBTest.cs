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
        _display = new ColorlightDeviceControlProtocolClassB(_mockClient.Object, "LED Wall", 1, 2);
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
}