using AVCoders.Core;
using AVCoders.MediaPlayer;

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

    private static readonly Dictionary<Input, char> InputMap = new Dictionary<Input, char>
    {
        { Input.Hdmi1, '\x10' },
        { Input.Hdmi2, '\x20' },
        { Input.Hdmi3, '\x30' },
        { Input.Hdmi4, '\x40' },
        { Input.DvbtTuner, '\x50' },
        { Input.Network6, '\x60' }
    }; 

    public CecDisplay(SerialClient cecStream, char sourceId = SourcePlayBack1, char destinationId = DestinationTv, int pollTime = 23) 
        : base(InputMap.Keys.ToList(), pollTime)
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
            Log("It's a power response");
            PowerState = incoming[2] switch
            {
                '\x00' => PowerState.On,
                '\x01' => PowerState.Off,
                _ => PowerState
            };
            Log($"Power state is {PowerState.ToString()}");
            ProcessPowerResponse();
        }
    }

    protected override void Poll()
    {
        Log("Polling Power");
        Send(new[] { '\xF0', '\x8F' });
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
        Log("Powering on");
        RemoteControlPassthrough('\x6D');
        Thread.Sleep(150);
        // One touch play - Image view on
        Send(new [] { _commandHeader, '\x04' });
    }

    protected override void DoPowerOff()
    {
        Log("Powering off");
        RemoteControlPassthrough('\x6C');
        // Broadcast system standby
        // Send(new [] { _broadcastHeader, '\x36' });
    }

    protected override void DoSetInput(Input input) => Send(new []{ _commandHeader, '\x82', InputMap[input], '\x00'});

    protected override void DoSetVolume(int percentage)
    {
        // scale to a value between 0-127
        char volume = (char)(percentage * 1.27);
        Log("Volume not available");
        Send(new []{ _commandHeader, '\x7A', volume});
        if(AudioMute != MuteState.Off)
            MuteStateHandlers?.Invoke(MuteState.Off);
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
        throw new NotImplementedException();
    }

    public void SetChannel(int channel)
    {
        throw new NotImplementedException();
    }

    public void ToggleSubtitles()
    {
        throw new NotImplementedException();
    }
}