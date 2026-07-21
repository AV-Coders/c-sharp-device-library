using AVCoders.Camera;
using AVCoders.Core;

namespace AVCoders.SignalR.Camera;

public interface ICameraHub
{
    Task OnPowerStateChanged(PowerState state);
    Task OnPresetRecalled(int presetIndex);
    Task OnPresetCleared();
    Task OnPresetDefinitionChanged(Dictionary<int, string> presets);
    Task OnTrackingCapabilityChanged(bool supportsTracking);
    Task OnTrackingModeChanged(CameraTrackingMode mode);
}