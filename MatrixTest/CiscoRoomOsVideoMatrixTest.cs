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

    private static readonly List<string> StatusDump =
    [
        "*s Video Input Connector 1 Connected: True\n",
        "*s Video Input Connector 1 SignalState: OK\n",
        "*s Video Input Connector 1 SourceId: 1\n",
        "*s Video Input Connector 1 Type: HDMI\n",
        "*s Video Input Connector 2 Connected: True\n",
        "*s Video Input Connector 2 SignalState: OK\n",
        "*s Video Input Connector 2 SourceId: 2\n",
        "*s Video Input Connector 2 Type: HDMI\n",
        "*s Video Input Connector 3 Connected: True\n",
        "*s Video Input Connector 3 SignalState: Unknown\n",
        "*s Video Input Connector 3 SourceId: 3\n",
        "*s Video Input Connector 3 Type: HDMI\n",
        "*s Video Input Source 1 ConnectorId: 1\n",
        "*s Video Input Source 1 FormatStatus: Ok\n",
        "*s Video Input Source 1 FormatType: Digital\n",
        "*s Video Input Source 1 MediaChannelId: 111\n",
        "*s Video Input Source 1 Resolution Height: 1080\n",
        "*s Video Input Source 1 Resolution RefreshRate: 60\n",
        "*s Video Input Source 1 Resolution Width: 1920\n",
        "*s Video Input Source 2 ConnectorId: 2\n",
        "*s Video Input Source 2 Resolution Height: 1080\n",
        "*s Video Input Source 2 Resolution RefreshRate: 60\n",
        "*s Video Input Source 2 Resolution Width: 1920\n",
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
        "*s Video Output Connector 2 Connected: True\n",
        "*s Video Output Connector 2 ConnectedDevice Name: \"CS-CODECPLUS\"\n",
        "*s Video Output Connector 2 ConnectedDevice PreferredFormat: \"1920x1080@60Hz\"\n",
        "*s Video Output Connector 2 ConnectedDevice ScreenSize: 55\n",
        "*s Video Output Connector 2 MonitorRole: First\n",
        "*s Video Output Connector 2 Resolution Height: 1080\n",
        "*s Video Output Connector 2 Resolution RefreshRate: 60\n",
        "*s Video Output Connector 2 Resolution Width: 1920\n",
        "*s Video Output Connector 2 Type: HDMI\n",
        "*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n",
        "*c xConfiguration Video Input Connector 2 Name: \"Bar Camera\"\n",
        "*c xConfiguration Video Input Connector 3 Name: \"Content\"\n"
    ];

    public CiscoRoomOsVideoMatrixTest()
    {
        _matrix = new CiscoRoomOsVideoMatrix(_mockClient.Object, "Codec Video");
    }

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void Respond(IEnumerable<string> responses)
    {
        foreach (var response in responses)
            Respond(response);
    }

    private void ClientConnection(ConnectionState state) =>
        _mockClient.Object.ConnectionStateHandlers!.Invoke(state);

    private CiscoRoomOsVideoInput Input(int connectorId) => _matrix.GetInput(connectorId)!;

    private CiscoRoomOsVideoOutput Output(int connectorId) => _matrix.GetOutput(connectorId)!;

    [Fact]
    public void Constructor_StartsWithNoConnectors()
    {
        Assert.Empty(_matrix.Inputs);
        Assert.Empty(_matrix.Outputs);
        Assert.Equal(0, _matrix.NumberOfInputs);
        Assert.Equal(0, _matrix.NumberOfOutputs);
        Assert.Empty(_matrix.GetInputs());
        Assert.Empty(_matrix.GetOutputs());
        Assert.Null(_matrix.GetInput(1));
        Assert.Null(_matrix.GetOutput(1));
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
    public void StatusDump_DiscoversTheConnectors()
    {
        Respond(StatusDump);

        Assert.Equal(3, _matrix.NumberOfInputs);
        Assert.Equal(2, _matrix.NumberOfOutputs);
        Assert.Equal([1, 2, 3], _matrix.Inputs.Select(input => input.ConnectorId));
        Assert.Equal([1, 2], _matrix.Outputs.Select(output => output.ConnectorId));
        Assert.Equal(3, _matrix.GetInputs().Count);
        Assert.Equal(2, _matrix.GetOutputs().Count);
        Assert.Equal(AVEndpointType.Encoder, _matrix.GetInputs()[0].DeviceType);
        Assert.Equal(AVEndpointType.Decoder, _matrix.GetOutputs()[0].DeviceType);
        Assert.Same(Input(2), _matrix.Inputs[1]);
        Assert.Same(Output(2), _matrix.GetOutputs()[1]);
    }

    [Fact]
    public void StatusDump_PopulatesTheConnectors()
    {
        Respond(StatusDump);

        Assert.Equal("Bench Camera", Input(1).Name);
        Assert.Equal(ConnectionState.Connected, Input(1).InputConnectionStatus);
        Assert.Equal("1920x1080@60", Input(1).InputResolution);
        Assert.Equal("Bar Camera", Input(2).Name);
        Assert.Equal("Content", Input(3).Name);
        Assert.Equal(ConnectionState.Disconnected, Input(3).InputConnectionStatus);
        Assert.Equal(string.Empty, Input(3).InputResolution);
        Assert.Equal("Output 1", Output(1).Name);
        Assert.Equal("SyncMaster", Output(1).ConnectedDeviceName);
        Assert.Equal(ConnectionState.Connected, Output(1).OutputConnectionStatus);
        Assert.Equal("1920x1080@60", Output(1).OutputResolution);
        Assert.Equal("Output 2", Output(2).Name);
        Assert.Equal("CS-CODECPLUS", Output(2).ConnectedDeviceName);
    }

    [Fact]
    public void Discovery_IsOrderedByConnectorAndReportedOnce()
    {
        var changes = 0;
        _matrix.EndpointsChangedHandlers += () => changes++;

        Respond("*s Video Input Connector 3 Type: HDMI\n");
        Respond("*s Video Output Connector 2 Type: HDMI\n");
        Respond("*s Video Input Connector 1 Type: HDMI\n");
        Respond("*s Video Input Connector 3 SignalState: OK\n");
        Respond("*c xConfiguration Video Input Connector 2 Name: \"Bar Camera\"\n");

        Assert.Equal([1, 2, 3], _matrix.Inputs.Select(input => input.ConnectorId));
        Assert.Equal([2], _matrix.Outputs.Select(output => output.ConnectorId));
        Assert.Equal(4, changes);
        Assert.Equal(4, _matrix.Events.Count(e => e.Info.StartsWith("Discovered")));
    }

    [Fact]
    public void SourceLines_DoNotCreateInputs()
    {
        Respond(
        [
            "*s Video Input Source 1 ConnectorId: 1\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n"
        ]);

        Assert.Empty(_matrix.Inputs);
    }

    [Theory]
    [InlineData("*s Video Input Connector 0 SignalState: OK\n")]
    [InlineData("*s Video Input Connector one SignalState: OK\n")]
    [InlineData("*s Video Output Connector -1 Connected: True\n")]
    [InlineData("*c xConfiguration Video Input Connector 0 Name: \"Nope\"\n")]
    public void InvalidConnectorNumbers_AreIgnored(string response)
    {
        Respond(response);

        Assert.Empty(_matrix.Inputs);
        Assert.Empty(_matrix.Outputs);
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
    public void Reconnect_QueriesStatusAgainAndKeepsTheConnectors()
    {
        Respond("*r Login successful\n");
        Respond(StatusDump);
        ClientConnection(ConnectionState.Disconnected);
        ClientConnection(ConnectionState.Connecting);
        ClientConnection(ConnectionState.Connected);
        Respond("*r Login successful\n");

        RefreshCommands.ForEach(command => _mockClient.Verify(x => x.Send($"{command}\r\n"), Times.Exactly(2)));
        Assert.Equal(CommunicationState.Okay, _matrix.CommunicationState);
        Assert.Equal(3, _matrix.NumberOfInputs);
        Assert.Equal(2, _matrix.NumberOfOutputs);
    }

    [Fact]
    public void ClientDisconnect_ForgetsTheConnectorStates()
    {
        Respond("*s Standby State: Off\n");
        Respond(StatusDump);
        Respond("*s Video Input Connector 1 HDCP State: Active\n");
        var input = Input(1);
        var output = Output(1);

        ClientConnection(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Error, _matrix.CommunicationState);
        Assert.Equal(PowerState.Unknown, _matrix.PowerState);
        Assert.Same(input, Input(1));
        Assert.Equal("Bench Camera", input.Name);
        Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
        Assert.Equal(string.Empty, input.InputResolution);
        Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        Assert.Same(output, Output(1));
        Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus);
        Assert.Equal(string.Empty, output.OutputResolution);
        Assert.Equal(string.Empty, output.ConnectedDeviceName);
    }

    [Fact]
    public void ClientDisconnect_ForgetsTheSourceMapping()
    {
        Respond("*s Video Input Connector 2 Type: HDMI\n");
        Respond("*s Video Input Connector 3 SourceId: 2\n");
        ClientConnection(ConnectionState.Disconnected);

        Respond(
        [
            "*s Video Input Source 2 Resolution Height: 2160\n",
            "*s Video Input Source 2 Resolution RefreshRate: 30\n",
            "*s Video Input Source 2 Resolution Width: 3840\n"
        ]);

        Assert.Equal(string.Empty, Input(3).InputResolution);
        Assert.Equal("3840x2160@30", Input(2).InputResolution);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting)]
    public void ClientConnecting_LeavesTheConnectorStatesAlone(ConnectionState state)
    {
        Respond("*s Video Input Connector 1 SignalState: OK\n");

        ClientConnection(state);

        Assert.Equal(ConnectionState.Connected, Input(1).InputConnectionStatus);
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

        Assert.Equal(expectedState, Input(1).InputConnectionStatus);
    }

    [Fact]
    public void InputDisconnected_UpdatesInputConnectionStatus()
    {
        Respond("*s Video Input Connector 2 SignalState: OK\n");
        Respond("*s Video Input Connector 2 Connected: False\n");

        Assert.Equal(ConnectionState.Disconnected, Input(2).InputConnectionStatus);
    }

    [Fact]
    public void InputSourceResolution_UpdatesInputResolution()
    {
        Respond(
        [
            "*s Video Input Connector 1 SourceId: 1\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n"
        ]);

        Assert.Equal("1920x1080@60", Input(1).InputResolution);
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

        Assert.Equal("3840x2160@30", Input(3).InputResolution);
        Assert.Null(_matrix.GetInput(2));
    }

    [Fact]
    public void InputSourceResolution_FallsBackToTheSourceNumberForAKnownConnector()
    {
        Respond(
        [
            "*s Video Input Connector 1 Type: HDMI\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n"
        ]);

        Assert.Equal("1920x1080@60", Input(1).InputResolution);
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

        Assert.Equal(string.Empty, Input(1).InputResolution);
    }

    [Fact]
    public void InputResponses_NotifySubscribers()
    {
        Respond("*s Video Input Connector 1 Type: HDMI\n");
        var handler = new Mock<SyncInfoHandler>();
        Input(1).InputStatusChangedHandlers += handler.Object;

        Respond("*s Video Input Connector 1 SignalState: OK\n");

        handler.Verify(x => x.Invoke(ConnectionState.Connected, string.Empty, HdcpStatus.Unknown), Times.Once);
    }

    [Theory]
    [InlineData("True", ConnectionState.Connected)]
    [InlineData("False", ConnectionState.Disconnected)]
    public void OutputConnected_UpdatesOutputConnectionStatus(string response, ConnectionState expectedState)
    {
        Respond($"*s Video Output Connector 1 Connected: {response}\n");

        Assert.Equal(expectedState, Output(1).OutputConnectionStatus);
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

        Assert.Equal("1920x1080@50", Output(2).OutputResolution);
    }

    [Theory]
    [InlineData("Active", HdcpStatus.Active)]
    [InlineData("Inactive", HdcpStatus.Available)]
    [InlineData("Unsupported", HdcpStatus.NotSupported)]
    public void OutputHdcpState_UpdatesOutputHdcpStatus(string response, HdcpStatus expectedStatus)
    {
        Respond($"*s Video Output Connector 1 HDCP State: {response}\n");

        Assert.Equal(expectedStatus, Output(1).OutputHdcpStatus);
    }

    [Fact]
    public void InputHdcpState_UpdatesInputHdcpStatus()
    {
        Respond("*s Video Input Connector 5 HDCP State: Active\n");

        Assert.Equal(HdcpStatus.Active, Input(5).InputHdcpStatus);
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
            "*s Audio Volume: 50\n",
            "*s Call 1 Status: Connected\n",
            "*r PeripheralsHeartBeatResult (status=OK): \n",
            "** end\n",
            "OK\n"
        ]);

        Assert.Empty(_matrix.Inputs);
        Assert.Empty(_matrix.Outputs);
        Assert.Single(_matrix.Events); // Only the constructor's NotAttempted communication state
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void UnusedConnectorFields_DiscoverTheConnectorButChangeNothingElse()
    {
        Respond(
        [
            "*s Video Output Connector 1 Type: HDMI\n",
            "*s Video Output Connector 1 MonitorRole: First\n",
            "*s Video Output Connector 1 ConnectedDevice CEC 1 DeviceType: \"Unknown\"\n",
            "*s Video Output Connector 1 ConnectedDevice PreferredFormat: \"1920x1080@60Hz\"\n",
            "*s Video Output Connector 1 ConnectedDevice SupportedFormat Res_1920_1080_60: True\n",
            "*s Video Output Connector 1 HDCP Version: None\n",
            "*s Video Output Connector 1 TouchInput Enabled: False\n",
            "*s Video Input Connector 1 Type: HDMI\n"
        ]);

        Assert.Equal(ConnectionState.Unknown, Input(1).InputConnectionStatus);
        Assert.Equal(ConnectionState.Unknown, Output(1).OutputConnectionStatus);
        Assert.Empty(Input(1).Events);
        Assert.Empty(Output(1).Events);
    }

    [Fact]
    public void MalformedLines_DoNotStopLaterLines()
    {
        Respond("*s Video Input Connector one SignalState: OK\n");
        Respond("*s Video\n");
        Respond("*s Video Input Connector 1 SignalState: OK\r\n*s Video Input Connector 2 SignalState: OK\r\n");

        Assert.Equal(ConnectionState.Connected, Input(1).InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, Input(2).InputConnectionStatus);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotStopLaterLines()
    {
        Respond("*s Video Input Connector 1 Type: HDMI\n");
        Input(1).InputStatusChangedHandlers += (_, _, _) => throw new InvalidOperationException("Bad subscriber");

        Respond("*s Video Input Connector 1 SignalState: OK\r\n*s Video Input Connector 2 SignalState: OK\r\n");

        Assert.Equal(ConnectionState.Connected, Input(2).InputConnectionStatus);
        Assert.Single(_matrix.Errors);
    }

    [Fact]
    public void InputHotplugFeedback_TracksTheConnectionStates()
    {
        Respond("*s Video Input Connector 1 Type: HDMI\n");
        var states = new List<ConnectionState>();
        Input(1).InputStatusChangedHandlers += (state, _, _) => states.Add(state);

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

        Assert.Equal(ConnectionState.Connected, Input(1).InputConnectionStatus);
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
    public void ChunkedResponses_AreSplitIntoLines()
    {
        Respond("*s Video Input Connector 1 SignalState: OK\r\n*s Video Input Connector 2 SignalState: OK\r\n");

        Assert.Equal(ConnectionState.Connected, Input(1).InputConnectionStatus);
        Assert.Equal(ConnectionState.Connected, Input(2).InputConnectionStatus);
    }

    [Theory]
    [InlineData("*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n", 1, "Bench Camera")]
    [InlineData("*c xConfiguration Video Input Connector 2 Name: \"Bar Camera\"\n", 2, "Bar Camera")]
    [InlineData("*c xConfiguration Video Input Connector 3 Name: \"Content\"\n", 3, "Content")]
    [InlineData("*c xConfiguration Video Input Connector 3 Name: \"Content Test\"", 3, "Content Test")]
    public void InputNameConfiguration_UpdatesTheInputName(string response, int connectorId, string expectedName)
    {
        Respond(response);

        Assert.Equal(expectedName, Input(connectorId).Name);
    }

    [Fact]
    public void InputNameConfiguration_AddsToTheHistoryOnce()
    {
        Respond("*c xConfiguration Video Input Connector 3 Name: \"Content\"\n");
        Respond("*c xConfiguration Video Input Connector 3 Name: \"Content\"\n");

        Assert.Single(Input(3).Events, e => e.Info == "Name Changed to Content");
    }

    [Fact]
    public void InputNameConfiguration_NotifiesNameSubscribers()
    {
        Respond("*s Video Input Connector 1 Type: HDMI\n");
        var handler = new Mock<StringHandler>();
        Input(1).NameChangedHandlers += handler.Object;

        Respond("*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n");

        handler.Verify(x => x.Invoke("Bench Camera"), Times.Once);
    }

    [Fact]
    public void EmptyInputName_RevertsToTheDefault()
    {
        Respond("*c xConfiguration Video Input Connector 1 Name: \"Bench Camera\"\n");
        Respond("*c xConfiguration Video Input Connector 1 Name: \"\"\n");

        Assert.Equal("Input 1", Input(1).Name);
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

        Assert.Empty(_matrix.Inputs);
        Assert.Empty(_matrix.Outputs);
    }

    [Theory]
    [InlineData("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n", 1, "SyncMaster")]
    [InlineData("*s Video Output Connector 2 ConnectedDevice Name: \"CS-CODECPLUS\"\n", 2, "CS-CODECPLUS")]
    [InlineData("*s Video Output Connector 1 ConnectedDevice Name: \"Extron HDMI\"\n", 1, "Extron HDMI")]
    public void OutputConnectedDeviceName_IsExposedAndLoggedButDoesNotRenameTheOutput(string response, int connectorId, string expectedName)
    {
        Respond(response);

        var output = Output(connectorId);
        Assert.Equal($"Output {connectorId}", output.Name);
        Assert.Equal(expectedName, output.ConnectedDeviceName);
        Assert.Single(output.Events, e => e.Info == $"Connected Device Changed to {expectedName}");
    }

    [Fact]
    public void OutputConnectedDeviceName_NotifiesSubscribersOnce()
    {
        Respond("*s Video Output Connector 1 Type: HDMI\n");
        var handler = new Mock<StringHandler>();
        Output(1).ConnectedDeviceNameChangedHandlers += handler.Object;

        Respond("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n");
        Respond("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n");

        handler.Verify(x => x.Invoke("SyncMaster"), Times.Once);
        Assert.Single(Output(1).Events, e => e.Info == "Connected Device Changed to SyncMaster");
    }

    [Fact]
    public void OutputDisconnect_ClearsTheConnectedDeviceName()
    {
        Respond("*s Video Output Connector 1 Connected: True\n");
        Respond("*s Video Output Connector 1 ConnectedDevice Name: \"SyncMaster\"\n");
        Respond("*s Video Output Connector 1 Connected: False\n");

        Assert.Equal(string.Empty, Output(1).ConnectedDeviceName);
        Assert.Equal("Output 1", Output(1).Name);
        Assert.Single(Output(1).Events, e => e.Info == "Connected Device Removed");
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

        Assert.Equal("Output 3", Output(3).Name);
        Assert.Equal(string.Empty, Output(3).ConnectedDeviceName);
        Assert.DoesNotContain(Output(3).Events, e => e.Info.StartsWith("Connected Device"));
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
        }, Input(1).Events.Select(e => e.Info).ToList());
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

    [Fact]
    public void SourceResolutionReportedBeforeTheConnector_IsAppliedOnDiscovery()
    {
        Respond(
        [
            "*s Video Input Source 2 ConnectorId: 3\n",
            "*s Video Input Source 2 Resolution Height: 1080\n",
            "*s Video Input Source 2 Resolution RefreshRate: 60\n",
            "*s Video Input Source 2 Resolution Width: 1920\n",
            "*s Video Input Connector 3 SignalState: OK\n"
        ]);

        Assert.Equal("1920x1080@60", Input(3).InputResolution);
        Assert.Null(_matrix.GetInput(2));
    }

    [Fact]
    public void SourceResolutionReportedBeforeTheMappingAndConnector_IsAppliedOnDiscovery()
    {
        Respond(
        [
            "*s Video Input Source 2 Resolution Height: 1080\n",
            "*s Video Input Source 2 Resolution RefreshRate: 60\n",
            "*s Video Input Source 2 Resolution Width: 1920\n",
            "*s Video Input Source 2 ConnectorId: 3\n",
            "*s Video Input Connector 3 SignalState: OK\n"
        ]);

        Assert.Equal("1920x1080@60", Input(3).InputResolution);
        Assert.Null(_matrix.GetInput(2));
    }

    [Fact]
    public void SourceMappingReportedAfterTheResolutionAndConnector_AppliesTheResolution()
    {
        Respond(
        [
            "*s Video Input Connector 3 SignalState: OK\n",
            "*s Video Input Source 2 Resolution Height: 1080\n",
            "*s Video Input Source 2 Resolution RefreshRate: 60\n",
            "*s Video Input Source 2 Resolution Width: 1920\n",
            "*s Video Input Connector 3 SourceId: 2\n"
        ]);

        Assert.Equal("1920x1080@60", Input(3).InputResolution);
    }

    [Fact]
    public void UnmappedSourceResolutionReportedBeforeTheConnector_FallsBackToTheSameNumber()
    {
        Respond(
        [
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Connector 1 SignalState: OK\n"
        ]);

        Assert.Equal("1920x1080@60", Input(1).InputResolution);
    }

    [Fact]
    public void SourceMappedElsewhere_DoesNotFallBackToTheSameNumber()
    {
        Respond(
        [
            "*s Video Input Source 1 ConnectorId: 2\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Connector 2 SignalState: OK\n"
        ]);

        Assert.Equal(string.Empty, Input(1).InputResolution);
        Assert.Equal("1920x1080@60", Input(2).InputResolution);
    }

    [Fact]
    public void ClientDisconnect_ForgetsPendingSourceResolutions()
    {
        Respond(
        [
            "*s Video Input Source 3 ConnectorId: 3\n",
            "*s Video Input Source 3 Resolution Height: 1080\n",
            "*s Video Input Source 3 Resolution RefreshRate: 60\n",
            "*s Video Input Source 3 Resolution Width: 1920\n"
        ]);
        ClientConnection(ConnectionState.Disconnected);

        Respond("*s Video Input Connector 3 SignalState: OK\n");

        Assert.Equal(string.Empty, Input(3).InputResolution);
    }

    [Fact]
    public void EndpointsChanged_FiresAfterTheConnectorIsVisible()
    {
        var seen = new List<(int Inputs, int Outputs)>();
        _matrix.EndpointsChangedHandlers += () => seen.Add((_matrix.NumberOfInputs, _matrix.GetOutputs().Count));

        Respond("*s Video Input Connector 2 Type: HDMI\n");
        Respond("*s Video Output Connector 1 Type: HDMI\n");

        Assert.Equal([(1, 0), (1, 1)], seen);
    }

    [Fact]
    public async Task Discovery_CanBeReadConcurrently()
    {
        const int connectors = 400;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                _ = _matrix.Inputs.Count + _matrix.Outputs.Count + _matrix.GetInputs().Count + _matrix.GetOutputs().Count;
                _ = _matrix.GetInput(reads % connectors + 1);
                reads++;
            }
            return reads;
        });

        var writer = Task.Run(() =>
        {
            for (var connector = 1; connector <= connectors; connector++)
            {
                Respond($"*s Video Input Connector {connector} SignalState: OK\n");
                Respond($"*s Video Output Connector {connector} Connected: True\n");
            }
        });

        await writer;
        stop.Cancel();
        var reads = await reader;

        Assert.True(reads > 0);
        Assert.Equal(connectors, _matrix.NumberOfInputs);
        Assert.Equal(connectors, _matrix.NumberOfOutputs);
        Assert.Equal(Enumerable.Range(1, connectors), _matrix.Inputs.Select(input => input.ConnectorId));
    }
}
