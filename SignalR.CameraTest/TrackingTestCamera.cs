using AVCoders.Camera;

namespace AVCoders.SignalR.Camera.Tests;

public class TrackingTestCamera : TestCamera, ITrackingCamera
{
    public readonly List<CameraTrackingMode> SetTrackingCalls = new();

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

    public TrackingTestCamera(string name = "TrackingCam") : base(name) { }

    public void SetTracking(CameraTrackingMode mode)
    {
        SetTrackingCalls.Add(mode);
        TrackingMode = mode;
    }

    public void SetTrackingModeForTest(CameraTrackingMode mode) => TrackingMode = mode;
}
