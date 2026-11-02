using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// Extron DTP2 CrossPoint 82 (including the IPCP SA / MA variants). Fixed 8x2 topology:
/// inputs 1-8, tie-able outputs 1 (HDMI) and 2 (DTP2, scaled) plus the HDMI loop out as
/// output 3, which reports sink and HDCP status and is tied with <see cref="SetLoopOutInput"/>.
/// Each output's <see cref="SyncStatus.StreamAddress"/> holds the input number its video is
/// tied to, updated from the switcher's tie feedback so changes made elsewhere are visible.
/// </summary>
public partial class ExtronDtp2Cp82 : ExtronDtpCpBase
{
    public const int InputCount = 8;
    public const int OutputCount = 2;
    public const int LoopOutput = 3;
    public const string UntiedAddress = "0";
    private const int UntiedInput = 0;
    private const string ModelName = "DTP2 CrossPoint 82";
    private const string ModelTag = "Inf00*";
    private const string ModelLabel = "Model";
    private const string ModelIssueKey = "model";
    private const string InputNameReplyStart = "VnamI";
    private const string OutputNameReplyStart = "VnamO";
    private const string InputHdcpReplyStart = "HdcpI";
    private const string OutputHdcpReplyStart = "HdcpO";
    private const string LoopOutReplyStart = "Lout";
    private static readonly ExtronSisCommand LoopOutQuery = new($"{EscapeHeader}LOUT\r", LoopOutReplyStart);
    private static readonly ExtronSisCommand SignalPresenceQuery = new($"{EscapeHeader}0LS\r", "In00 ");
    private static readonly ExtronSisQuery[] IdentityQueries =
    [
        new("*Q", "Bld", "Firmware", "firmware-read"),
        new("N", "Pno", "Part Number", "part-number-read"),
        new($"{EscapeHeader}CN\r", "Ipn ", "Unit Name", "unit-name-read"),
        new($"{EscapeHeader}CH\r", "Iph ", "MAC", "mac-read")
    ];
    private static readonly ExtronSisQuery TemperatureQuery = new($"{EscapeHeader}20STAT\r", "20Stat ", "Temperature", "temperature-read");

    public readonly List<ExtronMatrixInput> Inputs = Enumerable.Range(1, InputCount)
        .Select(index => new ExtronMatrixInput($"Input {index}", index))
        .ToList();

    public readonly List<ExtronMatrixEndpoint> Outputs =
    [
        new("Output 1", 1, AVEndpointType.Decoder),
        new("Output 2", 2, AVEndpointType.Decoder),
        new("Loop Out", LoopOutput, AVEndpointType.Decoder)
    ];

    public ExtronDtp2Cp82(CommunicationClient communicationClient, string name)
        : this(communicationClient, name, null)
    {
    }

    internal ExtronDtp2Cp82(CommunicationClient communicationClient, string name, TimeProvider? timeProvider)
        : base(communicationClient, OutputCount, name, timeProvider)
    {
    }

    [GeneratedRegex(@"^Vnam([IO])(\d+)\*(.*)$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^In(\d+)\*(\d+) (All|Vid|Aud)$")]
    private static partial Regex TieRegex();

    [GeneratedRegex(@"^Out(\d+) In00 All$")]
    private static partial Regex UntieOutputRegex();

    [GeneratedRegex(@"^Out00 In(\d+) All$")]
    private static partial Regex UntieInputRegex();

    [GeneratedRegex(@"^In00 ([01](?:\*[01])*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SignalPresenceRegex();

    protected override void ProcessResponse(string response)
    {
        var line = response.Trim();
        if (line.Length == 0)
            return;

        if (line.StartsWith(ModelTag))
            HandleModel(line[ModelTag.Length..]);
        else if (IdentityQueries.FirstOrDefault(query => query.IsAnsweredBy(line)) is { } identity)
            ShowRead(identity, line);
        else if (TemperatureQuery.IsAnsweredBy(line))
            ShowRead(TemperatureQuery, line);
        else if (line.StartsWith("Vnam"))
            HandleName(line);
        else if (line.StartsWith("HdcpI"))
            HandleInputHdcp(line);
        else if (line.StartsWith("HdcpO"))
            HandleOutputHdcp(line);
        else if (line.StartsWith("HplgO"))
            HandleHotplug(line);
        else if (line.StartsWith("Lout"))
            HandleLoopOut(line);
        else if (line == "Reconfig")
            Send(SignalPresenceQuery);
        else if (line == "In00 All")
            Outputs.Take(OutputCount).ToList().ForEach(output => output.SetStreamAddress(UntiedAddress));
        else if (SignalPresenceRegex().Match(line) is { Success: true } presence)
            HandleSignalPresence(presence.Groups[1].Value);
        else if (TieRegex().Match(line) is { Success: true } tie)
            HandleTie(int.Parse(tie.Groups[1].Value), int.Parse(tie.Groups[2].Value), tie.Groups[3].Value);
        else if (UntieOutputRegex().Match(line) is { Success: true } untiedOutput)
            HandleTie(UntiedInput, int.Parse(untiedOutput.Groups[1].Value), "All");
        else if (UntieInputRegex().Match(line) is { Success: true } untiedInput)
            HandleInputUntied(int.Parse(untiedInput.Groups[1].Value));
        else if (IsError(line))
            HandleError(line);
    }

    private void HandleModel(string model)
    {
        SetDetail(ModelLabel, model);
        if (model.Contains(ModelName))
        {
            ResolveIssue(ModelIssueKey);
            QueryStatus();
            Read(IdentityQueries);
            return;
        }

        RaiseOngoingIssue(ModelIssueKey, $"Expected a {ModelName} but the switcher reported {model}");
    }

    private void QueryStatus()
    {
        foreach (var input in Inputs)
        {
            Send(new ExtronSisCommand($"{EscapeHeader}I{input.Number}VNAM\r", InputNameReplyStart));
            Send(new ExtronSisCommand($"{EscapeHeader}I{input.Number}HDCP\r", InputHdcpReplyStart));
        }

        foreach (var output in Outputs)
        {
            Send(new ExtronSisCommand($"{EscapeHeader}O{output.Number}VNAM\r", OutputNameReplyStart));
            Send(new ExtronSisCommand($"{EscapeHeader}O{output.Number}HDCP\r", OutputHdcpReplyStart));
        }

        // The combined tie query (X@!) is rejected while an output has audio broken away,
        // so read the video and audio ties separately.
        for (var output = 1; output <= OutputCount; output++)
        {
            Send(new ExtronSisCommand($"{output}%", new ExtronSisTie(null, output, ExtronSisTie.Video)));
            Send(new ExtronSisCommand($"{output}$", new ExtronSisTie(null, output, ExtronSisTie.Audio)));
        }

        Send(LoopOutQuery);
        Send(SignalPresenceQuery);
    }

    private void HandleName(string line)
    {
        var match = NameRegex().Match(line);
        if (!match.Success)
        {
            LogWarning("Ignoring the name {Line} as it isn't an input or output number and a name", line);
            return;
        }
        var number = int.Parse(match.Groups[2].Value);
        var name = match.Groups[3].Value;
        if (match.Groups[1].Value == "I" && TryGetInput(number, out var input))
            input.SetName(name);
        else if (match.Groups[1].Value == "O" && TryGetOutput(number, out var output))
            output.SetName(name);
    }

    private void HandleInputHdcp(string line)
    {
        if (!TryParseHdcpResponse(line, out var endpoint, out var value))
            return;
        if (!int.TryParse(endpoint, out var number) || !TryGetInput(number, out var input))
            return;
        var (connection, hdcp) = DecodeInputHdcp(value);
        LogVerbose("Setting input {InputNumber} as {Status}", number, connection.ToString());
        input.SetInputStatus(connection);
        input.SetInputHdcpStatus(hdcp);
    }

    private void HandleOutputHdcp(string line)
    {
        if (!TryParseHdcpResponse(line, out var endpoint, out var value))
            return;
        if (!int.TryParse(endpoint, out var number) || !TryGetOutput(number, out var output))
            return;
        var (connection, hdcp) = DecodeOutputHdcp(value);
        LogVerbose("Setting output {OutputNumber} as {Status}", number, connection.ToString());
        output.SetOutputStatus(connection);
        output.SetOutputHdcpStatus(hdcp);
    }

    private void HandleHotplug(string line)
    {
        var outputToken = line[5..].Split('*')[0];
        if (int.TryParse(outputToken, out var number) && TryGetOutput(number, out _))
            Send(new ExtronSisCommand($"{EscapeHeader}O{number}HDCP\r", OutputHdcpReplyStart));
    }

    private void HandleLoopOut(string line)
    {
        if (int.TryParse(line[4..], out var input) && TryGetInput(input, out _))
            Outputs[LoopOutput - 1].SetStreamAddress(input.ToString());
    }

    private void HandleSignalPresence(string statuses)
    {
        var values = statuses.Split('*');
        if (values.Length != Inputs.Count)
        {
            LogWarning("Ignoring the signal presence {Statuses} as it doesn't list the {InputCount} inputs", statuses, Inputs.Count);
            return;
        }
        for (var index = 0; index < values.Length; index++)
            Inputs[index].SetInputStatus(values[index] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected);
    }

    private void HandleTie(int input, int output, string plane)
    {
        if (output < 1 || output > OutputCount)
            return;
        if (plane == "Aud")
        {
            LogVerbose("Output {OutputNumber} audio is tied to input {InputNumber}", output, input);
            return;
        }
        Outputs[output - 1].SetStreamAddress(input.ToString());
    }

    private void HandleInputUntied(int input)
    {
        foreach (var output in Outputs.Take(OutputCount).Where(output => output.StreamAddress == input.ToString()))
            output.SetStreamAddress(UntiedAddress);
    }

    private bool TryGetInput(int number, out ExtronMatrixInput input)
    {
        input = number >= 1 && number <= Inputs.Count ? Inputs[number - 1] : null!;
        return input != null;
    }

    private bool TryGetOutput(int number, out ExtronMatrixEndpoint output)
    {
        output = number >= 1 && number <= Outputs.Count ? Outputs[number - 1] : null!;
        return output != null;
    }

    protected override void SendPoll()
    {
        ReportMissedReplies();
        Send(SignalPresenceQuery);
        ReadUnlessNotSupported(TemperatureQuery);
        ReadAgainIfUnanswered(IdentityQueries);
    }

    public override List<SyncStatus> GetInputs() => [..Inputs];

    public override List<SyncStatus> GetOutputs() => [..Outputs];

    public override int NumberOfOutputs => OutputCount;

    public override int NumberOfInputs => InputCount;

    public override bool SupportsAudioBreakaway => true;

    public override void RouteAV(int input, int output)
    {
        if (IsValidTie(input, output, "RouteAV"))
            base.RouteAV(input, output);
    }

    public override void RouteVideo(int input, int output)
    {
        if (IsValidTie(input, output, "RouteVideo"))
            base.RouteVideo(input, output);
    }

    public override void RouteAudio(int input, int output)
    {
        if (IsValidTie(input, output, "RouteAudio"))
            base.RouteAudio(input, output);
    }

    // The switcher rejects the Esc +Q batch-tie form (E10), so lists are tied one output at a time.
    public void RouteAV(int input, List<int> outputs) => outputs.ForEach(output => RouteAV(input, output));

    public void RouteVideo(int input, List<int> outputs) => outputs.ForEach(output => RouteVideo(input, output));

    public void RouteAudio(int input, List<int> outputs) => outputs.ForEach(output => RouteAudio(input, output));

    private bool IsValidTie(int input, int output, string method)
    {
        if (input >= 1 && input <= InputCount && output >= 0 && output <= OutputCount)
            return true;

        AddEvent(EventType.Error,
            $"Not switching output {output} to input {input} as it is out of range. Inputs must be between 1 and {InputCount} and outputs between 0 and {OutputCount}; the loop out is set with SetLoopOutInput");
        using (PushProperties(method))
            LogWarning(
                "Not switching output {Output} to input {Input} as it is out of range. Inputs must be between 1 and {InputCount} and outputs between 0 and {OutputCount}; the loop out is set with SetLoopOutInput",
                output, input, InputCount, OutputCount);
        return false;
    }

    /// <summary>Ties the HDMI loop out (output 3) to an input. It is always tied and cannot be untied.</summary>
    public void SetLoopOutInput(int input)
    {
        if (input < 1 || input > InputCount)
        {
            AddEvent(EventType.Error, $"Not switching the loop out to input {input} as it is out of range, must be between 1 and {InputCount}");
            using (PushProperties("SetLoopOutInput"))
                LogWarning("Not switching the loop out to input {Input} as it is out of range, must be between 1 and {InputCount}", input, InputCount);
            return;
        }

        Send(new ExtronSisCommand($"{EscapeHeader}{input}LOUT\r", LoopOutReplyStart));
        AddEvent(EventType.Input, $"Switched the loop out to input {input}");
    }

    /// <summary>
    /// Sets how long the scaled output 2 keeps sync after the selected input loses video.
    /// 0 drops sync immediately, 1-500 is a delay in seconds and 501 never drops sync.
    /// </summary>
    public void SetSyncTimeout(int seconds)
    {
        if (seconds < 0 || seconds > 501)
        {
            AddEvent(EventType.Error, "The sync timeout must be between 0 and 501 seconds (501 = never)");
            using (PushProperties("SetSyncTimeout"))
                LogWarning("The sync timeout must be between 0 and 501 seconds (501 = never)");
            return;
        }

        Send(new ExtronSisCommand($"{EscapeHeader}T1*{seconds}SSAV\r", "Ssav"));
        AddEvent(EventType.VideoMute, $"Set the output 2 sync timeout to {seconds} seconds");
    }
}
