using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;

namespace AVCoders.Camera.Tests;

public class SonyViscaSerialTest
{
    private readonly SonyVisca _viscaCamera;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public SonyViscaSerialTest()
    {
        _viscaCamera = new SonyVisca(_mockClient.Object, false, "Test Cam",
            new Dictionary<int, string> () {{0, "Home"}, {1, "Lectern"}});
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x00, 0x02, 0xFF];
        _viscaCamera.PowerOn();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_SetsTheDesiredPowerState()
    {
        var desiredStates = new List<PowerState>();
        _viscaCamera.OnDesiredPowerStateChanged += state => desiredStates.Add(state);

        _viscaCamera.PowerOn();

        Assert.Equal(PowerState.On, _viscaCamera.DesiredPowerState);
        Assert.Equal([PowerState.On], desiredStates);
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x00, 0x03, 0xFF];
        _viscaCamera.PowerOff();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SetsTheDesiredPowerState()
    {
        var desiredStates = new List<PowerState>();
        _viscaCamera.OnDesiredPowerStateChanged += state => desiredStates.Add(state);

        _viscaCamera.PowerOff();

        Assert.Equal(PowerState.Off, _viscaCamera.DesiredPowerState);
        Assert.Equal([PowerState.Off], desiredStates);
    }

    [Fact]
    public void ZoomStop_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x07, 0x00, 0xFF];
        _viscaCamera.ZoomStop();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void ZoomIn_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x07, 0x23, 0xFF];
        _viscaCamera.ZoomIn();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void ZoomOut_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x07, 0x33, 0xFF];
        _viscaCamera.ZoomOut();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltStop_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x03, 0x03, 0xFF];
        _viscaCamera.PanTiltStop();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltUp_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x03, 0x01, 0xFF];
        _viscaCamera.PanTiltUp();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltDown_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x03, 0x02, 0xFF];
        _viscaCamera.PanTiltDown();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltLeft_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x01, 0x03, 0xFF];
        _viscaCamera.PanTiltLeft();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltRight_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x02, 0x03, 0xFF];
        _viscaCamera.PanTiltRight();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void RecallPreset_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x3f, 0x02, 0x01, 0xFF];
        _viscaCamera.DoRecallPreset(1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void RecallPreset_SendsThePresetNumber()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x3f, 0x02, 0x06, 0xFF];
        _viscaCamera.DoRecallPreset(6);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SavePreset_SendsTheCommand()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x3f, 0x01, 0x01, 0xFF];
        _viscaCamera.SavePreset(1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SavePreset_SendsThePresetNumber()
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x3f, 0x01, 0x06, 0xFF];
        _viscaCamera.SavePreset(6);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(PowerState.On, new byte [] {0x81, 0x01, 0x04, 0x38, 0x02, 0xFF})]
    [InlineData(PowerState.Off, new byte [] {0x81, 0x01, 0x04, 0x38, 0x03, 0xFF})]
    public void AutoFocus_SendsTheCommand(PowerState state, byte[] expectedCommand)
    {
        _viscaCamera.SetAutoFocus(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTilt_ClearsPresets()
    {
        Mock<Action<int>> mockPresetRecalled = new Mock<Action<int>>();
        Mock<Action> mockPresetCleared = new Mock<Action>();
        _viscaCamera.OnPresetCleared += mockPresetCleared.Object;
        _viscaCamera.OnPresetRecalled  += mockPresetRecalled.Object;
        _viscaCamera.RecallPreset(0);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);
        _viscaCamera.PanTiltStop();
        
        mockPresetCleared.Verify(x => x.Invoke(), Times.Once);
        mockPresetRecalled.Verify(x => x.Invoke(It.IsAny<int>()), Times.Once);
        Assert.Equal(-1, _viscaCamera.LastRecalledPreset);
    }

    [Fact]
    public void PanTilt_ClearsPresetsOnceOnly()
    {
        Mock<Action> mockPresetCleared = new Mock<Action>();
        _viscaCamera.OnPresetCleared += mockPresetCleared.Object;
        _viscaCamera.RecallPreset(0);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        _viscaCamera.PanTiltStop();
        _viscaCamera.PanTiltStop();
        
        mockPresetCleared.Verify(x => x.Invoke(), Times.Once);
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x41, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x42, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x51, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x52, 0xFF })]
    public void HandleResponse_AckAndCompletion_SetCommunicationStateOkay(byte[] response)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        Assert.Equal(CommunicationState.Okay, _viscaCamera.CommunicationState);
    }

    [Fact]
    public void HandleResponse_Error_SetsCommunicationStateError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        Assert.Equal(CommunicationState.Error, _viscaCamera.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ErrorAfterRecallPreset_ReportsThePreset()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x62, 0x41, 0xFF]);

        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to recall preset Lectern: Command not executable");
    }

    [Fact]
    public void RecallPreset_WithoutAResponse_DoesNotReportTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        Assert.Equal(CameraBase.NoActivePreset, _viscaCamera.LastRecalledPreset);
        Assert.DoesNotContain(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void RecallPreset_AfterCompletion_ReportsTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Fact]
    public void RecallPreset_AfterACompletionWithoutAnAck_ReportsTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x51, 0xFF]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Fact]
    public void RecallPreset_AfterAnAckWithoutACompletion_ReportsTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Fact]
    public void RecallPreset_AfterAckAndCompletion_ReportsTheRecallOnce()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void RecallPreset_AfterAnError_DoesNotReportTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x62, 0x41, 0xFF]);

        Assert.Equal(CameraBase.NoActivePreset, _viscaCamera.LastRecalledPreset);
        Assert.DoesNotContain(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void RecallPreset_WhenTheDeviceDoesNotSendResponses_ReportsTheRecallImmediately()
    {
        var camera = new SonyVisca(_mockClient.Object, false, "Test Cam",
            new Dictionary<int, string> { { 1, "Lectern" } }) { DeviceSendsResponses = false };

        camera.RecallPreset(1);

        Assert.Equal(1, camera.LastRecalledPreset);
        Assert.Contains(camera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Theory]
    [InlineData(PowerState.On)]
    [InlineData(PowerState.Off)]
    public void Power_WhenTheDeviceDoesNotSendResponses_AssumesThePowerState(PowerState state)
    {
        var camera = new SonyVisca(_mockClient.Object, false, "Test Cam",
            new Dictionary<int, string> { { 1, "Lectern" } }) { DeviceSendsResponses = false };

        if (state == PowerState.On)
            camera.PowerOn();
        else
            camera.PowerOff();

        Assert.Equal(state, camera.DesiredPowerState);
        Assert.Equal(state, camera.PowerState);
    }

    [Fact]
    public void DeviceSendsResponses_SetAfterConstruction_ChangesTheBehaviour()
    {
        _viscaCamera.DeviceSendsResponses = false;

        _viscaCamera.PowerOn();
        _viscaCamera.RecallPreset(1);

        Assert.Equal(PowerState.On, _viscaCamera.PowerState);
        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
    }

    [Fact]
    public async Task Power_WhenTheDeviceSendsResponses_WaitsForTheInquiryReply()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        camera.PowerOff();

        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task Power_WhenTheDeviceDoesNotSendResponses_ResolvesAStalePowerIssue()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        camera.PowerOn();
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);
        Assert.Contains(camera.GetOngoingIssues(), i => i.Key == "power-state");

        camera.DeviceSendsResponses = false;
        camera.PowerOn();

        Assert.DoesNotContain(camera.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Fact]
    public void Power_WhenTheSendFails_DoesNotAssumeThePowerState()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<byte[]>())).Throws(new IOException("Port closed"));
        _viscaCamera.DeviceSendsResponses = false;

        _viscaCamera.PowerOn();

        Assert.Equal(PowerState.On, _viscaCamera.DesiredPowerState);
        Assert.Equal(PowerState.Unknown, _viscaCamera.PowerState);
    }

    [Fact]
    public void RecallPreset_WhenTheSendFails_DoesNotReportTheRecall()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<byte[]>())).Throws(new IOException("Port closed"));
        _viscaCamera.DeviceSendsResponses = false;

        _viscaCamera.RecallPreset(1);

        Assert.Equal(CameraBase.NoActivePreset, _viscaCamera.LastRecalledPreset);
        Assert.DoesNotContain(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void DeviceSendsResponses_SetToFalse_IgnoresRepliesToEarlierCommands()
    {
        _viscaCamera.RecallPreset(0);

        _viscaCamera.DeviceSendsResponses = false;
        _viscaCamera.RecallPreset(1);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void DeviceSendsResponses_SetBackToTrue_DoesNotConfirmEarlierCommands()
    {
        _viscaCamera.RecallPreset(0);
        _viscaCamera.DeviceSendsResponses = false;
        _viscaCamera.DeviceSendsResponses = true;

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        Assert.Equal(CameraBase.NoActivePreset, _viscaCamera.LastRecalledPreset);
    }

    [Fact]
    public async Task DeviceSendsResponses_SetToFalse_AdoptsTheDesiredPowerStateAndResolvesTheIssue()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        camera.PowerOn();
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);
        Assert.Contains(camera.GetOngoingIssues(), i => i.Key == "power-state");

        camera.DeviceSendsResponses = false;

        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Fact]
    public async Task DeviceSendsResponses_TurnedOffAndOn_ClearsPendingInquiries()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        camera.DeviceSendsResponses = false;
        camera.DeviceSendsResponses = true;
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(PowerState.Unknown, camera.PowerState);
    }

    [Fact]
    public void DeviceSendsResponses_SetToFalseWithNoDesiredState_LeavesPowerUnknown()
    {
        _viscaCamera.DeviceSendsResponses = false;

        Assert.Equal(PowerState.Unknown, _viscaCamera.PowerState);
        Assert.Empty(_viscaCamera.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_WhenTheDeviceSendsResponses_SendsThePowerInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());

        await SonyViscaPolling.InvokePoll(camera);

        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF]], client.Sent);
    }

    [Fact]
    public async Task Poll_WhenTheDeviceDoesNotSendResponses_DoesNotSendThePowerInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>())
            { DeviceSendsResponses = false };

        await SonyViscaPolling.InvokePoll(camera);

        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Poll_WithASecondInquiry_PassesEachReplyToItsInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false);
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF, 0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x09, 0x08, 0x01, 0xFF]], client.Sent);
        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.Equal([[0x90, 0x50, 0x03, 0xFF]], camera.SecondReplies);
    }

    [Fact]
    public async Task Poll_ClearsAnInquiryThatGotNoReply()
    {
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(PowerState.Off, camera.PowerState);
        Assert.Empty(camera.SecondReplies);
    }

    [Fact]
    public async Task HandleResponse_UnsolicitedInquiryReply_LeavesThePowerStateAndLogsAWarning()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message)>();
        var client = new ConnectedClient();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = new CapturingLoggerFactory(logs);
        SonyVisca camera;
        try
        {
            camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.Contains(logs,
            log => log.Level == LogLevel.Warning && log.Message == "Unexpected inquiry reply 90-50-03-FF");
    }

    [Fact]
    public async Task HandleResponse_InquiryErrorWithAnInquiryPending_AnswersThatInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false);
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x90, 0x60, 0x41, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to query the power state: Command not executable");
        Assert.Equal(PowerState.Unknown, camera.PowerState);
        Assert.Equal([[0x90, 0x50, 0x02, 0xFF]], camera.SecondReplies);
    }

    [Fact]
    public async Task HandleResponse_SocketErrorWithAnInquiryPending_ReportsTheCommandAndKeepsTheInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string> { { 1, "Lectern" } });
        await SonyViscaPolling.InvokePoll(camera);
        camera.RecallPreset(1);

        client.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to recall preset Lectern: Command not executable");
        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task HandleResponse_SplitReply_IsReassembled()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50]);
        client.ResponseByteHandlers!.Invoke([0x02, 0xFF]);

        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task HandleResponse_PartialReplyOverSixteenBytes_ReportsAMalformedResponseAndIsCleared()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, ..new byte[15]]);

        Assert.Equal(CommunicationState.Error, camera.CommunicationState);
        Assert.Contains(camera.Events, e => e.Type == EventType.Error && e.Info == "The response was malformed");

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task HandleResponse_PartialReplyBeforeAConnectionChange_IsDropped()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string> { { 1, "Lectern" } });
        await SonyViscaPolling.InvokePoll(camera);
        camera.RecallPreset(1);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50]);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(1, camera.LastRecalledPreset);
        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task HandleResponse_PartialReplyBeforeResponsesAreTurnedOffAndOn_IsDropped()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string> { { 1, "Lectern" } });
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50]);

        camera.DeviceSendsResponses = false;
        camera.DeviceSendsResponses = true;
        await SonyViscaPolling.InvokePoll(camera);
        camera.RecallPreset(1);
        client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(1, camera.LastRecalledPreset);
        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Error);
    }

    private sealed class CapturingLoggerFactory(ConcurrentQueue<(LogLevel Level, string Message)> logs)
        : ILoggerFactory, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => logs.Enqueue((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void HandleResponse_ErrorAfterACompletedRecall_ReportsTheGenericError()
    {
        _viscaCamera.RecallPreset(1);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x51, 0xFF]);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == "Command not executable");
    }

    [Theory]
    [InlineData(0x02, PowerState.On)]
    [InlineData(0x03, PowerState.Off)]
    public async Task HandleResponse_PowerInquiryReply_SetsPowerState(byte value, PowerState expectedState)
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, value, 0xFF]);

        Assert.Equal(expectedState, camera.PowerState);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x90 })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x90, 0x41, 0x00 })]
    public void HandleResponse_MalformedResponse_DoesNotThrow(byte[] response)
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke(response));

        Assert.Null(exception);
        Assert.Equal(PowerState.Unknown, _viscaCamera.PowerState);
    }

    [Fact]
    public void HandleResponse_MultipleMessages_ProcessesAll()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF, 0x90, 0x61, 0x41, 0xFF]);

        Assert.Equal(CommunicationState.Error, _viscaCamera.CommunicationState);
    }
}

public class SonyViscaIpTest
{
    private readonly SonyVisca _viscaCamera;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public SonyViscaIpTest()
    {
        _viscaCamera = new SonyVisca(_mockClient.Object, true, "Test Camera",
            new Dictionary<int, string> () {{0, "Home"}, {1, "Lectern"}});
    }

    [Fact]
    public void PowerOn_SendsTheIpViscaCommandHeader()
    {
        byte[] expectedCommand = [0x01, 0x00, 0x00, 0x06, 0xff, 0xff, 0xff, 0x00,  0x81, 0x01, 0x04, 0x00, 0x02, 0xFF];
        _viscaCamera.PowerOn();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_IncrementsTheSequenceNumber()
    {
        byte[] expectedCommand = [0x01, 0x00, 0x00, 0x06, 0xff, 0xff, 0xff, 0x01, 0x81, 0x01, 0x04, 0x00, 0x02, 0xFF];

        //_mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Throws(new IOException("Oh No!"));
        _viscaCamera.PowerOn();
        _viscaCamera.PowerOn();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PTZRight_SendsTheCommand()
    {
        byte[] expectedString =
            [0x01, 0x00, 0x00, 0x09, 0xff, 0xff, 0xff, 0x00, 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x02, 0x03, 0xff];

        _viscaCamera.PanTiltRight();

        _mockClient.Verify(x => x.Send(expectedString), Times.Once);
    }

    [Fact]
    public void HandleResponse_Ack_SetsCommunicationStateOkay()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x2E, 0x90, 0x42, 0xFF]);

        Assert.Equal(CommunicationState.Okay, _viscaCamera.CommunicationState);
    }

    [Fact]
    public void HandleResponse_Error_SetsCommunicationStateError()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x01, 0x90, 0x61, 0x41, 0xFF]);

        Assert.Equal(CommunicationState.Error, _viscaCamera.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ErrorEchoingARecallSequenceNumber_ReportsThePreset()
    {
        _viscaCamera.RecallPreset(1);
        _viscaCamera.PowerOn();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x62, 0x41, 0xFF]);

        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to recall preset Lectern: Command not executable");
    }

    [Fact]
    public void HandleResponse_ErrorEchoingAnUnrelatedSequenceNumber_ReportsTheGenericError()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x07, 0x90, 0x61, 0x41, 0xFF]);

        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == "Command not executable");
    }

    [Fact]
    public void RecallPreset_AfterACompletionEchoingItsSequenceNumber_ReportsTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([
            0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x41, 0xFF,
            0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x51, 0xFF
        ]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Fact]
    public void RecallPreset_AfterACompletionWithoutAnAck_ReportsTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x51, 0xFF]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Fact]
    public void RecallPreset_AfterAnAckEchoingItsSequenceNumber_ReportsTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x41, 0xFF]);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Preset && e.Info == "Preset Lectern recalled");
    }

    [Fact]
    public void HandleResponse_ErrorAfterAnAckedRecall_ReportsThePreset()
    {
        _viscaCamera.RecallPreset(1);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x41, 0xFF]);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x62, 0x04, 0xFF]);

        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to recall preset Lectern: Command cancelled");
    }

    [Fact]
    public void RecallPreset_AfterAnError_DoesNotReportTheRecall()
    {
        _viscaCamera.RecallPreset(1);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x62, 0x41, 0xFF]);

        Assert.Equal(CameraBase.NoActivePreset, _viscaCamera.LastRecalledPreset);
        Assert.DoesNotContain(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void HandleResponse_MultipleMessages_ProcessesAll()
    {
        byte[] ackThenCompletion =
        [
            0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x2E, 0x90, 0x42, 0xFF,
            0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x2E, 0x90, 0x52, 0xFF
        ];
        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke(ackThenCompletion));

        Assert.Null(exception);
        Assert.Equal(CommunicationState.Okay, _viscaCamera.CommunicationState);
    }

    [Theory]
    [InlineData(0x02, PowerState.On)]
    [InlineData(0x03, PowerState.Off)]
    public async Task HandleResponse_PowerInquiryReply_SetsPowerState(byte value, PowerState expectedState)
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, value, 0xFF]);

        Assert.Equal(expectedState, camera.PowerState);
    }

    [Fact]
    public async Task HandleResponse_InquiryRepliesOutOfOrder_AreMatchedBySequenceNumber()
    {
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, true);
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([
            0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x01, 0x90, 0x50, 0x03, 0xFF,
            0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, 0x02, 0xFF
        ]);

        Assert.Equal([
            [0x01, 0x10, 0x00, 0x05, 0xFF, 0xFF, 0xFF, 0x00, 0x81, 0x09, 0x04, 0x00, 0xFF],
            [0x01, 0x10, 0x00, 0x05, 0xFF, 0xFF, 0xFF, 0x01, 0x81, 0x09, 0x08, 0x01, 0xFF]
        ], client.Sent);
        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.Equal([[0x90, 0x50, 0x03, 0xFF]], camera.SecondReplies);
    }

    [Fact]
    public async Task HandleResponse_ErrorEchoingAnInquirySequenceNumber_ReportsTheInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string> { { 1, "Lectern" } });
        camera.RecallPreset(1);
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x01, 0x90, 0x60, 0x02, 0xFF]);

        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to query the power state: Syntax error");
    }

    [Fact]
    public async Task HandleResponse_ErrorForACommandReusingAnUnansweredInquirySequenceNumber_ReportsTheGenericError()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);
        for (var i = 0; i <= byte.MaxValue; i++)
            camera.ZoomIn();

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x61, 0x41, 0xFF]);

        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == "Command not executable");
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x11, 0x00, 0x04 })]
    [InlineData(new byte[] { 0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x01, 0x90, 0x61 })]
    public void HandleResponse_TruncatedFrame_DoesNotThrow(byte[] response)
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke(response));

        Assert.Null(exception);
        Assert.Equal(CommunicationState.Error, _viscaCamera.CommunicationState);
    }
}

internal static class SonyViscaPolling
{
    public static Task InvokePoll(SonyVisca camera) =>
        (Task)typeof(SonyVisca).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(camera, [CancellationToken.None])!;
}

internal class ConnectedClient : CommunicationClient
{
    public readonly List<byte[]> Sent = [];

    public ConnectedClient() : base("Test", "host", 52381, CommandStringFormat.Hex)
    {
        ConnectionState = ConnectionState.Connected;
    }

    public void SetConnectionState(ConnectionState state) => ConnectionState = state;

    public override void Send(string message) { }

    public override void Send(byte[] bytes) { lock (Sent) Sent.Add(bytes); }
}

internal class SecondInquiryCamera(CommunicationClient client, bool useIpHeaders)
    : SonyVisca(client, useIpHeaders, "Test Cam", new Dictionary<int, string>())
{
    public readonly List<byte[]> SecondReplies = [];

    protected override void SendPollInquiries()
    {
        base.SendPollInquiries();
        SendInquiry([_header, 0x09, 0x08, 0x01, CommandFooter], "query tracking", reply => SecondReplies.Add(reply));
    }
}