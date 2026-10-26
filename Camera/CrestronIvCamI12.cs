using System.Net;
using System.Net.Sockets;
using AVCoders.Core;

namespace AVCoders.Camera;

public class CrestronIvCamI12 : CrestronIvCam, ITrackingCamera
{
    public const int UnknownCamera = 0;
    public const int PrimaryCamera = 1;
    private const int HomeShotPreset = 0;
    private const int TrackingShotPreset = 1;
    private const byte StartTrackingPreset = 80;
    private const byte PauseTrackingPreset = 81;
    private const int ResumeIntelligentSwitchingPreset = 84;
    private const int HostFeedPreset = 85;
    private const int PresenterTrackingFeedPreset = 86;
    private const byte TrackingActive = 0x02;
    private const byte TrackingPaused = 0x03;
    private const int TrackingReplyLength = 4;
    private const byte IntelligentSwitchingOnReply = 0x01;
    private const int OutputReplyLength = 5;
    private const int CompanionReplyLength = 12;
    private const int LiveCameraCheckSeconds = 1;

    private static readonly int[] CompanionCameras = [2, 3, 4, 5];

    private static readonly int[] BlockedRecallPresets =
    [
        StartTrackingPreset, PauseTrackingPreset, ResumeIntelligentSwitchingPreset, HostFeedPreset,
        PresenterTrackingFeedPreset, OsdMenuPreset, RebootPreset
    ];

    private static readonly int[] BlockedSavePresets =
    [
        HomeShotPreset, TrackingShotPreset, StartTrackingPreset, PauseTrackingPreset, ResumeIntelligentSwitchingPreset,
        HostFeedPreset, PresenterTrackingFeedPreset, OsdMenuPreset, RebootPreset
    ];

    private readonly Dictionary<int, string?> _companionIps = CompanionCameras.ToDictionary(camera => camera, _ => (string?)null);

    private CameraTrackingMode _trackingMode = CameraTrackingMode.Unknown;
    private int _activeCamera = UnknownCamera;
    private PowerState _intelligentSwitching = PowerState.Unknown;

    public CameraTrackingMode TrackingMode
    {
        get => _trackingMode;
        private set
        {
            if (_trackingMode == value)
                return;
            _trackingMode = value;
            OnTrackingModeChange?.Invoke(_trackingMode);
        }
    }

    public int ActiveCamera
    {
        get => _activeCamera;
        private set
        {
            if (_activeCamera == value)
                return;
            _activeCamera = value;
            OnActiveCameraChange?.Invoke(_activeCamera);
        }
    }

    public PowerState IntelligentSwitching
    {
        get => _intelligentSwitching;
        private set
        {
            if (_intelligentSwitching == value)
                return;
            _intelligentSwitching = value;
            OnIntelligentSwitchingChange?.Invoke(_intelligentSwitching);
        }
    }

    public IReadOnlyDictionary<int, string?> CompanionIps
    {
        get { lock (_companionIps) return new Dictionary<int, string?>(_companionIps); }
    }

    public event Action<CameraTrackingMode>? OnTrackingModeChange;
    public event Action<int>? OnActiveCameraChange;
    public event Action<PowerState>? OnIntelligentSwitchingChange;
    public event Action<int, string?>? OnCompanionIpChange;

    public IReadOnlyList<CameraTrackingMode> SupportedTrackingModes { get; } =
        [CameraTrackingMode.Auto, CameraTrackingMode.Disabled];

    protected override IReadOnlyCollection<int> PresetsBlockedOnRecall => BlockedRecallPresets;

    protected override IReadOnlyCollection<int> PresetsBlockedOnSave => BlockedSavePresets;

    public CrestronIvCamI12(CommunicationClient client, string name, Dictionary<int, string> presetNames, byte cameraId = 1, int pollTime = 30, ushort rtspPort = DefaultRtspPort)
        : base(client, name, presetNames, cameraId, pollTime, rtspPort)
    {
        var liveCameraWorker = new ThreadWorker(CheckLiveCamera, TimeSpan.FromSeconds(LiveCameraCheckSeconds), waitFirst: true);
        liveCameraWorker.Restart();
    }

    public void SetTracking(CameraTrackingMode mode)
    {
        using (PushProperties("SetTracking"))
        {
            if (!SupportedTrackingModes.Contains(mode))
            {
                LogWarning("Tracking mode {mode} isn't supported", mode);
                return;
            }
            var preset = mode == CameraTrackingMode.Auto ? StartTrackingPreset : PauseTrackingPreset;
            var description = mode == CameraTrackingMode.Auto ? "start tracking" : "pause tracking";
            void Confirm() => TrackingMode = mode;
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0x01, 0x04, 0x3F, 0x02, preset, CommandFooter],
                description, sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
        }
    }

    public void SelectCamera(int camera)
    {
        using (PushProperties("SelectCamera"))
        {
            if (camera != PrimaryCamera && !CompanionCameras.Contains(camera))
            {
                LogWarning("Camera {camera} can't be selected, it must be {primary} to {last}", camera, PrimaryCamera, CompanionCameras[^1]);
                AddEvent(EventType.Error, $"Camera {camera} can't be selected");
                return;
            }
            void Confirm()
            {
                ActiveCamera = camera;
                IntelligentSwitching = PowerState.Off;
            }
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0xC2, 0x01, 0x08, (byte)camera, CommandFooter],
                $"select camera {camera}", sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
        }
    }

    public void SetIntelligentSwitching(PowerState state)
    {
        using (PushProperties("SetIntelligentSwitching"))
        {
            if (state != PowerState.On && state != PowerState.Off)
            {
                LogWarning("Intelligent switching can't be set to {state}", state);
                return;
            }
            byte function = state == PowerState.On ? (byte)0x08 : (byte)0x0B;
            var description = state == PowerState.On ? "turn intelligent switching on" : "turn intelligent switching off";
            void Confirm() => IntelligentSwitching = state;
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0xC2, 0x01, function, 0x00, CommandFooter],
                description, sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
        }
    }

    public bool SetCompanionIp(int camera, string ipAddress)
    {
        using (PushProperties("SetCompanionIp"))
        {
            if (!CompanionCameras.Contains(camera))
            {
                LogWarning("Companion camera {camera} doesn't exist, it must be {first} to {last}", camera, CompanionCameras[0], CompanionCameras[^1]);
                AddEvent(EventType.Error, $"Companion camera {camera} doesn't exist");
                return false;
            }
            if (!IPAddress.TryParse(ipAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork
                                                                 || address.ToString() != ipAddress)
            {
                LogWarning("Companion camera {camera} wasn't set, {ipAddress} isn't an IPv4 address", camera, ipAddress);
                AddEvent(EventType.Error, $"Companion camera {camera} wasn't set, {ipAddress} isn't an IPv4 address");
                return false;
            }
            var nibbles = address.GetAddressBytes().SelectMany(octet => new[] { (byte)(octet >> 4), (byte)(octet & 0x0F) });
            void Confirm() => UpdateCompanionIp(camera, CompanionIpText(address));
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0xC2, 0x01, 0x09, (byte)camera, ..nibbles, CommandFooter],
                $"set companion camera {camera} to {ipAddress}", sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
            return true;
        }
    }

    public void ClearCompanionIps()
    {
        using (PushProperties("ClearCompanionIps"))
        {
            void Confirm()
            {
                foreach (var camera in CompanionCameras)
                    UpdateCompanionIp(camera, "");
            }
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0xC2, 0x01, 0x0A, 0x00, CommandFooter],
                "clear the companion cameras", sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
        }
    }

    protected override void SendPollInquiries()
    {
        base.SendPollInquiries();
        SendInquiry([_header, 0x09, 0x08, 0x01, CommandFooter], TrackingReplyLength, "query the tracking state", HandleTrackingReply);
        QueryActiveCamera();
        foreach (var camera in CompanionCameras)
            SendInquiry([_header, 0xC2, 0x09, 0x09, (byte)camera, CommandFooter], CompanionReplyLength,
                $"query companion camera {camera}", reply => HandleCompanionReply(camera, reply));
    }

    private Task CheckLiveCamera(CancellationToken token)
    {
        if (IntelligentSwitching != PowerState.On || !DeviceSendsResponses || CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        QueryActiveCamera();
        return Task.CompletedTask;
    }

    private void QueryActiveCamera() =>
        SendInquiry([_header, 0xC2, 0x09, 0x08, CommandFooter], OutputReplyLength, "query the active camera", HandleOutputReply);

    private void HandleTrackingReply(byte[] payload)
    {
        TrackingMode = payload[2] switch
        {
            TrackingActive => CameraTrackingMode.Auto,
            TrackingPaused => CameraTrackingMode.Disabled,
            _ => TrackingMode
        };
    }

    private void HandleOutputReply(byte[] payload)
    {
        if (payload.Length != OutputReplyLength)
        {
            LogWarning("Expected a {length} byte active camera reply but got {reply}", OutputReplyLength, BitConverter.ToString(payload));
            return;
        }
        IntelligentSwitching = payload[2] == IntelligentSwitchingOnReply ? PowerState.On : PowerState.Off;
        ActiveCamera = payload[3];
    }

    private void HandleCompanionReply(int camera, byte[] payload)
    {
        if (payload.Length != CompanionReplyLength || payload[2] != camera)
        {
            LogWarning("Expected a {length} byte reply for companion camera {camera} but got {reply}",
                CompanionReplyLength, camera, BitConverter.ToString(payload));
            return;
        }
        var octets = payload[3..11].Chunk(2).Select(pair => (byte)(pair[0] << 4 | pair[1])).ToArray();
        UpdateCompanionIp(camera, CompanionIpText(new IPAddress(octets)));
    }

    private static string CompanionIpText(IPAddress address) => address.Equals(IPAddress.Any) ? "" : address.ToString();

    private void UpdateCompanionIp(int camera, string ipAddress)
    {
        lock (_companionIps)
        {
            if (_companionIps[camera] == ipAddress)
                return;
            _companionIps[camera] = ipAddress;
        }
        OnCompanionIpChange?.Invoke(camera, ipAddress);
    }
}
