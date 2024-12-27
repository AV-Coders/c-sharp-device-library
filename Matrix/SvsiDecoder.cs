using AVCoders.Core;

namespace AVCoders.Matrix;

public class SvsiDecoder : SvsiBase
{
    public MuteStateHandler? MuteStateHandlers;
    public VolumeLevelHandler? VolumeLevelHandlers;
    private readonly Dictionary<MuteState, string> _muteDictionary;
    private MuteState _muteState = MuteState.Off;
    
    public SvsiDecoder(TcpClient tcpClient, int pollTime = 10) : base(tcpClient, pollTime, AVoIPDeviceType.Decoder)
    {
        _muteDictionary = new Dictionary<MuteState, string>
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
            UpdateOutputStatus(connected == "connected" ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                resolution.Replace(".mode", String.Empty),
                1);

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

    public void SetInput(uint streamId) => CommunicationClient.Send($"set:{streamId}\r");

    public void SetInput(SvsiEncoder encoder) => SetInput(encoder.StreamId);

    public void SetAudioMute(MuteState muteState) => CommunicationClient.Send($"{_muteDictionary[muteState]}\r");

    public void ToggleAudioMute() => SetAudioMute(_muteState == MuteState.Off ? MuteState.On : MuteState.Off);
}