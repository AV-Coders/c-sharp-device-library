using System.Diagnostics;
using System.Reflection;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Display.Tests;

public class NecUhdExternalControlTest
{
    private readonly NecUhdExternalControl _display;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<PowerStateHandler> _mockPowerStateHandler;
    private readonly Mock<InputHandler> _mockInputHandler;
    private readonly Mock<VolumeLevelHandler> _mockVolumeLevelHandler;

    public NecUhdExternalControlTest()
    {
        _display = new NecUhdExternalControl(_mockClient.Object, "Test display", Input.Hdmi1, (byte)'A', new FakeTimeProvider());
        _mockPowerStateHandler = new Mock<PowerStateHandler>();
        _display.PowerStateHandlers += _mockPowerStateHandler.Object;
        _mockInputHandler = new Mock<InputHandler>();
        _display.InputHandlers += _mockInputHandler.Object;
        _mockVolumeLevelHandler = new Mock<VolumeLevelHandler>();
        _display.VolumeLevelHandlers += _mockVolumeLevelHandler.Object;
    }
    
    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        byte[] expectedPowerCommand =
        [
            0x01, 0x30, 0x41, 0x30, 0x41, 0x30, 0x43, 0x02, 
            (byte)'C', (byte)'2', (byte)'0', (byte)'3', (byte)'D', (byte)'6', (byte)'0', (byte)'0', (byte)'0', (byte)'4', 0x03, 0x76, 0x0d
        ];
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = [0x01, 0x30, 0x41, 0x30, 0x41, 0x30, 0x43, 0x02, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, 0x03, 0x73, 0x0d
        ];
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Theory]
    [InlineData(Input.Hdmi1, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x31, 0x31, 0x03, 0x72, 0x0d })]
    [InlineData(Input.Hdmi2, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x31, 0x32, 0x03, 0x71, 0x0d })]
    [InlineData(Input.Hdmi3, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x38, 0x32, 0x03, 0x78, 0x0d })]
    [InlineData(Input.Hdmi4, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x38, 0x33, 0x03, 0x79, 0x0d })]
    public void SetInput_SendsTheExpectedCommand(Input source, byte[] command)
    {
        _display.SetInput(source);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
    
    [Theory]
    [InlineData(0,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x30, 0x03, 0x70, 0x0d })]
    [InlineData(1,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x31, 0x03, 0x71, 0x0d })]
    [InlineData(100, new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x36, 0x34, 0x03, 0x72, 0x0d })]
    public void SetVolume_SendsTheExpectedCommand(int volume, byte[] command)
    {
        _display.SetVolume(volume);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
    
    [Theory]
    [InlineData(MuteState.On,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, 0x31, 0x03, 0x09, 0x0d })]
    [InlineData(MuteState.Off,   new byte[] { 0x01, 0x30, 0x41, 0x30, 0x45, 0x30, 0x41, 0x02, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, 0x32, 0x03, 0x0A, 0x0d })]
    public void SetAudioMute_SendsTheExpectedCommand(MuteState state, byte[] command)
    {
        _display.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74, 0x0D }, PowerState.On)]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x34, 0x03, 0x71, 0x0D }, PowerState.Off)]
    public void ResponseHandler_HandlesPowerResponses(byte[] response, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        _mockPowerStateHandler.Verify(x => x.Invoke(expectedPowerState));
    }

    [Fact]
    public void ResponseHandler_UpdatesTheCommunicationState()
    {
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);

        _mockClient.Object.ResponseByteHandlers!.Invoke(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74, 0x0D });

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36 },
        new byte[] { 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74, 0x0D }, PowerState.On, Input.Unknown, 0)]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30 },
        new byte[] { 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03, 0x01, 0x0D }, PowerState.Unknown, Input.Hdmi1, 0)]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x32, 0x30 },
        new byte[] { 0x30, 0x30, 0x30, 0x36, 0x34, 0x30, 0x30, 0x31, 0x45, 0x03, 0x75, 0x0D }, PowerState.Unknown, Input.Unknown, 30)]
    public void ResponseHandler_ReassemblesAReplySplitAcrossTwoReceives(byte[] first, byte[] second, PowerState power, Input input, int volume)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(first);
        _mockClient.Object.ResponseByteHandlers!.Invoke(second);

        Assert.Equal((power, input, volume), (_display.PowerState, _display.Input, _display.Volume));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03, 0x01, 0x0D }, Input.Hdmi1)]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x32, 0x03, 0x02, 0x0D }, Input.Hdmi2)]
    public void ResponseHandler_HandlesInputResponses(byte[] response, Input expectedInput)
    {
        
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        _mockInputHandler.Verify(x => x.Invoke(expectedInput));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x30, 0x36, 0x34, 0x30, 0x30, 0x31, 0x45, 0x03, 0x75, 0x0D }, 30)]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x30, 0x36, 0x34, 0x30, 0x30, 0x36, 0x34, 0x03, 0x03, 0x0D }, 100)]
    public void ResponseHandler_HandlesVolumeLevelResponses(byte[] response, byte expectedVolumeLevel)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        _mockVolumeLevelHandler.Verify(x => x.Invoke(expectedVolumeLevel));
    }
    
    [Fact]
    public void ResponseHandler_HandlesMultipleResponcesInTheOnePayload()
    {
        var response1 = new byte[] { 0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44 };
        var response2 = new byte[] { 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74 };        
        var response3 = new byte[] { 0x0D, 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30 };
        var response4 = new byte[] { 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03 };        
        var response5 = new byte[] { 0x01, 0x0D, 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30 };
        var response6 = new byte[] { 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x30, 0x36, 0x34, 0x30, 0x30, 0x30, 0x30 };
        var response7 = new byte[] { 0x03, 0x01, 0x0D }; 
            
        _mockClient.Object.ResponseByteHandlers!.Invoke(response1);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response2);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response3);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response4);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response5);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response6);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response7);
        _mockPowerStateHandler.Verify(x => x.Invoke(PowerState.On));
        _mockInputHandler.Verify(x => x.Invoke(Input.Hdmi1));
    }

    internal static readonly byte[] PowerOnReply =[0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74, 0x0D];

    private static void SetConnectionState(Mock<TcpClient> mock, ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mock.Object, state);

    [Fact]
    public void ResponseHandler_IgnoresAShortFrame()
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x41, 0x42, 0x0D]));

        Assert.Null(exception);
        Assert.Equal(PowerState.Unknown, _display.PowerState);
    }

    [Fact]
    public void ResponseHandler_ReadsTheStandbyPowerStatusAsOff()
    {
        byte[] standby = [0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x32, 0x03, 0x77, 0x0D];
        _mockClient.Object.ResponseByteHandlers!.Invoke(PowerOnReply);

        _mockClient.Object.ResponseByteHandlers!.Invoke(standby);

        Assert.Equal(PowerState.Off, _display.PowerState);
    }

    [Fact]
    public void HandleConnectionState_GivenConnected_ReturnsWithoutWaiting()
    {
        var client = TestFactory.CreateTcpClient();
        _ = new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1, (byte)'A');
        var stopwatch = Stopwatch.StartNew();

        client.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        Assert.True(stopwatch.ElapsedMilliseconds < 1000);
    }

    private static readonly TimeSpan StartStep = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BeforeTheSecondPoll = TimeSpan.FromSeconds(20);

    private static int PowerPollsSent(Mock<TcpClient> client) =>
        client.Invocations.Count(i => i.Method.Name == "Send" && i.Arguments[0] is byte[] frame && Encoding.ASCII.GetString(frame[8..^3]) == "01D6");

    private static Task UntilPowerPolled(FakeTimeProvider time, Mock<TcpClient> client, int times) =>
        Wait.UntilAsync(time, StartStep, () => PowerPollsSent(client) >= times, $"The power poll was sent fewer than {times} times");

    private static Task AdvanceAsync(FakeTimeProvider time, TimeSpan duration)
    {
        var until = time.GetUtcNow() + duration;
        return Wait.UntilAsync(time, WindowStep, () => time.GetUtcNow() >= until, "The fake clock did not advance");
    }

    [Fact]
    public async Task Connected_SendsOnePowerPollBeforeTheNextPoll()
    {
        var time = new FakeTimeProvider();
        var client = TestFactory.CreateTcpClient();
        _ = new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1, 0x2A, time);

        SetConnectionState(client, ConnectionState.Connected);
        client.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await UntilPowerPolled(time, client, 1);
        await AdvanceAsync(time, BeforeTheSecondPoll);

        Assert.Equal(1, PowerPollsSent(client));
    }

    [Fact]
    public async Task AlreadyConnectedClient_SendsOnePowerPollBeforeTheNextPoll_ThenKeepsPolling()
    {
        var time = new FakeTimeProvider();
        var client = TestFactory.CreateTcpClient();
        SetConnectionState(client, ConnectionState.Connected);
        _ = new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1, 0x2A, time);

        await UntilPowerPolled(time, client, 1);
        await AdvanceAsync(time, BeforeTheSecondPoll);
        Assert.Equal(1, PowerPollsSent(client));

        await AdvanceAsync(time, BeforeTheSecondPoll);
        Assert.Equal(2, PowerPollsSent(client));
    }

    [Fact]
    public async Task ConnectedWhileBeingConstructed_SendsOnePowerPollBeforeTheNextPoll()
    {
        var time = new FakeTimeProvider();
        var client = TestFactory.CreateTcpClient();
        SetConnectionState(client, ConnectionState.Connected);
        _ = new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1, 0x2A, time);

        client.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);
        await UntilPowerPolled(time, client, 1);
        await AdvanceAsync(time, BeforeTheSecondPoll);

        Assert.Equal(1, PowerPollsSent(client));
    }

    [Fact]
    public async Task Poll_WhileDisconnected_SendsNothing()
    {
        SetConnectionState(_mockClient, ConnectionState.Disconnected);

        await (Task)typeof(NecUhdExternalControl).GetMethod("DoPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_display, [CancellationToken.None])!;

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public void Send_GivenAFailure_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Throws(new IOException("Oh No!"));

        _display.PowerOn();

        Assert.Equal(CommunicationState.Error, _display.CommunicationState);
    }

    [Fact]
    public void ResponseHandler_GivenAnErrorResultForThePowerStatus_ReportsAnError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x31, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x75, 0x0D]);

        Assert.Equal(PowerState.Unknown, _display.PowerState);
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
        Assert.Contains(_display.Events, e => e.Type == EventType.Error && e.Info == "The display returned an error result");
    }

    [Fact]
    public void ResponseHandler_GivenAnErrorResultForAParameter_ReportsAnError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x31, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03, 0x00, 0x0D]);

        Assert.Equal(Input.Unknown, _display.Input);
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
        Assert.Contains(_display.Events, e => e.Type == EventType.Error && e.Info == "The display returned an error result");
    }

    [Fact]
    public void ResponseHandler_IgnoresAReplyFromAnotherMonitor()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x42, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x77, 0x0D]);

        Assert.Equal(PowerState.Unknown, _display.PowerState);
    }

    [Fact]
    public void ResponseHandler_GivenTheAllMonitorsId_AcceptsAReplyFromAnyMonitor()
    {
        var client = TestFactory.CreateTcpClient();
        var display = new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1);

        client.Object.ResponseByteHandlers!.Invoke(PowerOnReply);

        Assert.Equal(PowerState.On, display.PowerState);
    }

    [Fact]
    public void ResponseHandler_HandlesMoreThan1024BytesOfWholeFramesInOneReceive()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(Enumerable.Repeat(PowerOnReply, 40).SelectMany(x => x).ToArray());

        _mockPowerStateHandler.Verify(x => x.Invoke(PowerState.On));
    }

    [Fact]
    public void ResponseHandler_HandlesSetParameterInputReplies()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x41, 0x46, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x32, 0x03, 0x00, 0x0D]);

        _mockInputHandler.Verify(x => x.Invoke(Input.Hdmi2));
    }

    [Fact]
    public void ResponseHandler_HandlesSetParameterVolumeReplies()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x41, 0x46, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, 0x30, 0x30, 0x36, 0x34, 0x30, 0x30, 0x31, 0x45, 0x03, 0x77, 0x0D]);

        _mockVolumeLevelHandler.Verify(x => x.Invoke(30));
    }

    [Fact]
    public void SetAudioMute_Unknown_SendsNothing()
    {
        _display.SetAudioMute(MuteState.Unknown);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class NecUhdExternalControlLogTest
{
    private static (Mock<TcpClient> Client, NecUhdExternalControl Display, ScopeCapturingLoggerFactory Logs) CreateDisplayWithCapturedLogs()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = TestFactory.CreateTcpClient();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (client, new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1, (byte)'A', new FakeTimeProvider()), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    [Fact]
    public void ResponseHandler_ReadsAnUnmappedInputCodeAsUnknown_AndLogsAWarning()
    {
        var (client, display, logs) = CreateDisplayWithCapturedLogs();
        byte[] hdmi1 = [0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03, 0x01, 0x0D];
        byte[] unmapped = [0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x30, 0x31, 0x03, 0x00, 0x0D];
        client.Object.ResponseByteHandlers!.Invoke(hdmi1);

        client.Object.ResponseByteHandlers!.Invoke(unmapped);

        Assert.Equal(Input.Unknown, display.Input);
        Assert.Contains(logs.Logs, l => l.Message == $"Unknown input code in {Convert.ToHexString(unmapped)}");
    }

    [Fact]
    public void ResponseHandler_KeepsThePowerStateOnAReservedPowerStatus_AndLogsAWarning()
    {
        var (client, display, logs) = CreateDisplayWithCapturedLogs();
        byte[] reserved = [0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x33, 0x03, 0x76, 0x0D];
        client.Object.ResponseByteHandlers!.Invoke(NecUhdExternalControlTest.PowerOnReply);

        client.Object.ResponseByteHandlers!.Invoke(reserved);

        Assert.Equal(PowerState.On, display.PowerState);
        Assert.Contains(logs.Logs, l => l.Message == $"Unknown power status in {Convert.ToHexString(reserved)}");
    }
}