using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

[Collection("LoggerFactory")]
public class ExtronSwTest : IDisposable
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private readonly ExtronSw _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronSwTest()
    {
        _switcher = new ExtronSw(_mockClient.Object, "Test switch");
        Respond("Inf01*SW4 HD 4K PLUS Series");
    }

    public void Dispose() => _switcher.Dispose();

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private Task Poll() => (Task)typeof(ExtronSw)
        .GetMethod("Poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private static (ExtronSw Switcher, ScopeCapturingLoggerFactory Logs) CreateWithCapturedLogs(Mock<CommunicationClient> client)
    {
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (new ExtronSw(client.Object, "Logged switch"), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private static void HoldFirstInputChange(SyncStatus input, ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        var changes = 0;
        input.InputStatusChangedHandlers += (_, _, _) =>
        {
            if (Interlocked.Increment(ref changes) != 1)
                return;
            entered.Set();
            release.Wait(HoldTimeout);
        };
    }

    [Fact]
    public void ConnectAndReconnect_EnableVerboseAndQueryStatus()
    {
        SetConnection(ConnectionState.Connected);
        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);

        foreach (var command in new[] { "\u001b3CV\r", "1I", "\u001bLS\r", "\u001bIHDCP\r", "\u001bOHDCP\r", "!" })
            _mockClient.Verify(client => client.Send(command), Times.Exactly(2));
    }

    [Fact]
    public void Constructor_InitializesAnAlreadyConnectedClient()
    {
        var client = TestFactory.CreateCommunicationClient();
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, ConnectionState.Connected);
        using var switcher = new ExtronSw(client.Object, "Auto discovery");
        client.Verify(c => c.Send("\u001b3CV\r"), Times.Once);
        client.Verify(c => c.Send("1I"), Times.Once);
        Assert.Empty(switcher.GetInputs());
        Assert.Single(switcher.GetOutputs());
        client.Object.ResponseHandlers!("Inf01*SW8 HD 4K PLUS Series");
        Assert.Equal(8, switcher.NumberOfInputs);
    }

    [Fact]
    public async Task Heartbeat_RefreshesStatusOnlyWhileConnected()
    {
        await Poll();
        _mockClient.Verify(c => c.Send(It.IsAny<string>()), Times.Never);
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        foreach (var command in new[] { "\u001bLS\r", "\u001bIHDCP\r", "\u001bOHDCP\r", "!" })
            _mockClient.Verify(c => c.Send(command), Times.Once);
        SetConnection(ConnectionState.Disconnected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(c => c.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void ModelResponse_DiscoversAllModelsAndPreservesExistingEndpoints(int count)
    {
        var first = _switcher.GetInputs()[0];
        var changes = 0;
        _switcher.EndpointsChangedHandlers += () => changes++;
        Respond($"Inf01*SW{count} HD 4K PLUS Series\r\n");
        Respond($"Inf01*SW{count} HD 4K PLUS Series");
        Assert.Equal(count, _switcher.NumberOfInputs);
        Assert.Equal(count, _switcher.GetInputs().Count);
        Assert.Same(first, _switcher.GetInputs()[0]);
        Assert.Equal(count == 4 ? 0 : 1, changes);
        Assert.Equal(1, _switcher.NumberOfOutputs);
        Assert.Equal(AVEndpointType.Decoder, Assert.Single(_switcher.GetOutputs()).DeviceType);
    }

    [Fact]
    public void SignalResponse_UpdatesEveryConnectorAndNotifiesSubscribers()
    {
        var inputs = _switcher.GetInputs();
        var output = Assert.Single(_switcher.GetOutputs());
        var inputChanges = 0;
        var outputChanges = 0;
        inputs[0].InputStatusChangedHandlers += (_, _, _) => inputChanges++;
        output.OutputStatusChangedHandlers += (_, _, _) => outputChanges++;
        Respond("Sig1 0 1 0*1\r\n");
        Assert.Equal(ConnectionState.Connected, inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[1].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, inputs[2].InputConnectionStatus);
        Assert.Equal(ConnectionState.Disconnected, inputs[3].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, output.OutputConnectionStatus);
        Respond("Sig0 0 0 0*0");
        Assert.Equal(ConnectionState.Disconnected, output.OutputConnectionStatus);
        Assert.Equal(2, inputChanges);
        Assert.Equal(2, outputChanges);
    }

    [Fact]
    public void HdcpResponse_UsesSwStatusCodesAndLeavesSignalStateIndependent()
    {
        Respond("Sig1 1 0 1*1\r\nHdcp I1 2 0 1\r\nHdcp O1\r\n");
        var inputs = _switcher.GetInputs();
        var output = Assert.Single(_switcher.GetOutputs());
        Assert.Equal(HdcpStatus.Active, inputs[0].InputHdcpStatus);
        Assert.Equal(HdcpStatus.NotSupported, inputs[1].InputHdcpStatus);
        Assert.Equal(HdcpStatus.Unknown, inputs[2].InputHdcpStatus);
        Assert.Equal(HdcpStatus.Available, output.OutputHdcpStatus);
        Respond("HdcpO2");
        Assert.Equal(HdcpStatus.NotSupported, output.OutputHdcpStatus);
        Respond("HdcpO0");
        Assert.Equal(HdcpStatus.Unknown, output.OutputHdcpStatus);
        Assert.Equal(ConnectionState.Connected, output.OutputConnectionStatus);
    }

    [Theory]
    [InlineData("In2 All", "2")]
    [InlineData("IN 03 All", "3")]
    [InlineData("In0 All", "0")]
    [InlineData("In4 Ausw0 Afmt0 Vmt0", "4")]
    public void TieFeedback_UpdatesOutputStreamAddress(string response, string selected)
    {
        Respond(response);
        Assert.Equal(selected, Assert.Single(_switcher.GetOutputs()).StreamAddress);
    }

    [Theory]
    [InlineData("Sig1 0 x 0*1")]
    [InlineData("Sig1 0 1 0*2")]
    [InlineData("Sig1 0 1*1")]
    [InlineData("Sig1 0 1 0")]
    [InlineData("Hdcp I1 2 3 1")]
    [InlineData("Hdcp I1 2")]
    [InlineData("In99999999999999999999 All")]
    [InlineData("Inf01*SW9 HD 4K PLUS Series")]
    public void InvalidResponses_DoNotChangeStatus(string response)
    {
        Respond(response);
        Assert.Equal(4, _switcher.NumberOfInputs);
        Assert.All(_switcher.GetInputs(), input =>
        {
            Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        });
        Assert.Equal(ConnectionState.Unknown, _switcher.GetOutputs()[0].OutputConnectionStatus);
        Assert.Empty(_switcher.GetOutputs()[0].StreamAddress);
    }

    [Theory]
    [InlineData("Sig1 0 x 0*1", "Malformed signal status 1 0 x 0*1")]
    [InlineData("Sig1 0 1*1", "Malformed signal status 1 0 1*1")]
    [InlineData("Hdcp I1 2 3 1", "Malformed HDCP status I1 2 3 1")]
    [InlineData("Hdcp I1 2", "Ignoring the input HDCP status I1 2 as it doesn't list the 4 inputs")]
    [InlineData("Hdcp O1 2", "Ignoring the output HDCP status O1 2 as it doesn't list the one output")]
    [InlineData("In99999999999999999999 All", "Ignoring the tie In99999999999999999999 All as it names an input outside the 4 inputs")]
    [InlineData("In5 All", "Ignoring the tie In5 All as it names an input outside the 4 inputs")]
    public void InvalidResponses_AreLoggedWithTheRawValue(string response, string logged)
    {
        var client = TestFactory.CreateCommunicationClient();
        var (switcher, logs) = CreateWithCapturedLogs(client);
        client.Object.ResponseHandlers!("Inf01*SW4 HD 4K PLUS Series");

        client.Object.ResponseHandlers!(response);

        Assert.Contains(logs.Logs, log => log.Message == logged);
        switcher.Dispose();
    }

    [Fact]
    public void TieBeforeTheInputCountIsKnown_IsLoggedAsSuch()
    {
        var client = TestFactory.CreateCommunicationClient();
        var (switcher, logs) = CreateWithCapturedLogs(client);

        client.Object.ResponseHandlers!("In2 All");

        Assert.Contains(logs.Logs, log => log.Message == "Ignoring the tie In2 All as it arrived before the input count is known");
        Assert.Empty(switcher.GetOutputs()[0].StreamAddress);
        switcher.Dispose();
    }

    [Fact]
    public async Task ConnectionChange_IsNotHeldUpByASlowSubscriberOnTheReceiveThread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        HoldFirstInputChange(_switcher.GetInputs()[0], entered, release);
        var receive = Task.Run(() => Respond("Sig1 0 1 0*1"));
        Assert.True(entered.Wait(ReturnTimeout), "The subscriber was never called");

        var disconnect = Task.Run(() => SetConnection(ConnectionState.Disconnected));

        await disconnect.WaitAsync(ReturnTimeout);
        release.Set();
        await receive;
    }

    [Fact]
    public async Task Dispose_DuringAResponse_StopsTheRestOfItCreatingEndpoints()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        HoldFirstInputChange(_switcher.GetInputs()[0], entered, release);
        var receive = Task.Run(() => Respond("Sig1 0 1 0*1\r\nInf01*SW8 HD 4K PLUS Series"));
        Assert.True(entered.Wait(ReturnTimeout), "The subscriber was never called");

        var dispose = Task.Run(_switcher.Dispose);

        await dispose.WaitAsync(ReturnTimeout);
        release.Set();
        await receive;
        Assert.Empty(_switcher.GetInputs());
        Assert.Empty(_switcher.GetOutputs());
    }

    [Fact]
    public void Dispose_DeregistersAndClearsTheEndpoints()
    {
        var inputs = _switcher.GetInputs();
        var output = _switcher.GetOutputs()[0];

        _switcher.Dispose();

        Assert.Empty(_switcher.GetInputs());
        Assert.Empty(_switcher.GetOutputs());
        Assert.DoesNotContain(LogBaseRegistry.GetAll(), instance => inputs.Contains(instance) || instance == output);
    }

    [Fact]
    public void Disconnect_ClearsStaleStatusWithoutReplacingEndpoints()
    {
        SetConnection(ConnectionState.Connected);
        Respond("Sig1 1 1 1*1\r\nHdcp I1 1 1 1\r\nIn2 All");
        var input = _switcher.GetInputs()[0];
        SetConnection(ConnectionState.Disconnected);
        Assert.Same(input, _switcher.GetInputs()[0]);
        Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
        Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        Assert.Empty(_switcher.GetOutputs()[0].StreamAddress);
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndStopsHeartbeat()
    {
        SetConnection(ConnectionState.Connected);
        _switcher.Dispose();
        _mockClient.Invocations.Clear();
        await Poll();
        Assert.Null(_mockClient.Object.ResponseHandlers);
        Assert.Null(_mockClient.Object.ConnectionStateHandlers);
        _mockClient.Verify(c => c.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1!")]
    [InlineData(2, "2!")]
    [InlineData(3, "3!")]
    [InlineData(4, "4!")]
    public void RouteAV_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)] // Discovered as an SW4
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 0);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteAV_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "2!";
        _switcher.RouteAV(2, 1);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteVideoAndRouteAudio_RecordAnEventAsTheyAreRefused()
    {
        _switcher.RouteVideo(2, 1);
        _switcher.RouteAudio(3, 1);

        Assert.Contains(_switcher.Events, e => e.Type == EventType.Input &&
            e.Info == "Not switching video output 1 to input 2 as this device doesn't support video breakaway");
        Assert.Contains(_switcher.Events, e => e.Type == EventType.Input &&
            e.Info == "Not switching audio output 1 to input 3 as this device doesn't support audio breakaway");
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }
}
