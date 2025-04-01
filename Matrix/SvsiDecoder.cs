using AVCoders.Core;

namespace AVCoders.Matrix;

public class SvsiDecoder : SvsiBase
{
    public MuteStateHandler? MuteStateHandlers;
    public VolumeLevelHandler? VolumeLevelHandlers;
    private readonly Dictionary<MuteState, string> _audoMuteDictionary;
    private MuteState _muteState = MuteState.Off;
    
    public SvsiDecoder(string name, TcpClient tcpClient) : base(name, tcpClient, AVoIPDeviceType.Decoder)
    {
        _audoMuteDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "mute" },
            { MuteState.Off, "unmute" },
        };
    }

    protected override void UpdateVariablesBasedOnStatus()
    {
        if (StatusDictionary.TryGetValue("STREAM", out var streamId))
            StreamId = uint.Parse(streamId);

        if (StatusDictionary.TryGetValue("MODE", out var resolution) &&
            StatusDictionary.TryGetValue("DVISTATUS", out var connected))
        {
            OutputConnectionStatus =
                connected == "connected" ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
            OutputResolution = resolution.Replace(".mode", String.Empty);
        }

        if (StatusDictionary.TryGetValue("MUTE", out var muteState))
        {
            MuteState currentMuteState = muteState switch
            {
                "0" => MuteState.Off,
                "1" => MuteState.On,
                _ => MuteState.Unknown
            };
            if (currentMuteState == _muteState) 
                return;
            _muteState = currentMuteState;
            MuteStateHandlers?.Invoke(_muteState);

        }
    }

    public void SetInput(uint streamId)
    {
        CommunicationClient.Send($"set:{streamId}\r");
        Thread.Sleep(200);
        CommunicationClient.Send("live\r");
    }

    public void SetPlaylist(uint playlistId)
    {
        CommunicationClient.Send($"local:{playlistId}\r");
    }

    public void SetInput(SvsiEncoder encoder) => SetInput(encoder.StreamId);
    
    public void SetAudioInput(SvsiEncoder encoder) => SetAudioInput(encoder.StreamId);
    
    /// <summary>Setting to 0 will enable audio follows video</summary>
    public void SetAudioInput(uint streamId)
    {
        CommunicationClient.Send($"seta:{streamId}\r");
    }

    public void SetAudioMute(MuteState muteState) => CommunicationClient.Send($"{_audoMuteDictionary[muteState]}\r");

    public void ToggleAudioMute() => SetAudioMute(_muteState == MuteState.Off ? MuteState.On : MuteState.Off);
    
    public void SetVideoMute(MuteState muteState)
    {
        string command = muteState == MuteState.On ? "Off" : "On";
        CommunicationClient.Send($"dvi{command}\r");
    }
}