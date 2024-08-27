using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public class ExterityTci : MediaPlayer, ISetTopBox
{
    private readonly CommunicationClient _communicationClient;
    private readonly string _password;
    private readonly Dictionary<MuteState, string> _muteDictionary = new()
    {
        { MuteState.On, "on"},
        { MuteState.Off, "off"}
    };
    
    public ExterityTci(CommunicationClient communicationClient, string password)
    {
        _communicationClient = communicationClient;
        _password = password;
        _communicationClient.ResponseHandlers += HandleResponse;
        _communicationClient.ConnectionStateHandlers += HandleConnectionState;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.Unknown;
    }

    private void WriteParameter(string parameter, string value)
    {
        _communicationClient.Send($"^set:{parameter}:{value}!\n");
    }

    private void SimulateRemoteKeypress(string key)
    {
        _communicationClient.Send($"^send:rm_{key}!\n");
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
            _communicationClient.Send("ctrl\n");
        }
        else if (response.Contains("'s password:"))
        {
            _communicationClient.Send($"{_password}\n");
        }
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
    }

    public void ChannelUp()
    {
        SimulateRemoteKeypress("chup");
    }

    public void ChannelDown()
    {
        SimulateRemoteKeypress("chdown");
    }

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
        WriteParameter("mute", _muteDictionary[state]);
    }

    public void SetChannel(int channelNumber)
    {
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