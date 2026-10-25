using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public class ExterityTci : MediaPlayer, ISetTopBox
{
    private readonly string _password;
    private readonly Dictionary<MuteState, string> _muteDictionary = new()
    {
        { MuteState.On, "on"},
        { MuteState.Off, "off"}
    };
    
    public ExterityTci(CommunicationClient communicationClient, string password, string name) 
        : base(name, communicationClient)
    {
        _password = password;
        CommunicationClient.ResponseHandlers += HandleResponse;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.Unknown;
    }

    private void WriteParameter(string parameter, string value)
    {
        CommunicationClient.Send($"^set:{parameter}:{value}!\n");
    }

    private void SimulateRemoteKeypress(string key)
    {
        CommunicationClient.Send($"^send:rm_{key}!\n");
    }

    public override void PowerOn()
    {
        WriteParameter("currentMode", "av");
        PowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        WriteParameter("currentMode", "off");
        PowerState = PowerState.Off;
    }

    private void HandleResponse(string response)
    {
        if (response.Contains("login as:"))
        {
            CommunicationClient.Send("ctrl\n");
        }
        else if (response.Contains("'s password:"))
        {
            CommunicationClient.Send($"{_password}\n");
        }
    }

    public void ChannelUp()
    {
        SimulateRemoteKeypress("chup");
    }

    public void ChannelDown()
    {
        SimulateRemoteKeypress("chdown");
    }

    public IReadOnlyCollection<RemoteButton> SupportedButtons => Enum.GetValues<RemoteButton>();

    public void SendIRCode(RemoteButton button)
    {
        SimulateRemoteKeypress(button.ToString());
    }

    public void VolumeUp()
    {
        SimulateRemoteKeypress("volup");
    }

    public void VolumeDown()
    {
        SimulateRemoteKeypress("voldown");
    }

    public void SetAudioMute(MuteState state)
    {
        if (!_muteDictionary.TryGetValue(state, out var mute))
        {
            LogError("Can't set the audio mute to {MuteState}", state);
            return;
        }

        WriteParameter("mute", mute);
    }

    public void SetChannel(int channelNumber)
    {
        if (channelNumber < 0)
        {
            LogError("Channel {Channel} is invalid, it can't be negative", channelNumber);
            return;
        }

        string channel = channelNumber.ToString();
        foreach (char digit in channel)
        {
            SimulateRemoteKeypress($"{digit}");
        }
        SimulateRemoteKeypress("enter");
    }

    public void ToggleSubtitles()
    {
        SimulateRemoteKeypress("subtitle");
    }
}