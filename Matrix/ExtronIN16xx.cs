using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public partial class ExtronIn16Xx : VideoMatrix
{
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public readonly List<ExtronMatrixInput> Inputs = [];
    public readonly List<ExtronMatrixEndpoint> Outputs =
    [
        new("Output 1A", 1, AVEndpointType.Decoder),
        new("Output 1B", 1, AVEndpointType.Decoder),
        new("Output 1C", 1, AVEndpointType.Decoder)
    ];

    private const string EscapeHeader = "\x1b";
    private const string SignalDetected = "1";
    private const string NotDetected = "0";
    private const string DetectedWithHdcp = "1";
    private const string DetectedWithoutHdcp = "2";
    private const string Reconfigured = "Reconfig";
    private const string UntiedAddress = "";
    private const int FirstHdmiInput = 3;
    private const int MaximumSyncTimeout = 501;
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private readonly int _numberOfInputs;
    private readonly TimeProvider _timeProvider;
    private readonly ThreadWorker _pollWorker;
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name)
        : this(communicationClient, numberOfInputs, name, TimeProvider.System)
    {
    }

    internal ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name, TimeProvider timeProvider)
        : base(1, communicationClient, name)
    {
        _numberOfInputs = numberOfInputs;
        _timeProvider = timeProvider;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, _timeProvider);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        communicationClient.ResponseHandlers += HandleResponse;
    }

    [GeneratedRegex(@"^In00 ([01](?:\*[01])*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SignalPresenceRegex();

    [GeneratedRegex(@"^In(\d+) (?:All|RGB)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTieRegex();

    [GeneratedRegex(@"^HdcpI(\d+)\*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex InputHdcpRegex();

    [GeneratedRegex(@"^HdcpO(\d+)\*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex OutputHdcpRegex();

    [GeneratedRegex(@"^HplgO(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HotplugRegex();

    [GeneratedRegex(@"^Nmi(\d+),(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex InputNameRegex();

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                ProcessResponse(line.Trim());
        }
    }

    private void ProcessResponse(string line)
    {
        if (SignalPresenceRegex().Match(line) is { Success: true } presence)
            HandleSignalPresence(presence.Groups[1].Value);
        else if (VideoTieRegex().Match(line) is { Success: true } tie)
            HandleVideoTie(tie.Groups[1].Value);
        else if (InputHdcpRegex().Match(line) is { Success: true } inputHdcp)
            HandleInputHdcp(line, int.Parse(inputHdcp.Groups[1].Value), inputHdcp.Groups[2].Value);
        else if (OutputHdcpRegex().Match(line) is { Success: true } outputHdcp)
            HandleOutputHdcp(line, int.Parse(outputHdcp.Groups[1].Value), outputHdcp.Groups[2].Value);
        else if (HotplugRegex().Match(line) is { Success: true } hotplug)
            WrapAndSendCommand($"O{hotplug.Groups[1].Value}HDCP");
        else if (InputNameRegex().Match(line) is { Success: true } inputName)
            HandleInputName(line, int.Parse(inputName.Groups[1].Value), inputName.Groups[2].Value);
        else if (line == Reconfigured)
            WrapAndSendCommand("0LS");
    }

    private void HandleSignalPresence(string statuses)
    {
        var values = statuses.Split('*');
        SetInputCount(values.Length);
        for (var index = 0; index < values.Length; index++)
            Inputs[index].SetInputStatus(values[index] == SignalDetected ? ConnectionState.Connected : ConnectionState.Disconnected);
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
            var input = new ExtronMatrixInput($"Input {number}", number);
            input.SetStreamAddress(number.ToString());
            Inputs.Add(input);
        }
        EndpointsChangedHandlers?.Invoke();
        QueryInputNames();
    }

    private void HandleVideoTie(string input)
    {
        var address = int.Parse(input).ToString();
        Outputs.ForEach(output => output.SetStreamAddress(address));
    }

    private void HandleInputHdcp(string line, int number, string value)
    {
        if (number < 1 || number > Inputs.Count)
        {
            LogWarning("Ignoring the input HDCP status {Line} as it doesn't name one of the {InputCount} inputs", line, Inputs.Count);
            return;
        }
        Inputs[number - 1].SetInputHdcpStatus(value switch
        {
            DetectedWithHdcp => HdcpStatus.Active,
            DetectedWithoutHdcp => HdcpStatus.NotSupported,
            _ => HdcpStatus.Unknown
        });
    }

    private void HandleOutputHdcp(string line, int connector, string value)
    {
        if (connector < 1 || connector > Outputs.Count)
        {
            LogWarning("Ignoring the output HDCP status {Line} as it doesn't name one of the {ConnectorCount} output connectors", line, Outputs.Count);
            return;
        }
        var (connection, hdcp) = value switch
        {
            NotDetected => (ConnectionState.Disconnected, HdcpStatus.Unknown),
            DetectedWithHdcp => (ConnectionState.Connected, HdcpStatus.Available),
            DetectedWithoutHdcp => (ConnectionState.Connected, HdcpStatus.NotSupported),
            _ => (ConnectionState.Unknown, HdcpStatus.Unknown)
        };
        Outputs[connector - 1].SetOutputStatus(connection);
        Outputs[connector - 1].SetOutputHdcpStatus(hdcp);
    }

    private void HandleInputName(string line, int number, string name)
    {
        if (number < 1 || number > Inputs.Count)
        {
            LogWarning("Ignoring the input name {Line} as it doesn't name one of the {InputCount} inputs", line, Inputs.Count);
            return;
        }
        Inputs[number - 1].SetName(name);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
        {
            ClearStatus();
            return;
        }
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(async _ =>
            {
                try
                {
                    await Task.Delay(ConnectStepGap, _timeProvider);
                    WrapAndSendCommand("3CV");
                    await Task.Delay(ConnectStepGap, _timeProvider);
                    SendCommand("I");
                    QueryStatus();
                    QueryInputNames();
                }
                catch (Exception e)
                {
                    LogException(e, "The connection sequence failed");
                }
            }, TaskScheduler.Default).Unwrap();
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
            output.SetStreamAddress(UntiedAddress);
        }
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        WrapAndSendCommand("CV");
        QueryStatus();
        return Task.CompletedTask;
    }

    private void QueryStatus()
    {
        WrapAndSendCommand("0LS");
        SendCommand("!");
        for (var number = FirstHdmiInput; number <= _numberOfInputs; number++)
            WrapAndSendCommand($"I{number}HDCP");
        for (var connector = 1; connector <= Outputs.Count; connector++)
            WrapAndSendCommand($"O{connector}HDCP");
    }

    private void QueryInputNames()
    {
        for (var number = 1; number <= Inputs.Count; number++)
            WrapAndSendCommand($"{number}NI");
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
            SendCommand($"{input}!");
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
            SendCommand($"{input}%");
            AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching video output {output} to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            using (PushProperties("RouteVideo"))
                LogWarning("Not switching video output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, _numberOfInputs);
        }
    }

    public override bool SupportsAudioBreakaway => true;

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
        {
            SendCommand($"{input}$");
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
        if (seconds <= MaximumSyncTimeout)
        {
            WrapAndSendCommand($"T{seconds}SSAV");
            AddEvent(EventType.VideoMute, $"Set sync timeout to {seconds} seconds");
        }
        else
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than {MaximumSyncTimeout} seconds");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout can't be longer than {MaximumSyncTimeout} seconds", MaximumSyncTimeout);
        }
    }

    public void SetVideoMute(MuteState state)
    {
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(async _ =>
            {
                try
                {
                    SendCommand(state == MuteState.On ? "1*2B" : "1*0B");
                    await Task.Delay(VideoMuteStepGap, _timeProvider);
                    SendCommand(state == MuteState.On ? "2*2B" : "2*0B");
                    await Task.Delay(VideoMuteStepGap, _timeProvider);
                    SendCommand(state == MuteState.On ? "3*2B" : "3*0B");
                }
                catch (Exception e)
                {
                    LogException(e, "The video mute sends failed");
                }
            }, TaskScheduler.Default).Unwrap();
        AddEvent(EventType.VideoMute, $"Switched video mute to {state}");
    }
}