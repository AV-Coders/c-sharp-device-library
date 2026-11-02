using System.Text;
using AVCoders.Core;

namespace AVCoders.Display;

public class PhilipsSICP : Display
{
    public const ushort DefaultPort = 5000;
    public static readonly SerialSpec DefaultSpec = new (SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    private readonly byte _monitorId;
    private readonly byte _groupId;
    private readonly List<byte> _gather = [];
    private const int MinimumResponseLength = 6;
    private const int CommandIndex = 3;
    private const int DataIndex = 4;
    private const byte PollGroup = 0x00;
    private const byte CommunicationControl = 0x00;
    private const byte Acknowledged = 0x06;
    private const byte NotAcknowledged = 0x15;
    private const byte NotAvailable = 0x18;
    private const byte SerialCodeGet = 0x15;
    private const byte PowerStateGet = 0x19;
    private const byte TemperatureGet = 0x2F;
    private const byte VolumeGet = 0x45;
    private const byte MuteGet = 0x46;
    private const byte ModelInfoGet = 0xA1;
    private const byte CurrentSourceGet = 0xAD;
    private const byte ModelNumberField = 0x00;
    private const byte FirmwareVersionField = 0x01;
    private const byte BuildDateField = 0x02;
    private const int PollsPerHealthRead = 5;
    private const int MaxMissedReplies = 3;
    private const string NotReadYet = "Not read yet: read while the display is off";
    private const string RejectedCommandIssueKey = "rejected-command";
    private static readonly TimeSpan CommandSpacing = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectedPollDelay = TimeSpan.FromSeconds(1);

    private static readonly PhilipsRead ModelNumberRead = new("model number", [ModelInfoGet, ModelNumberField], "model-read", "Model");
    private static readonly PhilipsRead FirmwareRead = new("firmware version", [ModelInfoGet, FirmwareVersionField], "firmware-read", "Firmware");
    private static readonly PhilipsRead BuildDateRead = new("build date", [ModelInfoGet, BuildDateField], "build-date-read", "Build Date");
    private static readonly PhilipsRead SerialRead = new("serial number", [SerialCodeGet], "serial-read", "Serial Number");
    private static readonly PhilipsRead TemperatureRead = new("temperature", [TemperatureGet], "temperature-read", "Temperature");
    private static readonly PhilipsRead[] IdentityReads = [ModelNumberRead, FirmwareRead, BuildDateRead, SerialRead];
    private static readonly PhilipsRead[] OffReads = [ModelNumberRead, FirmwareRead, BuildDateRead, SerialRead, TemperatureRead];

    private readonly object _readsLock = new();
    private readonly HashSet<PhilipsRead> _owedIdentity = [..IdentityReads];
    private readonly Dictionary<PhilipsRead, int> _missedReplies = new();
    private readonly SentCommandTracker<PhilipsCommand> _sent;
    private readonly ITimer _connectedPollStart;
    private bool _temperatureUnsupported;
    private int _polls;

    private static readonly Dictionary<Input, byte> InputMap = new Dictionary<Input, byte>
    {
        { Input.Hdmi1, 0x0d},
        { Input.Hdmi2, 0x06},
        { Input.Hdmi3, 0x0F},
        { Input.Hdmi4, 0x19},
    };

    public PhilipsSICP(CommunicationClient client, byte monitorId, byte groupId, string name, Input? defaultInput, int pollTime = 23) :
        this(client, monitorId, groupId, name, defaultInput, pollTime, null)
    {
    }

    internal PhilipsSICP(CommunicationClient client, byte monitorId, byte groupId, string name, Input? defaultInput, int pollTime, TimeProvider? timeProvider) : base(
        InputMap.Keys.ToList(), name, defaultInput, client, CommandStringFormat.Hex, pollTime, timeProvider)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _monitorId = monitorId;
        _groupId = groupId;
        _sent = new SentCommandTracker<PhilipsCommand>(CommandSpacing, TimeProvider);
        _connectedPollStart = TimeProvider.CreateTimer(_ => PollWorker.Restart(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (var read in OffReads)
            SetDetail(read.Label, NotReadYet);
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            HandleConnectionState(ConnectionState.Connected);
    }

    protected override void RestartPolling() { }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties())
        {
            _gather.AddRange(response);
            while (_gather.Count > 0)
            {
                if (_gather[0] < MinimumResponseLength)
                {
                    CommunicationState = CommunicationState.Error;
                    LogWarning("The response was too small: {Response}", Convert.ToHexString(_gather.ToArray()));
                    AddEvent(EventType.Error, "The response was too small");
                    _gather.Clear();
                    return;
                }

                if (_gather.Count < _gather[0])
                    return;

                byte[] frame = _gather.GetRange(0, _gather[0]).ToArray();
                _gather.RemoveRange(0, frame.Length);
                ProcessResponse(frame);
            }
        }
    }

    private void ProcessResponse(byte[] response)
    {
        if (GenerateChecksum(response[..^1]) != response[^1])
        {
            CommunicationState = CommunicationState.Error;
            LogWarning("The response checksum is wrong: {Response}", Convert.ToHexString(response));
            AddEvent(EventType.Error, "The response checksum is wrong");
            return;
        }

        if (response[1] != _monitorId)
        {
            LogDebug("Ignored reply for monitor {MonitorId}: {Response}", response[1], Convert.ToHexString(response));
            return;
        }

        CommunicationState = CommunicationState.Okay;
        byte code = response[CommandIndex];
        if (code == CommunicationControl)
        {
            ProcessCommunicationControl(response);
            return;
        }

        var sent = _sent.TakeReply(command => !command.IsSet && command.Data[0] == code);
        using (Trigger.Resume(sent?.TriggerId))
        {
            if (sent?.Command.Read is { } read)
                ShowRead(read, response);
            else
                ProcessReport(response);
        }
    }

    private void ProcessReport(byte[] response)
    {
        switch (response[CommandIndex])
        {
            case PowerStateGet:
                PowerState = response[4] switch
                {
                    0x01 => PowerState.Off,
                    0x02 => PowerState.On,
                    _ => PowerState
                };
                ProcessPowerResponse();
                break;
            case CurrentSourceGet:
                Input = response[4] switch
                {
                    0x0d => Input.Hdmi1,
                    0x06 => Input.Hdmi2,
                    0x0f => Input.Hdmi3,
                    0x19 => Input.Hdmi4,
                    _ => Input
                };
                ProcessInputResponse();
                break;
            case VolumeGet:
                Volume = response[4];
                break;
            case MuteGet:
                AudioMute = response[4] switch
                {
                    0x00 => MuteState.Off,
                    0x01 => MuteState.On,
                    _ => MuteState.Unknown
                };
                break;
            default:
                LogDebug("Unhandled reply {Response}", Convert.ToHexString(response));
                break;
        }
    }

    private void ProcessCommunicationControl(byte[] response)
    {
        switch (response[DataIndex])
        {
            case Acknowledged:
                _sent.TakeReply(command => command.IsSet);
                break;
            case NotAcknowledged:
            case NotAvailable:
                ProcessRejection(response);
                break;
        }
    }

    private void ProcessRejection(byte[] response)
    {
        var sent = _sent.TakeError();
        if (sent == null)
        {
            LogWarning("The display did not accept a command: {Response}", Convert.ToHexString(response));
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            if (sent.Command.Read is not { } read)
            {
                RaiseMomentaryIssue($"The display rejected the command {Convert.ToHexString(sent.Command.Data)} with {Convert.ToHexString(response)}",
                    RejectedCommandIssueKey);
                return;
            }
            if (response[DataIndex] == NotAcknowledged)
            {
                CountMiss(read, $"a NACK {Convert.ToHexString(response)}");
                return;
            }
            StopReading(read);
            LogInformation("The display does not support the {Read} read {Command}: {Response}",
                read.Name, Convert.ToHexString(read.Data), Convert.ToHexString(response));
            SetDetail(read.Label, $"Not supported: {Convert.ToHexString(response)}", DetailTone.Normal);
            ResolveIssue(read.IssueKey);
        }
    }

    private void ShowRead(PhilipsRead read, byte[] response)
    {
        if (read == TemperatureRead)
            ShowTemperature(response);
        else
            ShowText(read, response);
    }

    private void ShowText(PhilipsRead read, byte[] response)
    {
        ReadAnswered(read);
        string text = Encoding.Latin1.GetString(response[DataIndex..^1]);
        SetDetail(read.Label, text.Trim('\0', ' ').Length == 0 ? Convert.ToHexString(response) : text);
        ResolveIssue(read.IssueKey);
    }

    private void ShowTemperature(byte[] response)
    {
        byte[] temperatures = response[DataIndex..^1];
        if (temperatures.Length == 0)
        {
            CountMiss(TemperatureRead, Convert.ToHexString(response));
            return;
        }
        ReadAnswered(TemperatureRead);
        SetDetail(TemperatureRead.Label, string.Join(", ", temperatures.Select(celsius => $"{celsius} °C")));
        ResolveIssue(TemperatureRead.IssueKey);
    }

    private void ReadAnswered(PhilipsRead read)
    {
        lock (_readsLock)
        {
            _owedIdentity.Remove(read);
            _missedReplies.Remove(read);
        }
    }

    private void StopReading(PhilipsRead read)
    {
        ReadAnswered(read);
        if (read == TemperatureRead)
            _temperatureUnsupported = true;
    }

    private void CountMiss(PhilipsRead read, string reply)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(read) + 1;
            _missedReplies[read] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The {Read} read {Command} got {Reply}, asking again on a later poll ({Missed} of {MaxMissedReplies})",
                read.Name, Convert.ToHexString(read.Data), reply, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(read.IssueKey,
            $"The display did not answer the {read.Name} read {Convert.ToHexString(read.Data)} {MaxMissedReplies} times in a row, the last was {reply}",
            IssueSeverity.Minor);
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
                        CountMiss(read, $"no reply within {CommandSpacing.TotalSeconds} s");
                    else
                        LogDebug("No reply to {Command} within {CommandSpacing} s", Convert.ToHexString(missed.Command.Data), CommandSpacing.TotalSeconds);
                }
            }
        }
    }

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
        _temperatureUnsupported = false;
        _polls = 0;
        _connectedPollStart.Change(ConnectedPollDelay, Timeout.InfiniteTimeSpan);
    }

    protected override async Task DoPoll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        CountMissedReplies();
        Query([PowerStateGet]);
        if (PowerState == PowerState.Off)
        {
            await ReadWhileOff(token);
            return;
        }
        if (PowerState != PowerState.On)
            return;
        await Task.Delay(CommandSpacing, TimeProvider, token);
        Query([CurrentSourceGet]);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        Query([VolumeGet]);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        Query([MuteGet]);
    }

    private async Task ReadWhileOff(CancellationToken token)
    {
        await ReadIdentity(token);
        if (_polls++ % PollsPerHealthRead == 0)
            await ReadHealth(token);
    }

    private async Task ReadIdentity(CancellationToken token)
    {
        List<PhilipsRead> owed;
        lock (_readsLock)
            owed = IdentityReads.Where(_owedIdentity.Contains).ToList();
        if (owed.Count == 0)
            return;
        await Task.Delay(CommandSpacing, TimeProvider, token);
        bool modelInfoUnanswered = false;
        foreach (var read in owed)
        {
            // SICP V2.10 p.11: a model information report doesn't say which field it answers
            bool modelInfo = read.Data[0] == ModelInfoGet;
            if (modelInfo && modelInfoUnanswered)
                continue;
            await Read(read, token);
            lock (_readsLock)
                modelInfoUnanswered |= modelInfo && _owedIdentity.Contains(read);
        }
    }

    private async Task ReadHealth(CancellationToken token)
    {
        if (_temperatureUnsupported)
            return;
        await Task.Delay(CommandSpacing, TimeProvider, token);
        await Read(TemperatureRead, token);
    }

    private async Task Read(PhilipsRead read, CancellationToken token)
    {
        Query(read.Data, read);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        CountMissedReplies();
    }

    private void Query(byte[] data, PhilipsRead? read = null) => Send(new PhilipsCommand(data, false, read), PollGroup);

    private void Send(byte[] data) => Send(new PhilipsCommand(data, true, null), _groupId);

    private void Send(PhilipsCommand command, byte groupId)
    {
        byte[] data = command.Data;
        byte messageSize = (byte)(data.Length + 4);
        byte[] payload = new byte[messageSize];

        payload[0] = messageSize;
        payload[1] = _monitorId;
        payload[2] = groupId;
        Array.Copy(data, 0, payload, 3, data.Length);

        byte checksum = GenerateChecksum(payload);
        payload[messageSize - 1] = checksum;
        // SICP V2.10 p9: a command addressed to a group (1-254) gets no ACK or report
        if (groupId == PollGroup)
            Track(command);
        CommunicationClient.Send(payload);
    }

    private void Track(PhilipsCommand command)
    {
        if (_sent.Add(command) is { } dropped)
            LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply",
                Convert.ToHexString(dropped.Command.Data), SentCommandTracker<PhilipsCommand>.MaxCommands);
    }

    private byte GenerateChecksum(byte[] bytes)
    {
        byte checksum = 0x00;
        foreach (var t in bytes)
        {
            checksum ^= t;
        }

        return checksum;
    }

    protected override void DoPowerOn()
    {
        Send([0x18, 0x02]);
    }

    protected override void DoPowerOff()
    {
        Send([0x18, 0x01]);
    }

    protected override void DoSetInput(Input input)
    {
        Send([0xAC, InputMap[input], 0x00, 0x01, 0x00]);
    }

    protected override void DoSetVolume(int percentage)
    {
        Send([0x44, (byte)percentage, (byte)percentage]);
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        Send([0x47, (byte)(state == MuteState.On ? 0x01 : 0x00)]);
    }

    private record PhilipsRead(string Name, byte[] Data, string IssueKey, string Label);

    private sealed record PhilipsCommand(byte[] Data, bool IsSet, PhilipsRead? Read);
}
