using AVCoders.CommunicationClients;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class LumensLc300Test
{
    private readonly LumensLc300 _recorder;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public LumensLc300Test()
    {
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
    
    [Theory]
    [InlineData(1, 1, new byte[] { 0x55, 0xf0, 0x06, 0x01, 0x73, 0x43, 0x48, 0x31, 0x01, 0x0D })]
    [InlineData(1, 2, new byte[] { 0x55, 0xf0, 0x06, 0x01, 0x73, 0x43, 0x48, 0x31, 0x02, 0x0D })]
    public void SetVideoSourceId_SendsTheCommand(byte channel, byte index, byte[] expectedCommand)
    {
        _recorder.SetVideoSourceId(channel, index);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void SetVideoSourceId_WithAnInvalidChannel_LogsTheChannelAndSendsNothing(int channel)
    {
        var client = TestFactory.CreateTcpClient();
        var logs = LogCapture.Create(() => new LumensLc300("Logged Rec", client.Object), out var recorder);

        recorder.SetVideoSourceId(channel, 1);

        client.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        Assert.Contains(logs.Logs, log => log.Message == $"Channel {channel} is invalid.  It must be between 0 and 4");
    }

    [Fact]
    public void ResponseHandler_HandlesEveryReplyInOneRead()
    {
        Mock<IntHandler> handler = new Mock<IntHandler>();
        _recorder.LayoutChanged += handler.Object;

        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[]
        {
            0x55, 0xF0, 0x05, 0x01, 0x06, 0x53, 0x54, 0x33, 0x0d,
            0x55, 0xF0, 0x05, 0x01, 0x06, 0x4C, 0x4F, 0x0D, 0x0d
        });

        Assert.Equal(TransportState.Recording, _recorder.TransportState);
        handler.Verify(x => x.Invoke(13), Times.Once);
    }

    [Fact]
    public void ResponseHandler_HandlesAReplySplitAcrossReads()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x23, 0x53 });
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x54, 0x33, 0x0d, 0x55, 0xF0, 0x05 });
        Assert.Equal(TransportState.Recording, _recorder.TransportState);

        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x01, 0x06, 0x53, 0x54, 0x34, 0x0d });

        Assert.Equal(TransportState.RecordingPaused, _recorder.TransportState);
    }

    [Theory]
    [InlineData(new byte[] { 0x55, 0xF0, 0x00, 0x0d })]
    [InlineData(new byte[] { 0x23, 0x0d })]
    [InlineData(new byte[] { 0x23, 0x53, 0x54, 0x0d })]
    public void ResponseHandler_IgnoresAShortFrame(byte[] response)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        Assert.Equal(CommunicationState.Unknown, _recorder.CommunicationState);
        Assert.Equal(TransportState.Unknown, _recorder.TransportState);
    }

    [Fact]
    public void ResponseHandler_SkipsBytesBeforeAFrame()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x0d, 0x0a, 0x23, 0x53, 0x54, 0x33, 0x0d });

        Assert.Equal(TransportState.Recording, _recorder.TransportState);
    }

    [Fact]
    public void ResponseHandler_UpdatesTheCommunicationState()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x23, 0x53, 0x54, 0x32, 0x0d });

        Assert.Equal(CommunicationState.Okay, _recorder.CommunicationState);
    }

    [Fact]
    public void ResponseHandler_GivenANak_ReportsAnError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x55, 0xf0, 0x04, 0x01, 0x15, 0x53, 0x54, 0x0d });

        Assert.Equal(CommunicationState.Error, _recorder.CommunicationState);
    }

    [Fact]
    public void ResponseHandler_GivenABareNak_ReportsAnError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x15, 0x0d });

        Assert.Equal(CommunicationState.Error, _recorder.CommunicationState);
    }
}