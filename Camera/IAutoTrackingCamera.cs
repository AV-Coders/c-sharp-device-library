namespace AVCoders.Camera;

public interface IAutoTrackingCamera : ICamera
{
    public void SetTracking(CameraTrackingMode mode);
}