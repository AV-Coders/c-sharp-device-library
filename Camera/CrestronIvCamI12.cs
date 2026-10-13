using AVCoders.Core;

namespace AVCoders.Camera;

public class CrestronIvCamI12 : CrestronIvCam, ITrackingCamera
{
    private const int HomeShotPreset = 0;
    private const int TrackingShotPreset = 1;
    private const byte StartTrackingPreset = 80;
    private const byte PauseTrackingPreset = 81;
    private const int ResumeIntelligentSwitchingPreset = 84;
    private const int HostFeedPreset = 85;
    private const int PresenterTrackingFeedPreset = 86;
    private const byte TrackingActive = 0x02;
    private const byte TrackingPaused = 0x03;

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

    private CameraTrackingMode _trackingMode = CameraTrackingMode.Unknown;

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

    public event Action<CameraTrackingMode>? OnTrackingModeChange;

    public IReadOnlyList<CameraTrackingMode> SupportedTrackingModes { get; } =
        [CameraTrackingMode.Auto, CameraTrackingMode.Disabled];

    protected override IReadOnlyCollection<int> PresetsBlockedOnRecall => BlockedRecallPresets;

    protected override IReadOnlyCollection<int> PresetsBlockedOnSave => BlockedSavePresets;

    public CrestronIvCamI12(CommunicationClient client, string name, Dictionary<int, string> presetNames, byte cameraId = 1, int pollTime = 30, ushort rtspPort = DefaultRtspPort)
        : base(client, name, presetNames, cameraId, pollTime, rtspPort)
    {
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

    protected override void SendPollInquiries()
    {
        base.SendPollInquiries();
        SendInquiry([_header, 0x09, 0x08, 0x01, CommandFooter], "query the tracking state", HandleTrackingReply);
    }

    private void HandleTrackingReply(byte[] payload)
    {
        TrackingMode = payload[2] switch
        {
            TrackingActive => CameraTrackingMode.Auto,
            TrackingPaused => CameraTrackingMode.Disabled,
            _ => TrackingMode
        };
    }
}
