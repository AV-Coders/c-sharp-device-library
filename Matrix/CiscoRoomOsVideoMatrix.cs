using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// Presents the video connectors of a Cisco RoomOS / CE codec as a <see cref="VideoMatrix"/>, so the codec
/// can be shown in the same input/output grid as a matrix switcher. It owns nothing but video status:
/// share the codec's <see cref="CommunicationClient"/> with the conferencing driver and each parses the
/// responses it cares about (the codec deduplicates identical xFeedback registrations, so both drivers
/// registering /Status/Standby is harmless). Input names follow the connector names configured on the codec;
/// outputs keep their "Output n" name and expose the connected display's EDID name separately.
/// Response formats are from a Codec Plus on CE 9.8.
/// The codec cannot route video and this driver does not control standby, so routing and power are no-ops
/// that only record an event; <see cref="DeviceBase.PowerState"/> still mirrors the codec's standby state.
/// </summary>
public class CiscoRoomOsVideoMatrix : VideoMatrix
{
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private readonly Dictionary<int, int> _sourceIdToConnectorId = new();

    public readonly List<CiscoRoomOsVideoInput> Inputs;
    public readonly List<CiscoRoomOsVideoOutput> Outputs;

    /// <param name="communicationClient">The codec's client, usually shared with the conferencing driver.</param>
    /// <param name="inputCount">Video input connectors on the codec (a Codec Plus has 3).</param>
    /// <param name="outputCount">Video output connectors on the codec (a Codec Plus has 2).</param>
    /// <param name="name">Must be unique among the matrices in a UI.</param>
    public CiscoRoomOsVideoMatrix(CommunicationClient communicationClient, int inputCount, int outputCount, string name)
        : base(outputCount, communicationClient, name)
    {
        Inputs = Enumerable.Range(1, inputCount)
            .Select(index => new CiscoRoomOsVideoInput($"Input {index}", index))
            .ToList();
        Outputs = Enumerable.Range(1, outputCount)
            .Select(index => new CiscoRoomOsVideoOutput($"Output {index}", index))
            .ToList();
        CommunicationState = CommunicationState.NotAttempted;
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        // A client that logged in before this driver existed will never replay the login line.
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            Refresh();
    }

    /// <summary>
    /// Registers for video feedback and re-reads every connector. Called automatically when the codec
    /// reports a login; call it yourself if the client was already logged in when this driver was created.
    /// </summary>
    public void Refresh()
    {
        using (PushProperties("Refresh"))
        {
            SendCommand("xFeedback register /Status/Standby");
            SendCommand("xFeedback register /Status/Video/Input/Connector");
            SendCommand("xFeedback register /Status/Video/Input/Source");
            SendCommand("xFeedback register /Status/Video/Output/Connector");
            SendCommand("xFeedback register /Configuration/Video/Input/Connector");
            SendCommand("xStatus Standby");
            SendCommand("xStatus Video Input Connector");
            SendCommand("xStatus Video Input Source");
            SendCommand("xStatus Video Output Connector");
            SendCommand("xConfiguration Video Input Connector Name");
        }
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState is ConnectionState.Connected or ConnectionState.Connecting)
            return;
        using (PushProperties("HandleConnectionState"))
        {
            // Everything learnt from the codec is stale once the session drops; the login line that follows
            // a reconnect triggers Refresh, which repopulates it.
            LogDebug("The codec connection is {ConnectionState}, forgetting the connector states", connectionState);
            CommunicationState = CommunicationState.Error;
            PowerState = PowerState.Unknown;
            _sourceIdToConnectorId.Clear();
            Inputs.ForEach(input => input.Reset());
            Outputs.ForEach(output => output.Reset());
        }
    }

    private void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command + "\r\n");
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    // The SSH client delivers one stripped line per callback. A TCP client delivers whatever arrived, which
    // may hold several lines; a line split across two reads is not reassembled (the same limitation as the
    // Extron drivers). Each line is isolated so one bad line, or a throwing subscriber, cannot drop the rest
    // or stop the client from delivering the response to the conferencing driver sharing this client.
    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    ProcessResponse(line);
                }
                catch (Exception e)
                {
                    LogException(e, $"An exception was thrown while processing the line {line}");
                }
            }
        }
    }

    private void ProcessResponse(string line)
    {
        if (!line.StartsWith("*s ") && !line.StartsWith("*c ") && !line.StartsWith("*r "))
            return;

        var responses = line.Split(' ');
        if (responses.Length < 3)
            return;

        switch (responses[1])
        {
            case "Video":
                ProcessVideoResponse(responses);
                return;
            case "xConfiguration" when responses[2] == "Video":
                ProcessVideoConfigurationResponse(responses);
                return;
            case "Login" when responses[2].Trim() == "successful":
                Refresh();
                return;
            case "Standby" when responses[2] == "State:" && responses.Length >= 4:
                PowerState = responses[3].Trim() == "Off" ? PowerState.On : PowerState.Off;
                return;
        }
    }

    // *s Video Input Connector 1 SignalState: OK
    // *s Video Input Source 1 Resolution Height: 1080
    // *s Video Output Connector 1 ConnectedDevice Name: "SyncMaster"
    private void ProcessVideoResponse(string[] responses)
    {
        if (responses.Length < 7)
            return;
        if (!int.TryParse(responses[4], out var number))
            return;
        switch (responses[2])
        {
            case "Input" when responses[3] == "Connector":
                if (TryGetInput(number, out var input))
                    ProcessVideoInputConnectorResponse(input, responses);
                return;
            case "Input" when responses[3] == "Source":
                ProcessVideoInputSourceResponse(number, responses);
                return;
            case "Output" when responses[3] == "Connector":
                if (TryGetOutput(number, out var output))
                    ProcessVideoOutputConnectorResponse(output, responses);
                return;
        }
    }

    private void ProcessVideoInputConnectorResponse(CiscoRoomOsVideoInput input, string[] responses)
    {
        switch (responses[5])
        {
            case "Connected:":
                if (responses[6].Contains("False"))
                    input.SetConnectionState(ConnectionState.Disconnected);
                return;
            case "SignalState:":
                input.SetConnectionState(responses[6].Trim() switch
                {
                    "OK" => ConnectionState.Connected,
                    "DetectingFormat" => ConnectionState.Connecting,
                    "Unstable" => ConnectionState.Degraded,
                    "Unsupported" => ConnectionState.Error,
                    _ => ConnectionState.Disconnected
                });
                return;
            case "SourceId:":
                if (int.TryParse(responses[6], out var sourceId))
                    _sourceIdToConnectorId[sourceId] = input.ConnectorId;
                return;
            case "HDCP" when responses.Length >= 8 && responses[6] == "State:":
                input.SetHdcpStatus(ParseHdcpState(responses[7]));
                return;
        }
    }

    private void ProcessVideoInputSourceResponse(int sourceId, string[] responses)
    {
        switch (responses[5])
        {
            case "ConnectorId:":
                if (int.TryParse(responses[6], out var connectorId))
                    _sourceIdToConnectorId[sourceId] = connectorId;
                return;
            case "Resolution" when responses.Length >= 8:
                if (TryGetInput(_sourceIdToConnectorId.GetValueOrDefault(sourceId, sourceId), out var input))
                    SetResolutionPart(input, responses[6], responses[7]);
                return;
        }
    }

    private static void ProcessVideoOutputConnectorResponse(CiscoRoomOsVideoOutput output, string[] responses)
    {
        switch (responses[5])
        {
            case "ConnectedDevice" when responses.Length >= 8 && responses[6] == "Name:":
                output.SetConnectedDeviceName(JoinQuotedValue(responses, 7));
                return;
            case "Connected:":
                output.SetConnectionState(responses[6].Contains("True")
                    ? ConnectionState.Connected
                    : ConnectionState.Disconnected);
                return;
            case "Resolution" when responses.Length >= 8:
                SetResolutionPart(output, responses[6], responses[7]);
                return;
            case "HDCP" when responses.Length >= 8 && responses[6] == "State:":
                output.SetHdcpStatus(ParseHdcpState(responses[7]));
                return;
        }
    }

    // *c xConfiguration Video Input Connector 3 Name: "Content"
    private void ProcessVideoConfigurationResponse(string[] responses)
    {
        if (responses.Length < 8)
            return;
        if (responses[3] != "Input" || responses[4] != "Connector" || responses[6] != "Name:")
            return;
        if (int.TryParse(responses[5], out var connectorId) && TryGetInput(connectorId, out var input))
            input.SetName(JoinQuotedValue(responses, 7));
    }

    private static HdcpStatus ParseHdcpState(string value) => value.Trim() switch
    {
        "Active" => HdcpStatus.Active,
        "Inactive" => HdcpStatus.Available,
        "Unsupported" => HdcpStatus.NotSupported,
        _ => HdcpStatus.Unknown
    };

    private static string JoinQuotedValue(string[] responses, int startIndex) =>
        string.Join(' ', responses.Skip(startIndex)).Trim().Trim('"');

    private static void SetResolutionPart(CiscoRoomOsVideoConnector connector, string dimension, string value)
    {
        if (!int.TryParse(value.Trim(), out var parsed))
            return;
        switch (dimension)
        {
            case "Height:":
                connector.SetResolutionHeight(parsed);
                return;
            case "Width:":
                connector.SetResolutionWidth(parsed);
                return;
            case "RefreshRate:":
                connector.SetResolutionRefreshRate(parsed);
                return;
        }
    }

    private bool TryGetInput(int number, out CiscoRoomOsVideoInput input)
    {
        input = number >= 1 && number <= Inputs.Count ? Inputs[number - 1] : null!;
        if (input == null)
            LogVerbose("Ignoring input connector {Connector}, the codec was declared with {InputCount} inputs", number, Inputs.Count);
        return input != null;
    }

    private bool TryGetOutput(int number, out CiscoRoomOsVideoOutput output)
    {
        output = number >= 1 && number <= Outputs.Count ? Outputs[number - 1] : null!;
        if (output == null)
            LogVerbose("Ignoring output connector {Connector}, the codec was declared with {OutputCount} outputs", number, Outputs.Count);
        return output != null;
    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override int NumberOfInputs => Inputs.Count;

    public override int NumberOfOutputs => Outputs.Count;

    public override bool RequiresOutputSpecification => false;

    public override bool SupportsVideoBreakaway => false;

    public override bool SupportsAudioBreakaway => false;

    public override void RouteVideo(int input, int output) => IgnoreRoute("video", input, output);

    public override void RouteAudio(int input, int output) => IgnoreRoute("audio", input, output);

    public override void RouteAV(int input, int output) => IgnoreRoute("AV", input, output);

    private void IgnoreRoute(string plane, int input, int output)
    {
        AddEvent(EventType.Input, $"Ignoring {plane} route of input {input} to output {output}, a codec cannot route video");
        using (PushProperties("IgnoreRoute"))
            LogDebug("Ignoring {Plane} route of input {Input} to output {Output}, a codec cannot route video", plane, input, output);
    }

    public override void PowerOn() => AddEvent(EventType.Power, "Ignoring power on, standby is controlled by the conferencing driver");

    public override void PowerOff() => AddEvent(EventType.Power, "Ignoring power off, standby is controlled by the conferencing driver");
}
