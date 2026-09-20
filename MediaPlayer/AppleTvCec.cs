using System.Diagnostics;
using System.Text;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

/// <summary>Controls an Apple TV over HDMI-CEC from the TV side of the link (logical address 0), e.g. through a DM-NVX HDMI input.</summary>
/// <remarks>
/// Power, navigation, digit and transport keys are supported; see <see cref="SupportedButtons"/>.
/// Display, TopMenu, PopupMenu, Volume, Mute, Guide, Channel, Eject and the colour keys are not, and tvOS does not report Deck Status.
/// A sleep or wake from the physical remote is adopted as the desired power state rather than reversed.
/// </remarks>
public class AppleTvCec : MediaPlayer, ISetTopBox
{
    public const byte LogicalAddressPlayback1 = 0x04;
    public const byte LogicalAddressPlayback2 = 0x08;
    public const byte LogicalAddressPlayback3 = 0x0B;

    public const string OsdNameDetailLabel = "OSD name";
    public const string PhysicalAddressDetailLabel = "Physical address";
    public const string VendorDetailLabel = "Vendor";
    public const string CecVersionDetailLabel = "CEC version";
    public const string MenuDetailLabel = "Menu";
    public const string ActiveSourceDetailLabel = "Active source";

    private const char LogicalAddressTv = '\x00';
    private const char TvBroadcastHeader = '\x0F';

    private const char FeatureAbort = '\x00';
    private const char ImageViewOn = '\x04';
    private const char TextViewOn = '\x0D';
    private const char GiveDeckStatus = '\x1A';
    private const char DeckStatus = '\x1B';
    private const char Standby = '\x36';
    private const char UserControlPressed = '\x44';
    private const char UserControlReleased = '\x45';
    private const char GiveOsdName = '\x46';
    private const char SetOsdName = '\x47';
    private const char ActiveSource = '\x82';
    private const char GivePhysicalAddress = '\x83';
    private const char ReportPhysicalAddress = '\x84';
    private const char RequestActiveSource = '\x85';
    private const char SetStreamPath = '\x86';
    private const char DeviceVendorId = '\x87';
    private const char GiveDeviceVendorId = '\x8C';
    private const char MenuStatus = '\x8E';
    private const char GiveDevicePowerStatus = '\x8F';
    private const char ReportPowerStatus = '\x90';
    private const char InactiveSource = '\x9D';
    private const char CecVersion = '\x9E';
    private const char GetCecVersion = '\x9F';

    private const char CecVersion14 = '\x05';
    private const char DeckStatusRequestOnce = '\x03';
    private const char PowerOnFunctionKey = '\x6D';
    public const int MaxUnansweredPolls = 3;
    public static readonly TimeSpan KeyHold = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MinimumFrameSpacing = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan QueryReplySpacing = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultDuplicateResponseWindow = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<char, RemoteButton> NumberpadMap = new()
    {
        { '0', RemoteButton.Button0 },
        { '1', RemoteButton.Button1 },
        { '2', RemoteButton.Button2 },
        { '3', RemoteButton.Button3 },
        { '4', RemoteButton.Button4 },
        { '5', RemoteButton.Button5 },
        { '6', RemoteButton.Button6 },
        { '7', RemoteButton.Button7 },
        { '8', RemoteButton.Button8 },
        { '9', RemoteButton.Button9 },
    };

    private static readonly Dictionary<RemoteButton, char> RemoteButtonMap = new()
    {
        { RemoteButton.Enter, '\x00' },
        { RemoteButton.Up, '\x01' },
        { RemoteButton.Down, '\x02' },
        { RemoteButton.Left, '\x03' },
        { RemoteButton.Right, '\x04' },
        { RemoteButton.Home, '\x09' },
        { RemoteButton.Menu, '\x0D' },
        { RemoteButton.Back, '\x0D' },
        { RemoteButton.Button0, '\x20' },
        { RemoteButton.Button1, '\x21' },
        { RemoteButton.Button2, '\x22' },
        { RemoteButton.Button3, '\x23' },
        { RemoteButton.Button4, '\x24' },
        { RemoteButton.Button5, '\x25' },
        { RemoteButton.Button6, '\x26' },
        { RemoteButton.Button7, '\x27' },
        { RemoteButton.Button8, '\x28' },
        { RemoteButton.Button9, '\x29' },
        { RemoteButton.Play, '\x44' },
        { RemoteButton.Stop, '\x45' },
        { RemoteButton.Pause, '\x46' },
        { RemoteButton.Rewind, '\x48' },
        { RemoteButton.FastForward, '\x49' },
        { RemoteButton.Next, '\x4B' },
        { RemoteButton.Previous, '\x4C' },
        { RemoteButton.Subtitle, '\x51' },
    };

    private static readonly RemoteButton[] PowerButtons =
        [RemoteButton.Power, RemoteButton.PowerOn, RemoteButton.PowerOff];

    private static readonly IReadOnlyCollection<RemoteButton> SupportedButtonList =
        RemoteButtonMap.Keys.Concat(PowerButtons).ToList().AsReadOnly();

    public BoolHandler? ActiveSourceHandlers;

    private readonly SerialClient _cecStream;
    private readonly char _commandHeader;
    private readonly char _responseHeader;
    private readonly char _deviceBroadcastHeader;
    private readonly bool _answerTvQueries;
    private readonly ThreadWorker? _pollWorker;
    private readonly object _responseLock = new();
    private readonly object _sendLock = new();
    private long _lastSendTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
    private TimeSpan _spacingAfterLastSend = TimeSpan.Zero;

    private char[] _physicalAddress = ['\x10', '\x00'];
    private bool _discovered;
    private bool _isActiveSource;
    private int _unansweredPolls;
    private readonly TimeSpan _duplicateResponseWindow;
    private string _lastResponse = string.Empty;
    private DateTimeOffset _lastResponseTime = DateTimeOffset.MinValue;

    /// <summary>True while the Apple TV reports itself as the active source on the HDMI link.</summary>
    public bool IsActiveSource
    {
        get => _isActiveSource;
        private set
        {
            if (_isActiveSource == value)
                return;
            _isActiveSource = value;
            SetDetail(ActiveSourceDetailLabel, value ? "Yes" : "No");
            AddEvent(EventType.Input, value ? "Active source" : "Inactive source");
            ActiveSourceHandlers?.Invoke(value);
        }
    }

    /// <summary>The Apple TV's last reported physical address, e.g. "1.0.0.0".</summary>
    public string PhysicalAddress => FormatPhysicalAddress(_physicalAddress);

    /// <summary>A pollTime of 0 or less disables polling and the discovery that runs with the first poll.</summary>
    public AppleTvCec(SerialClient cecStream, string name, byte logicalAddress = LogicalAddressPlayback1,
        bool answerTvQueries = true, int pollTime = 23, TimeSpan? duplicateResponseWindow = null)
        : base(name, cecStream)
    {
        if (logicalAddress > 0x0E)
            throw new ArgumentOutOfRangeException(nameof(logicalAddress), "CEC logical addresses are 0 to 14");
        _cecStream = cecStream;
        _duplicateResponseWindow = duplicateResponseWindow ?? DefaultDuplicateResponseWindow;
        _answerTvQueries = answerTvQueries;
        _commandHeader = (char)((LogicalAddressTv << 4) | logicalAddress);
        _responseHeader = (char)((logicalAddress << 4) | LogicalAddressTv);
        _deviceBroadcastHeader = (char)((logicalAddress << 4) | 0x0F);
        _cecStream.ResponseHandlers += HandleResponse;
        _cecStream.ConnectionStateHandlers += HandleConnectionState;
        CommunicationState = CommunicationState.NotAttempted;
        if (pollTime <= 0)
            return;
        var pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        _pollWorker = pollWorker;
        new Thread(_ =>
        {
            Thread.Sleep(1000);
            pollWorker.Restart();
        }) { IsBackground = true }.Start();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        _discovered = false;
        _pollWorker?.Restart();
    }

    private Task Poll(CancellationToken token)
    {
        PollPowerStatus();
        return Task.CompletedTask;
    }

    /// <summary>Runs discovery if it has not completed, then asks for the power status.</summary>
    public void PollPowerStatus()
    {
        using (PushProperties("PollPowerStatus"))
        {
            if (!_discovered)
                Discover();
            if (_unansweredPolls >= MaxUnansweredPolls && CommunicationState != CommunicationState.Error)
            {
                LogWarning("No answer to the last {Count} power status polls", _unansweredPolls);
                CommunicationState = CommunicationState.Error;
                PowerState = PowerState.Unknown;
            }
            _unansweredPolls++;
            SendFrame([_commandHeader, GiveDevicePowerStatus], QueryReplySpacing);
        }
    }

    /// <summary>Asks for the physical address, OSD name, vendor ID, CEC version, active source and deck status; the answers appear in Details.</summary>
    public void Discover()
    {
        using (PushProperties("Discover"))
        {
            LogDebug("Sending CEC discovery requests");
            SendFrame([_commandHeader, GivePhysicalAddress], QueryReplySpacing);
            SendFrame([_commandHeader, GiveOsdName], QueryReplySpacing);
            SendFrame([_commandHeader, GiveDeviceVendorId], QueryReplySpacing);
            SendFrame([_commandHeader, GetCecVersion], QueryReplySpacing);
            SendFrame([TvBroadcastHeader, RequestActiveSource], QueryReplySpacing);
            SendFrame([_commandHeader, GiveDeckStatus, DeckStatusRequestOnce], QueryReplySpacing);
        }
    }

    private void HandleResponse(string incoming)
    {
        using (PushProperties("HandleResponse"))
        {
            bool isRepeat;
            lock (_responseLock)
            {
                var now = DateTimeOffset.UtcNow;
                isRepeat = incoming == _lastResponse;
                if (isRepeat && now - _lastResponseTime < _duplicateResponseWindow)
                    return;
                _lastResponse = incoming;
                _lastResponseTime = now;
            }

            var frame = StripRepeatedHeader(incoming);
            if (frame.Length < 1)
                return;
            var header = frame[0];
            if (header != _responseHeader && header != _deviceBroadcastHeader)
                return;
            if (frame.Length < 2)
                return;

            CommunicationState = CommunicationState.Okay;
            _unansweredPolls = 0;

            if (isRepeat && IsPhysicalRemoteIntent(header, frame[1]))
            {
                LogDebug("Ignoring replayed frame: {Frame}", Describe(frame));
                return;
            }

            switch (frame[1])
            {
                case ReportPowerStatus when frame.Length >= 3:
                    var reported = frame[2] switch
                    {
                        '\x00' => PowerState.On,
                        '\x01' => PowerState.Off,
                        '\x02' => PowerState.Warming,
                        '\x03' => PowerState.Cooling,
                        _ => PowerState
                    };
                    if (header == _deviceBroadcastHeader && reported is PowerState.On or PowerState.Off)
                        DesiredPowerState = reported;
                    PowerState = reported;
                    ProcessPowerState();
                    break;
                case Standby:
                    DesiredPowerState = PowerState.Off;
                    PowerState = PowerState.Off;
                    IsActiveSource = false;
                    ResolveIssue(PowerStateIssueKey);
                    break;
                case InactiveSource:
                    DesiredPowerState = PowerState.Off;
                    IsActiveSource = false;
                    ResolveIssue(PowerStateIssueKey);
                    break;
                case ActiveSource when frame.Length >= 4:
                    IsActiveSource = true;
                    break;
                case ImageViewOn:
                case TextViewOn:
                    DesiredPowerState = PowerState.On;
                    AddEvent(EventType.Power, "Apple TV asked the TV to turn on");
                    break;
                case ReportPhysicalAddress when frame.Length >= 4:
                    _physicalAddress = [frame[2], frame[3]];
                    SetDetail(PhysicalAddressDetailLabel, PhysicalAddress);
                    _discovered = true;
                    break;
                case SetOsdName when frame.Length >= 3:
                    SetDetail(OsdNameDetailLabel, new string(frame[2..]));
                    break;
                case DeviceVendorId when frame.Length >= 5:
                    SetDetail(VendorDetailLabel, FormatVendor(frame[2], frame[3], frame[4]));
                    break;
                case CecVersion when frame.Length >= 3:
                    SetDetail(CecVersionDetailLabel, FormatCecVersion(frame[2]));
                    break;
                case MenuStatus when frame.Length >= 3:
                    SetDetail(MenuDetailLabel, frame[2] == '\x00' ? "Activated" : "Deactivated");
                    break;
                case DeckStatus when frame.Length >= 3:
                    TransportState = DecodeDeckStatus(frame[2]);
                    break;
                case FeatureAbort when frame.Length >= 4:
                    HandleFeatureAbort(frame[2], frame[3]);
                    break;
                case UserControlPressed:
                case UserControlReleased:
                    LogDebug("Apple TV relayed a key to the TV: {Frame}", Describe(frame));
                    break;
                case GiveDevicePowerStatus:
                    AnswerQuery([_commandHeader, ReportPowerStatus, '\x00']);
                    break;
                case GivePhysicalAddress:
                    AnswerQuery([TvBroadcastHeader, ReportPhysicalAddress, '\x00', '\x00', '\x00']);
                    break;
                case GetCecVersion:
                    AnswerQuery([_commandHeader, CecVersion, CecVersion14]);
                    break;
                default:
                    LogDebug("Unhandled CEC frame: {Frame}", Describe(frame));
                    break;
            }
        }
    }

    private void HandleFeatureAbort(char opcode, char reason)
    {
        var reasonText = reason switch
        {
            '\x00' => "unrecognised opcode",
            '\x01' => "not in the correct mode to respond",
            '\x02' => "cannot provide source",
            '\x03' => "invalid operand",
            '\x04' => "refused",
            '\x05' => "unable to determine",
            _ => $"reason 0x{(int)reason:X2}"
        };
        switch (opcode)
        {
            case GiveDeckStatus:
                LogDebug("Deck status unavailable: {Reason}", reasonText);
                break;
            case UserControlPressed:
                LogWarning("The Apple TV rejected the last key: {Reason}", reasonText);
                AddEvent(EventType.Error, $"Key rejected: {reasonText}");
                break;
            default:
                LogDebug("Feature abort for opcode 0x{Opcode:X2}: {Reason}", (int)opcode, reasonText);
                break;
        }
    }

    private void AnswerQuery(char[] reply)
    {
        if (!_answerTvQueries)
            return;
        SendFrame(reply);
    }

    private static TransportState DecodeDeckStatus(char status) => status switch
    {
        '\x11' => TransportState.Playing,
        '\x12' => TransportState.Recording,
        '\x13' => TransportState.Playing,
        '\x14' => TransportState.Paused,
        '\x15' => TransportState.Playing,
        '\x16' => TransportState.Playing,
        '\x17' => TransportState.Playing,
        '\x18' => TransportState.Playing,
        '\x19' => TransportState.Stopped,
        '\x1A' => TransportState.Stopped,
        '\x1B' => TransportState.Playing,
        '\x1C' => TransportState.Playing,
        '\x1D' => TransportState.Playing,
        '\x1E' => TransportState.Playing,
        _ => TransportState.Unknown
    };

    private char[] StripRepeatedHeader(string incoming)
    {
        var frame = incoming.ToCharArray();
        while (frame.Length >= 2 && frame[0] == frame[1] &&
               (frame[0] == _responseHeader || frame[0] == _deviceBroadcastHeader))
            frame = frame[1..];
        return frame;
    }

    private static string FormatPhysicalAddress(char[] address) =>
        $"{address[0] >> 4}.{address[0] & 0x0F}.{address[1] >> 4}.{address[1] & 0x0F}";

    private static string FormatVendor(char b0, char b1, char b2)
    {
        var id = $"{(int)b0:X2}:{(int)b1:X2}:{(int)b2:X2}";
        return id == "00:10:FA" ? $"Apple ({id})" : id;
    }

    private static string FormatCecVersion(char version) => version switch
    {
        '\x00' => "1.1",
        '\x01' => "1.2",
        '\x02' => "1.2a",
        '\x03' => "1.3",
        '\x04' => "1.3a",
        '\x05' => "1.4",
        '\x06' => "2.0",
        _ => $"0x{(int)version:X2}"
    };

    private static string Describe(char[] frame)
    {
        var sb = new StringBuilder();
        foreach (var c in frame)
        {
            if (sb.Length > 0)
                sb.Append(':');
            sb.Append(((int)c).ToString("X2"));
        }
        return sb.ToString();
    }

    private void SendFrame(char[] frame, TimeSpan? spacingAfter = null)
    {
        lock (_sendLock)
        {
            var wait = _spacingAfterLastSend - Stopwatch.GetElapsedTime(_lastSendTimestamp);
            if (wait > TimeSpan.Zero)
                Thread.Sleep(wait);
            _cecStream.Send(frame);
            _lastSendTimestamp = Stopwatch.GetTimestamp();
            _spacingAfterLastSend = spacingAfter ?? MinimumFrameSpacing;
        }
    }

    private bool IsPhysicalRemoteIntent(char header, char opcode) => opcode switch
    {
        Standby or InactiveSource or ImageViewOn or TextViewOn => true,
        ReportPowerStatus => header == _deviceBroadcastHeader,
        _ => false
    };

    private void RemoteControlPassthrough(char code)
    {
        lock (_sendLock)
        {
            SendFrame([_commandHeader, UserControlPressed, code], KeyHold);
            SendFrame([_commandHeader, UserControlReleased]);
        }
    }

    /// <summary>Wakes the Apple TV with the Power On Function key.</summary>
    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            DesiredPowerState = PowerState.On;
            RemoteControlPassthrough(PowerOnFunctionKey);
        }
    }

    /// <summary>Puts the Apple TV into standby.</summary>
    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            DesiredPowerState = PowerState.Off;
            SendFrame([_commandHeader, Standby]);
        }
    }

    /// <summary>Broadcasts Set Stream Path to the Apple TV's physical address, which also wakes it.</summary>
    public void MakeActiveSource()
    {
        using (PushProperties("MakeActiveSource"))
            SendFrame([TvBroadcastHeader, SetStreamPath, _physicalAddress[0], _physicalAddress[1]]);
    }

    public void ChannelUp()
    {
        using (PushProperties("ChannelUp"))
            LogWarning("ChannelUp is not supported");
        AddEvent(EventType.Error, "ChannelUp is not supported");
    }

    public void ChannelDown()
    {
        using (PushProperties("ChannelDown"))
            LogWarning("ChannelDown is not supported");
        AddEvent(EventType.Error, "ChannelDown is not supported");
    }

    public IReadOnlyCollection<RemoteButton> SupportedButtons => SupportedButtonList;

    public void SendIRCode(RemoteButton button)
    {
        using (PushProperties("SendIRCode"))
        {
            switch (button)
            {
                case RemoteButton.PowerOn:
                    PowerOn();
                    return;
                case RemoteButton.PowerOff:
                    PowerOff();
                    return;
                case RemoteButton.Power:
                    if (PowerState == PowerState.On)
                        PowerOff();
                    else
                        PowerOn();
                    return;
            }

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
        lock (_sendLock)
        {
            foreach (var c in channel.ToString())
                RemoteControlPassthrough(RemoteButtonMap[NumberpadMap[c]]);
        }
    }

    public void ToggleSubtitles() => RemoteControlPassthrough(RemoteButtonMap[RemoteButton.Subtitle]);
}
