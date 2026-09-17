using AVCoders.Core;
using System.Text;
using System.Text.RegularExpressions;

namespace AVCoders.Matrix;

/// <summary>SW HD 4K PLUS switcher with model discovery and SIS sync feedback.</summary>
public partial class ExtronSw : VideoMatrix, IDisposable
{
    public readonly List<ExtronMatrixInput> Inputs = [];
    public readonly List<ExtronMatrixEndpoint> Outputs =
    [
        new("Output 1", 1, AVEndpointType.Decoder)
    ];

    private const string EscapeHeader = "\x1b";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private readonly ThreadWorker _pollWorker;
    private readonly object _responseLock = new();
    private readonly StringBuilder _pendingResponse = new();
    private bool _discardResponse;
    private bool _disposed;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    /// <summary>Discovers the input count on connection; the HDMI output is always output 1.</summary>
    public ExtronSw(CommunicationClient communicationClient, string name)
        : base(1, communicationClient, name)
    {
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(CommunicationClient.ConnectionState);
        _ = _pollWorker.Restart();
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

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (_disposed)
            return;
        lock (_responseLock)
        {
            _pendingResponse.Clear();
            _discardResponse = false;
        }
        foreach (var input in Inputs)
        {
            input.SetInputStatus(ConnectionState.Unknown);
            input.SetInputHdcpStatus(HdcpStatus.Unknown);
        }
        Outputs[0].SetOutputStatus(ConnectionState.Unknown);
        Outputs[0].SetOutputHdcpStatus(HdcpStatus.Unknown);
        Outputs[0].SetStreamAddress(string.Empty);
        if (connectionState != ConnectionState.Connected)
            return;
        WrapAndSendCommand("3CV");
        SendCommand("1I");
        QueryStatus();
    }

    private Task Poll(CancellationToken token)
    {
        if (!_disposed && !token.IsCancellationRequested &&
            CommunicationClient.ConnectionState == ConnectionState.Connected)
        {
            if (NumberOfInputs == 0)
                SendCommand("1I");
            QueryStatus();
        }
        return Task.CompletedTask;
    }

    // These read-only queries double as a heartbeat every 20 seconds while connected.
    private void QueryStatus()
    {
        WrapAndSendCommand("LS");
        WrapAndSendCommand("IHDCP");
        WrapAndSendCommand("OHDCP");
        SendCommand("!");
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    [GeneratedRegex(@"^(?:Inf01\*)?SW([2468])\s+HD\s+4K\s+PLUS(?:\s+Series)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"^In\s*(\d+)\s+(?:All|Ausw.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TieRegex();

    private void HandleResponse(string response)
    {
        if (_disposed)
            return;
        using (PushProperties("HandleResponse"))
        {
            lock (_responseLock)
            {
                // SSH supplies complete lines without terminators. TCP and serial supply chunks.
                if (CommunicationClient is not (TcpClient or SerialClient))
                {
                    foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                        ProcessResponse(line);
                    return;
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
                            ProcessResponse(line);
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
        }
    }

    private void ProcessResponse(string response)
    {
        var line = response.Trim();
        if (ModelRegex().Match(line) is { Success: true } model)
            SetInputCount(int.Parse(model.Groups[1].Value));
        else if (line.StartsWith("Sig", StringComparison.OrdinalIgnoreCase))
            HandleSignalStatus(line[3..]);
        else if (line.StartsWith("Hdcp", StringComparison.OrdinalIgnoreCase))
            HandleHdcp(line[4..].TrimStart());
        else if (TieRegex().Match(line) is { Success: true } tie &&
                 int.TryParse(tie.Groups[1].Value, out var selected) && selected <= NumberOfInputs)
            Outputs[0].SetStreamAddress(selected.ToString());
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
            return;
        SetInputCount(inputs.Length);
        for (var index = 0; index < inputs.Length; index++)
            Inputs[index].SetInputStatus(inputs[index] == 1 ? ConnectionState.Connected : ConnectionState.Disconnected);
        Outputs[0].SetOutputStatus(outputs[0] == 1 ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleHdcp(string status)
    {
        if (status.Length < 2 || !TryStatuses(status[1..], 2, out var values))
            return;
        if (status[0] == 'I' && values.Length == NumberOfInputs)
        {
            for (var index = 0; index < values.Length; index++)
                Inputs[index].SetInputHdcpStatus(values[index] switch
                {
                    1 => HdcpStatus.Active,
                    2 => HdcpStatus.NotSupported,
                    _ => HdcpStatus.Unknown
                });
        }
        else if (status[0] == 'O' && values.Length == 1)
            Outputs[0].SetOutputHdcpStatus(values[0] switch
            {
                1 => HdcpStatus.Available,
                2 => HdcpStatus.NotSupported,
                _ => HdcpStatus.Unknown
            });
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
        Inputs.DeregisterAndClear();
        foreach (var output in Outputs)
            LogBaseRegistry.Deregister(output);
        Outputs.Clear();
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
        using (PushProperties("RouteVideo"))
            LogWarning("This device doesn't support video breakaway");
    }

    public override void RouteAudio(int input, int output)
    {
        using (PushProperties("RouteAudio"))
            LogWarning("This device doesn't support audio breakaway");
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= NumberOfInputs)
        {
            SendCommand($"{input}!");
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
