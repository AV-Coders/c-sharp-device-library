namespace AVCoders.Camera;

public delegate void TrackingModeChangedHandler(CameraTrackingMode mode);

public interface ITrackingCamera
{
    void SetTracking(CameraTrackingMode mode);
    CameraTrackingMode TrackingMode { get; }
    event Action<CameraTrackingMode>? OnTrackingModeChange;
}
