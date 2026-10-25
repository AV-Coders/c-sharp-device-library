using AVCoders.Core;

namespace AVCoders.Camera;

public class AverVisca : SonyVisca, ITrackingCamera
{
    public static readonly SerialSpec DefaultSerialConfig = new SerialSpec(
        SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232
    );

    private static readonly Dictionary<CameraTrackingMode, byte> TrackingCodes = new()
    {
        { CameraTrackingMode.TriggerOnce, 0x00 },
        { CameraTrackingMode.Disabled, 0x01 },
        { CameraTrackingMode.Auto, 0x02 },
        { CameraTrackingMode.Manual, 0x03 }
    };

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
        [CameraTrackingMode.Disabled, CameraTrackingMode.Auto, CameraTrackingMode.Manual, CameraTrackingMode.TriggerOnce];

    public AverVisca(CommunicationClient client, string name, Dictionary<int, string> presetNames, bool useIpHeaders = false, byte cameraId = 1) :
        base(client, useIpHeaders, name, presetNames, cameraId)
    {
    }

    public void SetTracking(CameraTrackingMode mode)
    {
        using (PushProperties("SetTracking"))
        {
            if (!TrackingCodes.TryGetValue(mode, out var code))
            {
                LogWarning("Tracking mode {mode} has no command", mode);
                return;
            }
            void Confirm()
            {
                TrackingMode = mode;
                LogVerbose("Tracking Mode: {mode}", mode);
            }
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0x01, 0x04, 0x7D, code, 0x00, CommandFooter],
                $"set tracking to {mode}", sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
        }
    }

    protected override void DoZoomStop()
    {
        base.DoZoomStop();
        TrackingMode = CameraTrackingMode.Disabled;
    }

    protected override void DoPanTiltStop()
    {
        base.DoPanTiltStop();
        TrackingMode = CameraTrackingMode.Disabled;
    }

    public override void DoRecallPreset(int presetNumber)
    {
        base.DoRecallPreset(presetNumber);
        TrackingMode = CameraTrackingMode.Disabled;
    }

    public override void SavePreset(int presetNumber)
    {
        base.SavePreset(presetNumber);
        TrackingMode = CameraTrackingMode.Disabled;
    }
}