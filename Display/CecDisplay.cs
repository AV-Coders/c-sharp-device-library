using AVCoders.Core;
using AVCoders.MediaPlayer;
using Serilog;

namespace AVCoders.Display;

public class CecDisplay : Display, ISetTopBox
{
    private readonly SerialClient _cecStream;
    
    public const char SourcePlayBack1 = '\x40';
    public const char SourceUnregistered = '\xF0';
    public const char DestinationTv = '\x00';
    public const char DestinationBroadcast = '\x0F';

    private readonly char _commandHeader;
    private readonly char _broadcastHeader;
    private readonly char _responseHeader;

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

    private static readonly List<RemoteButton> UnsupportedButtons = new()
    {
        RemoteButton.Guide,
        RemoteButton.Home,
        RemoteButton.Menu
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

    public CecDisplay(SerialClient cecStream, string name,  char sourceId = SourcePlayBack1, char destinationId = DestinationTv, int pollTime = 23) 
        : base(InputMap.Keys.ToList(), name, null, cecStream, pollTime)
    {
        _cecStream = cecStream;
        _cecStream.ResponseHandlers += HandleResponse;
        _commandHeader = (char)(sourceId + destinationId);
        _responseHeader = '\x0F';
        _broadcastHeader = (char)(sourceId + '\x0F');
    }

    private void HandleResponse(string incoming)
    {
        if (incoming[0] == _responseHeader && incoming[1] == '\x90')
        {
            PowerState = incoming[2] switch
            {
                '\x00' => PowerState.On,
                '\x01' => PowerState.Off,
                _ => PowerState
            };
            ProcessPowerResponse();
        }
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
            PollWorker.Restart();
    }

    protected override Task DoPoll(CancellationToken token)
    {
        Send(new[] { '\xF0', '\x8F' });
        return Task.CompletedTask;
    }

    private void RemoteControlPassthrough(char command)
    {
        // Press
        _cecStream.Send(new [] { _commandHeader, '\x44', command });
        Thread.Sleep(75);
        // Release
        _cecStream.Send(new[] { _commandHeader, '\x45' });
    }
    

    private void Send(char[] command) => _cecStream.Send(command);

    protected override void DoPowerOn()
    {
        RemoteControlPassthrough('\x6D');
        Thread.Sleep(150);
        // One touch play - Image view on
        Send(new [] { _commandHeader, '\x04' });
    }

    protected override void DoPowerOff()
    {
        RemoteControlPassthrough('\x6C');
        // Broadcast system standby
        // Send(new [] { _broadcastHeader, '\x36' });
    }

    protected override void DoSetInput(Input input) => Send(new []{ _commandHeader, '\x82', InputMap[input], '\x00'});

    protected override void DoSetVolume(int percentage)
    {
        // scale to a value between 0-127
        char volume = (char)(percentage * 1.27);
        Send([_commandHeader, '\x7A', volume]);
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

    public void SendIRCode(RemoteButton button)
    {
        using (PushProperties("SendIRCode"))
        {
            if (UnsupportedButtons.Contains(button))
            {
                Log.Error($"Unsupported button - {button.ToString()}");
                return;
            }

            RemoteControlPassthrough(RemoteButtonMap[button]);
        }
    }

    public void SetChannel(int channel)
    {
        var channelString = channel.ToString();
        foreach (var c in channelString)
        {
            RemoteControlPassthrough(RemoteButtonMap[NumberpadMap[c]]);
            Thread.Sleep(50);
        }
    }

    public void ToggleSubtitles()
    {
        throw new NotImplementedException();
    }
}