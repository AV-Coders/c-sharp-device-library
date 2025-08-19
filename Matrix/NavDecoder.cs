using AVCoders.Core;

namespace AVCoders.Matrix;

public class NavDecoder : NavDeviceBase
{
    public MuteStateHandler? AudioMuteStateHandlers;
    public MuteStateHandler? VideoMuteStateHandlers;
    private MuteState _audioMuteState = MuteState.Unknown;
    private MuteState _videoMuteState = MuteState.Unknown;
    
    public MuteState AudioMute
    {
        get => _audioMuteState;
        private set
        {
            if (_audioMuteState == value)
                return;
            _audioMuteState = value;
            AudioMuteStateHandlers?.Invoke(value);
        }
    }
    
    public MuteState VideoMute
    {
        get => _videoMuteState;
        private set
        {
            if (_videoMuteState == value)
                return;
            _videoMuteState = value;
            VideoMuteStateHandlers?.Invoke(value);
        }
    }


    private readonly Dictionary<MuteState, string> _audioMuteStates = new Dictionary<MuteState, string>
    {
        { MuteState.Off, "0" },
        { MuteState.On, "1" },
    };

    public NavDecoder(string name, string ipAddress, Navigator navigator) 
        : base(name, AVEndpointType.Decoder, ipAddress, navigator)
    {
    }

    public void SetInput(uint deviceId) => Navigator.RouteAV(deviceId, DeviceNumber);
    public void SetVideo(uint deviceId) => Navigator.RouteVideo(deviceId, DeviceNumber);

    public void SetInput(NavEncoder encoder) => Navigator.RouteAV(encoder.DeviceNumber, DeviceNumber);
    

    public void SetAudioMute(MuteState muteState)
    {
        Send($"1*{_audioMuteStates[muteState]}Z");
        Send($"2*{_audioMuteStates[muteState]}Z");
    }

    public void ToggleAudioMute() => SetAudioMute(_audioMuteState == MuteState.Off ? MuteState.On : MuteState.Off);

    public void SetVideoMute(MuteState state) => Send(state == MuteState.On? "2B" : "0B");

    protected override Task Poll(CancellationToken arg)
    {
        Send($"I");
        return Task.CompletedTask;
    }

    protected override void HandleResponse(string response)
    {
        if (response.StartsWith("In"))
        {
            var streamId = response.Split(' ')[0][2..];
            StreamAddress = streamId;
            return;
        }

        if (response.StartsWith("Vmt"))
        {
            switch (int.Parse(response[3..]))
            {
                case 0:
                    VideoMute = MuteState.Off;
                    return;
                default:
                    VideoMute = MuteState.On;
                    return;
            }
        }

        if (response.StartsWith("Amt"))
        {
            switch (int.Parse(response[5..]))
            {
                case 0:
                    AudioMute = MuteState.Off;
                    break;
                case 1:
                    AudioMute = MuteState.On;
                    break;
            }
        }
    }

    protected override void ProcessConcatenatedResponse(string response)
    {

        if (!response.Contains('I'))
            return;
        
        var kvp = response.Split('I');
        switch (kvp[0])
        {
            case "Res":
                if (kvp[1].Contains("NOT DETECTED"))
                {
                    OutputConnectionStatus = ConnectionState.Disconnected;
                    OutputResolution = String.Empty;
                }
                else
                {
                    OutputConnectionStatus = ConnectionState.Connected;
                    OutputResolution = kvp[1];
                }
                break;
        }
    }
}