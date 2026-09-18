using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// Presents the HDMI ports of an Extron Annotator 401 as a <see cref="VideoMatrix"/>, so the annotator's sync
/// and HDCP status can be seen in the same input/output grid as a matrix switcher. The topology is fixed and
/// exists from construction: one HDMI input and two HDMI outputs, both of which always show that input with
/// the annotation overlay, so there is nothing to route and the routing methods only log.
/// It owns nothing but video status: share the annotator's <see cref="CommunicationClient"/> with
/// <c>AVCoders.Annotator.ExtronAnnotator401</c> and each driver parses the responses it cares about.
/// Status is read on connection and re-read by the 20 second poll, which doubles as a keepalive; unsolicited
/// updates in verbose mode use the same response formats and are handled by the same parser.
/// Response formats are from an Annotator 401 running firmware V1.01.
/// </summary>
public partial class ExtronAnnotator401VideoMatrix : VideoMatrix, IDisposable
{
    public static readonly ushort DefaultPort = 22023;
    public const int InputCount = 1;
    public const int OutputCount = 2;

    /// <summary>The address held by the single input and reported by both outputs, which always show it.</summary>
    private const string InputAddress = "1";
    private const string EscapeHeader = "\x1b";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private readonly ThreadWorker _pollWorker;
    private bool _disposed;

    public readonly List<ExtronMatrixInput> Inputs = [new("Input 1", 1)];

    public readonly List<ExtronMatrixEndpoint> Outputs =
    [
        new("Output 1", 1, AVEndpointType.Decoder),
        new("Output 2", 2, AVEndpointType.Decoder)
    ];

    /// <param name="communicationClient">The annotator's client, usually shared with the annotation driver.</param>
    /// <param name="name">Must be unique among the matrices in a UI.</param>
    public ExtronAnnotator401VideoMatrix(CommunicationClient communicationClient, string name)
        : base(OutputCount, communicationClient, name)
    {
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        // The ties are hard-wired, so they are reported once rather than polled. The input carries the same
        // address so a UI can resolve each output's source back to the input's name.
        Inputs[0].SetStreamAddress(InputAddress);
        Outputs.ForEach(output => output.SetStreamAddress(InputAddress));
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(CommunicationClient.ConnectionState);
        _ = _pollWorker.Restart();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (_disposed)
            return;
        // Whatever was last reported is only true while the annotator is reachable.
        Inputs[0].SetInputStatus(ConnectionState.Unknown);
        Inputs[0].SetInputHdcpStatus(HdcpStatus.Unknown);
        foreach (var output in Outputs)
        {
            output.SetOutputStatus(ConnectionState.Unknown);
            output.SetOutputHdcpStatus(HdcpStatus.Unknown);
        }

        if (connectionState != ConnectionState.Connected)
            return;
        // The annotation driver also asks for verbose mode. Setting it here too is idempotent and keeps this
        // driver working on its own, without a second driver on the client.
        WrapAndSendCommand("3CV");
        QueryStatus();
    }

    private Task Poll(CancellationToken token)
    {
        if (!_disposed && !token.IsCancellationRequested &&
            CommunicationClient.ConnectionState == ConnectionState.Connected)
            QueryStatus();
        return Task.CompletedTask;
    }

    // These read-only queries double as a heartbeat every 20 seconds while connected. The HDCP responses carry
    // the sync state as well, but 0LS is still worth asking for: verbose mode pushes In00 unsolicited, so the
    // input's sync state also updates between polls.
    private void QueryStatus()
    {
        WrapAndSendCommand("0LS");
        foreach (var input in Inputs)
            WrapAndSendCommand($"I{input.Number}HDCP");
        foreach (var output in Outputs)
            WrapAndSendCommand($"O{output.Number}HDCP");
    }

    // The annotator reports signal presence for its only input as a single flag, for example "In00 1".
    [GeneratedRegex(@"^In00 ([01])$")]
    private static partial Regex SignalPresenceRegex();

    [GeneratedRegex(@"^Hdcp([IO])(\d+)\*(\d+)$")]
    private static partial Regex HdcpRegex();

    [GeneratedRegex(@"^E\d\d$")]
    private static partial Regex ErrorRegex();

    // The SSH client delivers one stripped line per callback. A TCP client delivers whatever arrived, which
    // may hold several lines; a line split across two reads is not reassembled (the same limitation as the
    // other Extron drivers). Each line is isolated so one bad line, or a throwing subscriber, cannot drop the
    // rest or stop the client from delivering the response to the annotation driver sharing this client.
    private void HandleResponse(string response)
    {
        if (_disposed)
            return;
        using (PushProperties("HandleResponse"))
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

    private void ProcessResponse(string response)
    {
        var line = response.Trim();
        if (line.Length == 0)
            return;

        if (SignalPresenceRegex().Match(line) is { Success: true } presence)
            Inputs[0].SetInputStatus(presence.Groups[1].Value == "1"
                ? ConnectionState.Connected
                : ConnectionState.Disconnected);
        else if (HdcpRegex().Match(line) is { Success: true } hdcp && int.TryParse(hdcp.Groups[2].Value, out var endpoint))
            HandleHdcp(hdcp.Groups[1].Value == "I", endpoint, hdcp.Groups[3].Value);
        else if (ErrorRegex().IsMatch(line))
            LogWarning("The annotator rejected a command with error {ErrorCode}", line);
    }

    private void HandleHdcp(bool isInput, int number, string value)
    {
        if (isInput)
        {
            if (number < 1 || number > Inputs.Count)
                return;
            var (connection, hdcpStatus) = ExtronSisHdcp.DecodeInput(value);
            LogVerbose("Setting input {InputNumber} as {Status}", number, connection.ToString());
            Inputs[number - 1].SetInputStatus(connection);
            Inputs[number - 1].SetInputHdcpStatus(hdcpStatus);
            return;
        }

        if (number < 1 || number > Outputs.Count)
            return;
        var (outputConnection, outputHdcpStatus) = ExtronSisHdcp.DecodeOutput(value);
        LogVerbose("Setting output {OutputNumber} as {Status}", number, outputConnection.ToString());
        Outputs[number - 1].SetOutputStatus(outputConnection);
        Outputs[number - 1].SetOutputHdcpStatus(outputHdcpStatus);
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private void SendCommand(string command)
    {
        if (_disposed)
            return;
        try
        {
            CommunicationClient.Send(command);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    public override int NumberOfInputs => InputCount;
    public override int NumberOfOutputs => OutputCount;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;
    public override bool SupportsAudioBreakaway => false;

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override void RouteVideo(int input, int output) => LogRoutingIsNotSupported("RouteVideo");

    public override void RouteAudio(int input, int output) => LogRoutingIsNotSupported("RouteAudio");

    public override void RouteAV(int input, int output) => LogRoutingIsNotSupported("RouteAV");

    private void LogRoutingIsNotSupported(string method)
    {
        const string message = "The annotator has one input which both outputs always show, so there is nothing to route";
        AddEvent(EventType.Input, message);
        using (PushProperties(method))
            LogWarning(message);
    }

    /// <summary>Power is the annotation driver's to control; this driver only reports video status.</summary>
    public override void PowerOn() { }

    public override void PowerOff() { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CommunicationClient.ResponseHandlers -= HandleResponse;
        CommunicationClient.ConnectionStateHandlers -= HandleConnectionState;
        // Stop awaits the worker internally. Run it off the caller's UI context so its
        // continuations can complete even while synchronous Dispose waits for them.
        Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
        // The topology is fixed, so unlike the drivers that rebuild their lists at runtime the endpoints are
        // only deregistered: the lists stay populated to match the constant NumberOfInputs / NumberOfOutputs.
        foreach (var input in Inputs)
            LogBaseRegistry.Deregister(input);
        foreach (var output in Outputs)
            LogBaseRegistry.Deregister(output);
        LogBaseRegistry.Deregister(_pollWorker);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }
}
