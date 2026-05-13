namespace AVCoders.Camera;

public interface ITrackingCamera
{
    void SetTracking(CameraTrackingMode mode);
    CameraTrackingMode TrackingMode { get; }
    event Action<CameraTrackingMode>? OnTrackingModeChange;
}
