using AVCoders.Core;

namespace AVCoders.MediaPlayer.Tests;

public class ExtronSmp351Test
{
    public abstract class StubbedClient : IpComms
    {
        protected StubbedClient(string host, ushort port) : base(host, port){}

        public override void Send(string message){}

        public override void Send(byte[] bytes){}

        public override void SetPort(ushort port){}

        public override void SetHost(string host){}
        public new ConnectionState GetConnectionState() => ConnectionState.Connected;
    }
    
    private ExtronSmp351 _recorder;
    private readonly Mock<StubbedClient> _mockClient = new("foo", (ushort)1);
    private readonly Mock<RecordStateHandler> _recordStateHandler = new();
    private readonly Mock<TimestampHandler> _timestampHandler = new ();

    public ExtronSmp351Test()
    {
        _recorder = new ExtronSmp351(_mockClient.Object, 1024000, 512000);
        _recorder.RecordStateHandlers += _recordStateHandler.Object;
        _recorder.TimestampHandlers += _timestampHandler.Object;
    }

    [Theory]
    [InlineData("<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:00>*<41:28:03*00:00:00>\r\n", RecordState.Stopped)]
    [InlineData("<ChA1*ChB3>*<recording>*<internal*N/A>*<116606580*N/A>*<00:00:06>*<824:27:00*00:00:00>\r\n", RecordState.Recording)]
    [InlineData("<ChA1*ChB3>*<paused>*<internal*N/A>*<116606376*N/A>*<00:00:13>*<824:26:40*00:00:00>\n\r\n", RecordState.RecordingPaused)]
    public void HandleResponse_ReportsRecordingState(string response, RecordState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _recordStateHandler.Verify(x => x.Invoke(expectedState));
    }

    [Theory]
    [InlineData("<ChA1*ChB3>*<recording>*<internal*auto>*<116606760*N/A>*<00:00:06>*<41:28:03*00:00:00>\r\n", "00:00:06")]
    [InlineData("<ChA1*ChB3>*<paused>*<internal*N/A>*<116606580*N/A>*<09:10:06>*<824:27:00*00:00:00>\r\n", "09:10:06")]
    public void HandleResponse_ReportsRecordTime(string response, string expectedTime)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _timestampHandler.Verify(x => x.Invoke(expectedTime));
    }
    
    [Fact]
    public void HandleResponse_DoesNotReportRecordTimeWhenStopped()
    {
        var response = "<ChA1*ChB3>*<stopped>*<internal*auto>*<116606760*N/A>*<00:00:06>*<41:28:03*00:00:00>\r\n";
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        _timestampHandler.Verify(x => x.Invoke(It.IsAny<string>()), Times.Never);
    }
}