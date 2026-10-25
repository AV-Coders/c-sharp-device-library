using System.Diagnostics;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class ExtronSmp351Test
{
    private readonly ExtronSmp351 _recorder;
    private readonly Mock<SshClient> _mockClient = TestFactory.CreateSshClient();
    private readonly Mock<TransportStateHandler> _recordStateHandler = new();
    private readonly Mock<StringHandler> _timestampHandler = new ();
    private readonly Mock<ConnectionStateHandler> _mockFrontUsbConnectionStateHandler = new();
    private readonly Mock<ConnectionStateHandler> _mockRearUsbConnectionStateHandler = new();
    public const string EscapeHeader = "\x1b";
    private const string VerboseMode = $"{EscapeHeader}3CV\r";
    private static readonly TimeSpan VerboseModeDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    public ExtronSmp351Test()
    {
        _recorder = new ExtronSmp351(_mockClient.Object, 1024000, 512000, "test");
        _recorder.TransportStateHandlers += _recordStateHandler.Object;
        _recorder.TimestampHandlers += _timestampHandler.Object;
        _recorder.FrontUsbConnectionStateHandlers += _mockFrontUsbConnectionStateHandler.Object;
        _recorder.RearUsbConnectionStateHandlers += _mockRearUsbConnectionStateHandler.Object;
    }

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private int VerboseModeSends() => _mockClient.Invocations.Count(invocation =>
        invocation.Method.Name == nameof(CommunicationClient.Send) && VerboseMode.Equals(invocation.Arguments[0]));

    [Fact]
    public async Task Module_SetsVerboseMode3OnConnection()
    {
        var sent = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        _mockClient.Setup(x => x.Send(VerboseMode)).Callback(() => sent.TrySetResult(stopwatch.Elapsed));

        SetConnection(ConnectionState.Connected);

        var sentAfter = await sent.Task.WaitAsync(WaitTimeout);
        Assert.True(sentAfter >= VerboseModeDelay - ClockTolerance, $"3CV went out {sentAfter} after connecting");
        Assert.Equal(1, VerboseModeSends());
    }

    [Fact]
    public async Task HandleConnectionState_ReturnsBeforeSettingVerboseMode()
    {
        var stopwatch = Stopwatch.StartNew();
        SetConnection(ConnectionState.Connected);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < VerboseModeDelay, "The connection handler blocked the client");
        Assert.Equal(0, VerboseModeSends());
        await Wait.UntilAsync(() => VerboseModeSends() == 1, "3CV was never sent");
    }

    [Fact]
    public async Task HandleConnectionState_DisconnectInsideTheDelay_SendsNothing()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);

        await Task.Delay(VerboseModeDelay * 2);

        Assert.Equal(0, VerboseModeSends());
    }

    [Fact]
    public async Task HandleConnectionState_ReconnectInsideTheDelay_SetsVerboseModeOnce()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);

        await Wait.UntilAsync(() => VerboseModeSends() > 0, "3CV was never sent");
        await Task.Delay(VerboseModeDelay);

        Assert.Equal(1, VerboseModeSends());
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Error)]
    public void Module_DoesNotSetVerboseModeWhenNotConnected(ConnectionState state)
    {
        _mockClient.Object.ConnectionStateHandlers!.Invoke(state);

        _mockClient.Verify(x => x.Send($"{EscapeHeader}3CV\r"), Times.Never);
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

    [Fact]
    public void AddMarker_SendsTheCommand()
    {
        _recorder.AddMarker();
        
        _mockClient.Verify(x => x.Send($"{EscapeHeader}BRCDR\r"));
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

    [Fact]
    public void HandleResponse_ClearsTimestampWhenStopped()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("RcdrY0\r\n");
        
        _timestampHandler.Verify(x => x.Invoke(string.Empty));
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
        Assert.Equal(expectedState, _recorder.RearUsbConnectionState);
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
        Assert.Equal(expectedState, _recorder.FrontUsbConnectionState);
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("RcdrY0\r\n");

        Assert.Equal(CommunicationState.Okay, _recorder.CommunicationState);
    }

    [Fact]
    public void HandleResponse_IgnoresUnknownResponses()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("E22\r\n");

        Assert.NotEqual(CommunicationState.Okay, _recorder.CommunicationState);
    }

    [Theory]
    [InlineData("Inf*<ChA1*ChB3>*<recording>*<internal*N/A>\r\n", "Inf*<ChA1*ChB3>*<recording>*<internal*N/A>")]
    [InlineData("RcdrYx\r\n", "RcdrYx")]
    [InlineData("Inf57*usbrear/232*93MB*238557MB\r\n", "usbrear/232*93MB*238557MB")]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:49\r\n", "238464MB*86:49")]
    [InlineData("Inf57*usbrear/232*93MB*238557MB*238464MB*86:4x:20*0\r\n", "86:4x:20*0")]
    public void HandleResponse_LogsAMalformedResponseAndChangesNoState(string response, string logged)
    {
        var client = TestFactory.CreateSshClient();
        var logs = LogCapture.Create(() => new ExtronSmp351(client.Object, 1024000, 512000, "malformed"),
            out var recorder);
        var rearUsbHandler = new Mock<ConnectionStateHandler>();
        recorder.RearUsbConnectionStateHandlers += rearUsbHandler.Object;

        client.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(TransportState.Unknown, recorder.TransportState);
        Assert.Equal(ConnectionState.Unknown, recorder.RearUsbConnectionState);
        rearUsbHandler.Verify(x => x.Invoke(It.IsAny<ConnectionState>()), Times.Never);
        Assert.Contains(logs.Logs, log => log.Message.Contains(logged));
    }
}