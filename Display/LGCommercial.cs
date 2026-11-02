using System.Globalization;
using System.Net;
using AVCoders.Core;
using AVCoders.MediaPlayer;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.Display;

public class LGCommercial : Display, ISetTopBox
{
    // Source:
    // https://www.lg.com/ca_en/support/product-support/troubleshoot/help-library/cs-CT52001643-20153058982994/
    public static readonly ushort DefaultPort = 9761;
    private readonly byte[]? _wolPacket;
    private readonly int _setId;
    private const string PollArgument = "FF";
    private readonly string _powerHeader = "ka";
    private readonly string _inputHeader = "xb";
    private readonly string _volumeHeader = "kf";
    private readonly string _muteHeader = "ke";
    private readonly string _irccHeader = "mc";
    private readonly string _channelHeader = "ma";
    private const string SignalStatusArgument = "02 FF";
    private const char ReplyTerminator = 'x';
    private const char PowerReply = 'a';
    private const char InputReply = 'b';
    private const char VolumeReply = 'f';
    private const char MuteReply = 'e';
    private const char SerialReply = 'y';
    private const char SoftwareVersionReply = 'z';
    private const char TemperatureReply = 'n';
    private const char StatusReply = 'v';
    private const string Acknowledged = "OK";
    private const string Rejected = "NG";
    private const string NoSignalStatus = "0200";
    private const string SignalPresentStatus = "0201";
    private const int GatherLimit = 1024;
    private const int PollsPerHealthRead = 5;
    private const int MaxMissedReplies = 3;
    private const string NoReply = "no reply";
    private const string NotReadYet = "Not read yet: read while the display is off";
    private static readonly TimeSpan CommandSpacing = TimeSpan.FromSeconds(1);
    private const string WakeOnLanIssueKey = "wake-on-lan";
    private static readonly LgRead SerialRead = new("serial number", "fy", PollArgument, SerialReply, "serial-read", "Serial Number");
    private static readonly LgRead SoftwareVersionRead = new("software version", "fz", PollArgument, SoftwareVersionReply, "software-version-read", "Software Version");
    private static readonly LgRead TemperatureRead = new("temperature", "dn", PollArgument, TemperatureReply, "temperature-read", "Temperature");
    private static readonly LgRead SignalRead = new("signal status", "sv", SignalStatusArgument, StatusReply, "signal-read", "Signal");
    private static readonly LgRead[] IdentityReads = [SerialRead, SoftwareVersionRead];
    private static readonly LgRead[] OffReads = [SerialRead, SoftwareVersionRead, TemperatureRead, SignalRead];
    private string _gather = "";
    private readonly object _readsLock = new();
    private readonly HashSet<LgRead> _owedIdentity = [..IdentityReads];
    private readonly Dictionary<LgRead, int> _missedReplies = new();
    private readonly SentCommandTracker<LgRead> _sent;
    private bool _temperatureUnsupported;
    private bool _signalUnsupported;
    private int _polls;
    private static readonly Dictionary<Input, string> InputDictionary = new()
    {
        { Input.Hdmi1, "90" },
        { Input.Hdmi2, "91" },
        { Input.Hdmi3, "92" },
        { Input.Hdmi4, "93" },
        { Input.DvbtTuner, "00" }
    };
    private static readonly Dictionary<MuteState, string> MuteDictionary = new()
    {
        { MuteState.On, "00" },
        { MuteState.Off, "01" }
    };

    private static readonly Dictionary<RemoteButton, string> RemoteButtonMap = new()
    {
        { RemoteButton.Button0, "10"},
        { RemoteButton.Button1, "11"},
        { RemoteButton.Button2, "12"},
        { RemoteButton.Button3, "13"},
        { RemoteButton.Button4, "14"},
        { RemoteButton.Button5, "15"},
        { RemoteButton.Button6, "16"},
        { RemoteButton.Button7, "17"},
        { RemoteButton.Button8, "18"},
        { RemoteButton.Button9, "19"},
        { RemoteButton.Enter, "44"},
        { RemoteButton.Up, "40"},
        { RemoteButton.Down, "41"},
        { RemoteButton.Left, "07"},
        { RemoteButton.Right, "06"},
        { RemoteButton.Subtitle, "39"},
        { RemoteButton.Back, "5B"},
        { RemoteButton.Power, "08"},
        { RemoteButton.VolumeUp, "02"},
        { RemoteButton.VolumeDown, "03"},
        { RemoteButton.Mute, "09"},
        { RemoteButton.ChannelUp, "00"},
        { RemoteButton.ChannelDown, "01"},
        { RemoteButton.Play, "B0"},
        { RemoteButton.Pause, "01"},
        { RemoteButton.Stop, "B1"},
        { RemoteButton.Rewind, "8F"},
        { RemoteButton.FastForward, "8E"},
        { RemoteButton.Previous, "8F"},
        { RemoteButton.Next, "8E"},
        { RemoteButton.Home, "42"},
        { RemoteButton.Blue, "61"},
        { RemoteButton.Yellow, "63"},
        { RemoteButton.Green, "71"},
        { RemoteButton.Red, "73"},
        { RemoteButton.Guide, "AB"},
        { RemoteButton.Menu, "43"},
    };

    public LGCommercial(CommunicationClient comms, string name, string? mac, Input? defaultInput, int setId = 1) :
        this(comms, name, mac, defaultInput, setId, null)
    {
    }

    internal LGCommercial(CommunicationClient comms, string name, string? mac, Input? defaultInput, int setId, TimeProvider? timeProvider) :
        base(InputDictionary.Keys.ToList(), name, defaultInput, comms, CommandStringFormat.Ascii, 12, timeProvider)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        _setId = setId;
        _sent = new SentCommandTracker<LgRead>(CommandSpacing, TimeProvider);
        if (mac != null)
            _wolPacket = TryBuildWolPacket(mac);
        foreach (var read in OffReads)
            SetDetail(read.Label, NotReadYet);
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
        _signalUnsupported = false;
        _polls = 0;
        PollWorker.Restart();
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            _gather += response;
            int end;
            while ((end = _gather.IndexOf(ReplyTerminator)) >= 0)
            {
                string reply = _gather[..(end + 1)];
                _gather = _gather[(end + 1)..];
                ProcessResponse(reply);
            }

            if (_gather.Length > GatherLimit)
            {
                LogWarning("Gather buffer exceeded {GatherLimit} characters, clearing {Gather}", GatherLimit, _gather);
                _gather = "";
            }
        }
    }

    private void ProcessResponse(string response)
    {
        string setId = $" {_setId:d2} ";
        int setIdAt = response.IndexOf(setId, StringComparison.Ordinal);
        int statusAt = setIdAt + setId.Length;
        if (setIdAt < 1 || response.Length < statusAt + Acknowledged.Length + 1)
            return;
        char command = response[setIdAt - 1];
        string status = response.Substring(statusAt, Acknowledged.Length);
        string data = response[(statusAt + Acknowledged.Length)..^1];

        if (status == Rejected)
        {
            ProcessRejection(command, response);
            return;
        }
        if (status != Acknowledged)
            return;
        CommunicationState = CommunicationState.Okay;

        switch (command)
        {
            case PowerReply:
                PowerState = data switch
                {
                    "01" => PowerState.On,
                    "00" => PowerState.Off,
                    _ => PowerState
                };
                ProcessPowerResponse();
                break;
            case InputReply:
                Input = data switch
                {
                    "90" => Input.Hdmi1,
                    "91" => Input.Hdmi2,
                    "92" => Input.Hdmi3,
                    "93" => Input.Hdmi4,
                    "00" => Input.DvbtTuner,
                    _ => Input
                };
                ProcessInputResponse();
                break;
            case VolumeReply:
                if (data.Length < 2 || !int.TryParse(data[..2], NumberStyles.HexNumber, null, out var volumeLevel))
                {
                    LogWarning("Unreadable volume in {Response}", response);
                    return;
                }
                Volume = volumeLevel;
                break;
            case MuteReply:
                AudioMute = data switch
                {
                    "01" => MuteState.Off,
                    "00" => MuteState.On,
                    _ => AudioMute
                };
                break;
            case SerialReply:
            case SoftwareVersionReply:
            case TemperatureReply:
            case StatusReply:
                ProcessReadReply(command, data, response);
                break;
        }
    }

    private void ProcessReadReply(char reply, string data, string response)
    {
        var sent = _sent.TakeReply(read => read.Reply == reply);
        if (sent == null)
        {
            LogDebug("Ignored a reply that no read of ours is waiting for: {Response}", response);
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            switch (reply)
            {
                case SerialReply:
                    ShowSerial(data, response);
                    break;
                case SoftwareVersionReply:
                    ShowSoftwareVersion(data, response);
                    break;
                case TemperatureReply:
                    ShowTemperature(data, response);
                    break;
                case StatusReply:
                    ShowSignal(data, response);
                    break;
            }
        }
    }

    private void ProcessRejection(char command, string response)
    {
        switch (command)
        {
            case SerialReply:
            case SoftwareVersionReply:
            case TemperatureReply:
            case StatusReply:
                ProcessReadRejection(command, response);
                break;
            default:
                CommunicationState = CommunicationState.Error;
                break;
        }
    }

    private void ProcessReadRejection(char reply, string response)
    {
        var sent = _sent.TakeReply(read => read.Reply == reply);
        if (sent == null)
        {
            LogDebug("Ignored a rejection that no read of ours is waiting for: {Response}", response);
            return;
        }

        using (Trigger.Resume(sent.TriggerId))
        {
            switch (reply)
            {
                case TemperatureReply:
                    _temperatureUnsupported = true;
                    break;
                case StatusReply:
                    _signalUnsupported = true;
                    break;
            }
            ShowNotSupported(sent.Command, response);
        }
    }

    private void ShowNotSupported(LgRead read, string response)
    {
        ReadAnswered(read);
        LogInformation("The display does not support the {Read} query {Query}: {Response}", read.Name, QueryText(read), response);
        SetDetail(read.Label, $"Not supported: {response}", DetailTone.Normal);
        ResolveIssue(read.IssueKey);
    }

    private void ShowSerial(string serial, string response)
    {
        ReadAnswered(SerialRead);
        SetDetail(SerialRead.Label, serial.Length == 0 ? response : serial);
        ResolveIssue(SerialRead.IssueKey);
    }

    private void ShowSoftwareVersion(string version, string response)
    {
        ReadAnswered(SoftwareVersionRead);
        SetDetail(SoftwareVersionRead.Label, version.Length == 0 ? response : version);
        ResolveIssue(SoftwareVersionRead.IssueKey);
    }

    private void ShowTemperature(string temperature, string response)
    {
        if (!int.TryParse(temperature, NumberStyles.HexNumber, null, out var celsius))
        {
            CountMiss(TemperatureRead, response);
            return;
        }
        ReadAnswered(TemperatureRead);
        SetDetail(TemperatureRead.Label, $"{celsius} °C");
        ResolveIssue(TemperatureRead.IssueKey);
    }

    private void ShowSignal(string status, string response)
    {
        string compact = status.Replace(" ", "");
        if (compact.Length == 0 || !compact.All(Uri.IsHexDigit))
        {
            CountMiss(SignalRead, response);
            return;
        }
        ReadAnswered(SignalRead);
        SetDetail(SignalRead.Label, compact switch
        {
            NoSignalStatus => "No signal",
            SignalPresentStatus => "Present",
            _ => status
        });
        ResolveIssue(SignalRead.IssueKey);
    }

    private void ReadAnswered(LgRead read)
    {
        lock (_readsLock)
        {
            _owedIdentity.Remove(read);
            _missedReplies.Remove(read);
        }
    }

    private void CountMiss(LgRead read, string result)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(read) + 1;
            _missedReplies[read] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The {Read} query {Query} got {Result}, asking again on a later poll ({Missed} of {MaxMissedReplies})",
                read.Name, QueryText(read), result, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(read.IssueKey,
            $"The display did not give a real reply to the {read.Name} query {QueryText(read)} {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private string QueryText(LgRead read) => $"{read.Header} {_setId:d2} {read.Argument}";

    private void SendCommand(string header, string value) => CommunicationClient.Send($"{header} {_setId:d2} {value}\r");

    protected override async Task DoPoll(CancellationToken token)
    {
        // PowerState is only updated from ka poll responses (see ProcessResponse) - a
        // dropped connection does not mean the display is off, and a display with
        // PM Mode set to Screen Off Always stays connected while off.
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
        {
            // A display in deep standby (PM Mode: Power Off) drops the connection when
            // off, so a pending power-on can only be honoured by continuing to wake it.
            if (DesiredPowerState == PowerState.On)
            {
                RaiseOngoingIssue(PowerStateIssueKey, "Power should be On but the display is not connected");
                SendWol();
            }
            return;
        }

        SendCommand(_powerHeader, PollArgument);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        SendCommand(_inputHeader, PollArgument);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        SendCommand(_volumeHeader, PollArgument);
        await Task.Delay(CommandSpacing, TimeProvider, token);
        SendCommand(_muteHeader,  PollArgument);

        if (PowerState == PowerState.Off)
            await ReadWhileOff(token);
    }

    private async Task ReadWhileOff(CancellationToken token)
    {
        await ReadIdentity(token);
        if (_polls++ % PollsPerHealthRead == 0)
            await ReadHealth(token);
    }

    private async Task ReadIdentity(CancellationToken token)
    {
        List<LgRead> owed;
        lock (_readsLock)
            owed = IdentityReads.Where(_owedIdentity.Contains).ToList();
        foreach (var read in owed)
            await Read(read, token);
    }

    private async Task ReadHealth(CancellationToken token)
    {
        if (!_temperatureUnsupported)
            await Read(TemperatureRead, token);
        if (!_signalUnsupported)
            await Read(SignalRead, token);
    }

    private async Task Read(LgRead read, CancellationToken token)
    {
        await Task.Delay(CommandSpacing, TimeProvider, token);
        if (_sent.Add(read) is { } dropped)
            LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply", QueryText(dropped.Command), SentCommandTracker<LgRead>.MaxCommands);
        SendCommand(read.Header, read.Argument);
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

    private void SendWol()
    {
        if (_wolPacket == null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new UdpClient();
                for (int i = 0; i < 3; i++)
                {
                    client.Send(_wolPacket, new IPEndPoint(IPAddress.Broadcast, 7));
                    await Task.Delay(75);
                    client.Send(_wolPacket, new IPEndPoint(IPAddress.Broadcast, 9));
                    await Task.Delay(300);
                }
            }
            catch (Exception e)
            {
                LogException(e, "Failed to send the Wake-on-LAN packets");
            }
        });
    }

    private byte[] BuildMagicPacket(byte[] macAddress)
    {
        if (macAddress.Length != 6) throw new ArgumentException();

        List<byte> magic = [];
        for (int i = 0; i < 6; i++)
        {
            magic.Add(0xff);
        }

        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                magic.Add(macAddress[j]);
            }
        }
        return magic.ToArray();
    }

    // Accepts AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, aabb.ccdd.eeff and bare AABBCCDDEEFF
    // forms. A bad MAC disables Wake-on-LAN with a logged error instead of throwing out
    // of the constructor during program startup.
    private byte[]? TryBuildWolPacket(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
        {
            using (PushProperties("TryBuildWolPacket"))
                LogError("'{Mac}' is not a valid MAC address, Wake-on-LAN is disabled for this display", mac);
            AddEvent(EventType.Error, $"'{mac}' is not a valid MAC address, Wake-on-LAN is disabled");
            RaiseOngoingIssue(WakeOnLanIssueKey, $"'{mac}' is not a valid MAC address, Wake-on-LAN is disabled");
            return null;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return BuildMagicPacket(bytes);
    }

    protected override void DoPowerOn()
    {
        SendCommand(_powerHeader, "01");
        SendWol();
    }

    protected override void DoPowerOff() => SendCommand(_powerHeader, "00");

    protected override void DoSetInput(Input input) => SendCommand(_inputHeader, InputDictionary[input]);

    protected override void DoSetVolume(int percentage) => SendCommand(_volumeHeader, $"{percentage:x2}");

    protected override void DoSetAudioMute(MuteState state) => SendCommand(_muteHeader, MuteDictionary[state]);

    public void ChannelUp() => SendCommand(_irccHeader, "00");

    public void ChannelDown() => SendCommand(_irccHeader, "01");

    public IReadOnlyCollection<RemoteButton> SupportedButtons => RemoteButtonMap.Keys;

    public void SendIRCode(RemoteButton button)
    {
        if (!RemoteButtonMap.TryGetValue(button, out var code))
        {
            using (PushProperties("SendIRCode"))
                LogWarning("Unsupported button - {UnsupportedRemoteButton}", button.ToString());
            AddEvent(EventType.Error, $"Unsupported button - {button.ToString()}");
            return;
        }

        SendCommand(_irccHeader, code);

        if (button == RemoteButton.Power)
            DesiredPowerState = PowerState.Unknown;
    }

    public void SetChannel(int channel)
    {
        if (channel < 0)
        {
            using (PushProperties("SetChannel"))
                LogWarning("Channel {Channel} is negative", channel);
            AddEvent(EventType.Error, $"Channel {channel} is negative");
            return;
        }
        SendCommand(_channelHeader, $"00 {channel:X2} 10");
    }

    public void ToggleSubtitles()
    {
        SendIRCode(RemoteButton.Subtitle);
        Thread.Sleep(100);
        SendIRCode(RemoteButton.Subtitle);
        Thread.Sleep(100);
        SendIRCode(RemoteButton.Back);
    }

    private record LgRead(string Name, string Header, string Argument, char Reply, string IssueKey, string Label);
}
