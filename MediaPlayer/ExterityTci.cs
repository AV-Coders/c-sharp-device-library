using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public class ExterityTci : MediaPlayer
{
    private readonly CommunicationClient _communicationClient;
    private readonly Dictionary<MuteState, string> _muteDictionary = new Dictionary<MuteState, string>
    {
        { MuteState.On, "on"},
        { MuteState.Off, "off"}
    };
    
    public ExterityTci(CommunicationClient communicationClient)
    {
        _communicationClient = communicationClient;
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
}