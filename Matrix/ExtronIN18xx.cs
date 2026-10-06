using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public partial class ExtronIn18Xx : VideoMatrix
{
    private readonly int _numberOfInputs;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public readonly List<ExtronMatrixOutput> ComposedOutputs = [new("Output 1", 1)];
    public readonly List<ExtronMatrixInput> Inputs = [];
    public List<ExtronMatrixEndpoint> Outputs => ComposedOutputs
        .SelectMany(output => new[] { output.Primary, output.Secondary })
        .ToList();

    private readonly ThreadWorker _pollWorker;
    private const string EscapeHeader = "\x1b";
    private const string SignalDetected = "1";
    private const string HotplugAsserted = "1";
    private const int HdmiOutput = 1;
    private const int TwistedPairOutput = 2;
    private static readonly char[] LineSeparators = ['\r', '\n'];

    public ExtronIn18Xx(CommunicationClient communicationClient, int numberOfInputs, string name)
        : base(1, communicationClient, name)
    {
        _numberOfInputs = numberOfInputs;
        ComposedOutputs[0].Primary.SetName("Output 1A");
        ComposedOutputs[0].Secondary.SetName("Output 1B");
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        communicationClient.ResponseHandlers += HandleResponse;
        HandleConnectionState(communicationClient.ConnectionState);
    }

    [GeneratedRegex(@"^(?:Inf01\*)?IN180([468])\b")]
    private static partial Regex ModelRegex();

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                ProcessResponse(line);
        }
    }

    private void ProcessResponse(string line)
    {
        if (ModelRegex().Match(line) is { Success: true } model)
        {
            SetInputCount(int.Parse(model.Groups[1].Value));
            QueryStatus();
        }
        else if (line.StartsWith("IN00", StringComparison.OrdinalIgnoreCase))
            HandleSignalPresence(line[4..]);
        else if (line.StartsWith("HdcpI"))
            HandleInputHdcp(line[5..]);
        else if (line.StartsWith("HdcpO"))
            HandleOutputHdcp(line[5..]);
        else if (line.StartsWith("HplgO"))
            HandleOutputHotplug(line[5..]);
        else if (line.StartsWith("VnamI"))
            HandleInputName(line[5..]);
    }

    private void SetInputCount(int count)
    {
        if (Inputs.Count == count)
            return;
        while (Inputs.Count > count)
        {
            LogBaseRegistry.Deregister(Inputs[^1]);
            Inputs.RemoveAt(Inputs.Count - 1);
        }
        while (Inputs.Count < count)
        {
            var number = Inputs.Count + 1;
            Inputs.Add(new ExtronMatrixInput($"Input {number}", number));
        }
        EndpointsChangedHandlers?.Invoke();
    }

    private void HandleSignalPresence(string statuses)
    {
        var values = statuses.Split('*', StringSplitOptions.TrimEntries);
        if (values.Length != Inputs.Count)
            return;
        for (var index = 0; index < values.Length; index++)
            Inputs[index].SetInputStatus(values[index] == SignalDetected ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleInputHdcp(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || number < 1 || number > Inputs.Count)
            return;
        Inputs[number - 1].SetInputHdcpStatus(ExtronSisHdcp.DecodeInput(value).Hdcp);
    }

    private void HandleOutputHdcp(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || OutputConnector(number) is not { } output)
            return;
        var (connection, hdcp) = ExtronSisHdcp.DecodeOutput(value);
        output.SetOutputStatus(connection);
        output.SetOutputHdcpStatus(hdcp);
    }

    private void HandleOutputHotplug(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || OutputConnector(number) is not { } output)
            return;
        output.SetOutputStatus(value == HotplugAsserted ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleInputName(string text)
    {
        if (!TryParsePortValue(text, out var number, out var name) || number < 1 || number > Inputs.Count)
            return;
        Inputs[number - 1].SetName(name);
    }

    private ExtronMatrixEndpoint? OutputConnector(int number) => number switch
    {
        HdmiOutput => ComposedOutputs[0].Primary,
        TwistedPairOutput => ComposedOutputs[0].Secondary,
        _ => null
    };

    private static bool TryParsePortValue(string text, out int port, out string value)
    {
        var parts = text.Split('*', StringSplitOptions.TrimEntries);
        port = 0;
        value = parts.Length == 2 ? parts[1] : string.Empty;
        return parts.Length == 2 && int.TryParse(parts[0], out port);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
        {
            ClearStatus();
            return;
        }
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        WrapAndSendCommand("3CV");
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        SendCommand("1I");
    }

    private void ClearStatus()
    {
        foreach (var input in Inputs)
        {
            input.SetInputStatus(ConnectionState.Unknown);
            input.SetInputHdcpStatus(HdcpStatus.Unknown);
        }
        foreach (var output in Outputs)
        {
            output.SetOutputStatus(ConnectionState.Unknown);
            output.SetOutputHdcpStatus(HdcpStatus.Unknown);
        }
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        if (Inputs.Count == 0)
            SendCommand("1I");
        QueryStatus();
        return Task.CompletedTask;
    }

    private void QueryStatus()
    {
        WrapAndSendCommand("0LS");
        for (var number = 1; number <= Inputs.Count; number++)
            WrapAndSendCommand($"I{number}HDCP");
        WrapAndSendCommand($"O{HdmiOutput}HDCP");
        WrapAndSendCommand($"O{TwistedPairOutput}HDCP");
    }

    private void SendCommand(string command)
    {
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

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}*1!");
            AddEvent(EventType.Input, $"Switched output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteAV"))
                LogWarning("Not switching output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override int NumberOfOutputs => 1;
    public override int NumberOfInputs => Inputs.Count;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}*1%");
            AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching video output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteVideo"))
                LogWarning("Not switching video output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public override bool SupportsAudioBreakaway => false;

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}*1$");
            AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching audio output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteAudio"))
                LogWarning("Not switching audio output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds < 502)
        {
            WrapAndSendCommand($"T1*{seconds}SSAV");
            AddEvent(EventType.VideoMute, $"Set sync timeout to {seconds} seconds");
        }
        else
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than 502 seconds");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout can't be longer than 502 seconds");
        }
    }

    public void SetVideoMute(MuteState state)
    {
        SendCommand(state == MuteState.On ? "1*2B" : "1*0B");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        SendCommand(state == MuteState.On ? "2*2B" : "2*0B");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        SendCommand(state == MuteState.On ? "3*2B" : "3*0B");
        AddEvent(EventType.VideoMute, $"Switched video mute to {state}");
    }
}
