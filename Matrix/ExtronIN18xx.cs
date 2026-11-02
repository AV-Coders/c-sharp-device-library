using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public partial class ExtronIn18Xx : ExtronSisMatrix
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
    private const int MaximumSyncTimeout = 501;
    private const string ModelTag = "Inf01*";
    private const string ModelLabel = "Model";
    private const string ModelIssueKey = "model";
    private const int TieOutput = 1;
    private const string SyncTimeoutReplyStart = "Ssav";
    private const string VideoMuteReplyStart = "Vmt";
    private const string InputHdcpReplyStart = "HdcpI";
    private const string OutputHdcpReplyStart = "HdcpO";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private static readonly ExtronSisCommand VerboseMode = new($"{EscapeHeader}3CV\r", "Vrb");
    private static readonly ExtronSisCommand ModelQuery = new("1I", ModelTag);
    private static readonly ExtronSisCommand SignalPresenceQuery = new($"{EscapeHeader}0LS\r", "In00 ");
    private static readonly ExtronSisQuery[] IdentityQueries =
    [
        new("*Q", "Bld", "Firmware", "firmware-read"),
        new("N", "Pno", "Part Number", "part-number-read"),
        new($"{EscapeHeader}CN\r", "Ipn ", "Unit Name", "unit-name-read"),
        new($"{EscapeHeader}CH\r", "Iph ", "MAC", "mac-read")
    ];
    private static readonly ExtronSisQuery TemperatureQuery = new($"{EscapeHeader}20STAT\r", "20Stat ", "Temperature", "temperature-read");
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;

    public ExtronIn18Xx(CommunicationClient communicationClient, int numberOfInputs, string name)
        : this(communicationClient, numberOfInputs, name, null)
    {
    }

    internal ExtronIn18Xx(CommunicationClient communicationClient, int numberOfInputs, string name, TimeProvider? timeProvider)
        : base(1, communicationClient, name, timeProvider)
    {
        _numberOfInputs = numberOfInputs;
        ComposedOutputs[0].Primary.SetName("Output 1A");
        ComposedOutputs[0].Secondary.SetName("Output 1B");
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, TimeProvider);
        _pollWorker.Restart();
        communicationClient.ConnectionStateHandlers += HandleConnectionState;
        communicationClient.ResponseHandlers += HandleResponse;
        HandleConnectionState(communicationClient.ConnectionState);
    }

    [GeneratedRegex(@"^(?:Inf01\*)?(IN180([468])(?:\s.*)?)$")]
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
        using var reply = TakeReply(line);
        using var scope = PushProperties();
        if (ModelRegex().Match(line) is { Success: true } model)
            HandleModel(model.Groups[1].Value, int.Parse(model.Groups[2].Value));
        else if (line.StartsWith(ModelTag))
            HandleUnknownModel(line);
        else if (IdentityQueries.FirstOrDefault(query => query.IsAnsweredBy(line)) is { } identity)
            ShowRead(identity, line);
        else if (TemperatureQuery.IsAnsweredBy(line))
            ShowRead(TemperatureQuery, line);
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
        else if (IsError(line))
            HandleError(line);
    }

    private void HandleModel(string model, int inputCount)
    {
        SetDetail(ModelLabel, model);
        ResolveIssue(ModelIssueKey);
        SetInputCount(inputCount);
        QueryStatus();
        Read(IdentityQueries);
    }

    private void HandleUnknownModel(string line)
    {
        SetDetail(ModelLabel, line[ModelTag.Length..]);
        RaiseOngoingIssue(ModelIssueKey, $"Unable to read the input count from the model {line}");
    }

    private void SetInputCount(int count)
    {
        if (count != _numberOfInputs)
            LogWarning("The model reply reports {DiscoveredInputs} inputs but the driver was constructed with {ConfiguredInputs}; routing uses the discovered count", count, _numberOfInputs);
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
        {
            LogWarning("Ignoring the signal presence {Statuses} as it doesn't list the {InputCount} inputs", statuses, Inputs.Count);
            return;
        }
        for (var index = 0; index < values.Length; index++)
            Inputs[index].SetInputStatus(values[index] == SignalDetected ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleInputHdcp(string status)
    {
        if (!TryParsePortValue(status, out var number, out var value) || number < 1 || number > Inputs.Count)
        {
            LogWarning("Ignoring the input HDCP status {Status} as it doesn't name one of the {InputCount} inputs", status, Inputs.Count);
            return;
        }
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
        var parts = text.Split('*', 2, StringSplitOptions.TrimEntries);
        port = 0;
        value = parts.Length == 2 ? parts[1] : string.Empty;
        return parts.Length == 2 && int.TryParse(parts[0], out port);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        ForgetCommands();
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
                    await Task.Delay(ConnectStepGap, TimeProvider);
                    Send(VerboseMode);
                    await Task.Delay(ConnectStepGap, TimeProvider);
                    Send(ModelQuery);
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
        }
    }

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        ReportMissedReplies();
        if (Inputs.Count == 0)
            Send(ModelQuery);
        QueryStatus();
        ReadUnlessNotSupported(TemperatureQuery);
        ReadAgainIfUnanswered(IdentityQueries);
        return Task.CompletedTask;
    }

    private void QueryStatus()
    {
        Send(SignalPresenceQuery);
        for (var number = 1; number <= Inputs.Count; number++)
            Send(new ExtronSisCommand($"{EscapeHeader}I{number}HDCP\r", InputHdcpReplyStart));
        Send(new ExtronSisCommand($"{EscapeHeader}O{HdmiOutput}HDCP\r", OutputHdcpReplyStart));
        Send(new ExtronSisCommand($"{EscapeHeader}O{TwistedPairOutput}HDCP\r", OutputHdcpReplyStart));
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}*{TieOutput}!", new ExtronSisTie(input, TieOutput, ExtronSisTie.VideoAndAudio)));
            AddEvent(EventType.Input, $"Switched output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching output {output} to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteAV"))
                LogWarning("Not switching output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, NumberOfInputs);
        }
    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override int NumberOfOutputs => 1;
    public override int NumberOfInputs => Inputs.Count == 0 ? _numberOfInputs : Inputs.Count;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}*{TieOutput}%", new ExtronSisTie(input, TieOutput, ExtronSisTie.Video)));
            AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching video output {output} to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteVideo"))
                LogWarning("Not switching video output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, NumberOfInputs);
        }
    }

    public override bool SupportsAudioBreakaway => false;

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            Send(new ExtronSisCommand($"{input}*{TieOutput}$", new ExtronSisTie(input, TieOutput, ExtronSisTie.Audio)));
            AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
        }
        else
        {
            AddEvent(EventType.Error, $"Not switching audio output {output} to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteAudio"))
                LogWarning("Not switching audio output {Output} to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", output, input, NumberOfInputs);
        }
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds <= MaximumSyncTimeout)
        {
            Send(new ExtronSisCommand($"{EscapeHeader}T1*{seconds}SSAV\r", SyncTimeoutReplyStart));
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
                    Send(new ExtronSisCommand(state == MuteState.On ? "1*2B" : "1*0B", VideoMuteReplyStart));
                    await Task.Delay(VideoMuteStepGap, TimeProvider);
                    Send(new ExtronSisCommand(state == MuteState.On ? "2*2B" : "2*0B", VideoMuteReplyStart));
                    await Task.Delay(VideoMuteStepGap, TimeProvider);
                    Send(new ExtronSisCommand(state == MuteState.On ? "3*2B" : "3*0B", VideoMuteReplyStart));
                }
                catch (Exception e)
                {
                    LogException(e, "The video mute sends failed");
                }
            }, TaskScheduler.Default).Unwrap();
        AddEvent(EventType.VideoMute, $"Switched video mute to {state}");
    }
}
