using System.Diagnostics;
using System.Reflection;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;
using Xunit.Abstractions;

namespace AVCoders.Display.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class NecUhdExternalControlTest
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly NecUhdExternalControl _display;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<PowerStateHandler> _mockPowerStateHandler;
    private readonly Mock<InputHandler> _mockInputHandler;
    private readonly Mock<VolumeLevelHandler> _mockVolumeLevelHandler;

    public NecUhdExternalControlTest(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _display = new NecUhdExternalControl(_mockClient.Object, "Test display", Input.Hdmi1, (byte)'A');
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

    // [Fact]
    // public void PowerOff_UsesTheCorrectDisplayId()
    // {
    //     NecUhdExternalControl samsungMdcForDisplay2 = new NecUhdExternalControl(0x03, _mockClient.Object);
    //     byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x03, 0x01, 0x00, 0x15 };
    //
    //     samsungMdcForDisplay2.PowerOff();
    //     _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    // }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = [0x01, 0x30, 0x41, 0x30, 0x41, 0x30, 0x43, 0x02, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, 0x03, 0x73, 0x0d
        ];
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    // [Fact]
    // public void PowerOn_UsesTheCorrectDisplayId()
    // {
    //     SamsungMdc samsungMdcForDisplay2 = new SamsungMdc(0x02, _mockClient.Object);
    //     byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x02, 0x01, 0x01, 0x15 };
    //
    //     samsungMdcForDisplay2.PowerOn();
    //     _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    // }
    
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

        foreach (IInvocation x in _mockClient.Invocations)
        {
            foreach (object xArgument in x.Arguments)
            {
                if (xArgument.GetType() == typeof(byte[]))
                {
                    byte[] bytes = (byte[])xArgument;
                    _testOutputHelper.WriteLine(Encoding.ASCII.GetString(bytes));
                }
            }
        }

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

    [Fact]
    public void ResponseHandler_HandlesPartialPowerResponses()
    {
        var response1 = new byte[]
            { 0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36 };
        var response2 = new byte[] { 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74, 0x0D };
        _mockClient.Object.ResponseByteHandlers!.Invoke(response1);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response2);
        _mockPowerStateHandler.Verify(x => x.Invoke(PowerState.On));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03, 0x01, 0x0D }, Input.Hdmi1)]
    [InlineData(new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x32, 0x03, 0x02, 0x0D }, Input.Hdmi2)]
    public void ResponseHandler_HandlesInputResponses(byte[] response, Input expectedInput)
    {
        
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);
        _mockInputHandler.Verify(x => x.Invoke(expectedInput));
    }

    [Fact]
    public void ResponseHandler_HandlesPartialInputResponses()
    {
        var response1 = new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x30 };
        var response2 = new byte[] { 0x30, 0x30, 0x30, 0x30, 0x38, 0x38, 0x30, 0x30, 0x31, 0x31, 0x03, 0x01, 0x0D };
        _mockClient.Object.ResponseByteHandlers!.Invoke(response1);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response2);
        _mockInputHandler.Verify(x => x.Invoke(Input.Hdmi1));
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
    public void ResponseHandler_HandlesPartialVolumeLevelResponses()
    {
        var response1 = new byte[] { 0x01, 0x30, 0x30, 0x41, 0x44, 0x31, 0x32, 0x02, 0x30, 0x30, 0x30, 0x30, 0x36, 0x32, 0x30 };
        var response2 = new byte[] { 0x30, 0x30, 0x30, 0x36, 0x34, 0x30, 0x30, 0x31, 0x45, 0x03, 0x75, 0x0D }; 
            
        _mockClient.Object.ResponseByteHandlers!.Invoke(response1);
        _mockClient.Object.ResponseByteHandlers!.Invoke(response2);
        _mockVolumeLevelHandler.Verify(x => x.Invoke(30));
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

    private static readonly byte[] PowerOnReply = [0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x31, 0x03, 0x74, 0x0D];

    private static void SetConnectionState(Mock<TcpClient> mock, ConnectionState state) =>
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(mock.Object, state);

    private static (Mock<TcpClient> Client, NecUhdExternalControl Display, ScopeCapturingLoggerFactory Logs) CreateDisplayWithCapturedLogs()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = TestFactory.CreateTcpClient();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (client, new NecUhdExternalControl(client.Object, "Test display", Input.Hdmi1, (byte)'A'), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    [Fact]
    public void ResponseHandler_IgnoresAShortFrame()
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x30, 0x30, 0x41, 0x42, 0x0D]));

        Assert.Null(exception);
        Assert.Equal(PowerState.Unknown, _display.PowerState);
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
    public void ResponseHandler_ReadsTheStandbyPowerStatusAsOff()
    {
        byte[] standby = [0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x32, 0x03, 0x77, 0x0D];
        _mockClient.Object.ResponseByteHandlers!.Invoke(PowerOnReply);

        _mockClient.Object.ResponseByteHandlers!.Invoke(standby);

        Assert.Equal(PowerState.Off, _display.PowerState);
    }

    [Fact]
    public void ResponseHandler_KeepsThePowerStateOnAReservedPowerStatus_AndLogsAWarning()
    {
        var (client, display, logs) = CreateDisplayWithCapturedLogs();
        byte[] reserved = [0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30, 0x30, 0x34, 0x30, 0x30, 0x30, 0x33, 0x03, 0x76, 0x0D];
        client.Object.ResponseByteHandlers!.Invoke(PowerOnReply);

        client.Object.ResponseByteHandlers!.Invoke(reserved);

        Assert.Equal(PowerState.On, display.PowerState);
        Assert.Contains(logs.Logs, l => l.Message == $"Unknown power status in {Convert.ToHexString(reserved)}");
    }

    [Fact]
    public void HandleConnectionState_GivenConnected_ReturnsWithoutWaiting()
    {
        var stopwatch = Stopwatch.StartNew();

        _mockClient.Object.ConnectionStateHandlers!.Invoke(ConnectionState.Connected);

        Assert.True(stopwatch.ElapsedMilliseconds < 1000);
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