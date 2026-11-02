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
    private const string ModelIssueKey = "model";
    private const string ModelTag = "Inf00*";
    private const string ModelLabel = "Model";
    private const string BannerTag = "(c) Copyright";
    private const string FirmwareLabel = "Firmware";
    private const string PartNumberLabel = "Part Number";
    private const string InputHdcpReplyStart = "HdcpI";
    private const string OutputHdcpReplyStart = "HdcpO";
    private const string QuickTie = "Qik";
    private const int MaximumSyncTimeout = 501;
    private static readonly ExtronSisCommand PortTimeoutQuery = new($"{EscapeHeader}0TC\r", "Pti0*");
    private static readonly ExtronSisQuery[] IdentityQueries =
    [
        new("*Q", "Bld", "Firmware Build", "firmware-build-read"),
        new("N", "Pno", PartNumberLabel, "part-number-read"),
        new($"{EscapeHeader}CN\r", "Ipn ", "Unit Name", "unit-name-read"),
        new($"{EscapeHeader}CH\r", "Iph ", "MAC", "mac-read")
    ];
    private static readonly ExtronSisQuery SystemStatusQuery = new("S", "Sts00*", "System Status", "system-status-read");
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
        : this(communicationClient, numberOfOutputs, name, null)
    {
    }

    internal ExtronDtpCpxx(CommunicationClient communicationClient, int numberOfOutputs, string name, TimeProvider? timeProvider)
        : base(communicationClient, numberOfOutputs, name, timeProvider)
    {
    }

    [GeneratedRegex(@"^Out(\d+) In(\d+) (?:All|Vid)$")]
    private static partial Regex VideoTieRegex();

    [GeneratedRegex(@"^In(\d+) (?:All|Vid)$")]
    private static partial Regex AllOutputsVideoTieRegex();

    [GeneratedRegex(@"^\(c\) Copyright \d{4}, Extron Electronics,? [^,]+, V([^,]+), ([^,]+)$")]
    private static partial Regex BannerRegex();

    protected override void ProcessResponse(string response)
    {
        var line = response.Trim();
        if (IsError(line))
            HandleError(line);
        else if (line.StartsWith(BannerTag, StringComparison.Ordinal))
            HandleBanner(line);
        else if (IdentityQueries.FirstOrDefault(query => query.IsAnsweredBy(line)) is { } identity)
            ShowRead(identity, line);
        else if (SystemStatusQuery.IsAnsweredBy(line))
            ShowRead(SystemStatusQuery, line);
        else if (response.StartsWith("Ityp"))
        {
            var parts = response.TrimEnd('\r').Split('*');
            if (parts.Length != 2)
            {
                LogWarning("Ignoring the input type {Response} as it isn't an input and a type", response);
                return;
            }

            var inputNumber = int.Parse(parts[0].Substring(4)) - 1;
            var status = parts[1];

            if (inputNumber >= 0 && inputNumber < Inputs.Count)
            {
                var connectionStatus = status == "0" ? ConnectionState.Disconnected : ConnectionState.Connected;

                Inputs[inputNumber].SetInputStatus(connectionStatus);
                Send(new ExtronSisCommand($"{EscapeHeader}I{Inputs[inputNumber].Number}HDCP\r", InputHdcpReplyStart));
            }
        }
        else if (response.StartsWith("HdcpI"))
        {
            if (!TryParseHdcpResponse(response, out var endpoint, out var value))
            {
                LogWarning("Ignoring the input HDCP status {Response} as it isn't an input and a status", response);
                return;
            }
            int inputNumber = int.Parse(endpoint.TakeWhile(char.IsDigit).ToArray());
            if (inputNumber <= 0 || inputNumber > Inputs.Count)
            {
                LogWarning("Ignoring the input HDCP status {Response} as it doesn't name one of the {InputCount} inputs", response, Inputs.Count);
                return;
            }

            var (connectionStatus, hdcpStatus) = ParseInputHdcp(value);

            LogVerbose("Setting input {inputNumber} as {status}", inputNumber,
                connectionStatus.ToString());
            Inputs[inputNumber - 1].SetInputStatus(connectionStatus);
            Inputs[inputNumber - 1].SetInputHdcpStatus(hdcpStatus);
        }
        else if (response.StartsWith("HdcpO"))
        {
            if (!TryParseHdcpResponse(response, out var endpoint, out var value))
            {
                LogWarning("Ignoring the output HDCP status {Response} as it isn't an output and a status", response);
                return;
            }
            int outputNumber = int.Parse(endpoint.TakeWhile(char.IsDigit).ToArray());
            if (outputNumber <= 0 || outputNumber > ComposedOutputs.Count)
            {
                LogWarning("Ignoring the output HDCP status {Response} as it doesn't name one of the {OutputCount} outputs", response, ComposedOutputs.Count);
                return;
            }

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
            HandleHotplug(response);
        else if (response.StartsWith("Nmi"))
        {
            var parts = response.TrimEnd('\r').Split(',', 2);
            if (parts.Length != 2)
            {
                LogWarning("Ignoring the input name {Response} as it isn't an input and a name", response);
                return;
            }

            var inputNumber = int.Parse(parts[0].Substring(3)) - 1;
            var name = parts[1];

            if (inputNumber >= 0 && inputNumber < Inputs.Count)
            {
                Inputs[inputNumber].SetName(name);
            }
        }
        else if (response.StartsWith("Nmo"))
        {
            var parts = response.TrimEnd('\r').Split(',', 2);
            if (parts.Length != 2)
            {
                LogWarning("Ignoring the output name {Response} as it isn't an output and a name", response);
                return;
            }

            var outputNumber = int.Parse(parts[0].Substring(3)) - 1;
            var name = parts[1];

            if (outputNumber >= 0 && outputNumber < ComposedOutputs.Count)
            {
                ComposedOutputs[outputNumber].SetName(name);
            }
        }
        else if (response.StartsWith("Frq00"))
            HandleSignalPresence(response[6..]);
        else if (response.StartsWith($"{ModelTag}DTPCP"))
            HandleModel(response);
        else if (response == QuickTie)
            QueryTies();
        else if (VideoTieRegex().Match(response) is { Success: true } tie)
            SetVideoTie(int.Parse(tie.Groups[1].Value), int.Parse(tie.Groups[2].Value));
        else if (AllOutputsVideoTieRegex().Match(response) is { Success: true } allOutputsTie)
            SetVideoTieOnAllOutputs(int.Parse(allOutputsTie.Groups[1].Value));
    }

    private void HandleHotplug(string response)
    {
        var output = response[4..].TrimEnd();
        var digits = string.Concat(output.TakeWhile(char.IsDigit));
        if (!int.TryParse(digits, out var number))
        {
            LogWarning("Ignoring the hotplug event {Response} as it doesn't name an output", response);
            return;
        }
        Send(new ExtronSisCommand($"{EscapeHeader}O{number}{output[digits.Length..]}HDCP\r", OutputHdcpReplyStart));
    }

    private void HandleBanner(string banner)
    {
        if (BannerRegex().Match(banner) is not { Success: true } match)
        {
            LogWarning("Unable to read the firmware and part number from the banner {Banner}", banner);
            return;
        }
        SetDetail(FirmwareLabel, match.Groups[1].Value);
        SetDetail(PartNumberLabel, match.Groups[2].Value);
    }

    private void HandleModel(string response)
    {
        SetDetail(ModelLabel, response[ModelTag.Length..]);
        var digits = response.Remove(0, 11).TrimEnd('\r');
        if (digits.EndsWith(" 4K"))
            digits = digits.Remove(digits.Length - 3);
        if (digits.Length is not (2 or 3) || !int.TryParse(digits[..^1], out var inputCount) ||
            !int.TryParse(digits[^1..], out var outputCount) || inputCount == 0 || outputCount == 0)
        {
            RaiseOngoingIssue(ModelIssueKey, $"Unable to read the input and output counts from the model {response}");
            return;
        }

        ResolveIssue(ModelIssueKey);
        SetPortCounts(inputCount, outputCount);
        QueryPorts();
        Read(IdentityQueries);
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
            Send(new ExtronSisCommand($"{EscapeHeader}{number}NI\r", "Nmi"));
            Send(new ExtronSisCommand($"{EscapeHeader}I{number}HDCP\r", InputHdcpReplyStart));
        }
        for (var number = 1; number <= ComposedOutputs.Count; number++)
        {
            Send(new ExtronSisCommand($"{EscapeHeader}{number}NO\r", "Nmo"));
            Send(new ExtronSisCommand($"{EscapeHeader}O{number}HDCP\r", OutputHdcpReplyStart));
            Send(new ExtronSisCommand($"{EscapeHeader}O{number}AHDCP\r", OutputHdcpReplyStart));
            Send(new ExtronSisCommand($"{EscapeHeader}O{number}BHDCP\r", OutputHdcpReplyStart));
        }
        QueryTies();
    }

    private void QueryTies()
    {
        for (var number = 1; number <= ComposedOutputs.Count; number++)
            Send(new ExtronSisCommand($"{number}%", new ExtronSisTie(null, number, ExtronSisTie.Video)));
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

    protected override void SendPoll()
    {
        ReportMissedReplies();
        Send(PortTimeoutQuery);
        ReadUnlessNotSupported(SystemStatusQuery);
        ReadAgainIfUnanswered(IdentityQueries);
    }

    private protected override void ReportRejected(ExtronSisCommand command, string error)
    {
        if (command.ReplyStart == OutputHdcpReplyStart && error == ExtronSis.InvalidParameter)
            LogDebug("The switcher has no output for {Command}: {Error}", command.Name, error);
        else
            base.ReportRejected(command, error);
    }

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
        Send(new ExtronSisCommand(sb.ToString(), QuickTie));

        AddEvent(EventType.Input, $"Switched outputs [{string.Join(", ", outputs)}] to input {input}");
    }

    public override int NumberOfOutputs => ComposedOutputs.Count;

    public override int NumberOfInputs => Inputs.Count;

    public override bool SupportsAudioBreakaway => true;

    public void RouteVideo(int input, List<int> outputs)
    {
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}%"));
        sb.Append('\r');
        Send(new ExtronSisCommand(sb.ToString(), QuickTie));

        AddEvent(EventType.Input, $"Switched video outputs [{string.Join(", ", outputs)}] to input {input}");
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
        Send(new ExtronSisCommand(sb.ToString(), QuickTie));

        AddEvent(EventType.Input, $"Switched audio output [{string.Join(", ", outputs)}] to input {input}");
    }

    public void SetSyncTimeout(int seconds, int output)
    {
        if (seconds <= MaximumSyncTimeout)
        {
            Send(new ExtronSisCommand($"{EscapeHeader}T{output}*{seconds}SSAV\r", "Ssav"));
        }
        else
        {
            AddEvent(EventType.Error, $"The sync timeout can't be longer than {MaximumSyncTimeout} seconds");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout can't be longer than {MaximumSyncTimeout} seconds", MaximumSyncTimeout);
        }
    }
}
