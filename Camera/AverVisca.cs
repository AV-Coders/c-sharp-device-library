using AVCoders.Core;
using Serilog;

namespace AVCoders.Camera;

public class AverVisca : SonyVisca, ITrackingCamera
{
    public static readonly SerialSpec DefaultSerialConfig = new SerialSpec(
        SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232
    );

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

    public AverVisca(CommunicationClient client, string name, Dictionary<int, string> presetNames, bool useIpHeaders = false, byte cameraId = 1) :
        base(client, useIpHeaders, name, presetNames, cameraId)
    {
    }

    public void SetTracking(CameraTrackingMode mode)
    {
        using (PushProperties("SetTracking"))
        {
            switch (mode)
            {
                case CameraTrackingMode.Auto:
                    SendCommand([_header, 0x01, 0x04, 0x7D, 0x02, 0x00, CommandFooter]);
                    Log.Verbose("Tracking Mode: Auto");
                    break;
                case CameraTrackingMode.Disabled:
                    SendCommand([_header, 0x01, 0x04, 0x7D, 0x01, 0x00, CommandFooter]);
                    Log.Verbose("Tracking Mode: Disabled");
                    break;
                case CameraTrackingMode.TriggerOnce:
                    SendCommand([_header, 0x01, 0x04, 0x7D, 0x00, 0x00, CommandFooter]);
                    Log.Verbose("Tracking Mode: Trigger Once");
                    break;
                case CameraTrackingMode.Manual:
                    SendCommand([_header, 0x01, 0x04, 0x7D, 0x03, 0x00, CommandFooter]);
                    Log.Verbose("Tracking Mode: Manual");
                    break;
            }
            TrackingMode = mode;
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