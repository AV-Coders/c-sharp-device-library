using AVCoders.Core;
using AVCoders.MediaPlayer;

namespace AVCoders.Display;

public class CecDisplay : Display, ISetTopBox
{
    private readonly SerialClient _cecStream;
    private readonly Func<char[]> _sourcePhysicalAddress;

    private const char SourcePlayBack1 = '\x40';
    private const char SourceUnregistered = '\xF0';
    private const char DestinationTv = '\x00';
    private const char DestinationBroadcast = '\x0F';
    private const char Standby = '\x36';
    private const char UserControlPressed = '\x44';
    private const char UserControlReleased = '\x45';
    private const char ActiveSource = '\x82';
    private const char ReportPowerStatus = '\x90';
    private const int PhysicalAddressLength = 2;
    private const int PowerStatusReplyLength = 3;
    private static readonly TimeSpan ActiveSourceSettleTime = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeyPressTime = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan PowerOnSpacing = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ChannelDigitSpacing = TimeSpan.FromMilliseconds(50);

    private readonly char _commandHeader;
    private readonly char _broadcastHeader;
    private readonly char _responseHeader;
    private readonly object _sendLock = new();
    private Task _sends = Task.CompletedTask;

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
        : base(InputMap.Keys.ToList(), name, null, cecStream, CommandStringFormat.Hex, pollTime)
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
            if (incoming.Length < PowerStatusReplyLength)
            {
                LogDebug("Ignoring a short reply {Reply}", Convert.ToHexString(incoming.Select(c => (byte)c).ToArray()));
                return;
            }
            if (incoming[0] == _responseHeader && incoming[1] == ReportPowerStatus)
            {
                CommunicationState = CommunicationState.Okay;
                PowerState = incoming[2] switch
                {
                    '\x00' => PowerState.On,
                    // Samsung reports 02 (in transition to on) for as long as it is in standby.
                    '\x01' or '\x02' => PowerState.Off,
                    _ => PowerState
                };
                ProcessPowerResponse();
            }
        }
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
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
                    Send(([SourceUnregistered, '\x8F'], TimeSpan.Zero));
                    break;
            }
        }
        return Task.CompletedTask;
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
                await Task.Delay(spacingAfter);
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
}
