using AVCoders.Core;

namespace AVCoders.Matrix;

public class NavDecoder : NavDeviceBase
{
    public MuteStateHandler? AudioMuteStateHandlers;
    public MuteStateHandler? VideoMuteStateHandlers;
    protected const string VideoRouteIssueKey = "video-tie";
    protected const string AudioRouteIssueKey = "audio-tie";
    private MuteState _audioMuteState = MuteState.Unknown;
    private MuteState _videoMuteState = MuteState.Unknown;
    private int? _desiredVideoInput;
    private int? _desiredAudioInput;
    private int? _currentVideoInput;
    private int? _currentAudioInput;

    public int? DesiredVideoInput => _desiredVideoInput;
    public int? DesiredAudioInput => _desiredAudioInput;
    
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

    public void SetInput(int deviceId)
    {
        using (PushProperties())
        {
            if (DeviceNumber == 0)
            {
                LogWarning("Not requesting a route as my device number is 0");
                return;
            }
            UpdateDesiredRoute(deviceId, deviceId);
            Navigator.RouteAV(deviceId, DeviceNumber);
        }
    }

    public void SetVideo(int deviceId)
    {
        using (PushProperties())
        {
            if (DeviceNumber == 0)
            {
                LogWarning("Not requesting a video route as my device number is 0");
                return;
            }

            UpdateDesiredRoute(deviceId, null);
            Navigator.RouteVideo(deviceId, DeviceNumber);
        }
    }

    public void SetAudio(int deviceId)
    {
        using (PushProperties())
        {
            if (DeviceNumber == 0)
            {
                LogWarning("Not requesting an audio route as my device number is 0");
                return;
            }

            UpdateDesiredRoute(null, deviceId);
            Navigator.RouteAudio(deviceId, DeviceNumber);
        }
    }

    public void SetInput(NavEncoder encoder)
    {
        UpdateDesiredRoute(encoder.DeviceNumber, encoder.DeviceNumber);
        Navigator.RouteAV(encoder.DeviceNumber, DeviceNumber);
    }

    internal void UpdateDesiredRoute(int? videoInput, int? audioInput)
    {
        if (videoInput != null)
            _desiredVideoInput = videoInput;
        if (audioInput != null)
            _desiredAudioInput = audioInput;
    }

    private void ProcessRouteState()
    {
        if (_desiredVideoInput != null && _currentVideoInput != null)
        {
            if (_currentVideoInput == _desiredVideoInput)
                ResolveIssue(VideoRouteIssueKey);
            else
                RaiseOngoingIssue(VideoRouteIssueKey, $"Video input is {_currentVideoInput}, should be {_desiredVideoInput}");
        }
        if (_desiredAudioInput != null && _currentAudioInput != null)
        {
            if (_currentAudioInput == _desiredAudioInput)
                ResolveIssue(AudioRouteIssueKey);
            else
                RaiseOngoingIssue(AudioRouteIssueKey, $"Audio input is {_currentAudioInput}, should be {_desiredAudioInput}");
        }
    }


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
        ProcessRouteState();
        return Task.CompletedTask;
    }

    protected override void HandleResponse(string response)
    {
        using (PushProperties())
        {
            if (response.StartsWith("In"))
            {
                var parts = response.Split(' ');
                if (parts.Length < 2 || parts[1] == "Usb")
                    return;
                var streamId = parts[0][2..];
                switch (parts[1])
                {
                    case "All":
                        StreamAddress = streamId;
                        if (int.TryParse(streamId, out var avInput))
                        {
                            _currentVideoInput = avInput;
                            _currentAudioInput = avInput;
                        }
                        break;
                    case "Vid":
                        StreamAddress = streamId;
                        if (int.TryParse(streamId, out var videoInput))
                            _currentVideoInput = videoInput;
                        break;
                    case "Aud":
                        if (int.TryParse(streamId, out var audioInput))
                            _currentAudioInput = audioInput;
                        break;
                }
                ProcessRouteState();
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
            else if (response.StartsWith("HplgO"))
            {
                OutputConnectionStatus = response.Remove(0, 5) == "1" ? ConnectionState.Connected : ConnectionState.Disconnected;

                switch (OutputConnectionStatus)
                {
                    case ConnectionState.Connected:
                        Poll(CancellationToken.None);
                        break;
                    case ConnectionState.Disconnected:
                        OutputHdcpStatus = HdcpStatus.Unknown;
                        OutputResolution = string.Empty;
                        break;
                }
            }
            else if (response.StartsWith("HdcpO"))
            {
                OutputHdcpStatus = ParseSinkHdcpStatus(response.Remove(0, 5));
            }
        }
    }

    protected override void ProcessConcatenatedResponse(string response)
    {
        if (response.StartsWith("VidI"))
            InputConnectionStatus = response[4..] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected;
        else if (response.StartsWith("HdcpI"))
            InputHdcpStatus = ParseSourceHdcpStatus(response[5..]);
        else if (response.StartsWith("HdcpO"))
            OutputHdcpStatus = ParseSinkHdcpStatus(response[5..]);
        else if (response.StartsWith("ResI"))
        {
            var resolution = response[4..];
            if (resolution.Contains("NOT DETECTED") || resolution.StartsWith("0x0"))
            {
                OutputConnectionStatus = ConnectionState.Disconnected;
                OutputResolution = string.Empty;
            }
            else
            {
                OutputConnectionStatus = ConnectionState.Connected;
                OutputResolution = resolution;
            }
        }
    }
}