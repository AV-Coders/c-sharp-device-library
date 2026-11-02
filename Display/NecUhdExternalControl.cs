using System.Text;
using AVCoders.Core;

namespace AVCoders.Display;

public class NecUhdExternalControl : Display
{
    public static readonly ushort DefaultPort = 7142;
    private readonly byte _displayId;
    private List<byte> _gather = [];
    private readonly object _readsLock = new();
    private readonly HashSet<NecRead> _unreadIdentity = [..IdentityReads];
    private readonly Dictionary<NecRead, int> _missedReplies = new();
    private readonly Dictionary<NecRead, int> _nullReplies = new();
    private readonly SentCommandTracker<NecCommand> _sent;
    private readonly ITimer _connectedPollStart;
    private int _polls;

    private const byte StartOfHeader = 0x01;
    private const byte ReservedByte = 0x30;
    private const byte Controller = 0x30;
    private const byte MessageTypeCommand = 0x41;
    private const byte MessageTypeCommandReply = 0x42;
    private const byte MessageTypeGetParameter = 0x43;
    private const byte MessageTypeGetParameterReply = 0x44;
    private const byte MessageTypeSetParameter = 0x45;
    private const byte MessageTypeSetParameterReply = 0x46;
    private const byte FirstMonitorId = 0x41;
    private const byte LastMonitorId = 0xA4;
    private const int SourceIndex = 3;
    private const int MessageTypeIndex = 4;
    private const int MessageIndex = 8;
    private const int OpCodeIndex = 10;
    private const int OpCodeLength = 4;
    private const int ResultCodeLength = 2;
    private const int ReplyTrailerLength = 3;
    private const int ShortestReplyLength = 13;
    private const int ReplyLength = 27;
    private const byte PowerStatusOn = 0x31;
    private const byte PowerStatusStandby = 0x32;
    private const byte PowerStatusOff = 0x34;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Delimiter = 0x0d;
    private const int PollsPerSelfDiagnosis = 4;
    private const int MaxMissedReplies = 3;
    private static readonly TimeSpan ReplyWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectedPollDelay = TimeSpan.FromMilliseconds(1500);

    private const string PowerStatusCode = "D6";
    private const string PowerControlCode = "C203D6";
    private const string InputOpCode = "0060";
    private const string VolumeOpCode = "0062";
    private const string AudioMuteOpCode = "008D";
    private const string NullMessage = "BE";
    private const string SelfDiagnosisReplyCode = "A1";
    private const string ModelNameReplyCode = "C317";
    private const string SerialReplyCode = "C316";
    private const string FirmwareReplyCode = "CB02";
    private const string MacAddressReplyCode = "C320";
    private const string NoErrorResult = "00";
    private const string FirmwareRevisionType = "00";
    private const string MacAddressDevice = "00";
    private const string NormalDiagnosis = "00";
    private const string NoSignalDiagnosis = "B0";
    private const int DiagnosisCodeLength = 2;
    private const string SelfDiagnosisIssuePrefix = "self-diagnosis:";
    private const string ModelLabel = "Model";
    private const string SerialLabel = "Serial Number";
    private const string FirmwareLabel = "Firmware";
    private const string MacAddressLabel = "MAC Address";
    private const string SelfDiagnosisLabel = "Self-Diagnosis";
    private const string SignalLabel = "Signal";
    private const string NotReadYet = "Not read yet: read while the display is off";

    private static readonly NecRead ModelNameRead = new("model name", "C217", ModelNameReplyCode, "model-read", [ModelLabel]);
    private static readonly NecRead SerialRead = new("serial number", "C216", SerialReplyCode, "serial-read", [SerialLabel]);
    private static readonly NecRead FirmwareRead = new("firmware version", $"CA02{FirmwareRevisionType}", FirmwareReplyCode, "firmware-read", [FirmwareLabel]);
    private static readonly NecRead MacAddressRead = new("MAC address", $"C220{MacAddressDevice}", MacAddressReplyCode, "mac-address-read", [MacAddressLabel]);
    private static readonly NecRead SelfDiagnosisRead = new("self-diagnosis", "B1", SelfDiagnosisReplyCode, "self-diagnosis-read", [SelfDiagnosisLabel, SignalLabel]);
    private static readonly NecRead[] IdentityReads = [ModelNameRead, SerialRead, FirmwareRead, MacAddressRead];
    private static readonly NecRead[] OffReads = [ModelNameRead, SerialRead, FirmwareRead, MacAddressRead, SelfDiagnosisRead];

    private static readonly Dictionary<string, (string Fault, IssueSeverity Severity)> SelfDiagnosisFaults = new()
    {
        { "70", ("main power +3.3 V abnormality", IssueSeverity.Major) },
        { "71", ("main power +5 V abnormality", IssueSeverity.Major) },
        { "72", ("panel or fan power +12 V abnormality", IssueSeverity.Major) },
        { "78", ("audio or converter power +24 V abnormality", IssueSeverity.Major) },
        { "80", ("cooling fan 1 abnormality", IssueSeverity.Major) },
        { "81", ("cooling fan 2 abnormality", IssueSeverity.Major) },
        { "82", ("cooling fan 3 abnormality", IssueSeverity.Major) },
        { "A0", ("temperature abnormality shutdown", IssueSeverity.Major) },
        { "A1", ("temperature abnormality half brightness", IssueSeverity.Major) },
        { "A2", ("temperature reached the user threshold", IssueSeverity.Minor) },
        { "D0", ("error log buffer reduction", IssueSeverity.Minor) },
        { "E0", ("EEPROM error", IssueSeverity.Major) },
        { "E5", ("HDMI switch error", IssueSeverity.Major) },
        { "E7", ("DisplayPort block abnormality", IssueSeverity.Major) },
    };

    private static readonly Dictionary<Input, byte[]> InputDictionary = new()
    {
        { Input.Hdmi1, [0x31, 0x31] },
        { Input.Hdmi2, [0x31, 0x32] },
        { Input.Hdmi3, [0x38, 0x32] },
        { Input.Hdmi4, [0x38, 0x33] },
        { Input.DisplayPort, [0x30, 0x46] },
    };

    public NecUhdExternalControl(CommunicationClient comms, string name, Input? defaultInput, byte displayId = 0x2A)
        : this(comms, name, defaultInput, displayId, null)
    {
    }

    internal NecUhdExternalControl(CommunicationClient comms, string name, Input? defaultInput, byte displayId, TimeProvider? timeProvider)
        : base(InputDictionary.Keys.ToList(), name, defaultInput, comms, CommandStringFormat.Hex, DefaultPollSeconds, timeProvider)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _displayId = displayId;
        _sent = new SentCommandTracker<NecCommand>(ReplyWait, TimeProvider);
        _connectedPollStart = TimeProvider.CreateTimer(_ => PollWorker.Restart(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var label in OffReads.SelectMany(read => read.Labels))
            SetDetail(label, NotReadYet);
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            HandleConnectionState(ConnectionState.Connected);
    }

    protected override void RestartPolling() { }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        _sent.Clear();
        if (connectionState != ConnectionState.Connected)
            return;

        lock (_readsLock)
        {
            _unreadIdentity.UnionWith(IdentityReads);
            _missedReplies.Clear();
            _nullReplies.Clear();
        }
        _polls = 0;
        _connectedPollStart.Change(ConnectedPollDelay, Timeout.InfiniteTimeSpan);
    }

    protected override async Task DoPoll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
        {
            using (PushProperties("DoPoll"))
                LogDebug("Not polling");
            return;
        }

        CountMissedReplies();
        //Power
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeCommand), [Stx, 0x30, 0x31, 0x44, 0x36, Etx], MessageTypeCommandReply, PowerStatusCode);
        await Task.Delay(ReplyWait, TimeProvider, token);
        if (PowerState == PowerState.Off)
        {
            await ReadWhileOff(token);
            return;
        }
        if (PowerState != PowerState.On)
            return;

        //Input
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeGetParameter), [Stx, 0x30, 0x30, 0x36, 0x30, Etx], MessageTypeGetParameterReply, InputOpCode);
        await Task.Delay(ReplyWait, TimeProvider, token);
        if (PowerState == PowerState.On)
        {
            // Volume
            PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeGetParameter), [Stx, 0x30, 0x30, 0x36, 0x32, Etx], MessageTypeGetParameterReply, VolumeOpCode);
        }
    }

    private async Task ReadWhileOff(CancellationToken token)
    {
        await ReadIdentity(token);
        if (_polls++ % PollsPerSelfDiagnosis == 0)
            await ReadSelfDiagnosis(token);
    }

    private async Task ReadIdentity(CancellationToken token)
    {
        List<NecRead> unread;
        lock (_readsLock)
            unread = IdentityReads.Where(_unreadIdentity.Contains).ToList();
        foreach (var read in unread)
            await Read(read, token);
    }

    private async Task ReadSelfDiagnosis(CancellationToken token)
    {
        lock (_readsLock)
        {
            if (_nullReplies.GetValueOrDefault(SelfDiagnosisRead) >= MaxMissedReplies)
                return;
        }
        await Read(SelfDiagnosisRead, token);
    }

    private async Task Read(NecRead read, CancellationToken token)
    {
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeCommand), [Stx, ..Encoding.ASCII.GetBytes(read.Command), Etx],
            MessageTypeCommandReply, read.ReplyCode, read);
        await Task.Delay(ReplyWait, TimeProvider, token);
        CountMissedReplies();
    }

    private void CountMissedReplies()
    {
        using (PushProperties("CountMissedReplies"))
        {
            foreach (var missed in _sent.TakeMissed())
            {
                using (Trigger.Resume(missed.TriggerId))
                {
                    if (missed.Command.Read is { } read)
                        CountMiss(read, $"no reply within {ReplyWait.TotalMilliseconds} ms");
                    else
                        LogDebug("No reply to {Command} within {ReplyWait} ms", missed.Command.Message, ReplyWait.TotalMilliseconds);
                }
            }
        }
    }

    private void CountMiss(NecRead read, string result)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(read) + 1;
            _missedReplies[read] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The {Read} read {Command} got {Result}, asking again on a later poll ({Missed} of {MaxMissedReplies})",
                read.Name, read.Command, result, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(read.IssueKey,
            $"The display did not give a real reply to the {read.Name} read {read.Command} {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private void CountUnreadableReply(NecRead read, byte[] response)
    {
        CommunicationState = CommunicationState.Okay;
        CountMiss(read, Convert.ToHexString(response));
    }

    private void ShowEmptyValue(NecRead read, byte[] response)
    {
        ReadAnswered(read);
        SetDetail(read.Labels[0], Convert.ToHexString(response));
        ResolveIssue(read.IssueKey);
    }

    private void CountNullMessage(NecRead read, string nullMessage)
    {
        int nulls;
        lock (_readsLock)
        {
            nulls = _nullReplies.GetValueOrDefault(read) + 1;
            _nullReplies[read] = nulls;
            if (nulls >= MaxMissedReplies)
                _unreadIdentity.Remove(read);
        }
        if (nulls >= MaxMissedReplies)
        {
            ShowNotSupported(read, nullMessage);
            return;
        }
        LogInformation("The {Read} read {Command} got a NULL message {Response}, asking again on a later poll ({Nulls} of {MaxMissedReplies})",
            read.Name, read.Command, nullMessage, nulls, MaxMissedReplies);
    }

    private void ShowNotSupported(NecRead read, string response)
    {
        LogInformation("The display does not support the {Read} read {Command}: {Response}", read.Name, read.Command, response);
        foreach (var label in read.Labels)
            SetDetail(label, $"Not supported: {response}", DetailTone.Normal);
        ResolveIssue(read.IssueKey);
    }

    private void ReadAnswered(NecRead read)
    {
        CommunicationState = CommunicationState.Okay;
        lock (_readsLock)
        {
            _unreadIdentity.Remove(read);
            _missedReplies.Remove(read);
            _nullReplies.Remove(read);
        }
    }

    private int ConvertAsciiHexToNumber(byte[] asciiBytes)
    {
        if (asciiBytes.Length != 2)
            throw new ArgumentException("Array must contain exactly two bytes.");

        string hexString = System.Text.Encoding.ASCII.GetString(asciiBytes);
        int hexNumber = int.Parse(hexString, System.Globalization.NumberStyles.HexNumber);

        return hexNumber;
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties())
        {
            _gather.AddRange(response);

            while (_gather.Contains(0x0D))
            {
                int endIndex = _gather.IndexOf(0x0D) + 1;
                byte[] aResponsePayload = _gather.Take(endIndex).ToArray();
                _gather = _gather.Skip(endIndex).ToList();

                ProcessResponse(aResponsePayload);
            }

            if (_gather.Count > 1024)
            {
                LogWarning("Gather buffer exceeded 1024 bytes, clearing");
                _gather.Clear();
            }
        }
    }

    private void ProcessResponse(byte[] response)
    {
        if (response.Length < ShortestReplyLength || !IsFromThisMonitor(response))
        {
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
            return;
        }

        switch (response[MessageTypeIndex])
        {
            case MessageTypeCommandReply:
                ProcessCommandReply(response);
                break;
            case MessageTypeGetParameterReply:
            case MessageTypeSetParameterReply:
                ProcessParameterReply(response);
                break;
            default:
                LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
                break;
        }
    }

    private void ProcessCommandReply(byte[] response)
    {
        string message = Encoding.Latin1.GetString(response[MessageIndex..^ReplyTrailerLength]);
        if (message == NullMessage)
            HandleNullMessage(response);
        else if (OffReads.FirstOrDefault(candidate => message.StartsWith(candidate.ReplyCode, StringComparison.Ordinal)) is { } read)
            ProcessReadReply(read, message[read.ReplyCode.Length..], response);
        else if (message.IndexOf(PowerControlCode, StringComparison.Ordinal) == ResultCodeLength)
            ProcessPowerControlReply(response);
        else
            ProcessPowerReply(response);
    }

    private void ProcessReadReply(NecRead read, string data, byte[] response)
    {
        var sent = _sent.TakeReply(command => command.Read == read);
        if (sent == null)
        {
            LogDebug("Ignored a {Read} reply that no read of ours is waiting for: {Response}", read.Name, Convert.ToHexString(response));
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            switch (read.ReplyCode)
            {
                case SelfDiagnosisReplyCode:
                    ShowSelfDiagnosis(data, response);
                    break;
                case ModelNameReplyCode:
                    ShowModelName(data, response);
                    break;
                case SerialReplyCode:
                    ShowSerial(data, response);
                    break;
                case FirmwareReplyCode:
                    ShowFirmware(data, response);
                    break;
                case MacAddressReplyCode:
                    ShowMacAddress(data, response);
                    break;
            }
        }
    }

    private void ProcessPowerControlReply(byte[] response)
    {
        var sent = TakeReply(MessageTypeCommandReply, PowerControlCode);
        using (Trigger.Resume(sent?.TriggerId))
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
    }

    private void ProcessPowerReply(byte[] response)
    {
        if (response.Length < ReplyLength || response[12] != 0x44 || response[13] != 0x36)
        {
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
            return;
        }

        var sent = TakeReply(MessageTypeCommandReply, PowerStatusCode);
        using (Trigger.Resume(sent?.TriggerId))
            ShowPowerStatus(response);
    }

    private void ShowPowerStatus(byte[] response)
    {
        if (response[10] != 0x30 || response[11] != 0x30)
        {
            ReportErrorResult(response);
            return;
        }

        CommunicationState = CommunicationState.Okay;
        switch (response[23])
        {
            case PowerStatusOff:
            case PowerStatusStandby:
                PowerState = PowerState.Off;
                break;
            case PowerStatusOn:
                PowerState = PowerState.On;
                break;
            default:
                LogWarning("Unknown power status in {Response}", Convert.ToHexString(response));
                break;
        }

        ProcessPowerResponse();
    }

    private void ProcessParameterReply(byte[] response)
    {
        if (response.Length < ReplyLength)
        {
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
            return;
        }

        var sent = TakeReply(response[MessageTypeIndex], Encoding.Latin1.GetString(response, OpCodeIndex, OpCodeLength));
        using (Trigger.Resume(sent?.TriggerId))
            ShowParameter(response);
    }

    private void ShowParameter(byte[] response)
    {
        if (response[8] != 0x30 || response[9] != 0x30)
        {
            ReportErrorResult(response);
            return;
        }

        if (response[10] != 0x30 || response[11] != 0x30 || response[12] != 0x36)
        {
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
            return;
        }

        CommunicationState = CommunicationState.Okay;
        switch (response[13])
        {
            case 0x30: // Input response
                Input = ConvertAsciiHexToNumber([response[22], response[23]]) switch
                {
                    0x11 => Input.Hdmi1,
                    0x12 => Input.Hdmi2,
                    0x82 => Input.Hdmi3,
                    0x83 => Input.Hdmi4,
                    0x0F => Input.DisplayPort,
                    _ => Input.Unknown
                };
                if (Input == Input.Unknown)
                    LogWarning("Unknown input code in {Response}", Convert.ToHexString(response));
                ProcessInputResponse();
                break;
            case 0x32: // Volume Response
                Volume = ConvertAsciiHexToNumber([response[22], response[23]]);
                break;
        }
    }

    private SentCommand<NecCommand>? TakeReply(byte replyType, string replyCode) =>
        _sent.TakeReply(command => command.ReplyType == replyType && command.ReplyCode == replyCode);

    private void HandleNullMessage(byte[] response)
    {
        CommunicationState = CommunicationState.Okay;
        var sent = _sent.TakeError();
        if (sent == null)
        {
            LogDebug("Ignored a NULL message {Response}", Convert.ToHexString(response));
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            if (sent.Command.Read is { } read)
                CountNullMessage(read, Convert.ToHexString(response));
            else
                LogDebug("The display gave a NULL message {Response} for {Command}", Convert.ToHexString(response), sent.Command.Message);
        }
    }

    private void ShowModelName(string encoded, byte[] response)
    {
        if (encoded.Length == 0)
        {
            ShowEmptyValue(ModelNameRead, response);
            return;
        }
        if (DecodeHexText(encoded) is not { } model)
        {
            CountUnreadableReply(ModelNameRead, response);
            return;
        }
        ReadAnswered(ModelNameRead);
        SetDetail(ModelLabel, model);
        ResolveIssue(ModelNameRead.IssueKey);
    }

    private void ShowSerial(string encoded, byte[] response)
    {
        if (encoded.Length == 0)
        {
            ShowEmptyValue(SerialRead, response);
            return;
        }
        if (DecodeHexText(encoded) is not { } serial)
        {
            CountUnreadableReply(SerialRead, response);
            return;
        }
        ReadAnswered(SerialRead);
        SetDetail(SerialLabel, serial);
        ResolveIssue(SerialRead.IssueKey);
    }

    private void ShowFirmware(string data, byte[] response)
    {
        if (data.Length >= NoErrorResult.Length && !data.StartsWith(NoErrorResult, StringComparison.Ordinal))
        {
            ReadAnswered(FirmwareRead);
            ShowNotSupported(FirmwareRead, Convert.ToHexString(response));
            return;
        }
        string expectedStart = NoErrorResult + FirmwareRevisionType;
        if (data == expectedStart)
        {
            ShowEmptyValue(FirmwareRead, response);
            return;
        }
        if (!data.StartsWith(expectedStart, StringComparison.Ordinal))
        {
            CountUnreadableReply(FirmwareRead, response);
            return;
        }
        ReadAnswered(FirmwareRead);
        SetDetail(FirmwareLabel, data[expectedStart.Length..]);
        ResolveIssue(FirmwareRead.IssueKey);
    }

    private void ShowMacAddress(string data, byte[] response)
    {
        if (data == MacAddressDevice)
        {
            ShowEmptyValue(MacAddressRead, response);
            return;
        }
        if (!data.StartsWith(MacAddressDevice, StringComparison.Ordinal))
        {
            CountUnreadableReply(MacAddressRead, response);
            return;
        }
        ReadAnswered(MacAddressRead);
        SetDetail(MacAddressLabel, data[MacAddressDevice.Length..]);
        ResolveIssue(MacAddressRead.IssueKey);
    }

    private void ShowSelfDiagnosis(string codes, byte[] response)
    {
        if (codes.Length == 0 || codes.Length % DiagnosisCodeLength != 0)
        {
            CountUnreadableReply(SelfDiagnosisRead, response);
            return;
        }
        ReadAnswered(SelfDiagnosisRead);
        ResolveIssue(SelfDiagnosisRead.IssueKey);

        var reported = codes.Chunk(DiagnosisCodeLength).Select(code => new string(code)).ToList();
        SetDetail(SignalLabel, reported.Contains(NoSignalDiagnosis) ? "No signal" : "Present");

        var faults = reported.Where(code => code is not (NormalDiagnosis or NoSignalDiagnosis)).Distinct().ToList();
        foreach (var cleared in GetOngoingIssues().Where(issue => issue.Key.StartsWith(SelfDiagnosisIssuePrefix, StringComparison.Ordinal)
                     && !faults.Contains(issue.Key[SelfDiagnosisIssuePrefix.Length..])))
            ResolveIssue(cleared.Key);
        foreach (var code in faults)
            RaiseOngoingIssue($"{SelfDiagnosisIssuePrefix}{code}", $"Self-diagnosis reports {FaultName(code)} ({code})", FaultSeverity(code));

        if (faults.Count == 0)
            SetDetail(SelfDiagnosisLabel, "Normal");
        else
            SetDetail(SelfDiagnosisLabel, string.Join(", ", faults.Select(code => $"{FaultName(code)} ({code})")),
                faults.Any(code => FaultSeverity(code) == IssueSeverity.Major) ? DetailTone.Error : DetailTone.Warning);
    }

    private static string FaultName(string code) =>
        SelfDiagnosisFaults.TryGetValue(code, out var fault) ? fault.Fault : "an unrecognised code";

    private static IssueSeverity FaultSeverity(string code) =>
        SelfDiagnosisFaults.TryGetValue(code, out var fault) ? fault.Severity : IssueSeverity.Minor;

    private static string? DecodeHexText(string encoded)
    {
        if (encoded.Length % 2 != 0 || !encoded.All(Uri.IsHexDigit))
            return null;
        return Encoding.Latin1.GetString(Convert.FromHexString(encoded));
    }

    private bool IsFromThisMonitor(byte[] response) =>
        _displayId is < FirstMonitorId or > LastMonitorId || response[SourceIndex] == _displayId;

    private void ReportErrorResult(byte[] response)
    {
        LogWarning("The display returned an error result: {Response}", Convert.ToHexString(response));
        AddEvent(EventType.Error, "The display returned an error result");
    }

    private void PrepareAndSendCommand(List<byte> header, byte[] payload, byte replyType, string replyCode, NecRead? read = null)
    {
        AddPayloadLengthToCommand(header, payload.Length);
        AddPayloadToCommand(header, payload);
        AddChecksumToCommand(header);
        WrapAndSendCommand(header, new NecCommand(Encoding.ASCII.GetString(payload[1..^1]), replyType, replyCode, read));
    }

    private List<byte> GetCommandHeaderWithoutSoh(byte commandType)
    {
        return [ReservedByte, _displayId, Controller, commandType];
    }

    private void WrapAndSendCommand(List<byte> theCommand, NecCommand command)
    {
        // add soh
        theCommand.Insert(0, StartOfHeader);
        theCommand.Add(Delimiter);
        if (_sent.Add(command) is { } dropped)
            LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply", dropped.Command.Message, SentCommandTracker<NecCommand>.MaxCommands);
        try
        {
            CommunicationClient.Send(theCommand.ToArray());
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    private void AddPayloadLengthToCommand(List<byte> command, int payloadLength)
    {
        foreach (var theByte in Bytes.AsciiRepresentationOfHexEquivalentOf(payloadLength, 2))
        {
            command.Add(theByte);
        }
    }

    private void AddPayloadToCommand(List<byte> command, byte[] payload)
    {
        foreach (byte theByte in payload)
        {
            command.Add(theByte);
        }
    }

    private void AddChecksumToCommand(List<byte> command)
    {
        command.Add(Bytes.XorAList(command));
    }

    protected override void DoPowerOn()
    {
        byte[] payload = [Stx, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, Etx];
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeCommand), payload, MessageTypeCommandReply, PowerControlCode);
    }

    protected override void DoPowerOff()
    {
        byte[] payload = [Stx, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x34, Etx];
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeCommand), payload, MessageTypeCommandReply, PowerControlCode);
    }

    protected override void DoSetInput(Input input)
    {
        byte[] payload =
            [Stx, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, InputDictionary[input][0], InputDictionary[input][1], Etx];
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeSetParameter), payload, MessageTypeSetParameterReply, InputOpCode);
    }

    protected override void DoSetVolume(int percentage)
    {
        byte[] volumeBytes = Bytes.AsciiRepresentationOfHexEquivalentOf(percentage, 2);
        byte[] payload = [Stx, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, volumeBytes[0], volumeBytes[1], Etx];
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeSetParameter), payload, MessageTypeSetParameterReply, VolumeOpCode);
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        byte[] payload =
            [Stx, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, (byte)(state == MuteState.On ? 0x31 : 0x32), Etx];
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeSetParameter), payload, MessageTypeSetParameterReply, AudioMuteOpCode);
    }

    private record NecRead(string Name, string Command, string ReplyCode, string IssueKey, string[] Labels);

    private sealed record NecCommand(string Message, byte ReplyType, string ReplyCode, NecRead? Read);
}
