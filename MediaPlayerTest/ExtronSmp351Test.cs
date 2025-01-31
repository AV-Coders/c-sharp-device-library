using AVCoders.Core;

namespace AVCoders.MediaPlayer.Tests;

public class ExtronSmp351Test
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
    
    private readonly ExtronSmp351 _recorder;
    private readonly Mock<StubbedClient> _mockClient = new("foo", (ushort)1);
    private readonly Mock<TransportStateHandler> _recordStateHandler = new();
    private readonly Mock<StringHandler> _timestampHandler = new ();
    private readonly Mock<ConnectionStateHandler> _mockFrontUsbConnectionStateHandler = new();
    private readonly Mock<ConnectionStateHandler> _mockRearUsbConnectionStateHandler = new();
    public const string EscapeHeader = "\x1b";

    public ExtronSmp351Test()
    {
        _recorder = new ExtronSmp351(_mockClient.Object, 1024000, 512000);
        _recorder.TransportStateHandlers += _recordStateHandler.Object;
        _recorder.TimestampHandlers += _timestampHandler.Object;
        _recorder.FrontUsbConnectionStateHandlers += _mockFrontUsbConnectionStateHandler.Object;
        _recorder.RearUsbConnectionStateHandlers += _mockRearUsbConnectionStateHandler.Object;
    }

    [Fact]
    public void Module_SetsVerboseMode3OnConnection()
    {
        // This is required so all responses have a prefix.
        // Changing this will result in the device not returning the expected strings below 
        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        
        _mockClient.Verify(x  => x.Send($"{EscapeHeader}3CV\r"));
    }

    [Fact]
    public void Record_SendsTheCommand()
    {
        _recorder.Record();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}Y1RCDR\r"));
    }

    [Fact]
    public void Pause_SendsTheCommand()
    {
        _recorder.Pause();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}Y2RCDR\r"));
    }

    [Fact]
    public void Stop_SendsTheCommand()
    {
        _recorder.Stop();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}Y0RCDR\r"));
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>*<41:28:03*00:00:00>\r\n", TransportState.Stopped)]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*N/A>*<116606580*N/A>*<00:00:06>*<824:27:00*00:00:00>\r\n", TransportState.Recording)]
    [InlineData("Inf*<ChA1*ChB3>*<paused>*<internal*N/A>*<116606376*N/A>*<00:00:13>*<824:26:40*00:00:00>\n\r\n", TransportState.RecordingPaused)]
    [InlineData("RcdrY1\r\n", TransportState.Recording)]
    [InlineData("RcdrY0\r\n", TransportState.Stopped)]
    [InlineData("RcdrY2\r\n", TransportState.RecordingPaused)]
    [InlineData("RecStart05\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart04\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart03\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart02\r\n", TransportState.PreparingToRecord)]
    [InlineData("RecStart01\r\n", TransportState.PreparingToRecord)]
    public void HandleResponse_ReportsRecordingState(string response, TransportState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _recordStateHandler.Verify(x => x.Invoke(expectedState));
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*auto>*<116606760*N/A>*<00:00:06>*<41:28:03*00:00:00>\r\n", "00:00:06")]
    [InlineData("Inf*<ChA1*ChB3>*<paused>*<internal*N/A>*<116606580*N/A>*<09:10:06>*<824:27:00*00:00:00>\r\n", "09:10:06")]
    public void HandleResponse_ReportsRecordTime(string response, string expectedTime)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _timestampHandler.Verify(x => x.Invoke(expectedTime));
    }
    
    [Fact]
    public void HandleResponse_DoesNotReportRecordTimeWhenStopped()
    {
        var response = "Inf*<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:06>*<41:28:03*00:00:00>\r\n";
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _timestampHandler.Verify(x => x.Invoke(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("RcdrN0*usbrear\r\n", ConnectionState.Disconnected)]
    [InlineData("RcdrN1*usbrear\r\n", ConnectionState.Connected)]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:49:20*0\r\n", ConnectionState.Connected)]
    [InlineData("Inf57*N/A\r\n", ConnectionState.Disconnected)]
    public void HandleResponse_HandlesRearUsbStatus(string response, ConnectionState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _mockRearUsbConnectionStateHandler.Verify(x => x.Invoke(expectedState));
    }

    [Theory]
    [InlineData("RcdrN0*usbfront\r\n", ConnectionState.Disconnected)]
    [InlineData("RcdrN1*usbfront\r\n", ConnectionState.Connected)]
    [InlineData("Inf56*usbfront/232*93MB*238557MB*238464MB*86:49:20*0\r\n", ConnectionState.Connected)]
    [InlineData("Inf56*N/A\r\n", ConnectionState.Disconnected)]
    public void HandleResponse_HandlesFrontUsbStatus(string response, ConnectionState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _mockFrontUsbConnectionStateHandler.Verify(x => x.Invoke(expectedState));
    }
}