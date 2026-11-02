using AVCoders.Core;

namespace AVCoders.Matrix;

public class SvsiDecoder : SvsiBase
{
    public MuteStateHandler? MuteStateHandlers;
    public VolumeLevelHandler? VolumeLevelHandlers;
    private static readonly TimeSpan SetInputLiveGap = TimeSpan.FromMilliseconds(200);
    private const string NeedsVideoStreamKey = "NEEDVSTRM";
    private const string NeedsVideoStreamLabel = "Needs Video Stream";
    private const string VideoStreamNotNeeded = "0";
    private static readonly (string Key, string Label)[] DropCounters =
    [
        ("ND_V_DROP", "Video Drops"),
        ("ND_A_DROP", "Audio Drops"),
        ("ND_F_DROP", "Frame Drops")
    ];
    private readonly Dictionary<MuteState, string> _audoMuteDictionary;
    private readonly object _sendLock = new();
    private Task _sends = Task.CompletedTask;
    private MuteState _muteState = MuteState.Off;
    
    public SvsiDecoder(string name, TcpClient tcpClient) : this(name, tcpClient, null)
    {
    }

    internal SvsiDecoder(string name, TcpClient tcpClient, TimeProvider? timeProvider)
        : base(name, tcpClient, AVEndpointType.Decoder, timeProvider)
    {
        _audoMuteDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "mute" },
            { MuteState.Off, "unmute" },
        };
    }

    protected override void UpdateVariablesBasedOnStatus()
    {
        if (StatusDictionary.TryGetValue("MODE", out var resolution) &&
            StatusDictionary.TryGetValue("DVISTATUS", out var connected))
        {
            OutputConnectionStatus =
                connected == "connected" ? ConnectionState.Connected : ConnectionState.Disconnected;
            OutputResolution = resolution.Replace(".mode", string.Empty);
        }

        ShowStreamHealth();

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

    private void ShowStreamHealth()
    {
        foreach (var (key, label) in DropCounters)
        {
            if (StatusDictionary.TryGetValue(key, out var count))
                SetDetail(label, count);
        }
        if (StatusDictionary.TryGetValue(NeedsVideoStreamKey, out var needsVideoStream))
            SetDetail(NeedsVideoStreamLabel, needsVideoStream,
                needsVideoStream == VideoStreamNotNeeded ? DetailTone.Normal : DetailTone.Warning);
    }

    private void Send(params (string Message, TimeSpan SpacingAfter)[] messages)
    {
        lock (_sendLock)
            _sends = _sends.ContinueWith(_ => SendSpaced(messages), TaskScheduler.Default).Unwrap();
    }

    private async Task SendSpaced((string Message, TimeSpan SpacingAfter)[] messages)
    {
        try
        {
            foreach (var (message, spacingAfter) in messages)
            {
                CommunicationClient.Send(message);
                await Task.Delay(spacingAfter, TimeProvider);
            }
        }
        catch (Exception e)
        {
            LogException(e, "Sending to the decoder failed");
        }
    }

    public void SetInput(uint streamId) => Send(($"set:{streamId}\r", SetInputLiveGap), ("live\r", TimeSpan.Zero));

    public void SetPlaylist(uint playlistId) => Send(($"local:{playlistId}\r", TimeSpan.Zero));

    public void SetInput(SvsiEncoder encoder) => SetInput(encoder.StreamId);
    
    public void SetAudioInput(SvsiEncoder encoder) => SetAudioInput(encoder.StreamId);
    
    /// <summary>Setting to 0 will enable audio follows video</summary>
    public void SetAudioInput(uint streamId) => Send(($"seta:{streamId}\r", TimeSpan.Zero));

    public void SetAudioMute(MuteState muteState)
    {
        if (muteState == MuteState.Unknown)
        {
            using (PushProperties())
                LogWarning("Cannot set the mute state to Unknown");
            return;
        }
        Send(($"{_audoMuteDictionary[muteState]}\r", TimeSpan.Zero));
    }

    public void ToggleAudioMute() => SetAudioMute(_muteState == MuteState.Off ? MuteState.On : MuteState.Off);
    
    public void SetVideoMute(MuteState muteState)
    {
        string command = muteState == MuteState.On ? "Off" : "On";
        Send(($"dvi{command}\r", TimeSpan.Zero));
    }
}