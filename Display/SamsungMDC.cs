using System.Text;
using AVCoders.Core;

namespace AVCoders.Display;

public class SamsungMdc : Display
{
    public static readonly ushort DefaultPort = 1515;
    private readonly byte _displayId;
    private List<byte> _gather = [];
    private static readonly Dictionary<Input, byte> InputDictionary = new Dictionary<Input, byte>
    {
        { Input.Hdmi1, 0x21 },
        { Input.Hdmi2, 0x23 },
        { Input.Hdmi3, 0x31 },
        { Input.Hdmi4, 0x33 },
        { Input.DvbtTuner, 0x40 }
    };
    private readonly Dictionary<MuteState, byte> _muteDictionary;

    private const byte Header = 0xAA;
    private const byte ReplyHeader = 0xFF;
    private const int MinimumReplyLength = 8;
    private const int DisplayIdIndex = 2;
    private const int AcknowledgementIndex = 4;
    private const int CommandIndex = 5;
    private const int DataIndex = 6;
    private const byte LastSingleDisplayId = 0xFD;
    private const byte NotAcknowledged = 0x4E;
    private const byte SerialNumberCommand = 0x0B;
    private const byte ErrorStatusCommand = 0x0D;
    private const byte SoftwareVersionCommand = 0x0E;
    private const byte PowerControlCommand = 0x11;
    private const byte VolumeControlCommand = 0x12;
    private const byte MuteControlCommand = 0x13;
    private const byte InputControlCommand = 0x14;
    private const byte PanelOnTimeCommand = 0x83;
    private const byte ModelNameCommand = 0x8A;
    private const byte NoData = 0x00;
    private const byte DataLength1 = 0x01;
    private const int ErrorStatusLength = 6;
    private const int MaximumPanelOnTimeLength = 4;
    private const int PanelOnTimeUnitsPerHour = 6;
    private const byte StatusNormal = 0x00;
    private const byte StatusError = 0x01;
    private const byte BrightnessSensorNotFitted = 0x00;
    private const byte BrightnessSensorNormal = 0x02;
    private const byte FanNotFitted = 0x02;
    private const int PollsPerHealthRead = 5;
    private const int MaxMissedReplies = 3;
    private const string NoReply = "no reply";
    private const string NotReadYet = "Not read yet: read while the display is off";
    private static readonly TimeSpan CommandSpacing = TimeSpan.FromSeconds(1);

    private readonly byte[] _pollPowerCommand;
    private readonly byte[] _pollInputCommand;
    private readonly byte[] _pollVolumeCommand;
    private readonly byte[] _pollMuteCommand;
    private const string UnansweredVolumeIssueKey = "unanswered-volume";
    private const string LampErrorIssueKey = "lamp-error";
    private const string TemperatureErrorIssueKey = "temperature-error";
    private const string BrightnessSensorErrorIssueKey = "brightness-sensor-error";
    private const string FanErrorIssueKey = "fan-error";
    private const string SerialLabel = "Serial Number";
    private const string SoftwareVersionLabel = "Software Version";
    private const string ModelLabel = "Model";
    private const string LampLabel = "Lamp";
    private const string TemperatureLabel = "Temperature";
    private const string BrightnessSensorLabel = "Brightness Sensor";
    private const string SignalLabel = "Signal";
    private const string FanLabel = "Fan";
    private const string PanelOnTimeLabel = "Panel On Time";

    private static readonly MdcRead SerialRead = new("serial number", SerialNumberCommand, "serial-read", [SerialLabel]);
    private static readonly MdcRead SoftwareVersionRead = new("software version", SoftwareVersionCommand, "software-version-read", [SoftwareVersionLabel]);
    private static readonly MdcRead ModelRead = new("model name", ModelNameCommand, "model-read", [ModelLabel]);
    private static readonly MdcRead ErrorStatusRead = new("error status", ErrorStatusCommand, "error-status-read",
        [LampLabel, TemperatureLabel, BrightnessSensorLabel, SignalLabel, FanLabel]);
    private static readonly MdcRead PanelOnTimeRead = new("panel on time", PanelOnTimeCommand, "panel-on-time-read", [PanelOnTimeLabel]);
    private static readonly MdcRead[] IdentityReads = [SerialRead, SoftwareVersionRead, ModelRead];
    private static readonly MdcRead[] OffReads = [SerialRead, SoftwareVersionRead, ModelRead, ErrorStatusRead, PanelOnTimeRead];

    private readonly object _readsLock = new();
    private readonly HashSet<MdcRead> _owedIdentity = [..IdentityReads];
    private readonly Dictionary<MdcRead, int> _missedReplies = new();
    private readonly SentCommandTracker<MdcRead> _sent;
    private bool _volumePollPending;
    private bool _errorStatusUnsupported;
    private bool _panelOnTimeUnsupported;
    private int _polls;


    public SamsungMdc(CommunicationClient communicationClient, byte displayId, string name, Input? defaultInput) :
        this(communicationClient, displayId, name, defaultInput, null)
    {
    }

    internal SamsungMdc(CommunicationClient communicationClient, byte displayId, string name, Input? defaultInput, TimeProvider? timeProvider) :
        base(InputDictionary.Keys.ToList(), name, defaultInput, communicationClient, CommandStringFormat.Hex, DefaultPollSeconds, timeProvider)
    {
        _displayId = displayId;
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _sent = new SentCommandTracker<MdcRead>(CommandSpacing, TimeProvider);
        _muteDictionary = new Dictionary<MuteState, byte>
        {
            { MuteState.On, 0x01 },
            { MuteState.Off, 0x00 }
        };

        byte[] pollPowerCommandWithoutChecksum = [0xAA, PowerControlCommand, _displayId, 0x00];
        _pollPowerCommand = [0xAA, 0x11, _displayId, 0x00, GenerateChecksum(pollPowerCommandWithoutChecksum)];
        
        byte[] pollInputCommandWithoutChecksum = [0xAA, InputControlCommand, _displayId, 0x00];
        _pollInputCommand = [0xAA, InputControlCommand, _displayId, 0x00,  GenerateChecksum(pollInputCommandWithoutChecksum)
        ];

        byte[] pollVolumeCommandWithoutChecksum = [0xAA, VolumeControlCommand, _displayId, 0x00];
        _pollVolumeCommand = [0xAA, VolumeControlCommand, _displayId, 0x00, GenerateChecksum(pollVolumeCommandWithoutChecksum)
        ];
        
        byte[] pollMuteCommandWithoutChecksum = [0xAA, MuteControlCommand, _displayId, 0x00];
        _pollMuteCommand = [0xAA, MuteControlCommand, _displayId, 0x00,  GenerateChecksum(pollMuteCommandWithoutChecksum)
        ];

        foreach (var label in OffReads.SelectMany(read => read.Labels))
            SetDetail(label, NotReadYet);
    }

    protected override async Task DoPoll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
        {
            using (PushProperties("DoPoll"))
                LogDebug("Not polling");
            return;
        }

        SendByteArray(_pollPowerCommand);
        if (PowerState == PowerState.Off)
        {
            await ReadWhileOff(token);
            return;
        }
        if (PowerState != PowerState.On)
            return;

        await Task.Delay(CommandSpacing, TimeProvider, token);
        SendByteArray(_pollInputCommand);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        if (_volumePollPending)
            RaiseMomentaryIssue("The volume poll was not answered", key: UnansweredVolumeIssueKey,
                escalateAfter: 3);
        _volumePollPending = true;
        SendByteArray(_pollVolumeCommand);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        SendByteArray(_pollMuteCommand);
    }

    private async Task ReadWhileOff(CancellationToken token)
    {
        await ReadIdentity(token);
        if (_polls++ % PollsPerHealthRead == 0)
            await ReadHealth(token);
    }

    private async Task ReadIdentity(CancellationToken token)
    {
        List<MdcRead> owed;
        lock (_readsLock)
            owed = IdentityReads.Where(_owedIdentity.Contains).ToList();
        foreach (var read in owed)
            await Read(read, token);
    }

    private async Task ReadHealth(CancellationToken token)
    {
        if (!_errorStatusUnsupported)
            await Read(ErrorStatusRead, token);
        if (!_panelOnTimeUnsupported)
            await Read(PanelOnTimeRead, token);
    }

    private async Task Read(MdcRead read, CancellationToken token)
    {
        await Task.Delay(CommandSpacing, TimeProvider, token);
        if (_sent.Add(read) is { } dropped)
            LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply", $"0x{dropped.Command.Command:X2}", SentCommandTracker<MdcRead>.MaxCommands);
        SendQuery(read.Command);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        CountMissedReplies();
    }

    private void CountMissedReplies()
    {
        using (PushProperties("CountMissedReplies"))
        {
            foreach (var missed in _sent.TakeMissed())
            {
                using (Trigger.Resume(missed.TriggerId))
                    CountMiss(missed.Command, NoReply);
            }
        }
    }

    private void CountMiss(MdcRead read, string result)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(read) + 1;
            _missedReplies[read] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The {Read} query 0x{Command:X2} got {Result}, asking again on a later poll ({Missed} of {MaxMissedReplies})",
                read.Name, read.Command, result, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(read.IssueKey,
            $"The display did not give a real reply to the {read.Name} query 0x{read.Command:X2} {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private void ReadAnswered(MdcRead read)
    {
        lock (_readsLock)
        {
            _owedIdentity.Remove(read);
            _missedReplies.Remove(read);
        }
    }

    private void SendQuery(byte command)
    {
        byte[] query = [Header, command, _displayId, NoData];
        SendByteArray([..query, GenerateChecksum(query)]);
    }

    private void SendByteArray(byte[] bytes)
    {
        try
        {
            CommunicationClient.Send(bytes);
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    private void PowerCommand(byte data)
    {
        byte[] commandWithoutChecksum = [Header, PowerControlCommand, _displayId, DataLength1, data];
        byte[] commandWithChecksum =
            [Header, PowerControlCommand, _displayId, DataLength1, data, GenerateChecksum(commandWithoutChecksum)];
        SendByteArray(commandWithChecksum);
    }

    protected override void DoPowerOn() => PowerCommand(0x01);

    protected override void DoPowerOff() => PowerCommand(0x00);
    
    private byte GenerateChecksum(byte[] input)
    {
        IEnumerable<byte> source = (input[0] == 0xAA) ? input.Skip(1) : input;

        var value = source.Sum(v => v).ToString("X2");

        var sum = Convert.ToByte(value.Substring(value.Length - 2, 2), 16);

        return sum;
    }

    public void HandleResponse(byte[] response)
    {
        using (PushProperties())
        {
            _gather.AddRange(response);

            while (_gather.Count > 0 && _gather[0] != 0xAA)
            {
                _gather.RemoveAt(0);
            }

            while (_gather.Count > 4 && _gather.Count >= _gather[3] + 5)
            {
                int endIndex = _gather[3] + 5;
                byte[] aResponsePayload = _gather.Take(endIndex).ToArray();
                _gather = _gather.Skip(endIndex).ToList();
                ProcessResponse(aResponsePayload);
            }
        }
    }

    public void ProcessResponse(byte[] response)
    {
        using (PushProperties("ProcessResponse"))
        {
            if (response.Length < MinimumReplyLength)
            {
                LogWarning("The response is too short: {Response}", Convert.ToHexString(response));
                AddEvent(EventType.Error, "The response is too short");
                return;
            }

            if (response[0] != Header || response[1] != ReplyHeader)
            {
                LogWarning("The response does not have the correct header: {Response}", Convert.ToHexString(response));
                AddEvent(EventType.Error, "The response does not have the correct header");
                return;
            }

            if (GenerateChecksum(response[..^1]) != response[^1])
            {
                LogWarning("The response checksum is wrong: {Response}", Convert.ToHexString(response));
                AddEvent(EventType.Error, "The response checksum is wrong");
                return;
            }

            if (!IsFromThisDisplay(response))
            {
                LogDebug("Ignored reply for display {DisplayId}: {Response}", response[DisplayIdIndex], Convert.ToHexString(response));
                return;
            }

            if (response[AcknowledgementIndex] == NotAcknowledged)
            {
                ProcessNak(response);
                return;
            }

            CommunicationState = CommunicationState.Okay;

            switch (response[CommandIndex])
            {
                case PowerControlCommand:
                    PowerState = response[6] switch
                    {
                        0x00 => PowerState.Off,
                        0x01 => PowerState.On,
                        _ => PowerState
                    };
                    ProcessPowerResponse();
                    break;
                case InputControlCommand:
                    var input = response[6] switch
                    {
                        0x21 => Input.Hdmi1,
                        0x22 => Input.Hdmi1,
                        0x23 => Input.Hdmi2,
                        0x24 => Input.Hdmi2,
                        0x31 => Input.Hdmi3,
                        0x32 => Input.Hdmi3,
                        0x33 => Input.Hdmi4,
                        0x34 => Input.Hdmi4,
                        0x40 => Input.DvbtTuner,
                        _ => Input.Unknown
                    };
                    if (input == Input.Unknown && Input != Input.Unknown)
                        LogWarning("Unknown input code 0x{Code:X2} in {Response}", response[6], Convert.ToHexString(response));
                    else if (input == Input.Unknown)
                        LogDebug("Unknown input code 0x{Code:X2} in {Response}", response[6], Convert.ToHexString(response));
                    Input = input;
                    ProcessInputResponse();
                    break;
                case VolumeControlCommand:
                    Volume = response[6];
                    _volumePollPending = false;
                    ResolveIssue(UnansweredVolumeIssueKey);
                    break;
                case MuteControlCommand:
                    AudioMute = response[6] switch
                    {
                        0x00 => MuteState.Off,
                        0x01 => MuteState.On,
                        _ => MuteState.Unknown
                    };
                    break;
                case SerialNumberCommand:
                case SoftwareVersionCommand:
                case ModelNameCommand:
                case ErrorStatusCommand:
                case PanelOnTimeCommand:
                    ProcessReadReply(response);
                    break;
            }
        }
    }

    private void ProcessReadReply(byte[] response)
    {
        byte command = response[CommandIndex];
        var sent = _sent.TakeReply(read => read.Command == command);
        if (sent == null)
        {
            LogDebug("Ignored a reply that no read of ours is waiting for: {Response}", Convert.ToHexString(response));
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            switch (command)
            {
                case SerialNumberCommand:
                case SoftwareVersionCommand:
                case ModelNameCommand:
                    ShowText(sent.Command, response);
                    break;
                case ErrorStatusCommand:
                    ShowErrorStatus(response);
                    break;
                case PanelOnTimeCommand:
                    ShowPanelOnTime(response);
                    break;
            }
        }
    }

    private void ProcessNak(byte[] response)
    {
        switch (response[CommandIndex])
        {
            case SerialNumberCommand:
            case SoftwareVersionCommand:
            case ModelNameCommand:
            case ErrorStatusCommand:
            case PanelOnTimeCommand:
                ProcessReadNak(response);
                break;
            default:
                LogWarning("NAK Received");
                CommunicationState = CommunicationState.Error;
                break;
        }
    }

    private void ProcessReadNak(byte[] response)
    {
        byte command = response[CommandIndex];
        var sent = _sent.TakeReply(read => read.Command == command);
        if (sent == null)
        {
            LogDebug("Ignored a NAK that no read of ours is waiting for: {Response}", Convert.ToHexString(response));
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            switch (command)
            {
                case ErrorStatusCommand:
                    _errorStatusUnsupported = true;
                    break;
                case PanelOnTimeCommand:
                    _panelOnTimeUnsupported = true;
                    break;
            }
            ShowNotSupported(sent.Command, response);
        }
    }

    private void ShowNotSupported(MdcRead read, byte[] response)
    {
        ReadAnswered(read);
        string raw = Convert.ToHexString(response);
        LogInformation("The display does not support the {Read} query 0x{Command:X2}: {Response}", read.Name, read.Command, raw);
        foreach (var label in read.Labels)
            SetDetail(label, $"Not supported: {raw}", DetailTone.Normal);
        ResolveIssue(read.IssueKey);
    }

    private void ShowText(MdcRead read, byte[] response)
    {
        ReadAnswered(read);
        string text = Encoding.UTF8.GetString(response[DataIndex..^1]).TrimEnd('\0');
        SetDetail(read.Labels[0], text.Length == 0 ? Convert.ToHexString(response) : text);
        ResolveIssue(read.IssueKey);
    }

    private void ShowErrorStatus(byte[] response)
    {
        byte[] status = response[DataIndex..^1];
        if (status.Length != ErrorStatusLength)
        {
            CountMiss(ErrorStatusRead, Convert.ToHexString(response));
            return;
        }
        ReadAnswered(ErrorStatusRead);
        ResolveIssue(ErrorStatusRead.IssueKey);

        byte lamp = status[0];
        byte temperatureStatus = status[1];
        byte brightnessSensor = status[2];
        byte sync = status[3];
        int temperature = status[4];
        byte fan = status[5];

        SetDetail(LampLabel, lamp switch
        {
            StatusNormal => "Normal",
            StatusError => "Error",
            _ => Raw(lamp)
        }, lamp == StatusError ? DetailTone.Error : DetailTone.Normal);
        SetDetail(TemperatureLabel, temperatureStatus is StatusNormal or StatusError ? $"{temperature} °C" : $"{temperature} °C, status {Raw(temperatureStatus)}",
            temperatureStatus == StatusError ? DetailTone.Error : DetailTone.Normal);
        SetDetail(BrightnessSensorLabel, brightnessSensor switch
        {
            BrightnessSensorNotFitted => "Not fitted",
            BrightnessSensorNormal => "Normal",
            StatusError => "Error",
            _ => Raw(brightnessSensor)
        }, brightnessSensor == StatusError ? DetailTone.Warning : DetailTone.Normal);
        SetDetail(SignalLabel, sync switch
        {
            StatusNormal => "Present",
            StatusError => "No sync",
            _ => Raw(sync)
        });
        SetDetail(FanLabel, fan switch
        {
            StatusNormal => "Normal",
            StatusError => "Error",
            FanNotFitted => "Not fitted",
            _ => Raw(fan)
        }, fan == StatusError ? DetailTone.Error : DetailTone.Normal);

        if (lamp == StatusError)
            RaiseOngoingIssue(LampErrorIssueKey, "The display reports a lamp error", IssueSeverity.Major);
        else
            ResolveIssue(LampErrorIssueKey);

        if (temperatureStatus == StatusError)
            RaiseOngoingIssue(TemperatureErrorIssueKey, $"The display reports a temperature error at {temperature} °C", IssueSeverity.Major);
        else
            ResolveIssue(TemperatureErrorIssueKey);

        if (brightnessSensor == StatusError)
            RaiseOngoingIssue(BrightnessSensorErrorIssueKey, "The display reports a brightness sensor error", IssueSeverity.Minor);
        else
            ResolveIssue(BrightnessSensorErrorIssueKey);

        if (fan == StatusError)
            RaiseOngoingIssue(FanErrorIssueKey, "The display reports a fan error", IssueSeverity.Major);
        else
            ResolveIssue(FanErrorIssueKey);
    }

    private void ShowPanelOnTime(byte[] response)
    {
        byte[] count = response[DataIndex..^1];
        if (count.Length is 0 or > MaximumPanelOnTimeLength)
        {
            CountMiss(PanelOnTimeRead, Convert.ToHexString(response));
            return;
        }
        ReadAnswered(PanelOnTimeRead);
        long tenMinutePeriods = count.Aggregate(0L, (total, next) => (total << 8) | next);
        SetDetail(PanelOnTimeLabel, $"{tenMinutePeriods / PanelOnTimeUnitsPerHour} h");
        ResolveIssue(PanelOnTimeRead.IssueKey);
    }

    private static string Raw(byte value) => $"0x{value:X2}";

    private bool IsFromThisDisplay(byte[] response) =>
        _displayId > LastSingleDisplayId || response[DisplayIdIndex] == _displayId;

    private void sendCommandWithOneDataLength(byte command, byte data)
    {
        byte[] commandWithoutChecksum = [Header, command, _displayId, DataLength1, data];
        byte[] commandWithChecksum =
            [Header, command, _displayId, DataLength1, data, GenerateChecksum(commandWithoutChecksum)];
        SendByteArray(commandWithChecksum);
    }

    protected override void DoSetInput(Input input) => sendCommandWithOneDataLength(InputControlCommand, InputDictionary[input]);

    protected override void DoSetVolume(int volume) => sendCommandWithOneDataLength(VolumeControlCommand, (byte)volume);

    protected override void DoSetAudioMute(MuteState state) => sendCommandWithOneDataLength(MuteControlCommand, _muteDictionary[state]);
    
    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        _sent.Clear();
        if (connectionState != ConnectionState.Connected)
            return;
        lock (_readsLock)
        {
            _owedIdentity.UnionWith(IdentityReads);
            _missedReplies.Clear();
        }
        _errorStatusUnsupported = false;
        _panelOnTimeUnsupported = false;
        _polls = 0;
    }

    private record MdcRead(string Name, byte Command, string IssueKey, string[] Labels);
}