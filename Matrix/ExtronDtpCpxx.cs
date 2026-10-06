using System.Text;
using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public delegate void EndpointArrayChangedHandler(List<ExtronMatrixEndpoint> endpoints);

/// <summary>
/// First-generation DTP CrossPoint 82 / 84 / 86 / 108 (4K) matrix switchers. Endpoints are
/// discovered from the model number and the split outputs are exposed as A/B pairs.
/// </summary>
public partial class ExtronDtpCpxx : ExtronDtpCpBase
{
    private const char SignalDetected = '1';
    private const string UntiedAddress = "";
    private const string NoSource = "0";
    private const string HdcpCompliantSource = "1";
    private const string NonHdcpSource = "2";
    private const string NoMonitor = "0";
    private const string NonHdcpMonitor = "1";
    private const string UnencryptedHdcpMonitor = "2";
    private const string EncryptedHdcpMonitor = "3";
    public readonly List<ExtronMatrixOutput> ComposedOutputs = [];
    public readonly List<ExtronMatrixInput> Inputs = [];
    public List<ExtronMatrixEndpoint> Outputs => ComposedOutputs
        .SelectMany(output => new[] { output.Primary, output.Secondary })
        .Where(endpoint => endpoint.InUse)
        .ToList();

    public List<SyncStatus> InputsAndOutputs
    {
        get
        {
            var combined = new List<SyncStatus>((Inputs?.Count ?? 0) + (Outputs?.Count ?? 0));
            if (Inputs is { Count: > 0 })
                combined.AddRange(Inputs);
            if (Outputs is { Count: > 0 })
                combined.AddRange(Outputs);
            return combined;
        }
    }

    public ExtronDtpCpxx(CommunicationClient communicationClient, int numberOfOutputs, string name)
        : base(communicationClient, numberOfOutputs, name)
    {
    }

    [GeneratedRegex(@"^Out(\d+) In(\d+) (?:All|Vid)$")]
    private static partial Regex VideoTieRegex();

    [GeneratedRegex(@"^In(\d+) (?:All|Vid)$")]
    private static partial Regex AllOutputsVideoTieRegex();

    protected override void ProcessResponse(string response)
    {
        if (response.StartsWith("E13"))
            return;

        if (response.StartsWith("Ityp"))
        {
            var parts = response.TrimEnd('\r').Split('*');
            if (parts.Length != 2) return;

            var inputNumber = int.Parse(parts[0].Substring(4)) - 1;
            var status = parts[1];

            if (inputNumber >= 0 && inputNumber < Inputs.Count)
            {
                var connectionStatus = status == "0" ? ConnectionState.Disconnected : ConnectionState.Connected;

                Inputs[inputNumber].SetInputStatus(connectionStatus);
                WrapAndSendCommand($"I{Inputs[inputNumber].Number}HDCP");
            }
        }
        else if (response.StartsWith("HdcpI"))
        {
            if (!TryParseHdcpResponse(response, out var endpoint, out var value))
                return;
            int inputNumber = int.Parse(endpoint.TakeWhile(char.IsDigit).ToArray());
            if (inputNumber <= 0 || inputNumber > Inputs.Count)
                return;

            var (connectionStatus, hdcpStatus) = ParseInputHdcp(value);

            LogVerbose("Setting input {inputNumber} as {status}", inputNumber,
                connectionStatus.ToString());
            Inputs[inputNumber - 1].SetInputStatus(connectionStatus);
            Inputs[inputNumber - 1].SetInputHdcpStatus(hdcpStatus);
        }
        else if (response.StartsWith("HdcpO"))
        {
            if (!TryParseHdcpResponse(response, out var endpoint, out var value))
                return;
            int outputNumber = int.Parse(endpoint.TakeWhile(char.IsDigit).ToArray());
            if (outputNumber <= 0 || outputNumber > ComposedOutputs.Count)
                return;

            var (connectionStatus, hdcpStatus) = ParseOutputHdcp(value);
            var output = endpoint.Contains('B')
                ? ComposedOutputs[outputNumber - 1].Secondary
                : ComposedOutputs[outputNumber - 1].Primary;
            var discovered = !output.InUse;

            LogVerbose("Setting output {output} as {status}", endpoint, connectionStatus.ToString());
            output.SetOutputStatus(connectionStatus);
            output.SetOutputHdcpStatus(hdcpStatus);
            if (discovered)
                EndpointsChangedHandlers?.Invoke();
        }
        else if (response.StartsWith("Hplg"))
        {
            var outputNumber = response.Substring(5).TrimEnd();
            WrapAndSendCommand($"O{outputNumber}HDCP");
        }
        else if (response.StartsWith("Nmi"))
        {
            var parts = response.TrimEnd('\r').Split(',');
            if (parts.Length != 2) return;

            var inputNumber = int.Parse(parts[0].Substring(3)) - 1;
            var name = parts[1];

            if (inputNumber >= 0 && inputNumber < Inputs.Count)
            {
                Inputs[inputNumber].SetName(name);
            }
        }
        else if (response.StartsWith("Nmo"))
        {
            var parts = response.TrimEnd('\r').Split(',');
            if (parts.Length != 2) return;

            var outputNumber = int.Parse(parts[0].Substring(3)) - 1;
            var name = parts[1];

            if (outputNumber >= 0 && outputNumber < ComposedOutputs.Count)
            {
                ComposedOutputs[outputNumber].SetName(name);
            }
        }
        else if (response.StartsWith("Frq00"))
            HandleSignalPresence(response[6..]);
        else if (response.StartsWith("Inf00*DTPCP"))
        {
            var digits = response.Remove(0, 11).TrimEnd('\r');
            if(digits.EndsWith(" 4K"))
                digits = digits.Remove(digits.Length - 3);
            int inputCount = 0;
            int outputCount = 0;
            switch (digits.Length)
            {
                case 2:
                    inputCount = int.Parse(digits[..1]);
                    outputCount = int.Parse(digits[^1].ToString());
                    break;
                case 3:
                    inputCount = int.Parse(digits[..2]);
                    outputCount = int.Parse(digits[^1].ToString());
                    break;
                default:
                    throw new ArgumentOutOfRangeException("The model number digits are unsupported");
            }

            if (inputCount == 0 || outputCount == 0)
                throw new ArgumentOutOfRangeException(
                    "Unable to determine the number of inputs or outputs. Please check the model number and try again.");

            SetPortCounts(inputCount, outputCount);
            QueryPorts();
        }
        else if (response == "Qik")
            QueryTies();
        else if (VideoTieRegex().Match(response) is { Success: true } tie)
            SetVideoTie(int.Parse(tie.Groups[1].Value), int.Parse(tie.Groups[2].Value));
        else if (AllOutputsVideoTieRegex().Match(response) is { Success: true } allOutputsTie)
            SetVideoTieOnAllOutputs(int.Parse(allOutputsTie.Groups[1].Value));
    }

    private static (ConnectionState Connection, HdcpStatus Hdcp) ParseInputHdcp(string value) => value switch
    {
        NoSource => (ConnectionState.Disconnected, HdcpStatus.Unknown),
        HdcpCompliantSource => (ConnectionState.Connected, HdcpStatus.Active),
        NonHdcpSource => (ConnectionState.Connected, HdcpStatus.NotSupported),
        _ => (ConnectionState.Unknown, HdcpStatus.Unknown)
    };

    private static (ConnectionState Connection, HdcpStatus Hdcp) ParseOutputHdcp(string value) => value switch
    {
        NoMonitor => (ConnectionState.Disconnected, HdcpStatus.Unknown),
        NonHdcpMonitor => (ConnectionState.Connected, HdcpStatus.NotSupported),
        UnencryptedHdcpMonitor => (ConnectionState.Connected, HdcpStatus.Available),
        EncryptedHdcpMonitor => (ConnectionState.Connected, HdcpStatus.Active),
        _ => (ConnectionState.Unknown, HdcpStatus.Unknown)
    };

    private void SetPortCounts(int inputCount, int outputCount)
    {
        if (Inputs.Count == inputCount && ComposedOutputs.Count == outputCount)
            return;
        while (Inputs.Count > inputCount)
        {
            LogBaseRegistry.Deregister(Inputs[^1]);
            Inputs.RemoveAt(Inputs.Count - 1);
        }
        while (Inputs.Count < inputCount)
        {
            var number = Inputs.Count + 1;
            var input = new ExtronMatrixInput($"Input {number}", number);
            input.SetStreamAddress(number.ToString());
            Inputs.Add(input);
        }
        while (ComposedOutputs.Count > outputCount)
        {
            LogBaseRegistry.Deregister(ComposedOutputs[^1].Primary);
            LogBaseRegistry.Deregister(ComposedOutputs[^1].Secondary);
            ComposedOutputs.RemoveAt(ComposedOutputs.Count - 1);
        }
        while (ComposedOutputs.Count < outputCount)
        {
            var number = ComposedOutputs.Count + 1;
            ComposedOutputs.Add(new ExtronMatrixOutput($"Output {number}", number));
        }
        EndpointsChangedHandlers?.Invoke();
    }

    private void QueryPorts()
    {
        for (var number = 1; number <= Inputs.Count; number++)
        {
            WrapAndSendCommand($"{number}NI");
            WrapAndSendCommand($"I{number}HDCP");
        }
        for (var number = 1; number <= ComposedOutputs.Count; number++)
        {
            WrapAndSendCommand($"{number}NO");
            WrapAndSendCommand($"O{number}HDCP");
            WrapAndSendCommand($"O{number}AHDCP");
            WrapAndSendCommand($"O{number}BHDCP");
        }
        QueryTies();
    }

    private void QueryTies()
    {
        for (var number = 1; number <= ComposedOutputs.Count; number++)
            SendCommand($"{number}%");
    }

    private void HandleSignalPresence(string presence)
    {
        SetPortCounts(presence.Length, ComposedOutputs.Count);
        for (var index = 0; index < presence.Length; index++)
            Inputs[index].SetInputStatus(presence[index] == SignalDetected ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void SetVideoTie(int output, int input)
    {
        if (output < 1 || output > ComposedOutputs.Count)
            return;
        ComposedOutputs[output - 1].SetStreamAddress(AddressOf(input));
    }

    private void SetVideoTieOnAllOutputs(int input) =>
        ComposedOutputs.ForEach(output => output.SetStreamAddress(AddressOf(input)));

    private static string AddressOf(int input) => input == 0 ? UntiedAddress : input.ToString();

    protected override void SendPoll() => WrapAndSendCommand("0TC");

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public void RouteAV(int input, List<int> outputs)
    {
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}!"));
        sb.Append('\r');
        SendCommand(sb.ToString());

        AddEvent(EventType.Input, $"Switched outputs {outputs} to input {input}");
    }

    public override int NumberOfOutputs => ComposedOutputs.Count;

    public override int NumberOfInputs => Inputs.Count;

    public override bool SupportsAudioBreakaway { get; }

    public void RouteVideo(int input, List<int> outputs)
    {
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}%"));
        sb.Append('\r');
        SendCommand(sb.ToString());

        AddEvent(EventType.Input, $"Switched video outputs {outputs} to input {input}");
    }

    public void RouteAudio(int input, List<int> outputs)
    {
        if (input <= 0)
            return;
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}$"));
        sb.Append('\r');
        SendCommand(sb.ToString());

        AddEvent(EventType.Input, $"Switched audio output {outputs} to input {input}");
    }

    public void SetSyncTimeout(int seconds, int output)
    {
        if (seconds < 502)
        {
            SendCommand($"\u001bT{seconds}*{output}SSAV\u0027");
        }
        else
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than 502 seconds");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout can't be longer than 502 seconds");
        }
    }
}
