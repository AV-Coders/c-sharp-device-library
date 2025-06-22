using AVCoders.CommunicationClients;
using AVCoders.Core;

namespace AVCoders.MediaPlayer.Tests;

public class LumensLc300Test
{
    private readonly LumensLc300 _recorder;
    private readonly Mock<AvCodersTcpClient> _mockClient;

    public LumensLc300Test()
    {
        _mockClient = new Mock<AvCodersTcpClient>("Foo", (ushort)1, "A");
        _recorder = new LumensLc300("Main Rec", _mockClient.Object);
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        _recorder.PowerOn();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x55, 0xf0, 0x05, 0x01, 0x73, 0x53, 0x52, 0x32, 0x0D }), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        _recorder.PowerOff();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x55, 0xf0, 0x05, 0x01, 0x73, 0x53, 0x52, 0x31, 0x0D }), Times.Once);
    }
    
    [Fact]
    public void Record_SendsTheCommand()
    {
        _recorder.Record();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x55, 0xf0, 0x04, 0x01, 0x73, 0x52, 0x43, 0x0D }), Times.Once);
    }
    
    [Fact]
    public void Pause_SendsTheCommand()
    {
        _recorder.Pause();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x55, 0xf0, 0x04, 0x01, 0x73, 0x50, 0x53, 0x0D }), Times.Once);
    }
    
    [Fact]
    public void Stop_SendsTheCommand()
    {
        _recorder.Stop();
        
        _mockClient.Verify(x => x.Send(new byte[] { 0x55, 0xf0, 0x04, 0x01, 0x73, 0x53, 0x50, 0x0D }), Times.Once);
    }

    [Theory]
    [InlineData(new byte[] { 0x55, 0xF0, 0x05, 0x01, 0x06, 0x53, 0x54, 0x32, 0x0d }, TransportState.Stopped)]
    [InlineData(new byte[] { 0x55, 0xF0, 0x05, 0x01, 0x06, 0x53, 0x54, 0x33, 0x0d }, TransportState.Recording)]
    [InlineData(new byte[] { 0x55, 0xF0, 0x05, 0x01, 0x06, 0x53, 0x54, 0x34, 0x0d }, TransportState.RecordingPaused)]
    public void ResponseHandler_UpdatesTheRecordState(byte[] response, TransportState expectedState)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        
        Assert.Equal(expectedState, _recorder.TransportState);
    }

    [Theory]
    [InlineData(new byte[] { 0x55, 0xF0, 0x05, 0x01, 0x06, 0x4C, 0x4F, 0x1, 0x0d }, 1)]
    [InlineData(new byte[] { 0x55, 0xF0, 0x05, 0x01, 0x06, 0x4C, 0x4F, 0x10, 0x0d }, 16)]
    [InlineData(new byte[] { 0x55, 0xF0, 0x05, 0x01, 0x06, 0x4C, 0x4F, 0x0B, 0x0d }, 11)]
    public void ResponseHandler_UpdatesTheActiveLayout(byte[] response, int expectedLayout)
    {
        Mock<IntHandler> handler = new Mock<IntHandler>();
        _recorder.LayoutChanged += handler.Object;
        
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        handler.Verify(x => x.Invoke(expectedLayout), Times.Once);
    }

    [Theory]
    [InlineData(1, new byte[] { 0x55, 0xf0, 0x05, 0x01, 0x73, 0x4C, 0x4f, 0x01, 0x0D })]
    [InlineData(18, new byte[] { 0x55, 0xf0, 0x05, 0x01, 0x73, 0x4C, 0x4f, 0x12, 0x0D })]
    public void SetLayout_SendsTheCommand(uint layoutIndex, byte[] expectedCommand)
    {
        _recorder.SetLayout(layoutIndex);
        
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
    
    [Theory]
    [InlineData(new byte[] { 0x23, 0x53, 0x54, 0x32, 0x0d }, TransportState.Stopped)]
    [InlineData(new byte[] { 0x23, 0x53, 0x54, 0x33, 0x0d }, TransportState.Recording)]
    [InlineData(new byte[] { 0x23, 0x53, 0x54, 0x34, 0x0d }, TransportState.RecordingPaused)]
    public void ResponseHandler_HandlesTransportStateEvents(byte[] response, TransportState expectedState)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        
        Assert.Equal(expectedState, _recorder.TransportState);
    }
    
}