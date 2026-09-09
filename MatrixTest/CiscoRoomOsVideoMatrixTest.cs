using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

// Response strings are taken verbatim from a Cisco Codec Plus on CE 9.8.0.
public class CiscoRoomOsVideoMatrixTest
{
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly CiscoRoomOsVideoMatrix _matrix;

    private static readonly List<string> RefreshCommands =
    [
        "xFeedback register /Status/Standby",
        "xFeedback register /Status/Video/Input/Connector",
        "xFeedback register /Status/Video/Input/Source",
        "xFeedback register /Status/Video/Output/Connector",
        "xFeedback register /Configuration/Video/Input/Connector",
        "xStatus Standby",
        "xStatus Video Input Connector",
        "xStatus Video Input Source",
        "xStatus Video Output Connector",
        "xConfiguration Video Input Connector Name"
    ];

    public CiscoRoomOsVideoMatrixTest()
    {
        _matrix = new CiscoRoomOsVideoMatrix(_mockClient.Object, 5, 3, "Codec Video");
    }

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void Respond(IEnumerable<string> responses)
    {
        foreach (var response in responses)
            Respond(response);
    }

    private void ClientConnection(ConnectionState state) =>
        _mockClient.Object.ConnectionStateHandlers!.Invoke(state);

    [Fact]
    public void Constructor_BuildsTheDeclaredTopology()
    {
        Assert.Equal(5, _matrix.Inputs.Count);
        Assert.Equal(3, _matrix.Outputs.Count);
        Assert.Equal(5, _matrix.NumberOfInputs);
        Assert.Equal(3, _matrix.NumberOfOutputs);
        Assert.Equal(5, _matrix.GetInputs().Count);
        Assert.Equal(3, _matrix.GetOutputs().Count);
        Assert.Equal("Input 1", _matrix.Inputs[0].Name);
        Assert.Equal("Output 3", _matrix.Outputs[2].Name);
        Assert.Equal(3, _matrix.Outputs[2].ConnectorId);
        Assert.Equal(AVEndpointType.Encoder, _matrix.GetInputs()[0].DeviceType);
        Assert.Equal(AVEndpointType.Decoder, _matrix.GetOutputs()[0].DeviceType);
        Assert.Equal("Codec Video", _matrix.Name);
        Assert.Same(_mockClient.Object, _matrix.CommunicationClient);
    }

    [Fact]
    public void Constructor_SendsNothingWhileTheClientIsNotConnected()
    {
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Equal(CommunicationState.NotAttempted, _matrix.CommunicationState);
    }

    [Fact]
    public void Login_RegistersFeedbackAndQueriesStatus()
    {
        Respond("*r Login successful\n");

        RefreshCommands.ForEach(command => _mockClient.Verify(x => x.Send($"{command}\r\n"), Times.Once));
        Assert.Equal(CommunicationState.Okay, _matrix.CommunicationState);
    }

    [Fact]
    public void Refresh_QueriesStatusAgain()
    {
        Respond("*r Login successful\n");

        _matrix.Refresh();

        RefreshCommands.ForEach(command => _mockClient.Verify(x => x.Send($"{command}\r\n"), Times.Exactly(2)));
    }

    [Fact]
    public void Reconnect_QueriesStatusAgain()
    {
        Respond("*r Login successful\n");
        ClientConnection(ConnectionState.Disconnected);
        ClientConnection(ConnectionState.Connecting);
        ClientConnection(ConnectionState.Connected);
        Respond("*r Login successful\n");

        RefreshCommands.ForEach(command => _mockClient.Verify(x => x.Send($"{command}\r\n"), Times.Exactly(2)));
        Assert.Equal(CommunicationState.Okay, _matrix.CommunicationState);
    }

    [Fact]
    public void ClientDisconnect_ForgetsTheConnectorStates()
    {
        Respond(
        [
            "*s Standby State: Off\n",
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Connector 1 HDCP State: Active\n",
            "*s Video Output Connector 1 Connected: True\n",
            "*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n",
            "*s Video Output Connector 1 Resolution Height: 1080\n",
            "*s Video Output Connector 1 Resolution RefreshRate: 60\n",
            "*s Video Output Connector 1 Resolution Width: 1920\n"
        ]);

        ClientConnection(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Error, _matrix.CommunicationState);
        Assert.Equal(PowerState.Unknown, _matrix.PowerState);
        Assert.Equal(ConnectionState.Unknown, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(string.Empty, _matrix.Inputs[0].InputResolution);
        Assert.Equal(HdcpStatus.Unknown, _matrix.Inputs[0].InputHdcpStatus);
        Assert.Equal(ConnectionState.Unknown, _matrix.Outputs[0].OutputConnectionStatus);
        Assert.Equal(string.Empty, _matrix.Outputs[0].OutputResolution);
        Assert.Equal(string.Empty, _matrix.Outputs[0].ConnectedDeviceName);
    }

    [Fact]
    public void ClientDisconnect_ForgetsTheSourceMapping()
    {
        Respond("*s Video Input Connector 3 SourceId: 2\n");
        ClientConnection(ConnectionState.Disconnected);

        Respond(
        [
            "*s Video Input Source 2 Resolution Height: 2160\n",
            "*s Video Input Source 2 Resolution RefreshRate: 30\n",
            "*s Video Input Source 2 Resolution Width: 3840\n"
        ]);

        Assert.Equal(string.Empty, _matrix.Inputs[2].InputResolution);
        Assert.Equal("3840x2160@30", _matrix.Inputs[1].InputResolution);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting)]
    public void ClientConnecting_LeavesTheConnectorStatesAlone(ConnectionState state)
    {
        Respond("*s Video Input Connector 1 SignalState: OK\n");

        ClientConnection(state);

        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[0].InputConnectionStatus);
        Assert.NotEqual(CommunicationState.Error, _matrix.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _matrix.Refresh();

        Assert.Equal(CommunicationState.Error, _matrix.CommunicationState);
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        var method = _matrix.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_matrix, ["Foo"]);

        _mockClient.Verify(x => x.Send("Foo\r\n"), Times.Once);
        Assert.Equal(CommunicationState.Okay, _matrix.CommunicationState);
    }

    [Theory]
    [InlineData("OK", ConnectionState.Connected)]
    [InlineData("Unstable", ConnectionState.Degraded)]
    [InlineData("Unsupported", ConnectionState.Error)]
    [InlineData("Unknown", ConnectionState.Disconnected)]
    [InlineData("NotFound", ConnectionState.Disconnected)]
    [InlineData("DetectingFormat", ConnectionState.Connecting)]
    public void InputSignalState_UpdatesInputConnectionStatus(string response, ConnectionState expectedState)
    {
        Respond($"*s Video Input Connector 1 SignalState: {response}\n");

        Assert.Equal(expectedState, _matrix.Inputs[0].InputConnectionStatus);
    }

    [Fact]
    public void InputDisconnected_UpdatesInputConnectionStatus()
    {
        Respond("*s Video Input Connector 2 SignalState: OK\n");
        Respond("*s Video Input Connector 2 Connected: False\n");

        Assert.Equal(ConnectionState.Disconnected, _matrix.Inputs[1].InputConnectionStatus);
    }

    [Fact]
    public void InputSourceResolution_UpdatesInputResolution()
    {
        Respond(
        [
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n"
        ]);

        Assert.Equal("1920x1080@60", _matrix.Inputs[0].InputResolution);
    }

    [Fact]
    public void InputSourceResolution_UsesTheConnectorMapping()
    {
        Respond(
        [
            "*s Video Input Connector 3 SourceId: 2\n",
            "*s Video Input Source 2 Resolution Height: 2160\n",
            "*s Video Input Source 2 Resolution RefreshRate: 30\n",
            "*s Video Input Source 2 Resolution Width: 3840\n"
        ]);

        Assert.Equal("3840x2160@30", _matrix.Inputs[2].InputResolution);
        Assert.Equal(string.Empty, _matrix.Inputs[1].InputResolution);
    }

    [Fact]
    public void InputDisconnect_ClearsTheResolution()
    {
        Respond(
        [
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Connector 1 Connected: False\n"
        ]);

        Assert.Equal(string.Empty, _matrix.Inputs[0].InputResolution);
    }

    [Fact]
    public void InputResponses_NotifySubscribers()
    {
        var handler = new Mock<SyncInfoHandler>();
        _matrix.Inputs[0].InputStatusChangedHandlers += handler.Object;

        Respond("*s Video Input Connector 1 SignalState: OK\n");

        handler.Verify(x => x.Invoke(ConnectionState.Connected, string.Empty, HdcpStatus.Unknown), Times.Once);
    }

    [Theory]
    [InlineData("True", ConnectionState.Connected)]
    [InlineData("False", ConnectionState.Disconnected)]
    public void OutputConnected_UpdatesOutputConnectionStatus(string response, ConnectionState expectedState)
    {
        Respond($"*s Video Output Connector 1 Connected: {response}\n");

        Assert.Equal(expectedState, _matrix.Outputs[0].OutputConnectionStatus);
    }

    [Fact]
    public void OutputResolution_UpdatesOutputResolution()
    {
        Respond(
        [
            "*s Video Output Connector 2 Resolution Height: 1080\n",
            "*s Video Output Connector 2 Resolution RefreshRate: 50\n",
            "*s Video Output Connector 2 Resolution Width: 1920\n"
        ]);

        Assert.Equal("1920x1080@50", _matrix.Outputs[1].OutputResolution);
    }

    [Theory]
    [InlineData("Active", HdcpStatus.Active)]
    [InlineData("Inactive", HdcpStatus.Available)]
    [InlineData("Unsupported", HdcpStatus.NotSupported)]
    public void OutputHdcpState_UpdatesOutputHdcpStatus(string response, HdcpStatus expectedStatus)
    {
        Respond($"*s Video Output Connector 1 HDCP State: {response}\n");

        Assert.Equal(expectedStatus, _matrix.Outputs[0].OutputHdcpStatus);
    }

    [Fact]
    public void InputHdcpState_UpdatesInputHdcpStatus()
    {
        Respond("*s Video Input Connector 5 HDCP State: Active\n");

        Assert.Equal(HdcpStatus.Active, _matrix.Inputs[4].InputHdcpStatus);
    }

    [Fact]
    public void UnrelatedResponses_AreIgnored()
    {
        Respond(
        [
            "*s Video Input MainVideoSource: 1\n",
            "*s Video Monitors: Single\n",
            "*s Video Input Source 3 (ghost=True)\n",
            "*s Video Input Source 1 Availability: Idle\n",
            "*s Video Input Source 1 FormatStatus: Ok\n",
            "*s Video Input Source 1 MediaChannelId: 118\n",
            "*s Video Output Connector 1 Type: HDMI\n",
            "*s Video Output Connector 1 MonitorRole: First\n",
            "*s Video Output Connector 1 ConnectedDevice CEC 1 DeviceType: \"Unknown\"\n",
            "*s Video Output Connector 1 ConnectedDevice PreferredFormat: \"1920x1080@60Hz\"\n",
            "*s Video Output Connector 1 ConnectedDevice SupportedFormat Res_1920_1080_60: True\n",
            "*s Video Output Connector 1 HDCP Version: None\n",
            "*s Video Output Connector 1 TouchInput Enabled: False\n",
            "*s Video Input Connector 1 Type: HDMI\n",
            "*s Audio Volume: 50\n",
            "*s Call 1 Status: Connected\n",
            "*r PeripheralsHeartBeatResult (status=OK): \n",
            "** end\n",
            "OK\n"
        ]);

        Assert.All(_matrix.Inputs, input => Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus));
        Assert.All(_matrix.Outputs, output => Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus));
        Assert.All(_matrix.Inputs, input => Assert.Empty(input.Events));
        Assert.All(_matrix.Outputs, output => Assert.Empty(output.Events));
        Assert.Single(_matrix.Events); // Only the constructor's NotAttempted communication state
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void MalformedLines_DoNotStopLaterLines()
    {
        Respond("*s Video Input Connector one SignalState: OK\n");
        Respond("*s Video\n");
        Respond("*s Video Input Connector 1 SignalState: OK\r\n*s Video Input Connector 2 SignalState: OK\r\n");

        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[1].InputConnectionStatus);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotStopLaterLines()
    {
        _matrix.Inputs[0].InputStatusChangedHandlers += (_, _, _) => throw new InvalidOperationException("Bad subscriber");

        Respond("*s Video Input Connector 1 SignalState: OK\r\n*s Video Input Connector 2 SignalState: OK\r\n");

        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[1].InputConnectionStatus);
        Assert.Single(_matrix.Errors);
    }

    [Fact]
    public void ConnectorsOutsideTheDeclaredTopology_AreIgnored()
    {
        Respond("*s Video Input Connector 6 SignalState: OK\n");
        Respond("*s Video Output Connector 4 Connected: True\n");
        Respond("*c xConfiguration Video Input Connector 0 Name: \"Nope\"\n");

        Assert.Equal(5, _matrix.Inputs.Count);
        Assert.Equal(3, _matrix.Outputs.Count);
        Assert.All(_matrix.Inputs, input => Assert.StartsWith("Input ", input.Name));
        Assert.All(_matrix.Inputs, input => Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus));
        Assert.All(_matrix.Outputs, output => Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus));
    }

    [Fact]
    public void InputHotplugFeedback_TracksTheConnectionStates()
    {
        var states = new List<ConnectionState>();
        _matrix.Inputs[0].InputStatusChangedHandlers += (state, _, _) => states.Add(state);

        Respond(
        [
            "*s Video Input Connector 1 Connected: False\n",
            "*s Video Input Connector 1 SignalState: NotFound\n",
            "*s Video Input Connector 1 Connected: True\n",
            "*s Video Input Connector 1 SignalState: DetectingFormat\n",
            "*s Video Input Connector 1 SignalState: NotFound\n",
            "*s Video Input Connector 1 SignalState: DetectingFormat\n",
            "*s Video Input Connector 1 SignalState: OK\n"
        ]);

        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(new List<ConnectionState>
        {
            ConnectionState.Disconnected,
            ConnectionState.Connecting,
            ConnectionState.Disconnected,
            ConnectionState.Connecting,
            ConnectionState.Connected
        }, states);
    }

    [Fact]
    public void StatusDump_PopulatesTheConnectors()
    {
        Respond(
        [
            "*s Video Input Connector 1 Connected: True\n",
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Connector 1 SourceId: 1\n",
            "*s Video Input Connector 1 Type: HDMI\n",
            "*s Video Input Connector 3 Connected: True\n",
            "*s Video Input Connector 3 SignalState: Unknown\n",
            "*s Video Input Connector 3 SourceId: 3\n",
            "*s Video Input Source 1 ConnectorId: 1\n",
            "*s Video Input Source 1 FormatStatus: Ok\n",
            "*s Video Input Source 1 FormatType: Digital\n",
            "*s Video Input Source 1 MediaChannelId: 111\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Source 3 ConnectorId: 3\n",
            "*s Video Input Source 3 FormatStatus: NotFound\n",
            "*s Video Input Source 3 Resolution Height: 0\n",
            "*s Video Input Source 3 Resolution RefreshRate: 0\n",
            "*s Video Input Source 3 Resolution Width: 0\n",
            "*s Video Output Connector 1 Connected: True\n",
            "*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n",
            "*s Video Output Connector 1 ConnectedDevice PreferredFormat: \"3840x2160@60Hz\"\n",
            "*s Video Output Connector 1 ConnectedDevice ScreenSize: 64\n",
            "*s Video Output Connector 1 MonitorRole: First\n",
            "*s Video Output Connector 1 Resolution Height: 1080\n",
            "*s Video Output Connector 1 Resolution RefreshRate: 60\n",
            "*s Video Output Connector 1 Resolution Width: 1920\n",
            "*s Video Output Connector 1 Type: HDMI\n",
            "*s Video Output Connector 3 Connected: False\n",
            "*s Video Output Connector 3 ConnectedDevice PreferredFormat: \"-1x-1@-1Hz\"\n",
            "*s Video Output Connector 3 ConnectedDevice ScreenSize: -1\n",
            "*s Video Output Connector 3 HDCP State: Unsupported\n",
            "*s Video Output Connector 3 Resolution Height: 0\n",
            "*s Video Output Connector 3 Resolution RefreshRate: 0\n",
            "*s Video Output Connector 3 Resolution Width: 0\n",
            "*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n",
            "*c xConfiguration Video Input Connector 2 Name: \"Bar Camera\"\n",
            "*c xConfiguration Video Input Connector 3 Name: \"Content\"\n"
        ]);

        Assert.Equal("Bench Camera", _matrix.Inputs[0].Name);
        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal("1920x1080@60", _matrix.Inputs[0].InputResolution);
        Assert.Equal("Content", _matrix.Inputs[2].Name);
        Assert.Equal(ConnectionState.Disconnected, _matrix.Inputs[2].InputConnectionStatus);
        Assert.Equal(string.Empty, _matrix.Inputs[2].InputResolution);
        Assert.Equal("Output 1", _matrix.Outputs[0].Name);
        Assert.Equal("SyncMaster", _matrix.Outputs[0].ConnectedDeviceName);
        Assert.Equal(ConnectionState.Connected, _matrix.Outputs[0].OutputConnectionStatus);
        Assert.Equal("1920x1080@60", _matrix.Outputs[0].OutputResolution);
        Assert.Equal(ConnectionState.Disconnected, _matrix.Outputs[2].OutputConnectionStatus);
        Assert.Equal(string.Empty, _matrix.Outputs[2].OutputResolution);
        Assert.Equal(HdcpStatus.NotSupported, _matrix.Outputs[2].OutputHdcpStatus);
    }

    [Fact]
    public void ChunkedResponses_AreSplitIntoLines()
    {
        Respond("*s Video Input Connector 1 SignalState: OK\r\n*s Video Input Connector 2 SignalState: OK\r\n");

        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[0].InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, _matrix.Inputs[1].InputConnectionStatus);
    }

    [Theory]
    [InlineData("*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n", 0, "Bench Camera")]
    [InlineData("*c xConfiguration Video Input Connector 2 Name: \"Bar Camera\"\n", 1, "Bar Camera")]
    [InlineData("*c xConfiguration Video Input Connector 3 Name: \"Content\"\n", 2, "Content")]
    [InlineData("*c xConfiguration Video Input Connector 3 Name: \"Content Test\"", 2, "Content Test")]
    public void InputNameConfiguration_UpdatesTheInputName(string response, int index, string expectedName)
    {
        Respond(response);

        Assert.Equal(expectedName, _matrix.Inputs[index].Name);
    }

    [Fact]
    public void InputNameConfiguration_AddsToTheHistoryOnce()
    {
        Respond("*c xConfiguration Video Input Connector 3 Name: \"Content\"\n");
        Respond("*c xConfiguration Video Input Connector 3 Name: \"Content\"\n");

        Assert.Single(_matrix.Inputs[2].Events, e => e.Info == "Name Changed to Content");
    }

    [Fact]
    public void InputNameConfiguration_NotifiesNameSubscribers()
    {
        var handler = new Mock<StringHandler>();
        _matrix.Inputs[0].NameChangedHandlers += handler.Object;

        Respond("*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n");

        handler.Verify(x => x.Invoke("Bench Camera"), Times.Once);
    }

    [Fact]
    public void EmptyInputName_RevertsToTheDefault()
    {
        Respond("*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n");
        Respond("*c xConfiguration Video Input Connector 1 Name: \"\"\n");

        Assert.Equal("Input 1", _matrix.Inputs[0].Name);
    }

    [Fact]
    public void OtherVideoConfiguration_IsIgnored()
    {
        Respond(
        [
            "*c xConfiguration Video Input Connector 1 CEC Mode: Off\n",
            "*c xConfiguration Video Input Connector 1 InputSourceType: camera\n",
            "*c xConfiguration Video Input Connector 1 Visibility: Always\n",
            "*c xConfiguration Video Output Connector 1 MonitorRole: Auto\n",
            "*c xConfiguration Video Output Connector 1 Resolution: 1920_1080_60\n"
        ]);

        Assert.Equal("Input 1", _matrix.Inputs[0].Name);
        Assert.Equal("Output 1", _matrix.Outputs[0].Name);
    }

    [Theory]
    [InlineData("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n", 0, "SyncMaster")]
    [InlineData("*s Video Output Connector 2 ConnectedDevice Name: \"CS-CODECPLUS\"\n", 1, "CS-CODECPLUS")]
    [InlineData("*s Video Output Connector 1 ConnectedDevice Name: \"Extron HDMI\"\n", 0, "Extron HDMI")]
    public void OutputConnectedDeviceName_IsExposedAndLoggedButDoesNotRenameTheOutput(string response, int index, string expectedName)
    {
        var handler = new Mock<StringHandler>();
        _matrix.Outputs[index].ConnectedDeviceNameChangedHandlers += handler.Object;

        Respond(response);

        var output = _matrix.Outputs[index];
        Assert.Equal($"Output {index + 1}", output.Name);
        Assert.Equal(expectedName, output.ConnectedDeviceName);
        Assert.Single(output.Events, e => e.Info == $"Connected Device Changed to {expectedName}");
        handler.Verify(x => x.Invoke(expectedName), Times.Once);
    }

    [Fact]
    public void OutputConnectedDeviceName_IsLoggedOnce()
    {
        Respond("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n");
        Respond("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n");

        Assert.Single(_matrix.Outputs[0].Events, e => e.Info == "Connected Device Changed to SyncMaster");
    }

    [Fact]
    public void OutputDisconnect_ClearsTheConnectedDeviceName()
    {
        Respond("*s Video Output Connector 1 Connected: True\n");
        Respond("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n");
        Respond("*s Video Output Connector 1 Connected: False\n");

        Assert.Equal(string.Empty, _matrix.Outputs[0].ConnectedDeviceName);
        Assert.Equal("Output 1", _matrix.Outputs[0].Name);
        Assert.Single(_matrix.Outputs[0].Events, e => e.Info == "Connected Device Removed");
    }

    [Fact]
    public void OutputWithoutAConnectedDevice_HasNoDeviceName()
    {
        Respond(
        [
            "*s Video Output Connector 3 Connected: False\n",
            "*s Video Output Connector 3 ConnectedDevice PreferredFormat: \"-1x-1@-1Hz\"\n",
            "*s Video Output Connector 3 ConnectedDevice ScreenSize: -1\n"
        ]);

        Assert.Equal("Output 3", _matrix.Outputs[2].Name);
        Assert.Equal(string.Empty, _matrix.Outputs[2].ConnectedDeviceName);
        Assert.DoesNotContain(_matrix.Outputs[2].Events, e => e.Info.StartsWith("Connected Device"));
    }

    [Fact]
    public void ConnectorHistory_RecordsSyncChanges()
    {
        Respond(
        [
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Connector 1 Connected: False\n"
        ]);

        Assert.Equal(new List<string>
        {
            "Input Connection Changed to Connected",
            "Input Resolution Changed to 1920x1080@60",
            "Input Resolution Changed to ",
            "Input Connection Changed to Disconnected"
        }, _matrix.Inputs[0].Events.Select(e => e.Info).ToList());
    }

    [Theory]
    [InlineData("*s Standby State: Off\n", PowerState.On)]
    [InlineData("*s Standby State: Standby\n", PowerState.Off)]
    [InlineData("*s Standby State: Halfwake\n", PowerState.Off)]
    [InlineData("*s Standby State: EnteringStandby\n", PowerState.Off)]
    public void StandbyState_MirrorsTheCodecPowerState(string response, PowerState expected)
    {
        var handler = new Mock<PowerStateHandler>();
        _matrix.PowerStateHandlers += handler.Object;

        Respond(response);

        Assert.Equal(expected, _matrix.PowerState);
        handler.Verify(x => x.Invoke(expected), Times.Once);
    }

    [Fact]
    public void Routing_IsNotSupported()
    {
        Assert.False(_matrix.RequiresOutputSpecification);
        Assert.False(_matrix.SupportsVideoBreakaway);
        Assert.False(_matrix.SupportsAudioBreakaway);
    }

    [Fact]
    public void RoutingAndPower_SendNothingAndRecordEvents()
    {
        _matrix.RouteAV(1, 2);
        _matrix.RouteVideo(2, 1);
        _matrix.RouteAudio(3, 1);
        _matrix.PowerOn();
        _matrix.PowerOff();

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Equal(5, _matrix.Events.Count(e => e.Info.StartsWith("Ignoring")));
    }
}
