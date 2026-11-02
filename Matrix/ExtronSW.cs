using AVCoders.Core;
using System.Text;
using System.Text.RegularExpressions;

namespace AVCoders.Matrix;

/// <summary>SW HD 4K PLUS switcher with model discovery and SIS sync feedback.</summary>
public partial class ExtronSw : ExtronSisMatrix, IDisposable
{
    public readonly List<ExtronMatrixInput> Inputs = [];
    public readonly List<ExtronMatrixEndpoint> Outputs =
    [
        new("Output 1", 1, AVEndpointType.Decoder)
    ];

    private const string EscapeHeader = "\x1b";
    private const string ModelTag = "Inf01*";
    private const string ModelLabel = "Model";
    private const string ModelIssueKey = "model";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly ExtronSisCommand VerboseMode = new($"{EscapeHeader}3CV\r", "Vrb");
    private static readonly ExtronSisCommand ModelQuery = new("1I", ModelTag);
    private static readonly ExtronSisCommand SignalStatusQuery = new($"{EscapeHeader}LS\r", "Sig");
    private static readonly ExtronSisCommand InputHdcpQuery = new($"{EscapeHeader}IHDCP\r", "Hdcp");
    private static readonly ExtronSisCommand OutputHdcpQuery = new($"{EscapeHeader}OHDCP\r", "Hdcp");
    private static readonly ExtronSisCommand TieQuery = new("!", new ExtronSisTie(null, ExtronSisTie.AllOutputs, ExtronSisTie.VideoAndAudio));
    private static readonly ExtronSisQuery[] IdentityQueries =
    [
        new("Q", "Ver01*", "Firmware", "firmware-read"),
        new("N", "Pno", "Part Number", "part-number-read"),
        new($"{EscapeHeader}CN\r", "Ipn ", "Unit Name", "unit-name-read"),
        new($"{EscapeHeader}CH\r", "Iph ", "MAC", "mac-read")
    ];
    private readonly ThreadWorker _pollWorker;
    private readonly object _responseLock = new();
    private readonly StringBuilder _pendingResponse = new();
    private bool _discardResponse;
    private bool _disposed;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    /// <summary>Discovers the input count on connection; the HDMI output is always output 1.</summary>
    public ExtronSw(CommunicationClient communicationClient, string name)
        : this(communicationClient, name, null)
    {
    }

    internal ExtronSw(CommunicationClient communicationClient, string name, TimeProvider? timeProvider)
        : base(1, communicationClient, name, timeProvider)
    {
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, TimeProvider);
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(CommunicationClient.ConnectionState);
        _ = _pollWorker.Restart();
    }

    private void SetInputCount(int count)
    {
        lock (_responseLock)
        {
            if (_disposed || Inputs.Count == count)
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
        }
        EndpointsChangedHandlers?.Invoke();
    }

    private (List<ExtronMatrixInput> Inputs, ExtronMatrixEndpoint Output)? Endpoints()
    {
        lock (_responseLock)
        {
            if (_disposed)
                return null;
            return (Inputs.ToList(), Outputs[0]);
        }
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        List<ExtronMatrixInput> inputs;
        ExtronMatrixEndpoint output;
        lock (_responseLock)
        {
            if (_disposed)
                return;
            _pendingResponse.Clear();
            _discardResponse = false;
            inputs = [..Inputs];
            output = Outputs[0];
        }
        ForgetCommands();
        foreach (var input in inputs)
        {
            input.SetInputStatus(ConnectionState.Unknown);
            input.SetInputHdcpStatus(HdcpStatus.Unknown);
        }
        output.SetOutputStatus(ConnectionState.Unknown);
        output.SetOutputHdcpStatus(HdcpStatus.Unknown);
        output.SetStreamAddress(string.Empty);
        if (connectionState != ConnectionState.Connected)
            return;
        SendCommand(VerboseMode);
        SendCommand(ModelQuery);
        QueryStatus();
    }

    private Task Poll(CancellationToken token)
    {
        if (!_disposed && !token.IsCancellationRequested &&
            CommunicationClient.ConnectionState == ConnectionState.Connected)
        {
            ReportMissedReplies();
            if (NumberOfInputs == 0)
                SendCommand(ModelQuery);
            QueryStatus();
            ReadAgainIfUnanswered(IdentityQueries);
        }
        return Task.CompletedTask;
    }

    // These read-only queries double as a heartbeat every 20 seconds while connected.
    private void QueryStatus()
    {
        SendCommand(SignalStatusQuery);
        SendCommand(InputHdcpQuery);
        SendCommand(OutputHdcpQuery);
        SendCommand(TieQuery);
    }

    [GeneratedRegex(@"^(?:Inf01\*)?(SW([2468])\s+HD\s+4K\s+PLUS(?:\s+Series)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"^In\s*(\d+)\s+(?:All|Ausw.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TieRegex();

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            foreach (var line in TakeCompleteLines(response))
                ProcessResponse(line);
        }
    }

    private List<string> TakeCompleteLines(string response)
    {
        List<string> lines = [];
        lock (_responseLock)
        {
            if (_disposed)
                return lines;
            // SSH supplies complete lines without terminators. TCP and serial supply chunks.
            if (CommunicationClient is not (TcpClient or SerialClient))
            {
                lines.AddRange(response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries));
                return lines;
            }

            foreach (var character in response)
            {
                if (character is '\r' or '\n')
                {
                    var line = _pendingResponse.ToString();
                    _pendingResponse.Clear();
                    var discard = _discardResponse;
                    _discardResponse = false;
                    if (!discard && line.Length > 0)
                        lines.Add(line);
                }
                else if (!_discardResponse)
                {
                    // Bound memory for malformed traffic, then recover at the next terminator.
                    if (_pendingResponse.Length == 4096)
                    {
                        _pendingResponse.Clear();
                        _discardResponse = true;
                    }
                    else
                        _pendingResponse.Append(character);
                }
            }
        }
        return lines;
    }

    private void ProcessResponse(string response)
    {
        var line = response.Trim();
        using var reply = TakeReply(line);
        using var scope = PushProperties();
        if (ModelRegex().Match(line) is { Success: true } model)
            HandleModel(model.Groups[1].Value, int.Parse(model.Groups[2].Value));
        else if (line.StartsWith(ModelTag))
            HandleUnknownModel(line);
        else if (IdentityQueries.FirstOrDefault(query => query.IsAnsweredBy(line)) is { } identity)
            ShowRead(identity, line);
        else if (line.StartsWith("Sig", StringComparison.OrdinalIgnoreCase))
            HandleSignalStatus(line[3..]);
        else if (line.StartsWith("Hdcp", StringComparison.OrdinalIgnoreCase))
            HandleHdcp(line[4..].TrimStart());
        else if (TieRegex().Match(line) is { Success: true } tie)
            HandleTie(line, tie.Groups[1].Value);
        else if (IsError(line))
            HandleError(line);
    }

    private void HandleModel(string model, int inputCount)
    {
        SetDetail(ModelLabel, model);
        ResolveIssue(ModelIssueKey);
        SetInputCount(inputCount);
        Read(IdentityQueries);
    }

    private void HandleUnknownModel(string line)
    {
        SetDetail(ModelLabel, line[ModelTag.Length..]);
        RaiseOngoingIssue(ModelIssueKey, $"Unable to read the input count from the model {line}");
    }

    private void HandleTie(string line, string input)
    {
        if (Endpoints() is not { } endpoints)
            return;
        var known = int.TryParse(input, out var selected) && selected <= endpoints.Inputs.Count;
        if (!known && endpoints.Inputs.Count == 0)
            LogWarning("Ignoring the tie {Line} as it arrived before the input count is known", line);
        else if (!known)
            LogWarning("Ignoring the tie {Line} as it names an input outside the {InputCount} inputs", line, endpoints.Inputs.Count);
        else
            endpoints.Output.SetStreamAddress(selected.ToString());
    }

    private void HandleSignalStatus(string status)
    {
        // SIS: Sig<input states separated by spaces>*<output state>.
        // SW2 firmware also wraps the complete signal list in double quotes.
        var values = status.Trim();
        if (values.Length >= 2 && values[0] == '"' && values[^1] == '"')
            values = values[1..^1];
        var parts = values.Split('*');
        if (parts.Length != 2 || !TryStatuses(parts[0], 1, out var inputs) ||
            !TryStatuses(parts[1], 1, out var outputs) || outputs.Length != 1 ||
            inputs.Length is not (2 or 4 or 6 or 8))
        {
            LogWarning("Malformed signal status {Status}", status);
            return;
        }
        SetInputCount(inputs.Length);
        if (Endpoints() is not { } endpoints)
            return;
        for (var index = 0; index < inputs.Length; index++)
            endpoints.Inputs[index].SetInputStatus(inputs[index] == 1 ? ConnectionState.Connected : ConnectionState.Disconnected);
        endpoints.Output.SetOutputStatus(outputs[0] == 1 ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleHdcp(string status)
    {
        if (status.Length < 2 || !TryStatuses(status[1..], 2, out var values))
        {
            LogWarning("Malformed HDCP status {Status}", status);
            return;
        }
        if (Endpoints() is not { } endpoints)
            return;
        if (status[0] == 'I' && values.Length == endpoints.Inputs.Count)
        {
            for (var index = 0; index < values.Length; index++)
                endpoints.Inputs[index].SetInputHdcpStatus(values[index] switch
                {
                    1 => HdcpStatus.Active,
                    2 => HdcpStatus.NotSupported,
                    _ => HdcpStatus.Unknown
                });
        }
        else if (status[0] == 'I')
            LogWarning("Ignoring the input HDCP status {Status} as it doesn't list the {InputCount} inputs", status, endpoints.Inputs.Count);
        else if (status[0] == 'O' && values.Length == 1)
            endpoints.Output.SetOutputHdcpStatus(values[0] switch
            {
                1 => HdcpStatus.Available,
                2 => HdcpStatus.NotSupported,
                _ => HdcpStatus.Unknown
            });
        else if (status[0] == 'O')
            LogWarning("Ignoring the output HDCP status {Status} as it doesn't list the one output", status);
        else
            LogWarning("Malformed HDCP status {Status}", status);
    }

    private static bool TryStatuses(string text, int maximum, out int[] values)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        values = new int[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
            if (!int.TryParse(tokens[index], out values[index]) || values[index] < 0 || values[index] > maximum)
                return false;
        return tokens.Length > 0;
    }

    private void SendCommand(ExtronSisCommand command)
    {
        if (!_disposed)
            Send(command);
    }

    public void Dispose()
    {
        lock (_responseLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        CommunicationClient.ResponseHandlers -= HandleResponse;
        CommunicationClient.ConnectionStateHandlers -= HandleConnectionState;
        CommunicationClient.ConnectionStateHandlers -= HandleRequestedDisconnect;
        // Stop awaits the worker internally. Run it off the caller's UI context so its
        // continuations can complete even while synchronous Dispose waits for them.
        Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
        lock (_responseLock)
        {
            Inputs.DeregisterAndClear();
            foreach (var output in Outputs)
                LogBaseRegistry.Deregister(output);
            Outputs.Clear();
        }
        LogBaseRegistry.Deregister(_pollWorker);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }

    public override void PowerOn() { }
    public override void PowerOff() { }
    public override int NumberOfOutputs => 1;
    public override int NumberOfInputs => Inputs.Count;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;
    public override bool SupportsAudioBreakaway => false;

    public override void RouteVideo(int input, int output)
    {
        AddEvent(EventType.Input, $"Not switching video output {output} to input {input} as this device doesn't support video breakaway");
        using (PushProperties("RouteVideo"))
            LogWarning("Not switching video output {Output} to input {Input} as this device doesn't support video breakaway", output, input);
    }

    public override void RouteAudio(int input, int output)
    {
        AddEvent(EventType.Input, $"Not switching audio output {output} to input {input} as this device doesn't support audio breakaway");
        using (PushProperties("RouteAudio"))
            LogWarning("Not switching audio output {Output} to input {Input} as this device doesn't support audio breakaway", output, input);
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            SendCommand(new ExtronSisCommand($"{input}!", new ExtronSisTie(input, ExtronSisTie.AllOutputs, ExtronSisTie.VideoAndAudio)));
            AddEvent(EventType.Input, $"Switched to input {input}");
        }
        else
        {
            AddEvent(EventType.Input, $"Not switching to input {input} as it is out of range, must be between 1 and {NumberOfInputs}");
            using (PushProperties("RouteAV"))
                LogWarning("Not switching to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", input, NumberOfInputs);
        }
    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];
}
