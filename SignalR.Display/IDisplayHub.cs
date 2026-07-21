using AVCoders.Core;
using AVCoders.Display;

namespace AVCoders.SignalR.Display;

public interface IDisplayHub
{
    Task OnPowerStateChanged(PowerState state);
    Task OnSupportedInputsChanged(List<Input> inputs);
    Task OnInputChanged(Input input);
    Task OnVolumeChanged(int volume);
    Task OnAudioMuteChanged(MuteState state);
}
