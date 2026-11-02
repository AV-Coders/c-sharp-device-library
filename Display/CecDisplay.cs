using AVCoders.Core;
using AVCoders.MediaPlayer;

namespace AVCoders.Display;

public class CecDisplay : Display, ISetTopBox
{
    public const string OsdNameDetailLabel = "OSD name";
    public const string PhysicalAddressDetailLabel = "Physical address";
    public const string VendorDetailLabel = "Vendor";
    public const string CecVersionDetailLabel = "CEC version";

    private readonly SerialClient _cecStream;
    private readonly Func<char[]> _sourcePhysicalAddress;

    private const char SourcePlayBack1 = '\x40';
    private const char SourceUnregistered = '\xF0';
    private const char DestinationTv = '\x00';
    private const char DestinationBroadcast = '\x0F';
    private const char FeatureAbort = '\x00';
    private const char Standby = '\x36';
    private const char UserControlPressed = '\x44';
    private const char UserControlReleased = '\x45';
    private const char GiveOsdName = '\x46';
    private const char SetOsdName = '\x47';
    private const char ActiveSource = '\x82';
    private const char GivePhysicalAddress = '\x83';
    private const char ReportPhysicalAddress = '\x84';
    private const char DeviceVendorId = '\x87';
    private const char GiveDeviceVendorId = '\x8C';
    private const char GiveDevicePowerStatus = '\x8F';
    private const char ReportPowerStatus = '\x90';
    private const char CecVersion = '\x9E';
    private const char GetCecVersion = '\x9F';
    private const char UnrecognisedOpcode = '\x00';
    private const char NotInCorrectMode = '\x01';
    private const char CannotProvideSource = '\x02';
    private const char InvalidOperand = '\x03';
    private const char Refused = '\x04';
    private const char UnableToDetermine = '\x05';
    private static readonly char[] NotSupportedAbortReasons = [UnrecognisedOpcode, CannotProvideSource, InvalidOperand];
    private const int PhysicalAddressLength = 2;
    private const int ShortestReplyLength = 3;
    private const int ReportPhysicalAddressLength = 5;
    private const int DeviceVendorIdLength = 5;
    private const int CecVersionLength = 3;
    private const int FeatureAbortLength = 4;
    private const string OsdNameIssueKey = "osd-name-read";
    private const string PhysicalAddressIssueKey = "physical-address-read";
    private const string VendorIssueKey = "vendor-read";
    private const string CecVersionIssueKey = "cec-version-read";
    private static readonly TimeSpan ActiveSourceSettleTime = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeyPressTime = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan PowerOnSpacing = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ChannelDigitSpacing = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan QueryReplySpacing = TimeSpan.FromMilliseconds(500);
    private const int MaxMissedReplies = 3;
    private const int PollsPerDiscoveryRetry = 10;
    private const string NoReply = "no reply";
    private const string NoAnswerFromUnregistered = "No answer (the TV may ignore requests from address 15)";

    private static readonly DiscoveryRequest[] DiscoveryRequests =
    [
        new(GivePhysicalAddress, "Give Physical Address", PhysicalAddressIssueKey, PhysicalAddressDetailLabel, BroadcastReply: true),
        new(GiveOsdName, "Give OSD Name", OsdNameIssueKey, OsdNameDetailLabel, BroadcastReply: false),
        new(GiveDeviceVendorId, "Give Device Vendor ID", VendorIssueKey, VendorDetailLabel, BroadcastReply: true),
        new(GetCecVersion, "Get CEC Version", CecVersionIssueKey, CecVersionDetailLabel, BroadcastReply: false),
    ];

    private static readonly Dictionary<string, string> TvVendors = new()
    {
        { "00:00:39", "Toshiba" },
        { "00:00:F0", "Samsung" },
        { "00:80:45", "Panasonic" },
        { "00:90:3E", "Philips" },
        { "00:E0:91", "LG" },
        { "08:00:1F", "Sharp" },
        { "08:00:46", "Sony" },
    };

    private readonly char _commandHeader;
    private readonly char _broadcastHeader;
    private readonly char _responseHeader;
    private readonly object _sendLock = new();
    private Task _sends = Task.CompletedTask;
    private readonly object _discoveryLock = new();
    private Task _discoverySends = Task.CompletedTask;
    private CancellationTokenSource _discoveryCancellation = new();
    private readonly HashSet<char> _answeredRequests = [];
    private readonly Dictionary<char, string> _nonAnswers = new();
    private int _discoveryPolls;

    private static readonly Dictionary<char, RemoteButton> NumberpadMap = new()
    {
        { '0', RemoteButton.Button0},
        { '1', RemoteButton.Button1},
        { '2', RemoteButton.Button2},
        { '3', RemoteButton.Button3},
        { '4', RemoteButton.Button4},
        { '5', RemoteButton.Button5},
        { '6', RemoteButton.Button6},
        { '7', RemoteButton.Button7},
        { '8', RemoteButton.Button8},
        { '9', RemoteButton.Button9},
    };

    private static readonly Dictionary<RemoteButton, char> RemoteButtonMap = new()
    {
        { RemoteButton.Button0, '\x20'},
        { RemoteButton.Button1, '\x21'},
        { RemoteButton.Button2, '\x22'},
        { RemoteButton.Button3, '\x23'},
        { RemoteButton.Button4, '\x24'},
        { RemoteButton.Button5, '\x25'},
        { RemoteButton.Button6, '\x26'},
        { RemoteButton.Button7, '\x27'},
        { RemoteButton.Button8, '\x28'},
        { RemoteButton.Button9, '\x29'},
        { RemoteButton.Enter, '\x2B'},
        { RemoteButton.Back, '\x0D' },
        { RemoteButton.Up, '\x01'},
        { RemoteButton.Down, '\x02'},
        { RemoteButton.Left, '\x03'},
        { RemoteButton.Right, '\x04'},
        { RemoteButton.Subtitle, '\x51'},
        { RemoteButton.Power, '\x6B'},
        { RemoteButton.VolumeUp, '\x41'},
        { RemoteButton.VolumeDown, '\x42'},
        { RemoteButton.Mute, '\x43'},
        { RemoteButton.ChannelUp, '\x30'},
        { RemoteButton.ChannelDown, '\x31'},
        { RemoteButton.Play, '\x44'},
        { RemoteButton.Pause, '\x46'},
        { RemoteButton.Stop, '\x45'},
        { RemoteButton.Rewind, '\x48'},
        { RemoteButton.FastForward, '\x49'},
        { RemoteButton.Previous, '\x4C'},
        { RemoteButton.Next, '\x4B'},
        { RemoteButton.Blue, '\x71'},
        { RemoteButton.Yellow, '\x74'},
        { RemoteButton.Green, '\x73'},
        { RemoteButton.Red, '\x72'}
    };

    private static readonly Dictionary<Input, char> InputMap = new Dictionary<Input, char>
    {
        { Input.Hdmi1, '\x10' },
        { Input.Hdmi2, '\x20' },
        { Input.Hdmi3, '\x30' },
        { Input.Hdmi4, '\x40' },
        { Input.DvbtTuner, '\x50' },
        { Input.Network6, '\x60' }
    };

    public CecDisplay(SerialClient cecStream, string name, Func<char[]> sourcePhysicalAddress, char sourceId = SourcePlayBack1, char destinationId = DestinationTv, int pollTime = 23)
        : this(cecStream, name, sourcePhysicalAddress, sourceId, destinationId, pollTime, null)
    {
    }

    internal CecDisplay(SerialClient cecStream, string name, Func<char[]> sourcePhysicalAddress, char sourceId, char destinationId, int pollTime, TimeProvider? timeProvider)
        : base(InputMap.Keys.ToList(), name, null, cecStream, CommandStringFormat.Hex, pollTime, timeProvider)
    {
        _cecStream = cecStream;
        _sourcePhysicalAddress = sourcePhysicalAddress;
        _cecStream.ResponseHandlers += HandleResponse;
        _commandHeader = (char)(sourceId | destinationId);
        _responseHeader = '\x0F';
        _broadcastHeader = (char)(sourceId | DestinationBroadcast);
    }

    private void HandleResponse(string incoming)
    {
        using (PushProperties())
        {
            if (incoming.Length < ShortestReplyLength)
            {
                LogDebug("Ignoring a short reply {Reply}", Convert.ToHexString(incoming.Select(c => (byte)c).ToArray()));
                return;
            }
            if (incoming[0] != _responseHeader)
                return;

            switch (incoming[1])
            {
                case ReportPowerStatus:
                    CommunicationState = CommunicationState.Okay;
                    PowerState = incoming[2] switch
                    {
                        '\x00' => PowerState.On,
                        // Samsung reports 02 (in transition to on) for as long as it is in standby.
                        '\x01' or '\x02' => PowerState.Off,
                        _ => PowerState
                    };
                    ProcessPowerResponse();
                    break;
                case SetOsdName:
                    Answered(GiveOsdName);
                    SetDetail(OsdNameDetailLabel, incoming[2..]);
                    ResolveIssue(OsdNameIssueKey);
                    break;
                case ReportPhysicalAddress:
                    ShowPhysicalAddress(incoming);
                    break;
                case DeviceVendorId:
                    ShowVendor(incoming);
                    break;
                case CecVersion:
                    ShowCecVersion(incoming);
                    break;
                case FeatureAbort:
                    HandleFeatureAbort(incoming);
                    break;
                default:
                    LogDebug("Unhandled CEC frame {Frame}", Describe(incoming));
                    break;
            }
        }
    }

    private void ShowPhysicalAddress(string frame)
    {
        if (frame.Length != ReportPhysicalAddressLength)
        {
            KeepNonAnswer(GivePhysicalAddress, Describe(frame));
            return;
        }
        Answered(GivePhysicalAddress);
        SetDetail(PhysicalAddressDetailLabel, $"{frame[2] >> 4}.{frame[2] & 0x0F}.{frame[3] >> 4}.{frame[3] & 0x0F}");
        ResolveIssue(PhysicalAddressIssueKey);
    }

    private void ShowVendor(string frame)
    {
        if (frame.Length != DeviceVendorIdLength)
        {
            KeepNonAnswer(GiveDeviceVendorId, Describe(frame));
            return;
        }
        Answered(GiveDeviceVendorId);
        string id = Describe(frame[2..]);
        SetDetail(VendorDetailLabel, TvVendors.TryGetValue(id, out var vendor) ? $"{vendor} ({id})" : id);
        ResolveIssue(VendorIssueKey);
    }

    private void ShowCecVersion(string frame)
    {
        if (frame.Length != CecVersionLength)
        {
            KeepNonAnswer(GetCecVersion, Describe(frame));
            return;
        }
        Answered(GetCecVersion);
        SetDetail(CecVersionDetailLabel, frame[2] switch
        {
            '\x00' => "1.1",
            '\x01' => "1.2",
            '\x02' => "1.2a",
            '\x03' => "1.3",
            '\x04' => "1.3a",
            '\x05' => "1.4",
            '\x06' => "2.0",
            _ => $"0x{(int)frame[2]:X2}"
        });
        ResolveIssue(CecVersionIssueKey);
    }

    private void HandleFeatureAbort(string frame)
    {
        if (frame.Length != FeatureAbortLength)
        {
            LogWarning("The TV sent a Feature Abort that is not an opcode and a reason: {Frame}", Describe(frame));
            return;
        }
        string reason = frame[3] switch
        {
            UnrecognisedOpcode => "unrecognised opcode",
            NotInCorrectMode => "not in the correct mode to respond",
            CannotProvideSource => "cannot provide source",
            InvalidOperand => "invalid operand",
            Refused => "refused",
            UnableToDetermine => "unable to determine",
            _ => $"reason 0x{(int)frame[3]:X2}"
        };
        var request = DiscoveryRequests.FirstOrDefault(known => known.Opcode == frame[2]);
        if (request == null)
        {
            LogDebug("Feature abort for opcode 0x{Opcode:X2}: {Reason}", (int)frame[2], reason);
            return;
        }
        CommunicationState = CommunicationState.Okay;
        if (!NotSupportedAbortReasons.Contains(frame[3]))
        {
            KeepNonAnswer(request.Opcode, $"{Describe(frame)} ({reason})");
            return;
        }
        Answered(request.Opcode);
        LogInformation("The TV does not support {Request}: {Reason} ({Frame})", request.Name, reason, Describe(frame));
        SetDetail(request.Label, $"Not supported: {Describe(frame)}", DetailTone.Normal);
        ResolveIssue(request.IssueKey);
    }

    private void Answered(char request)
    {
        bool askedFor;
        lock (_discoveryLock)
        {
            askedFor = _answeredRequests.Add(request);
            _nonAnswers.Remove(request);
        }
        if (askedFor)
            CommunicationState = CommunicationState.Okay;
    }

    private void KeepNonAnswer(char request, string reply)
    {
        LogDebug("The TV gave {Reply}, it counts as no answer at the next poll", reply);
        lock (_discoveryLock)
            _nonAnswers[request] = reply;
    }

    private static string Describe(string frame) => string.Join(':', frame.Select(c => ((int)c).ToString("X2")));

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        Interlocked.Exchange(ref _discoveryCancellation, new CancellationTokenSource()).Cancel();
        if (connectionState != ConnectionState.Connected)
            return;
        lock (_discoveryLock)
        {
            _answeredRequests.Clear();
            _nonAnswers.Clear();
            _discoveryPolls = 0;
        }
        PollWorker.Restart();
    }

    protected override Task DoPoll(CancellationToken token)
    {
        using (PushProperties("DoPoll"))
        {
            var connectionState = CommunicationClient.ConnectionState;
            switch (connectionState)
            {
                case ConnectionState.Disconnected:
                case ConnectionState.Disconnecting:
                case ConnectionState.Error:
                    LogDebug("Not polling, the CEC stream is {ConnectionState}", connectionState);
                    break;
                default:
                    Send(([SourceUnregistered, GiveDevicePowerStatus], TimeSpan.Zero));
                    Discover();
                    break;
            }
        }
        return Task.CompletedTask;
    }

    private void Discover()
    {
        List<(DiscoveryRequest Request, string Result)> unanswered;
        int earlierPolls;
        lock (_discoveryLock)
        {
            unanswered = DiscoveryRequests.Where(request => !_answeredRequests.Contains(request.Opcode))
                .Select(request => (request, _nonAnswers.GetValueOrDefault(request.Opcode, NoReply))).ToList();
            if (unanswered.Count == 0)
                return;
            earlierPolls = _discoveryPolls++;
        }
        if (earlierPolls >= MaxMissedReplies)
        {
            foreach (var (request, result) in unanswered)
            {
                if (request.BroadcastReply)
                    RaiseOngoingIssue(request.IssueKey,
                        $"The TV did not give a real reply to {request.Name} {MaxMissedReplies} times in a row, the last was {result}", IssueSeverity.Minor);
                else
                    SetDetail(request.Label, $"{NoAnswerFromUnregistered}: {result}");
            }
            if (earlierPolls % PollsPerDiscoveryRetry != 0)
                return;
        }
        else if (earlierPolls > 0)
        {
            LogInformation("The TV gave {Results}, asking again ({Missed} of {MaxMissedReplies})",
                string.Join(", ", unanswered.Select(entry => $"{entry.Result} to {entry.Request.Name}")), earlierPolls, MaxMissedReplies);
        }
        var frames = unanswered.Select(entry => new[] { SourceUnregistered, entry.Request.Opcode }).ToArray();
        lock (_discoveryLock)
        {
            foreach (var (request, _) in unanswered)
                _nonAnswers.Remove(request.Opcode);
            var token = _discoveryCancellation.Token;
            _discoverySends = _discoverySends.ContinueWith(_ => SendBetweenCommands(frames, token), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SendBetweenCommands(char[][] frames, CancellationToken token)
    {
        try
        {
            foreach (var frame in frames)
            {
                await Task.Delay(QueryReplySpacing, TimeProvider, token);
                await SendWhenNoCommandIsWaiting(frame, token);
            }
        }
        catch (OperationCanceledException)
        {
            LogDebug("Dropped the identity requests still waiting to go out, the connection changed");
        }
        catch (Exception e)
        {
            LogException(e, "Sending the identity requests failed");
        }
    }

    private async Task SendWhenNoCommandIsWaiting(char[] frame, CancellationToken token)
    {
        while (true)
        {
            Task commands;
            lock (_sendLock)
            {
                token.ThrowIfCancellationRequested();
                if (_sends.IsCompleted)
                {
                    Send((frame, TimeSpan.Zero));
                    return;
                }
                commands = _sends;
            }
            await commands;
        }
    }

    private void Send(params (char[] Frame, TimeSpan SpacingAfter)[] frames)
    {
        lock (_sendLock)
            _sends = _sends.ContinueWith(_ => SendSpaced(frames), TaskScheduler.Default).Unwrap();
    }

    private async Task SendSpaced((char[] Frame, TimeSpan SpacingAfter)[] frames)
    {
        try
        {
            foreach (var (frame, spacingAfter) in frames)
            {
                _cecStream.Send(frame);
                await Task.Delay(spacingAfter, TimeProvider);
            }
        }
        catch (Exception e)
        {
            CommunicationState = CommunicationState.Error;
            LogException(e, "Sending CEC frames failed");
        }
    }

    private (char[] Frame, TimeSpan SpacingAfter)[] KeyPress(char command, TimeSpan spacingAfterRelease) =>
    [
        ([_commandHeader, UserControlPressed, command], KeyPressTime),
        ([_commandHeader, UserControlReleased], spacingAfterRelease)
    ];

    private void RemoteControlPassthrough(char command) => Send(KeyPress(command, TimeSpan.Zero));

    protected override void DoPowerOn()
    {
        // One touch play - Image view on
        Send([..KeyPress('\x6D', PowerOnSpacing), ([_commandHeader, '\x04'], TimeSpan.Zero)]);
    }

    protected override void DoPowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            var physicalAddress = _sourcePhysicalAddress();
            if (physicalAddress.Length == PhysicalAddressLength)
            {
                // Samsung ignores Standby from a source that isn't the active source.
                Send(([_broadcastHeader, ActiveSource, physicalAddress[0], physicalAddress[1]], ActiveSourceSettleTime),
                    ([_commandHeader, Standby], TimeSpan.Zero));
                return;
            }
            LogWarning("The source physical address is unknown, sending Standby without claiming active source");
            AddEvent(EventType.Error, "Source physical address unknown, Standby sent without claiming active source");
            Send(([_commandHeader, Standby], TimeSpan.Zero));
        }
    }

    protected override void DoSetInput(Input input) => Send(([_commandHeader, ActiveSource, InputMap[input], '\x00'], TimeSpan.Zero));

    protected override void DoSetVolume(int percentage)
    {
        // scale to a value between 0-127
        char volume = (char)(percentage * 1.27);
        Send(([_commandHeader, '\x7A', volume], TimeSpan.Zero));
        DesiredAudioMute = MuteState.Off;
        AudioMute = MuteState.Off;
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        switch (state)
        {
            case MuteState.On:
                RemoteControlPassthrough('\x65');
                break;
            case MuteState.Off:
                RemoteControlPassthrough('\x66');
                break;
        }

    }

    public void ChannelUp() => RemoteControlPassthrough('\x30');

    public void ChannelDown() => RemoteControlPassthrough('\x31');

    public IReadOnlyCollection<RemoteButton> SupportedButtons => RemoteButtonMap.Keys;

    public void SendIRCode(RemoteButton button)
    {
        using (PushProperties("SendIRCode"))
        {
            if (!RemoteButtonMap.TryGetValue(button, out var code))
            {
                LogWarning("Unsupported button - {UnsupportedRemoteButton}", button.ToString());
                AddEvent(EventType.Error, $"Unsupported button - {button.ToString()}");
                return;
            }

            RemoteControlPassthrough(code);
        }
    }

    public void SetChannel(int channel)
    {
        if (channel < 0)
        {
            using (PushProperties("SetChannel"))
                LogWarning("Channel {Channel} can't be sent: CEC has no key for '-'", channel);
            AddEvent(EventType.Error, $"Channel {channel} can't be sent: CEC has no key for '-'");
            return;
        }
        Send(channel.ToString().SelectMany(digit => KeyPress(RemoteButtonMap[NumberpadMap[digit]], ChannelDigitSpacing)).ToArray());
    }

    public void ToggleSubtitles()
    {
        using (PushProperties("ToggleSubtitles"))
            LogWarning("ToggleSubtitles is not supported");
        AddEvent(EventType.Error, "ToggleSubtitles is not supported");
    }

    private record DiscoveryRequest(char Opcode, string Name, string IssueKey, string Label, bool BroadcastReply);
}
