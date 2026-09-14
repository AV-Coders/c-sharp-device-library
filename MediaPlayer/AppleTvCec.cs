using System.Diagnostics;
using System.Text;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

/// <summary>
/// Controls an Apple TV over HDMI-CEC from the "TV" side of the link (logical address 0), for
/// example through the CEC object of a DM-NVX encoder's HDMI input.
/// </summary>
/// <remarks>
/// Exercised against a tvOS Apple TV (CEC 2.0) on a DM-NVX-360 input. Power was verified by
/// reading the power status back; every key was sent with a person watching the screen (Plex
/// app, menus and playback), so each mapping below is backed by what was seen:
/// <list type="bullet">
/// <item><b>Confirmed working on screen:</b> power on is User Control Pressed "Power On Function"
/// (0x6D), and Set Stream Path to the device's physical address also wakes it; power off is
/// Standby (0x36). The Apple TV answers Standby with Inactive Source and broadcasts Report Power
/// Status on every change, which the driver uses as feedback along with periodic polling. Image
/// View On, Power (0x40) and Power Toggle (0x6B) demonstrably do nothing, so
/// <see cref="RemoteButton.Power"/> is toggled by the driver from its last known state. During
/// Plex playback Pause (0x46) paused, Play (0x44) resumed, Stop (0x45) exited playback, and Next
/// (Forward 0x4B) / Previous (Backward 0x4C) skipped once per press. In the Plex menus Up, Down,
/// Left and Right (0x01-0x04) moved the highlight, Select (0x00) opened the highlighted item and
/// Exit (0x0D) went back one level. Root Menu (0x09) acts as the Siri remote's TV button: with
/// the tvOS "TV Button" setting (Settings > Remotes and Devices) on "Apple TV App" it launched
/// the Apple TV app, on "Home Screen" it went to the home screen. Contents Menu (0x0B) behaved
/// identically to 0x09 - it is the same TV button and followed the same setting. The digit keys
/// Number 0 to Number 9 (0x20-0x29) each registered on screen.</item>
/// <item><b>Menu:</b> tvOS has no distinct menu function over CEC. Setup Menu (0x0A) was
/// acknowledged but did nothing (tried from inside Plex, one level deep), and 0x0B is just the
/// TV button again. The older Siri remote's physical Menu button is Back, so
/// <see cref="RemoteButton.Menu"/> is sent as Exit (0x0D), the same as
/// <see cref="RemoteButton.Back"/>.</item>
/// <item><b>Acknowledged, no effect in Plex, plausibly app-dependent, unconfirmed elsewhere:</b>
/// Rewind (0x48), FastForward (0x49) and Subtitle (Sub Picture 0x51, sent during playback) did
/// nothing in Plex even when sent twice; they stay mapped because the Apple TV accepts them and
/// another app may honour them.</item>
/// <item><b>Rejected or not supported</b> (left out of <see cref="SupportedButtons"/>; sending
/// one logs a warning and an Error event and puts nothing on the bus): Display, TopMenu and
/// PopupMenu are answered with a Feature Abort "invalid operand". VolumeUp, VolumeDown and Mute
/// are acknowledged but the Apple TV only relays them back to the TV as its own key presses,
/// which goes nowhere on an encoder input. Guide (0x53), ChannelUp (0x30), ChannelDown (0x31),
/// Eject (0x4A) and the Blue/Red/Green/Yellow keys (0x71-0x74) are acknowledged but confirmed
/// to do nothing - tvOS has no guide, channel, eject or colour-key functions in any app, so
/// they would only be dead buttons on a panel. Setup Menu (0x0A) is likewise a confirmed no-op
/// (see Menu above).</item>
/// <item><b>Key repeat:</b> tvOS treats a User Control Pressed that is not released within
/// roughly 300 ms as a held key - Previous skipped back twice with a 300 ms hold and once with
/// 150 ms or 100 ms - so a key is held for <see cref="KeyHoldMilliseconds"/>.</item>
/// <item><b>No transport feedback:</b> tvOS Feature Aborts Give Deck Status ("not in correct
/// mode") on the home screen and during playback alike, so it does not implement Deck Status
/// and <see cref="MediaPlayer.TransportState"/> only changes if a device ever reports one.</item>
/// <item><b>Physical remote wins:</b> the Siri remote's power button was captured on the bus.
/// Sleep: Standby broadcast (4F:36), Standby to the TV (40:36), Inactive Source (40:9D), then
/// Report Power Status Standby (4F:90:01). Wake: Report Power Status On (4F:90:00), Image View
/// On (40:04), Active Source (4F:82), Menu Status activated (40:8E:00), and about 10 s later the
/// usual query burst (40:83, 40:8F, 40:9F, 40:8C), which the driver answers. A Standby,
/// Inactive Source or <i>broadcast</i> Report Power Status (4F:90:xx) is taken as the user's
/// intent and becomes the desired power state, so the driver does not switch it straight back
/// and raises no power-state issue; Image View On from it does the same for On. The Apple TV
/// broadcasts 4F:90:xx on every power change whoever caused it - it follows the driver's own
/// Standby and Power On Function too - and adopting it is harmless in those cases only because
/// it already agrees with the desired state. Active Source (4F:82) is deliberately not intent:
/// it is also the solicited reply to the driver's own Request Active Source and Set Stream Path.
/// A <i>directed</i> Report Power Status (40:90:xx) is only the answer to the driver's poll, so
/// a poll that finds the device off with no such frame first is still enforced as usual.
/// Because CrestronCecStream replays the last received frame on every CEC event (NACKs
/// included), an intent frame only counts when it differs from the previous received string;
/// a genuine sleep or wake sequence never repeats a frame back to back.
/// <see cref="PowerOn"/> and <see cref="PowerOff"/> from the program set the desired state and
/// force it as before.</item>
/// </list>
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

    // Opcodes
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
    public const int MaxUnansweredPolls = 3;
    /// <summary>
    /// How long a key is held: the gap between User Control Pressed and User Control Released.
    /// Holds of about 300 ms make tvOS repeat the key (Previous skipped back twice); 150 ms and
    /// 100 ms produced a single action and went out without a NACK.
    /// </summary>
    public const int KeyHoldMilliseconds = 100;
    public static readonly TimeSpan KeyHold = TimeSpan.FromMilliseconds(KeyHoldMilliseconds);
    /// <summary>
    /// Minimum gap after a frame that gets no answer (a key's Released frame, Standby, Set Stream
    /// Path, the replies to the Apple TV's queries) before the next frame goes out. Verified
    /// clean on the NVX link.
    /// </summary>
    public static readonly TimeSpan MinimumFrameSpacing = TimeSpan.FromMilliseconds(150);
    /// <summary>
    /// Minimum gap after a query the Apple TV answers. Its replies take 250-400 ms to arrive and
    /// the NVX CEC sig concatenates or drops frames that overlap: at 150 ms every second query
    /// NACKed and the replies came back merged, at 300 ms two replies still merged, at 500 ms
    /// all six discovery replies arrived cleanly.
    /// </summary>
    public static readonly TimeSpan ReplySpacing = TimeSpan.FromMilliseconds(500);
    /// <summary>
    /// Default for how long an identical incoming string is ignored as a CrestronCecStream replay
    /// (it re-fires the last received value on every CEC event). Intent frames are additionally
    /// ignored whenever they repeat the previous string, regardless of time.
    /// </summary>
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

    // CEC User Control codes (CEC 1.4 table 27) the Apple TV acknowledges without a Feature Abort.
    // Every entry was watched on screen (Plex): all confirmed working except Rewind, FastForward
    // and Subtitle, which did nothing in Plex but may in other apps. See the class remarks.
    private static readonly Dictionary<RemoteButton, char> RemoteButtonMap = new()
    {
        { RemoteButton.Enter, '\x00' },        // Select
        { RemoteButton.Up, '\x01' },
        { RemoteButton.Down, '\x02' },
        { RemoteButton.Left, '\x03' },
        { RemoteButton.Right, '\x04' },
        { RemoteButton.Home, '\x09' },         // Root Menu - the Siri remote's TV button (0x0B does the same)
        { RemoteButton.Menu, '\x0D' },         // Exit - tvOS has no separate menu; Setup Menu 0x0A is a no-op
        { RemoteButton.Back, '\x0D' },         // Exit
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
        { RemoteButton.Next, '\x4B' },         // Forward
        { RemoteButton.Previous, '\x4C' },     // Backward
        { RemoteButton.Subtitle, '\x51' },     // Sub Picture - no effect in Plex
    };

    // Handled by the driver's own power methods rather than a single key code: the Apple TV
    // ignores the Power (0x40) and Power Toggle (0x6B) keys.
    private static readonly RemoteButton[] PowerButtons =
        [RemoteButton.Power, RemoteButton.PowerOn, RemoteButton.PowerOff];

    private static readonly IReadOnlyCollection<RemoteButton> SupportedButtonList =
        RemoteButtonMap.Keys.Concat(PowerButtons).ToList().AsReadOnly();

    public BoolHandler? ActiveSourceHandlers;

    private readonly SerialClient _cecStream;
    private readonly char _commandHeader;        // TV -> Apple TV
    private readonly char _responseHeader;       // Apple TV -> TV
    private readonly char _deviceBroadcastHeader; // Apple TV -> everyone
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

    /// <summary>The Apple TV's physical address as last reported (e.g. "1.0.0.0"), defaulting to 1.0.0.0.</summary>
    public string PhysicalAddress => FormatPhysicalAddress(_physicalAddress);

    /// <param name="cecStream">The CEC link, e.g. a CrestronCecStream wrapping an NVX HDMI input.</param>
    /// <param name="name">Device name for logging.</param>
    /// <param name="logicalAddress">The Apple TV's CEC logical address; playback device 1 (4) unless it has taken 8 or 11.</param>
    /// <param name="answerTvQueries">Reply to the Apple TV's own Give Power Status / Give Physical Address /
    /// Get CEC Version queries as an "on" TV so it treats the link as live.</param>
    /// <param name="pollTime">Seconds between power status polls; 0 or less disables polling (and the
    /// discovery that runs with the first poll), leaving <see cref="PollPowerStatus"/> and
    /// <see cref="Discover"/> to the caller.</param>
    /// <param name="duplicateResponseWindow">How long an identical incoming string is ignored as a replay;
    /// defaults to <see cref="DefaultDuplicateResponseWindow"/>.</param>
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

    /// <summary>
    /// One poll cycle, as run by the poll worker: discovery if it has not completed yet, then
    /// Give Device Power Status. After <see cref="MaxUnansweredPolls"/> polls with no frame
    /// back from the Apple TV the communication state becomes Error and the power state Unknown.
    /// </summary>
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
            SendFrame([_commandHeader, GiveDevicePowerStatus], ReplySpacing);
        }
    }

    /// <summary>
    /// Asks the Apple TV for its physical address, OSD name, vendor ID, CEC version and (once)
    /// its deck status, and asks the bus which device is the active source. The answers appear
    /// in <see cref="LogBase.Details"/>.
    /// </summary>
    public void Discover()
    {
        using (PushProperties("Discover"))
        {
            LogDebug("Sending CEC discovery requests");
            SendFrame([_commandHeader, GivePhysicalAddress], ReplySpacing);
            SendFrame([_commandHeader, GiveOsdName], ReplySpacing);
            SendFrame([_commandHeader, GiveDeviceVendorId], ReplySpacing);
            SendFrame([_commandHeader, GetCecVersion], ReplySpacing);
            SendFrame([TvBroadcastHeader, RequestActiveSource], ReplySpacing);
            // tvOS Feature Aborts this on the home screen and during playback alike (it does not
            // implement Deck Status); asked once so a device that does can report, without a
            // pointless abort every poll.
            SendFrame([_commandHeader, GiveDeckStatus, DeckStatusRequestOnce], ReplySpacing);
        }
    }

    private void HandleResponse(string incoming)
    {
        using (PushProperties("HandleResponse"))
        {
            // CrestronCecStream re-emits the last received frame on every CEC event (NACKs and
            // physical-address events included), so the same string can arrive again at any
            // time. Inside the window it is dropped outright; after it, parsing is idempotent
            // and only the frames that express user intent must not be acted on again.
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
                return; // Another device, or the TV side of the bus - not ours to parse.
            if (frame.Length < 2)
                return; // Polling message

            CommunicationState = CommunicationState.Okay;
            _unansweredPolls = 0;

            if (isRepeat && IsIntentFrame(header, frame[1]))
            {
                // A stale replay of a sleep/wake frame; a genuine sequence never repeats one
                // back to back. Acting on it could undo a PowerOn()/PowerOff() issued since.
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
                        '\x02' => PowerState.Warming, // Standby -> On
                        '\x03' => PowerState.Cooling, // On -> Standby
                        _ => PowerState
                    };
                    // The Apple TV broadcasts 4F:90 on every power change whoever caused it. When
                    // the physical remote caused it this is the first frame of the wake sequence,
                    // so it has to be adopted as the desired state; when the driver caused it the
                    // adoption is harmless only because it already agrees with the desired state.
                    // A directed 40:90 is just the answer to our poll and is reconciled as usual.
                    if (header == _deviceBroadcastHeader && reported is PowerState.On or PowerState.Off)
                        DesiredPowerState = reported;
                    PowerState = reported;
                    ProcessPowerState();
                    break;
                case Standby:
                    // The Apple TV putting itself to sleep is the person at the physical remote;
                    // adopt it as the desired state so the next Report Power Status is not fought.
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
                    // Not intent: 4F:82 is also the reply to our own Request Active Source and
                    // Set Stream Path. Wake intent comes from the broadcast 90:00 / Image View On.
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
                    // The Apple TV relays volume / mute keys to the TV instead of acting on them.
                    LogDebug("Apple TV relayed a key to the TV: {Frame}", Describe(frame));
                    break;
                case GiveDevicePowerStatus:
                    AnswerQuery([_commandHeader, ReportPowerStatus, '\x00']);
                    break;
                case GivePhysicalAddress:
                    AnswerQuery([TvBroadcastHeader, ReportPhysicalAddress, '\x00', '\x00', '\x00']); // 0.0.0.0, TV
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
                // tvOS answers this from the home screen with "not in correct mode"; it is not
                // evidence of any transport state, so leave TransportState alone.
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
        '\x11' => TransportState.Playing,       // Play
        '\x12' => TransportState.Recording,     // Record
        '\x13' => TransportState.Playing,       // Play Reverse
        '\x14' => TransportState.Paused,        // Still
        '\x15' => TransportState.Playing,       // Slow
        '\x16' => TransportState.Playing,       // Slow Reverse
        '\x17' => TransportState.Playing,       // Fast Forward
        '\x18' => TransportState.Playing,       // Fast Reverse
        '\x19' => TransportState.Stopped,       // No Media
        '\x1A' => TransportState.Stopped,       // Stop
        '\x1B' => TransportState.Playing,       // Skip Forward / Wind
        '\x1C' => TransportState.Playing,       // Skip Reverse / Rewind
        '\x1D' => TransportState.Playing,       // Index Search Forward
        '\x1E' => TransportState.Playing,       // Index Search Reverse
        _ => TransportState.Unknown             // Other Status
    };

    /// <summary>
    /// The NVX CEC sig has been seen delivering a frame with its header byte doubled
    /// ("4F 4F 90 01") when two frames arrive back to back; drop the duplicate.
    /// </summary>
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

    /// <summary>
    /// The single path to the bus. Serialises every send (polls, discovery, key presses and the
    /// replies sent from inside the receive callback) and holds each frame back until the gap
    /// the previous frame asked for has passed: <see cref="ReplySpacing"/> after a query whose
    /// answer has to clear the link first, <see cref="KeyHold"/> after a User Control Pressed,
    /// otherwise <see cref="MinimumFrameSpacing"/>.
    /// </summary>
    /// <param name="spacingAfter">Gap the next frame must leave after this one; defaults to <see cref="MinimumFrameSpacing"/>.</param>
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

    /// <summary>Frames that express the user's intent at the physical remote, which must never be acted on from a replay.</summary>
    private bool IsIntentFrame(char header, char opcode) => opcode switch
    {
        Standby or InactiveSource or ImageViewOn or TextViewOn => true,
        ReportPowerStatus => header == _deviceBroadcastHeader,
        _ => false
    };

    private void RemoteControlPassthrough(char code)
    {
        // Hold the send lock across the pair: a poll or a query answer slipping in between would
        // delay the release by its own spacing and tvOS would see a held key (repeat at ~300 ms).
        lock (_sendLock)
        {
            SendFrame([_commandHeader, UserControlPressed, code], KeyHold);
            SendFrame([_commandHeader, UserControlReleased]);
        }
    }

    /// <summary>Wakes the Apple TV with the "Power On Function" key, which tvOS honours; it then makes itself the active source.</summary>
    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            DesiredPowerState = PowerState.On;
            RemoteControlPassthrough('\x6D'); // Power On Function
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

    /// <summary>
    /// Broadcasts Set Stream Path to the Apple TV's physical address, which makes it the active
    /// source and wakes it if it is in standby.
    /// </summary>
    public void MakeActiveSource()
    {
        using (PushProperties("MakeActiveSource"))
            SendFrame([TvBroadcastHeader, SetStreamPath, _physicalAddress[0], _physicalAddress[1]]);
    }

    // tvOS has no channels: Channel Up / Down (0x30 / 0x31) are acknowledged but confirmed to do nothing.
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
                    // The Apple TV ignores the Power and Power Toggle keys, so toggle from known state.
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
