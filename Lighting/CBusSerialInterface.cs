using AVCoders.Core;
using System.Text;

namespace AVCoders.Lighting;

public enum CBusRampTime : byte
{
    Instant = 0x02,
    FourSeconds = 0x0A,
    EightSeconds = 0x12,
    TwelveSeconds = 0x1A,
    TwentySeconds = 0x22,
    ThirtySeconds = 0x2A,
    FortySeconds = 0x32,
    OneMinute = 0x3A,
    OneMinute30 = 0x42,
    TwoMinutes = 0x4A,
    ThreeMinutes = 0x52,
    FiveMinutes = 0x5A,
    SevenMinutes = 0x62,
    TenMinutes = 0x6A,
    FifteenMinutes = 0x72,
    SeventeenMinutes = 0x7A,
}

public record CBusGroupMessage(byte SourceUnit, byte Application, byte Command, byte Group, byte Level);

public record CBusChannelLevel(byte Unit, byte Channel, byte Level);

public delegate void CBusGroupMessageHandler(CBusGroupMessage message);

public delegate void CBusChannelLevelHandler(CBusChannelLevel channelLevel);

public class CBusSerialInterface : LogBase
{
    public const byte LightingApplication = 0x38;
    public const byte SceneApplication = 0xCA;
    public const byte OnCommand = 0x79;
    public const byte OffCommand = 0x01;
    public const byte FullLevel = 0xFF;
    public const byte ZeroLevel = 0x00;
    public const int MaxQueuedCommands = 95;
    public const int MaxLineLength = 150;
    public static readonly SerialSpec SerialSpec = new(SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan SetupStepDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan HealthReadInterval = TimeSpan.FromMinutes(1);

    private const int MaxMissedReplies = 3;
    private const string NoReply = "no reply";
    private const byte PointToMultipointHeader = 0x05;
    private const byte PointToPointHeader = 0x06;
    private const byte CalReplyHeader = 0x86;
    private const byte StatusReplyCommand = 0x82;
    private const byte ReplyCommand = 0x80;
    private const byte ReplyCommandMask = 0xE0;
    private const byte ReplyLengthMask = 0x1F;
    private const string PciIdentifyCommand = "@21";
    private const byte UnitTypeAttribute = 0x01;
    private const byte FirmwareAttribute = 0x02;
    private const byte DiagnosticSummaryAttribute = 0x04;
    private const byte NetworkVoltageAttribute = 0x07;
    private const int DiagnosticSummaryLength = 12;
    private const int CommonStatusIndex = 10;
    private const int ErrorFlagsIndex = 11;
    private const byte NetworkVoltageMarginalBit = 0x20;
    private const byte NetworkVoltageLowBit = 0x40;
    private const byte PowerOnResetFlag = 0x01;
    private const byte EepromErrorFlags = 0x70;
    private const byte FirstPrintableCharacter = 0x20;
    private const byte LastPrintableCharacter = 0x7E;
    private const string UnitTypeLabel = "Unit Type";
    private const string FirmwareLabel = "Firmware";
    private const string NetworkVoltageLabel = "Network Voltage";
    private const string ErrorFlagsLabel = "Error Flags";
    private const string UnitTypeIssueKey = "unit-type-read";
    private const string FirmwareIssueKey = "firmware-read";
    private const string DiagnosticSummaryIssueKey = "diagnostic-summary-read";
    private const string NetworkVoltageReadIssueKey = "network-voltage-read";
    private const string NetworkVoltageIssueKey = "network-voltage";
    private const string ErrorFlagsIssueKey = "error-flags";
    private const byte StatusRequestCommand = 0x2A;
    private const byte StatusRequestChannelCount = 0x01;
    private const byte LocalNetwork = 0x00;
    private const byte LongFormFlag = 0x80;
    private const byte LongFormLengthMask = 0x1F;
    private const byte ShortFormLengthMask = 0x07;
    private const int RampArgumentCount = 2;
    private const int MultipointHeaderLength = 3;
    private const int HeaderIndex = 0;
    private const int SourceUnitIndex = 1;
    private const int ApplicationIndex = 2;
    private const int RoutingIndex = 3;
    private const int SalHeaderLength = 4;
    private const int ReplyUnitIndex = 1;
    private const int PciUnitIndex = 2;
    private const int CalCommandIndex = 4;
    private const int StatusChannelIndex = 5;
    private const int StatusLevelIndex = 6;
    private const int StatusReplyLength = 8;
    private const int IdentifyAttributeIndex = 5;
    private const int IdentifyDataIndex = 6;
    private const int LongFormReplyFramingLength = 6;
    private const int HexCharactersPerByte = 2;
    private const byte ConfirmationCharacter = (byte)'r';
    private const byte LineFeed = 0x0A;
    private const byte HighestHexCharacter = (byte)'F';
    private const char Delimiter = '\r';
    private const char BeginPacket = '\\';

    private record CBusCommand(string Wire, string? TriggerId);

    private static readonly string[] PciSetupStrings =
    [
        "~~~\r",
        "~~~p\r",
        "@A330005100p\r",
        "@A3410051p\r",
        "@A3420007p\r",
        "@A32100FF00p\r",
    ];

    private static readonly byte[] IdentifyAttributes = [UnitTypeAttribute, FirmwareAttribute, NetworkVoltageAttribute, DiagnosticSummaryAttribute];
    private static readonly byte[] HealthAttributes = [NetworkVoltageAttribute, DiagnosticSummaryAttribute];

    private static readonly Dictionary<byte, string> IdentifyIssueKeys = new()
    {
        { UnitTypeAttribute, UnitTypeIssueKey },
        { FirmwareAttribute, FirmwareIssueKey },
        { DiagnosticSummaryAttribute, DiagnosticSummaryIssueKey },
        { NetworkVoltageAttribute, NetworkVoltageReadIssueKey },
    };

    private static readonly string[] ErrorFlagNames =
    [
        "micro power on reset",
        "internal stack overflow",
        "comms tx error",
        "micro reset",
        "EE data error",
        "EE checksum error",
        "EE write error",
        "installation MMI error",
    ];

    private readonly CommunicationClient _comms;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<CBusCommand> _queue = new();
    private readonly object _queueLock = new();
    private readonly List<byte> _line = [];
    private readonly HashSet<byte> _awaitedReads = [];
    private readonly Dictionary<byte, int> _missedReplies = new();
    private readonly object _readsLock = new();
    private ITimer? _confirmationTimer;
    private ITimer? _healthTimer;
    private CBusCommand? _inFlight;

    public CBusGroupMessageHandler? GroupMessageHandlers;
    public CBusChannelLevelHandler? ChannelLevelHandlers;

    public CBusSerialInterface(CommunicationClient comms, TimeProvider? timeProvider = null) : base("Cbus Interface")
    {
        _comms = comms;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _comms.ResponseByteHandlers += HandleResponse;
    }

    public int QueuedCommands
    {
        get
        {
            lock (_queueLock)
                return _queue.Count;
        }
    }

    public async Task SetupPci()
    {
        lock (_queueLock)
        {
            _queue.Clear();
            _confirmationTimer?.Dispose();
            _inFlight = null;
        }
        Interlocked.Exchange(ref _healthTimer, null)?.Dispose();
        lock (_readsLock)
        {
            _awaitedReads.Clear();
            _missedReplies.Clear();
        }

        foreach (var setupString in PciSetupStrings)
        {
            _comms.Send(setupString);
            await Task.Delay(SetupStepDelay, _timeProvider).ConfigureAwait(false);
        }

        ReadThePci();
    }

    private void ReadThePci()
    {
        foreach (var attribute in IdentifyAttributes)
            ReadIdentify(attribute);
        ITimer healthTimer;
        using (ExecutionContext.SuppressFlow())
            healthTimer = _timeProvider.CreateTimer(HandleHealthTimer, null, HealthReadInterval, HealthReadInterval);
        Interlocked.Exchange(ref _healthTimer, healthTimer)?.Dispose();
    }

    private void HandleHealthTimer(object? state)
    {
        using (Trigger.Begin())
        using (PushProperties("HandleHealthTimer"))
        {
            try
            {
                var owed = CountMissedReads();
                foreach (var attribute in IdentifyAttributes.Where(attribute => owed.Contains(attribute) || HealthAttributes.Contains(attribute)))
                    ReadIdentify(attribute);
            }
            catch (Exception e)
            {
                LogException(e, "Reading the PCI's network voltage and status failed");
            }
        }
    }

    private List<byte> CountMissedReads()
    {
        List<byte> unanswered;
        List<byte> owed;
        lock (_readsLock)
        {
            unanswered = _awaitedReads.ToList();
            _awaitedReads.Clear();
            owed = unanswered.Union(_missedReplies.Keys).ToList();
        }
        foreach (var attribute in unanswered)
            CountMissedReply(attribute, NoReply);
        return owed;
    }

    private void CountMissedReply(byte attribute, string result)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(attribute) + 1;
            _missedReplies[attribute] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("{Command} got {Result}, asking the PCI again ({Missed} of {MaxMissedReplies})",
                $"{PciIdentifyCommand}{attribute:X2}", result, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(IdentifyIssueKeys[attribute],
            $"The PCI did not answer {PciIdentifyCommand}{attribute:X2} {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private void ReadAnswered(byte attribute, string issueKey)
    {
        lock (_readsLock)
            _missedReplies.Remove(attribute);
        ResolveIssue(issueKey);
    }

    private void ReadIdentify(byte attribute)
    {
        lock (_readsLock)
            _awaitedReads.Add(attribute);
        Enqueue($"{PciIdentifyCommand}{attribute:X2}{(char)ConfirmationCharacter}{Delimiter}");
    }

    private bool IsAwaited(byte attribute)
    {
        lock (_readsLock)
            return _awaitedReads.Contains(attribute);
    }

    public void SendPointToMultipointPayload(byte application, byte[] data)
    {
        byte[] payload = new byte[MultipointHeaderLength + data.Length];
        payload[0] = PointToMultipointHeader;
        payload[1] = application;
        payload[2] = LocalNetwork;
        Array.Copy(data, 0, payload, MultipointHeaderLength, data.Length);
        Enqueue(BuildWireString(payload));
    }

    public void RequestChannelStatus(byte unit, byte channel)
    {
        Enqueue(BuildWireString([PointToPointHeader, unit, LocalNetwork, StatusRequestCommand, channel, StatusRequestChannelCount]));
    }

    private static string BuildWireString(byte[] data)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(BeginPacket);
        sb.Append(Convert.ToHexString(data));
        sb.Append((char)ConfirmationCharacter);
        sb.Append(Delimiter);
        return sb.ToString();
    }

    private void Enqueue(string wire)
    {
        lock (_queueLock)
        {
            var command = new CBusCommand(wire, Trigger.Id);
            if (_inFlight == null)
            {
                Transmit(command);
                return;
            }

            if (_queue.Count >= MaxQueuedCommands)
            {
                LogWarning("{Count} commands waiting for the PCI, clearing the queue", _queue.Count);
                _queue.Clear();
            }

            _queue.Enqueue(command);
        }
    }

    private void Transmit(CBusCommand command)
    {
        _inFlight = command;
        using (ExecutionContext.SuppressFlow())
            _confirmationTimer = _timeProvider.CreateTimer(HandleConfirmationTimeout, command, ConfirmationTimeout, Timeout.InfiniteTimeSpan);
        using (Trigger.Resume(command.TriggerId))
            _comms.Send(command.Wire);
    }

    private void SendNext()
    {
        lock (_queueLock)
        {
            _confirmationTimer?.Dispose();
            if (_queue.Count == 0)
            {
                _inFlight = null;
                return;
            }

            Transmit(_queue.Dequeue());
        }
    }

    private void HandleConfirmation()
    {
        lock (_queueLock)
        {
            if (_inFlight == null)
                return;
            using (Trigger.Resume(_inFlight.TriggerId))
            using (PushProperties("HandleConfirmation"))
                LogVerbose("PCI confirmed {Command}", _inFlight.Wire.TrimEnd());
            SendNext();
        }
    }

    private void HandleConfirmationTimeout(object? command)
    {
        lock (_queueLock)
        {
            if (!ReferenceEquals(command, _inFlight))
                return;
            try
            {
                using (Trigger.Resume(_inFlight!.TriggerId))
                using (PushProperties("HandleConfirmationTimeout"))
                    LogWarning("No confirmation from the PCI within {Timeout} ms, sending the next command", ConfirmationTimeout.TotalMilliseconds);
                SendNext();
            }
            catch (Exception e)
            {
                LogException(e, "Sending the next command after the confirmation timeout failed");
            }
        }
    }

    private void HandleResponse(byte[] response)
    {
        foreach (byte b in response)
        {
            switch (b)
            {
                case ConfirmationCharacter:
                    _line.Clear();
                    HandleConfirmation();
                    break;
                case LineFeed:
                    byte[] line = _line.ToArray();
                    _line.Clear();
                    ProcessLine(line);
                    break;
                case >= (byte)'0' and <= (byte)'9':
                case >= (byte)'A' and <= HighestHexCharacter:
                    if (_line.Count == MaxLineLength)
                    {
                        LogWarning("No line feed within {Length} characters, clearing the line", MaxLineLength);
                        _line.Clear();
                    }
                    _line.Add(b);
                    break;
                case > HighestHexCharacter:
                    _line.Clear();
                    break;
            }
        }
    }

    private void ProcessLine(byte[] line)
    {
        if (line.Length < HexCharactersPerByte || line.Length % HexCharactersPerByte != 0)
            return;

        string raw = Encoding.ASCII.GetString(line);
        byte[] message = Convert.FromHexString(raw);
        using (PushProperties("ProcessLine"))
        {
            LogVerbose("{Message}", BitConverter.ToString(message));
        }

        switch (message[HeaderIndex])
        {
            case PointToMultipointHeader:
                ProcessMonitoredSal(message);
                break;
            case CalReplyHeader:
                ProcessCalReply(message, raw);
                break;
        }
    }

    private void ProcessCalReply(byte[] message, string raw)
    {
        if (message.Length == StatusReplyLength && message[CalCommandIndex] == StatusReplyCommand)
            ProcessStatusReply(message);
        else if (message.Length > IdentifyAttributeIndex && message[ReplyUnitIndex] == message[PciUnitIndex]
            && IsAwaited(message[IdentifyAttributeIndex]))
            ProcessPciReply(message, raw);
        else
            ProcessStatusReply(message);
    }

    private void ProcessPciReply(byte[] message, string raw)
    {
        byte command = message[CalCommandIndex];
        byte attribute = message[IdentifyAttributeIndex];
        if ((command & ReplyCommandMask) != ReplyCommand || !IdentifyIssueKeys.TryGetValue(attribute, out var issueKey))
            return;

        lock (_readsLock)
            _awaitedReads.Remove(attribute);
        using (PushProperties("ProcessPciReply"))
        {
            if (message.Length <= IdentifyDataIndex || message.Length != (command & ReplyLengthMask) + LongFormReplyFramingLength)
            {
                CountMissedReply(attribute, $"{raw}, which is not the length its header gives");
                return;
            }

            byte[] data = message[IdentifyDataIndex..^1];
            switch (attribute)
            {
                case UnitTypeAttribute:
                    ShowText(UnitTypeLabel, issueKey, attribute, data, raw);
                    break;
                case FirmwareAttribute:
                    ShowText(FirmwareLabel, issueKey, attribute, data, raw);
                    break;
                case NetworkVoltageAttribute:
                    ShowText(NetworkVoltageLabel, issueKey, attribute, data, raw);
                    break;
                case DiagnosticSummaryAttribute:
                    ShowDiagnosticSummary(data, raw);
                    break;
            }
        }
    }

    private void ShowText(string label, string issueKey, byte attribute, byte[] data, string raw)
    {
        if (data.Any(b => b is < FirstPrintableCharacter or > LastPrintableCharacter))
        {
            CountMissedReply(attribute, $"{raw}, which is not ASCII text");
            return;
        }
        string text = Encoding.ASCII.GetString(data).Trim();
        SetDetail(label, text.Length == 0 ? raw : text);
        ReadAnswered(attribute, issueKey);
    }

    private void ShowDiagnosticSummary(byte[] data, string raw)
    {
        if (data.Length != DiagnosticSummaryLength)
        {
            CountMissedReply(DiagnosticSummaryAttribute, $"{raw}, which is not {DiagnosticSummaryLength} bytes");
            return;
        }
        ReadAnswered(DiagnosticSummaryAttribute, DiagnosticSummaryIssueKey);
        CheckTheNetworkVoltage(data[CommonStatusIndex]);
        ShowErrorFlags(data[ErrorFlagsIndex]);
    }

    private void CheckTheNetworkVoltage(byte commonStatus)
    {
        if ((commonStatus & NetworkVoltageLowBit) != 0)
            RaiseOngoingIssue(NetworkVoltageIssueKey,
                $"The PCI reports the C-Bus network voltage is low (network LED off), common status {commonStatus:X2}");
        else if ((commonStatus & NetworkVoltageMarginalBit) != 0)
            RaiseOngoingIssue(NetworkVoltageIssueKey,
                $"The PCI reports the C-Bus network voltage is marginal (network LED flashing), common status {commonStatus:X2}", IssueSeverity.Minor);
        else
            ResolveIssue(NetworkVoltageIssueKey);
    }

    private void ShowErrorFlags(byte flags)
    {
        string names = string.Join(", ", ErrorFlagNames.Where((_, bit) => (flags & (1 << bit)) != 0));
        string text = names.Length == 0 ? $"{flags:X2}" : $"{flags:X2} ({names})";
        SetDetail(ErrorFlagsLabel, text);
        if ((flags & EepromErrorFlags) != 0)
            RaiseOngoingIssue(ErrorFlagsIssueKey, $"The PCI reports error flags {text}");
        else if ((flags & ~PowerOnResetFlag) != 0)
            RaiseOngoingIssue(ErrorFlagsIssueKey, $"The PCI reports error flags {text}", IssueSeverity.Minor);
        else
            ResolveIssue(ErrorFlagsIssueKey);
    }

    private void ProcessMonitoredSal(byte[] message)
    {
        if (message.Length <= SalHeaderLength)
            return;

        byte sourceUnit = message[SourceUnitIndex];
        byte application = message[ApplicationIndex];
        int checksumIndex = message.Length - 1;
        int index = SalHeaderLength + message[RoutingIndex];

        while (index < checksumIndex)
        {
            byte command = message[index];
            int argumentCount = (command & LongFormFlag) != 0 ? command & LongFormLengthMask : command & ShortFormLengthMask;
            if (index + argumentCount >= checksumIndex)
                return;

            switch (command)
            {
                case OnCommand:
                    GroupMessageHandlers?.Invoke(new CBusGroupMessage(sourceUnit, application, command, message[index + 1], FullLevel));
                    break;
                case OffCommand:
                    GroupMessageHandlers?.Invoke(new CBusGroupMessage(sourceUnit, application, command, message[index + 1], ZeroLevel));
                    break;
                default:
                    if ((command & LongFormFlag) == 0 && argumentCount == RampArgumentCount)
                        GroupMessageHandlers?.Invoke(new CBusGroupMessage(sourceUnit, application, command, message[index + 1], message[index + 2]));
                    break;
            }

            index += 1 + argumentCount;
        }
    }

    private void ProcessStatusReply(byte[] message)
    {
        if (message.Length < StatusReplyLength || message[CalCommandIndex] != StatusReplyCommand)
            return;

        ChannelLevelHandlers?.Invoke(new CBusChannelLevel(message[ReplyUnitIndex], message[StatusChannelIndex], message[StatusLevelIndex]));
    }
}
