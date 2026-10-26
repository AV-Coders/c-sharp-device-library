using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

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

    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(6, 0x06)]
    public void RecallPreset_SendsThePresetNumber(int preset, byte expectedPreset)
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x3f, 0x02, expectedPreset, 0xFF];
        _viscaCamera.DoRecallPreset(preset);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(6, 0x06)]
    public void SavePreset_SendsThePresetNumber(int preset, byte expectedPreset)
    {
        byte[] expectedCommand = [0x81, 0x01, 0x04, 0x3f, 0x01, expectedPreset, 0xFF];
        _viscaCamera.SavePreset(preset);

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
        Assert.Equal("Preset Lectern recalled", Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Preset).Info);
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

        Assert.Equal([
            [0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x09, 0x08, 0x01, 0xFF], SonyViscaPolling.VersionInquiry
        ], client.Sent);
        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.Equal([[0x90, 0x50, 0x03, 0xFF]], camera.SecondReplies);
    }

    [Fact]
    public async Task Poll_SendsTheSecondInquiryOnlyAfterTheFirstIsAnswered()
    {
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false);

        await SonyViscaPolling.InvokePoll(camera);
        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF]], client.Sent);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x09, 0x08, 0x01, 0xFF]], client.Sent);
    }

    [Fact]
    public async Task Poll_InquiryThatGetsNoReply_ExpiresAndTheNextIsSent()
    {
        var time = new FakeTimeProvider();
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false, time);
        await SonyViscaPolling.InvokePoll(camera);

        SonyViscaPolling.ExpireInquiries(time, 1);
        Assert.Equal([[0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x09, 0x08, 0x01, 0xFF]], client.Sent);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(PowerState.Unknown, camera.PowerState);
        Assert.Equal([[0x90, 0x50, 0x02, 0xFF]], camera.SecondReplies);
    }

    [Fact]
    public async Task Poll_WhileAnInquiryAwaitsItsReply_KeepsThatInquiry()
    {
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF, 0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal([[0x90, 0x50, 0x03, 0xFF]], camera.SecondReplies);
        Assert.Equal(PowerState.Off, camera.PowerState);
    }

    [Fact]
    public async Task HandleResponse_InquiryUnansweredForASecond_TakesNoLaterReply()
    {
        var time = new FakeTimeProvider();
        var client = new ConnectedClient();
        var camera = new SecondInquiryCamera(client, false, time);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        SonyViscaPolling.ExpireInquiries(time, 2);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(PowerState.Off, camera.PowerState);
        Assert.Empty(camera.SecondReplies);
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
    public async Task HandleResponse_CommandSyntaxErrorWhileTheVersionInquiryIsPending_IsReportedForTheCommand()
    {
        var client = new ConnectedClient();
        var camera = new RecallDuringPollCamera(client);
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.SyntaxErrorReply);
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReply);

        Assert.Equal([
            [0x81, 0x09, 0x04, 0x00, 0xFF], [0x81, 0x01, 0x04, 0x3F, 0x02, 0x01, 0xFF], SonyViscaPolling.VersionInquiry
        ], client.Sent);
        Assert.Contains(camera.Events,
            e => e.Type == EventType.Error && e.Info == "Unable to recall preset Lectern: Syntax error");
        Assert.Equal("0000", camera.Details.Single(detail => detail.Label == "Model ID").Value);
        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task HandleResponse_LostPowerReply_LeavesTheLaterRepliesWithTheirInquiries()
    {
        var time = new FakeTimeProvider();
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>(), 1, 30, time);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        time.Advance(TimeSpan.FromSeconds(1));
        await SonyViscaPolling.InvokePoll(camera);
        time.Advance(TimeSpan.FromSeconds(1));

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x20, 0x20, 0xFF]);
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReply);

        Assert.Equal("pan-mechanism", Assert.Single(camera.GetOngoingIssues()).Key);
        Assert.Equal("0000", camera.Details.Single(detail => detail.Label == "Model ID").Value);

        time.Advance(TimeSpan.FromSeconds(1));
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(PowerState.Off, camera.PowerState);
    }

    [Fact]
    public void HandleResponse_CompletionAfterLaterTraffic_IsMatchedByItsSocket()
    {
        _viscaCamera.RecallPreset(1);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);
        var saveId = Trigger.Start();
        _viscaCamera.SavePreset(0);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x42, 0xFF]);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x51, 0xFF]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x62, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Error && e.Info.StartsWith("Unable to"));
        Assert.Equal("Unable to save preset Home: Command not executable", error.Info);
        Assert.Equal(saveId, error.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void HandleResponse_ErrorAfterLaterTraffic_IsMatchedByItsSocket()
    {
        var recallId = Trigger.Start();
        _viscaCamera.RecallPreset(1);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);
        Trigger.Start();
        _viscaCamera.SavePreset(0);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x42, 0xFF, 0x90, 0x52, 0xFF]);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Error && e.Info.StartsWith("Unable to"));
        Assert.Equal("Unable to recall preset Lectern: Command not executable", error.Info);
        Assert.Equal(recallId, error.Context[LogBase.TriggerIdProperty]);
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
        client.ResponseByteHandlers!.Invoke([0x90, 0x50]);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.SetConnectionState(ConnectionState.Connected);
        await SonyViscaPolling.InvokePoll(camera);
        camera.RecallPreset(1);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);

        Assert.Equal(1, camera.LastRecalledPreset);
        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task ConnectionChange_ForgetsTheRequestsSentBeforeIt()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string> { { 1, "Lectern" } });
        await SonyViscaPolling.InvokePoll(camera);
        camera.RecallPreset(1);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);
        client.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        Assert.Equal(CameraBase.NoActivePreset, camera.LastRecalledPreset);
        Assert.Equal(PowerState.Unknown, camera.PowerState);
        Assert.Contains(camera.Events, e => e.Type == EventType.Error && e.Info == "Command not executable");
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

    [Fact]
    public void RecallPreset_Ack_ReportsTheRecallUnderTheRecallsTriggerId()
    {
        var recallId = Trigger.Start();
        _viscaCamera.RecallPreset(1);
        var receiveId = Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);

        var recalled = Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Preset);
        Assert.Equal(recallId, recalled.Context[LogBase.TriggerIdProperty]);
        Assert.Equal(receiveId, Trigger.Id);
    }

    [Fact]
    public async Task HandleResponse_PowerInquiryReply_SetsThePowerStateUnderThePollsTriggerId()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        var powerChangeIds = new List<string?>();
        camera.PowerStateHandlers += _ => powerChangeIds.Add(Trigger.Id);
        var pollId = Trigger.Start();
        await SonyViscaPolling.InvokePoll(camera);
        Trigger.Start();

        client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal([pollId], powerChangeIds);
    }

    [Fact]
    public void HandleResponse_ErrorForAPendingRecall_ReportsItUnderTheRecallsTriggerId()
    {
        var recallId = Trigger.Start();
        _viscaCamera.RecallPreset(1);
        Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events,
            e => e.Info == "Unable to recall preset Lectern: Command not executable");
        Assert.Equal(recallId, error.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void HandleResponse_ErrorForACommandWithNoDescription_ReportsTheGenericErrorUnderItsTriggerId()
    {
        var zoomId = Trigger.Start();
        _viscaCamera.ZoomIn();
        Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events, e => e.Info == "Command not executable");
        Assert.Equal(zoomId, error.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void HandleResponse_ErrorForARecallSentWithNoTriggerId_KeepsTheReceiveTriggerId()
    {
        Assert.Null(Trigger.Id);
        _viscaCamera.RecallPreset(1);
        var receiveId = Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events,
            e => e.Info == "Unable to recall preset Lectern: Command not executable");
        Assert.Equal(receiveId, error.Context[LogBase.TriggerIdProperty]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(255)]
    [InlineData(256)]
    public void RecallPreset_OutOfRange_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _viscaCamera.RecallPreset(preset);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is out of range, it must be between 0 and 254");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(255)]
    [InlineData(256)]
    public void SavePreset_OutOfRange_SendsNothingAndAddsAnErrorEvent(int preset)
    {
        _viscaCamera.SavePreset(preset);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == $"Preset {preset} is out of range, it must be between 0 and 254");
        Assert.DoesNotContain(_viscaCamera.Events, e => e.Type == EventType.Preset);
    }

    [Fact]
    public void RecallPreset_OutOfRangeWhenTheDeviceDoesNotSendResponses_KeepsTheLastPreset()
    {
        _viscaCamera.DeviceSendsResponses = false;
        _viscaCamera.RecallPreset(1);

        _viscaCamera.RecallPreset(256);

        Assert.Equal(1, _viscaCamera.LastRecalledPreset);
    }

    [Fact]
    public void RecallPreset_HighestPreset_SendsIt()
    {
        _viscaCamera.RecallPreset(254);

        _mockClient.Verify(x => x.Send(new byte[] { 0x81, 0x01, 0x04, 0x3F, 0x02, 0xFE, 0xFF }), Times.Once);
    }

    [Theory]
    [InlineData(0, 0x20, 0x30)]
    [InlineData(7, 0x27, 0x37)]
    public void SetZoomSpeed_InRange_SetsTheZoomSpeeds(byte speed, byte zoomIn, byte zoomOut)
    {
        _viscaCamera.SetZoomSpeed(speed);
        _viscaCamera.ZoomIn();
        _viscaCamera.ZoomOut();

        _mockClient.Verify(x => x.Send(new byte[] { 0x81, 0x01, 0x04, 0x07, zoomIn, 0xFF }), Times.Once);
        _mockClient.Verify(x => x.Send(new byte[] { 0x81, 0x01, 0x04, 0x07, zoomOut, 0xFF }), Times.Once);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0x10)]
    [InlineData(0xE0)]
    public void SetZoomSpeed_OutOfRange_KeepsTheZoomSpeedsAndAddsAnErrorEvent(byte speed)
    {
        _viscaCamera.SetZoomSpeed(speed);
        _viscaCamera.ZoomIn();

        _mockClient.Verify(x => x.Send(new byte[] { 0x81, 0x01, 0x04, 0x07, 0x23, 0xFF }), Times.Once);
        Assert.Contains(_viscaCamera.Events,
            e => e.Type == EventType.Error && e.Info == $"Zoom speed {speed} is out of range, it must be between 0 and 7");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(0x80)]
    public async Task Constructor_CameraIdOutOfRange_RaisesAnIssueAndSendsNothing(byte cameraId)
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>(), cameraId);

        camera.PowerOn();
        await SonyViscaPolling.InvokePoll(camera);

        Assert.Empty(client.Sent);
        Assert.Contains(camera.GetOngoingIssues(),
            i => i.Key == "camera-id" && i.Message == $"Camera id {cameraId} is out of range, it must be between 1 and 7");
    }

    [Fact]
    public void SetCameraId_InRange_SendsToThatCameraAndResolvesTheIssue()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>(), 8);

        camera.SetCameraId(7);
        camera.PowerOn();

        Assert.Equal([[0x87, 0x01, 0x04, 0x00, 0x02, 0xFF]], client.Sent);
        Assert.DoesNotContain(camera.GetOngoingIssues(), i => i.Key == "camera-id");
    }

    [Fact]
    public void PowerOn_WhenTheDeviceSendsResponses_LeavesTheCommunicationStateUntilAReply()
    {
        _viscaCamera.PowerOn();

        Assert.Equal(CommunicationState.NotAttempted, _viscaCamera.CommunicationState);

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x90, 0x41, 0xFF]);

        Assert.Equal(CommunicationState.Okay, _viscaCamera.CommunicationState);
    }

    [Fact]
    public void PowerOn_WhenTheDeviceDoesNotSendResponses_SetsTheCommunicationStateOkay()
    {
        _viscaCamera.DeviceSendsResponses = false;

        _viscaCamera.PowerOn();

        Assert.Equal(CommunicationState.Okay, _viscaCamera.CommunicationState);
    }

    [Fact]
    public void Dispose_StopsThePollWorkerAndUnsubscribes()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, false, "Test Cam", new Dictionary<int, string>());
        var pollWorker = (ThreadWorker)typeof(SonyVisca)
            .GetField("_pollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(camera)!;
        Assert.True(pollWorker.IsRunning);

        camera.Dispose();

        Assert.False(pollWorker.IsRunning);
        Assert.Null(client.ResponseByteHandlers);
        Assert.Null(client.ConnectionStateHandlers);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class SonyViscaLogTest
{
    private readonly ConnectedClient _client = new();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly SonyVisca _camera;

    public SonyViscaLogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _camera = new SonyVisca(_client, false, "Test Cam", new Dictionary<int, string> { { 1, "Lectern" } });
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    [Fact]
    public async Task HandleResponse_UnsolicitedInquiryReply_LeavesThePowerStateAndLogsAWarning()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        _client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReply);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x03, 0xFF]);

        Assert.Equal(PowerState.On, _camera.PowerState);
        Assert.Contains(_logs.Logs,
            log => log.Level == LogLevel.Warning && log.Message == "Unexpected inquiry reply 90-50-03-FF");
    }

    [Fact]
    public void SendCommand_MoreCommandsAwaitingAReplyThanTheTrackerHolds_LogsTheDroppedCommand()
    {
        for (var command = 0; command <= SentCommandTracker<int>.MaxCommands; command++)
            _camera.ZoomIn();

        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning &&
            log.Message == "Dropped 81-01-04-07-23-FF: more than 256 requests awaiting a reply");
    }

    [Fact]
    public async Task HandleResponse_ReplyOfALengthNoInquiryExpects_IsLoggedAndLeavesTheInquiriesWaiting()
    {
        await SonyViscaPolling.InvokePoll(_camera);

        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x00, 0x01, 0x05, 0xFF]);
        _client.ResponseByteHandlers!.Invoke([0x90, 0x50, 0x02, 0xFF]);
        _client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReply);

        Assert.Contains(_logs.Logs,
            log => log.Level == LogLevel.Warning && log.Message == "Unexpected inquiry reply 90-50-00-01-05-FF");
        Assert.Equal(PowerState.On, _camera.PowerState);
        Assert.Equal("0000", _camera.Details.Single(detail => detail.Label == "Model ID").Value);
    }

    [Fact]
    public void HandleResponse_ErrorForAPendingRecall_LogsUnderTheRecallsTriggerId()
    {
        var recallId = Trigger.Start();
        _camera.RecallPreset(1);
        Trigger.Start();

        _client.ResponseByteHandlers!.Invoke([0x90, 0x61, 0x41, 0xFF]);

        Assert.Contains(_logs.Logs,
            log => log.Message.StartsWith("The camera reported an error") && log.TriggerId == recallId);
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
            [0x01, 0x10, 0x00, 0x05, 0xFF, 0xFF, 0xFF, 0x01, 0x81, 0x09, 0x08, 0x01, 0xFF],
            [0x01, 0x10, 0x00, 0x05, 0xFF, 0xFF, 0xFF, 0x02, ..SonyViscaPolling.VersionInquiry]
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
    public void HandleResponse_TruncatedFrame_IsHeldWithoutAnError(byte[] response)
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseByteHandlers!.Invoke(response));

        Assert.Null(exception);
        Assert.Equal(CommunicationState.NotAttempted, _viscaCamera.CommunicationState);
        Assert.DoesNotContain(_viscaCamera.Events, e => e.Type == EventType.Error);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task HandleResponse_SplitFrame_IsReassembled(int split)
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);
        byte[] reply = [0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, 0x02, 0xFF];

        client.ResponseByteHandlers!.Invoke(reply[..split]);
        client.ResponseByteHandlers!.Invoke(reply[split..]);

        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public async Task HandleResponse_FrameLongerThanSixteenBytes_ReportsAMalformedResponseAndIsCleared()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x11, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50]);

        Assert.Equal(CommunicationState.Error, camera.CommunicationState);
        Assert.Contains(camera.Events, e => e.Type == EventType.Error && e.Info == "The response was malformed");

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task HandleResponse_CorruptDatagramThenAGoodReply_ReportsTheCorruptOneAndProcessesTheReply()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90]);
        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.Equal(CommunicationState.Okay, camera.CommunicationState);
        Assert.Single(camera.Events, e => e.Type == EventType.Error && e.Info == "The response was malformed");
    }

    [Fact]
    public async Task HandleResponse_PartialFrameBeforeAConnectionChange_IsDropped()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00]);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal(PowerState.On, camera.PowerState);
        Assert.DoesNotContain(camera.Events, e => e.Type == EventType.Error);
    }

    [Fact]
    public void SendCommand_PayloadLongerThan255Bytes_WritesTheLengthHighByte()
    {
        var client = new ConnectedClient();
        var camera = new LongCommandCamera(client);

        camera.SendLongCommand(300);

        Assert.Equal([0x01, 0x00, 0x01, 0x2C], client.Sent[0][..4]);
    }

    [Fact]
    public void RecallPreset_CompletionEchoingItsSequenceNumber_ReportsTheRecallUnderTheRecallsTriggerId()
    {
        var recallId = Trigger.Start();
        _viscaCamera.RecallPreset(1);
        Trigger.Start();
        _viscaCamera.PowerOn();
        Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([
            0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x01, 0x90, 0x51, 0xFF,
            0x01, 0x11, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x51, 0xFF
        ]);

        var recalled = Assert.Single(_viscaCamera.Events, e => e.Type == EventType.Preset);
        Assert.Equal(recallId, recalled.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public async Task HandleResponse_PowerInquiryReply_SetsThePowerStateUnderThePollsTriggerId()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        var powerChangeIds = new List<string?>();
        camera.PowerStateHandlers += _ => powerChangeIds.Add(Trigger.Id);
        var pollId = Trigger.Start();
        await SonyViscaPolling.InvokePoll(camera);
        Trigger.Start();

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x50, 0x02, 0xFF]);

        Assert.Equal([pollId], powerChangeIds);
    }

    [Fact]
    public void HandleResponse_ErrorEchoingARecallSequenceNumber_ReportsItUnderTheRecallsTriggerId()
    {
        var recallId = Trigger.Start();
        _viscaCamera.RecallPreset(1);
        Trigger.Start();
        _viscaCamera.PowerOn();
        Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x00, 0x90, 0x62, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events, e => e.Info == "Unable to recall preset Lectern: Command not executable");
        Assert.Equal(recallId, error.Context[LogBase.TriggerIdProperty]);
    }

    [Fact]
    public void HandleResponse_ErrorEchoingAnUnrelatedSequenceNumber_KeepsTheReceiveTriggerId()
    {
        Trigger.Start();
        _viscaCamera.RecallPreset(1);
        var receiveId = Trigger.Start();

        _mockClient.Object.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x07, 0x90, 0x61, 0x41, 0xFF]);

        var error = Assert.Single(_viscaCamera.Events, e => e.Info == "Command not executable");
        Assert.Equal(receiveId, error.Context[LogBase.TriggerIdProperty]);
    }
}

public class SonyViscaStatusTest
{
    private static readonly byte[] PowerOnReply = [0x90, 0x50, 0x02, 0xFF];
    private static readonly byte[] PowerOffReply = [0x90, 0x50, 0x03, 0xFF];
    private readonly ConnectedClient _client = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SonyVisca _camera;

    public SonyViscaStatusTest()
    {
        _camera = new SonyVisca(_client, false, "Test Cam", new Dictionary<int, string>(), 1, 30, _time);
    }

    private void Reply(byte[] reply) => _client.ResponseByteHandlers!.Invoke(reply);

    private string? Detail(string label) => _camera.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? Ongoing(string key) => _camera.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    private int Sends(byte[] inquiry) => _client.Sent.Count(sent => sent.SequenceEqual(inquiry));

    private async Task FirstPollAsync(byte[] versionReply)
    {
        await SonyViscaPolling.InvokePoll(_camera);
        Reply(PowerOnReply);
        Reply(versionReply);
    }

    private async Task PollAndAnswerAsync(byte[] panTiltStatusReply)
    {
        await SonyViscaPolling.InvokePoll(_camera);
        Reply(PowerOnReply);
        Reply(panTiltStatusReply);
    }

    private async Task PollAndAnswerOnlyThePowerAsync()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        Reply(PowerOnReply);
        SonyViscaPolling.ExpireInquiries(_time, 1);
    }

    [Fact]
    public async Task CourtCameraVersionReply_ShowsTheAllZeroModelAndRomAsReported()
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);

        Assert.Equal(
        [
            new DeviceDetail("Vendor ID", "0001", DetailTone.Normal),
            new DeviceDetail("Model ID", "0000", DetailTone.Normal),
            new DeviceDetail("ROM Revision", "0000", DetailTone.Normal)
        ], _camera.Details);
        Assert.Empty(_camera.GetIssues());
    }

    [Fact]
    public async Task CourtCameraBenchConversation_ShowsTheVersionAndThatThePanTiltStatusIsNotSupported()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "CCV Court 7-3 - Bench", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.Logged(@"\x01 \x11 \x00 \x04 \xFF \xFF \xFF \x00 \x90 \x50 \x02 \xFF"));
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReplyOverIp);
        await SonyViscaPolling.InvokePoll(camera);
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.Logged(@"\x01 \x11 \x00 \x04 \xFF \xFF \xFF \x02 \x90 \x50 \x02 \xFF"));
        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraPanTiltSyntaxErrorOverIp);

        Assert.Equal(new[] { "01100005FFFFFF0081090400FF", "01100005FFFFFF0181090002FF",
            "01100005FFFFFF0281090400FF", "01100005FFFFFF0381090610FF" }.Select(hex => Convert.FromHexString(hex)), client.Sent);
        Assert.Equal(
        [
            new DeviceDetail("Vendor ID", "0001", DetailTone.Normal),
            new DeviceDetail("Model ID", "0000", DetailTone.Normal),
            new DeviceDetail("ROM Revision", "0000", DetailTone.Normal),
            new DeviceDetail("Pan/Tilt Status", "Not supported: 90-60-02-FF", DetailTone.Normal)
        ], camera.Details);
        Assert.Empty(camera.GetIssues());
        Assert.Equal(CommunicationState.Okay, camera.CommunicationState);
        Assert.Equal(PowerState.On, camera.PowerState);

        await SonyViscaPolling.InvokePoll(camera);

        Assert.Equal(Convert.FromHexString("01100005FFFFFF0481090400FF"), Assert.Single(client.Sent[4..]));
    }

    [Fact]
    public async Task Poll_WhileThePowerIsNotOn_DoesNotAskForThePanTiltStatus()
    {
        await SonyViscaPolling.InvokePoll(_camera);
        Reply([0x90, 0x50, 0x03, 0xFF]);

        await SonyViscaPolling.InvokePoll(_camera);

        Assert.Equal(0, Sends(SonyViscaPolling.PanTiltStatusInquiry));
        Assert.Empty(_camera.GetOngoingIssues());
    }

    [Fact]
    public async Task Poll_AfterTheVersionIsAnswered_AsksOnlyForThePanTiltStatusAgain()
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerAsync(SonyViscaPolling.HealthyPanTiltStatusReply);

        await SonyViscaPolling.InvokePoll(_camera);
        Reply(PowerOnReply);

        Assert.Equal(1, Sends(SonyViscaPolling.VersionInquiry));
        Assert.Equal(2, Sends(SonyViscaPolling.PanTiltStatusInquiry));
    }

    [Fact]
    public async Task Poll_UnansweredVersionInquiry_IsAskedAgainAndRaisesTheReadIssueOnTheThirdMissUntilAnswered()
    {
        for (var poll = 0; poll < 3; poll++)
        {
            await SonyViscaPolling.InvokePoll(_camera);
            Reply(PowerOffReply);
            SonyViscaPolling.ExpireInquiries(_time, 1);
        }

        Assert.Empty(_camera.GetIssues());
        Assert.Equal(3, Sends(SonyViscaPolling.VersionInquiry));

        await SonyViscaPolling.InvokePoll(_camera);

        Assert.Equal(IssueSeverity.Minor, Ongoing("version-read")!.Severity);
        Assert.Equal("The camera did not answer the version inquiry 3 times in a row, the last was no reply",
            Ongoing("version-read")!.Message);

        Reply(PowerOffReply);
        Assert.Equal(4, Sends(SonyViscaPolling.VersionInquiry));
        Reply(SonyViscaPolling.CourtCameraVersionReply);

        Assert.Empty(_camera.GetOngoingIssues());
        Assert.Equal("0000", Detail("Model ID"));
    }

    [Fact]
    public async Task Poll_UnansweredPanTiltStatusInquiry_IsAskedAgainAndRaisesTheReadIssueOnTheThirdMissUntilAnswered()
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerOnlyThePowerAsync();
        await PollAndAnswerOnlyThePowerAsync();
        await PollAndAnswerOnlyThePowerAsync();

        Assert.Empty(_camera.GetIssues());
        Assert.Equal(3, Sends(SonyViscaPolling.PanTiltStatusInquiry));

        await SonyViscaPolling.InvokePoll(_camera);

        Assert.Equal("The camera did not answer the pan/tilt status inquiry 3 times in a row, the last was no reply",
            Ongoing("pan-tilt-status-read")!.Message);

        Reply(PowerOnReply);
        Assert.Equal(4, Sends(SonyViscaPolling.PanTiltStatusInquiry));
        Reply(SonyViscaPolling.HealthyPanTiltStatusReply);

        Assert.Empty(_camera.GetOngoingIssues());
    }

    [Fact]
    public async Task VersionReplyOfTheWrongLengthOverIp_IsAskedAgainAndRaisesTheReadIssueWithTheRawReplyOnTheThirdInARow()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        byte[] wrongLength = [0x90, 0x50, 0x03, 0xFF];
        void IpReply(byte sequenceNumber, byte[] payload) => client.ResponseByteHandlers!.Invoke(
            [0x01, 0x11, 0x00, (byte)payload.Length, 0xFF, 0xFF, 0xFF, sequenceNumber, ..payload]);
        await SonyViscaPolling.InvokePoll(camera);
        IpReply(0x00, PowerOnReply);
        IpReply(0x01, wrongLength);
        await SonyViscaPolling.InvokePoll(camera);
        IpReply(0x02, PowerOnReply);
        IpReply(0x03, SonyViscaPolling.HealthyPanTiltStatusReply);
        IpReply(0x04, wrongLength);

        Assert.Empty(camera.GetIssues());

        await SonyViscaPolling.InvokePoll(camera);
        IpReply(0x05, PowerOnReply);
        IpReply(0x06, SonyViscaPolling.HealthyPanTiltStatusReply);
        IpReply(0x07, wrongLength);

        Assert.Equal(3, client.Sent.Count(sent => sent[8..].SequenceEqual(SonyViscaPolling.VersionInquiry)));
        var issue = camera.GetOngoingIssues().Single(i => i.Key == "version-read");
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The camera did not answer the version inquiry 3 times in a row, the last was 90-50-03-FF, which is not a version reply",
            issue.Message);
        Assert.DoesNotContain(camera.Details, detail => detail.Label == "Vendor ID");

        await SonyViscaPolling.InvokePoll(camera);
        IpReply(0x08, PowerOnReply);
        IpReply(0x09, SonyViscaPolling.HealthyPanTiltStatusReply);
        IpReply(0x0A, SonyViscaPolling.CourtCameraVersionReply);

        Assert.Empty(camera.GetOngoingIssues());
        Assert.Equal("0000", camera.Details.Single(detail => detail.Label == "Model ID").Value);
    }

    [Fact]
    public async Task VersionInquiryRejected_ShowsItIsNotSupportedWithNoIssueAndIsNotAskedAgain()
    {
        await FirstPollAsync(SonyViscaPolling.SyntaxErrorReply);

        await PollAndAnswerAsync(SonyViscaPolling.HealthyPanTiltStatusReply);

        Assert.Equal(
        [
            new DeviceDetail("Vendor ID", "Not supported: 90-60-02-FF", DetailTone.Normal),
            new DeviceDetail("Model ID", "Not supported: 90-60-02-FF", DetailTone.Normal),
            new DeviceDetail("ROM Revision", "Not supported: 90-60-02-FF", DetailTone.Normal),
            new DeviceDetail("Pan/Tilt Status", "Normal: 90-50-20-00-FF", DetailTone.Normal)
        ], _camera.Details);
        Assert.Empty(_camera.GetIssues());
        Assert.Equal(CommunicationState.Okay, _camera.CommunicationState);
        Assert.Equal(1, Sends(SonyViscaPolling.VersionInquiry));
        Assert.Equal(1, Sends(SonyViscaPolling.PanTiltStatusInquiry));
        Assert.Equal(PowerState.On, _camera.PowerState);
    }

    [Fact]
    public async Task PanTiltStatusRejectedWithASyntaxError_ShowsItIsNotSupportedWithNoIssueAndStopsAsking()
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerAsync(SonyViscaPolling.SyntaxErrorReply);

        await SonyViscaPolling.InvokePoll(_camera);

        Assert.Contains(new DeviceDetail("Pan/Tilt Status", "Not supported: 90-60-02-FF", DetailTone.Normal), _camera.Details);
        Assert.Empty(_camera.GetIssues());
        Assert.Equal(CommunicationState.Okay, _camera.CommunicationState);
        Assert.Equal(1, Sends(SonyViscaPolling.PanTiltStatusInquiry));
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x60, 0x03, 0xFF }, "90-60-03-FF (Command buffer full)")]
    [InlineData(new byte[] { 0x90, 0x60, 0x04, 0xFF }, "90-60-04-FF (Command cancelled)")]
    [InlineData(new byte[] { 0x90, 0x60, 0x05, 0xFF }, "90-60-05-FF (No socket)")]
    [InlineData(new byte[] { 0x90, 0x60, 0x41, 0xFF }, "90-60-41-FF (Command not executable)")]
    public async Task PanTiltStatusAnsweredWithATemporaryError_IsAskedAgainAndRaisesTheReadIssueOnTheThirdMiss(byte[] error, string raw)
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerAsync(error);
        await PollAndAnswerAsync(error);

        Assert.Empty(_camera.GetIssues());

        await PollAndAnswerAsync(error);

        Assert.Equal(3, Sends(SonyViscaPolling.PanTiltStatusInquiry));
        Assert.Equal(IssueSeverity.Minor, Ongoing("pan-tilt-status-read")!.Severity);
        Assert.Equal($"The camera did not answer the pan/tilt status inquiry 3 times in a row, the last was {raw}",
            Ongoing("pan-tilt-status-read")!.Message);
        Assert.Null(Detail("Pan/Tilt Status"));

        await PollAndAnswerAsync(SonyViscaPolling.HealthyPanTiltStatusReply);

        Assert.Equal(4, Sends(SonyViscaPolling.PanTiltStatusInquiry));
        Assert.Empty(_camera.GetOngoingIssues());
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x50, 0x20, 0x20, 0xFF }, "pan-mechanism", "The pan mechanism is defective")]
    [InlineData(new byte[] { 0x90, 0x50, 0x20, 0x10, 0xFF }, "pan-position", "The pan position cannot be detected")]
    [InlineData(new byte[] { 0x90, 0x50, 0x22, 0x00, 0xFF }, "tilt-mechanism", "The tilt mechanism is defective")]
    [InlineData(new byte[] { 0x90, 0x50, 0x21, 0x00, 0xFF }, "tilt-position", "The tilt position cannot be detected")]
    [InlineData(new byte[] { 0x90, 0x50, 0x2C, 0x00, 0xFF }, "pan-tilt-operation", "A pan/tilt operation failed")]
    [InlineData(new byte[] { 0x90, 0x50, 0x30, 0x00, 0xFF }, "pan-tilt-initialisation", "The pan/tilt initialisation failed")]
    public async Task PanTiltStatus_Fault_RaisesAMajorIssueUntilCleared(byte[] reply, string key, string message)
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerAsync(reply);

        var issue = Assert.Single(_camera.GetOngoingIssues());
        Assert.Equal(key, issue.Key);
        Assert.Equal(message, issue.Message);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Null(Detail("Pan/Tilt Status"));

        await PollAndAnswerAsync(SonyViscaPolling.HealthyPanTiltStatusReply);

        Assert.Empty(_camera.GetOngoingIssues());
        Assert.Equal("Normal: 90-50-20-00-FF", Detail("Pan/Tilt Status"));
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x50, 0x20, 0x0F, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x50, 0x20, 0xC0, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x50, 0x24, 0x00, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x50, 0x28, 0x00, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x50, 0x10, 0x00, 0xFF })]
    [InlineData(new byte[] { 0x90, 0x50, 0x00, 0x00, 0xFF })]
    public async Task PanTiltStatus_LimitsMovementAndInitialising_RaiseNoIssueAndShowNormalWithTheRawReply(byte[] reply)
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerAsync(reply);

        Assert.Empty(_camera.GetOngoingIssues());
        Assert.Contains(new DeviceDetail("Pan/Tilt Status", $"Normal: {BitConverter.ToString(reply)}", DetailTone.Normal), _camera.Details);
    }

    [Fact]
    public async Task Reconnection_AsksForTheVersionAndARejectedStatusAgain()
    {
        await FirstPollAsync(SonyViscaPolling.CourtCameraVersionReply);
        await PollAndAnswerAsync(SonyViscaPolling.SyntaxErrorReply);

        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        await SonyViscaPolling.InvokePoll(_camera);
        Reply(PowerOnReply);
        Reply(SonyViscaPolling.SyntaxErrorReply);

        Assert.Equal(2, Sends(SonyViscaPolling.VersionInquiry));
        Assert.Equal(2, Sends(SonyViscaPolling.PanTiltStatusInquiry));
    }

    [Fact]
    public async Task VersionReplyOverIp_IsMatchedBySequenceNumber()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);

        client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReplyOverIp);

        Assert.Equal("0001", camera.Details.Single(detail => detail.Label == "Vendor ID").Value);
    }

    [Fact]
    public async Task VersionRejectedOverIp_ShowsItIsNotSupportedWithNoIssue()
    {
        var client = new ConnectedClient();
        var camera = new SonyVisca(client, true, "Test Camera", new Dictionary<int, string>());
        await SonyViscaPolling.InvokePoll(camera);
        var communicationState = camera.CommunicationState;

        client.ResponseByteHandlers!.Invoke([0x01, 0x11, 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0x01, ..SonyViscaPolling.SyntaxErrorReply]);

        Assert.Contains(new DeviceDetail("Model ID", "Not supported: 90-60-02-FF", DetailTone.Normal), camera.Details);
        Assert.Empty(camera.GetIssues());
        Assert.Equal(communicationState, camera.CommunicationState);
    }
}

public class SonyViscaSilentCameraTest
{
    private static readonly string[] CourtBenchFirstFourPollsOverIp =
    [
        "01100005FFFFFF0081090400FF", "01100005FFFFFF0181090002FF",
        "01100005FFFFFF0281090400FF", "01100005FFFFFF0381090002FF",
        "01100005FFFFFF0481090400FF", "01100005FFFFFF0581090002FF",
        "01100005FFFFFF0681090400FF", "01100005FFFFFF0781090002FF"
    ];
    private const string PowerOnReplyToSequenceSixOverIp = @"\x01 \x11 \x00 \x04 \xFF \xFF \xFF \x06 \x90 \x50 \x02 \xFF";
    private static readonly byte[] PowerOnReply = [0x90, 0x50, 0x02, 0xFF];
    private const int InquiriesPerPoll = 3;
    private readonly ConnectedClient _client = new();
    private readonly FakeTimeProvider _time = new();

    private async Task PollAsync(SonyVisca camera, int polls)
    {
        for (var poll = 0; poll < polls; poll++)
        {
            await SonyViscaPolling.InvokePoll(camera);
            SonyViscaPolling.ExpireInquiries(_time, InquiriesPerPoll);
        }
    }

    private static void AssertOnlyTheCommunicationIssue(SonyVisca camera)
    {
        var issue = Assert.Single(camera.GetOngoingIssues());
        Assert.Equal("communication", issue.Key);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal("Device communication error", issue.Message);
    }

    [Fact]
    public async Task ThreeSilentPollsOverIp_SetCommunicationToErrorWithTheCriticalIssueAndNoVersionReadIssue()
    {
        var camera = new SonyVisca(_client, true, "Test Camera", new Dictionary<int, string>());
        await PollAsync(camera, 3);

        Assert.Equal(CommunicationState.NotAttempted, camera.CommunicationState);
        Assert.Empty(camera.GetIssues());

        await SonyViscaPolling.InvokePoll(camera);

        Assert.Equal(CourtBenchFirstFourPollsOverIp.Select(hex => Convert.FromHexString(hex)), _client.Sent);
        Assert.Equal(CommunicationState.Error, camera.CommunicationState);
        AssertOnlyTheCommunicationIssue(camera);
        Assert.Contains(camera.Events, e => e.Type == EventType.Error &&
            e.Info == "No reply to 01-10-00-05-FF-FF-FF-04-81-09-04-00-FF, 01-10-00-05-FF-FF-FF-05-81-09-00-02-FF");
    }

    [Fact]
    public async Task ThreeSilentPollsWithoutIpHeaders_SetCommunicationToErrorWithTheCriticalIssueAndNoVersionReadIssue()
    {
        var camera = new SonyVisca(_client, false, "Test Cam", new Dictionary<int, string>(), 1, 30, _time);
        await PollAsync(camera, 3);

        Assert.Equal(CommunicationState.NotAttempted, camera.CommunicationState);
        Assert.Empty(camera.GetIssues());

        await SonyViscaPolling.InvokePoll(camera);

        Assert.Equal(CommunicationState.Error, camera.CommunicationState);
        AssertOnlyTheCommunicationIssue(camera);
        Assert.Contains(camera.Events, e => e.Type == EventType.Error && e.Info == "No reply to 81-09-04-00-FF, 81-09-00-02-FF");
    }

    [Fact]
    public async Task ReplyAfterTheSilentPollsOverIp_RestoresCommunicationAndResolvesTheIssue()
    {
        var camera = new SonyVisca(_client, true, "Test Camera", new Dictionary<int, string>());
        await PollAsync(camera, 4);

        _client.ResponseByteHandlers!.Invoke(SonyViscaPolling.Logged(PowerOnReplyToSequenceSixOverIp));

        Assert.Equal(CommunicationState.Okay, camera.CommunicationState);
        Assert.Empty(camera.GetOngoingIssues());
        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task ReplyAfterTheSilentPollsWithoutIpHeaders_RestoresCommunicationAndResolvesTheIssue()
    {
        var camera = new SonyVisca(_client, false, "Test Cam", new Dictionary<int, string>(), 1, 30, _time);
        await PollAsync(camera, 3);
        await SonyViscaPolling.InvokePoll(camera);

        _client.ResponseByteHandlers!.Invoke(PowerOnReply);

        Assert.Equal(CommunicationState.Okay, camera.CommunicationState);
        Assert.Empty(camera.GetOngoingIssues());
        Assert.Equal(PowerState.On, camera.PowerState);
    }

    [Fact]
    public async Task SilentPollsSeparatedByAReply_LeaveCommunicationOkay()
    {
        var camera = new SonyVisca(_client, false, "Test Cam", new Dictionary<int, string>());
        await PollAsync(camera, 3);
        _client.ResponseByteHandlers!.Invoke(PowerOnReply);

        await PollAsync(camera, 3);

        Assert.Equal(CommunicationState.Okay, camera.CommunicationState);
        Assert.DoesNotContain(camera.GetIssues(), issue => issue.Key == "communication");
    }

    [Fact]
    public async Task CameraThatGoesSilentWhileOn_RaisesNoPanTiltStatusReadIssue()
    {
        var camera = new SonyVisca(_client, false, "Test Cam", new Dictionary<int, string>(), 1, 30, _time);
        await SonyViscaPolling.InvokePoll(camera);
        _client.ResponseByteHandlers!.Invoke(PowerOnReply);
        _client.ResponseByteHandlers!.Invoke(SonyViscaPolling.CourtCameraVersionReply);

        await PollAsync(camera, 4);

        Assert.Equal(4, _client.Sent.Count(sent => sent.SequenceEqual(SonyViscaPolling.PanTiltStatusInquiry)));
        Assert.Equal(CommunicationState.Error, camera.CommunicationState);
        AssertOnlyTheCommunicationIssue(camera);
    }
}

internal static class SonyViscaPolling
{
    private const int IpHeaderLength = 8;
    public static readonly byte[] VersionInquiry = [0x81, 0x09, 0x00, 0x02, 0xFF];
    public static readonly byte[] PanTiltStatusInquiry = [0x81, 0x09, 0x06, 0x10, 0xFF];
    public static readonly byte[] CourtCameraVersionReplyOverIp =
        Logged(@"\x01 \x11 \x00 \x0A \xFF \xFF \xFF \x01 \x90 \x50 \x00 \x01 \x00 \x00 \x00 \x00 \x53 \xFF");
    public static readonly byte[] CourtCameraVersionReply = CourtCameraVersionReplyOverIp[IpHeaderLength..];
    public static readonly byte[] CourtCameraPanTiltSyntaxErrorOverIp =
        Logged(@"\x01 \x11 \x00 \x04 \xFF \xFF \xFF \x03 \x90 \x60 \x02 \xFF");
    public static readonly byte[] SyntaxErrorReply = CourtCameraPanTiltSyntaxErrorOverIp[IpHeaderLength..];
    public static readonly byte[] HealthyPanTiltStatusReply = [0x90, 0x50, 0x20, 0x00, 0xFF];
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    public static byte[] Logged(string received) => received.Split(' ').Select(b => Convert.ToByte(b[2..], 16)).ToArray();

    public static void ExpireInquiries(FakeTimeProvider time, int inquiries)
    {
        for (var inquiry = 0; inquiry < inquiries; inquiry++)
            time.Advance(ReplyTimeout);
    }

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

internal class SecondInquiryCamera(CommunicationClient client, bool useIpHeaders, TimeProvider? timeProvider = null)
    : SonyVisca(client, useIpHeaders, "Test Cam", new Dictionary<int, string>(), 1, 30, timeProvider)
{
    private const int TrackingReplyLength = 4;
    public readonly List<byte[]> SecondReplies = [];

    protected override void SendPollInquiries()
    {
        base.SendPollInquiries();
        SendInquiry([_header, 0x09, 0x08, 0x01, CommandFooter], TrackingReplyLength, "query tracking", reply => SecondReplies.Add(reply));
    }
}

internal class RecallDuringPollCamera(CommunicationClient client)
    : SonyVisca(client, false, "Test Cam", new Dictionary<int, string> { { 1, "Lectern" } })
{
    protected override void SendPollInquiries()
    {
        base.SendPollInquiries();
        RecallPreset(1);
    }
}

internal class LongCommandCamera(CommunicationClient client)
    : SonyVisca(client, true, "Test Camera", new Dictionary<int, string>())
{
    public void SendLongCommand(int length) => SendCommand(new byte[length]);
}