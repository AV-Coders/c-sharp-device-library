using AVCoders.Core;
using AVCoders.Display;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Display;

public class DisplayUiSignalR : DeviceBase
{
    private readonly DisplayManager _displayManager;
    private readonly IHubContext<DisplayHub, IDisplayHub> _hubContext;

    public DisplayUiSignalR(DisplayManager displayManager, IHubContext<DisplayHub, IDisplayHub> hubContext)
        : base(displayManager.Name, CommunicationClient.None)
    {
        _displayManager = displayManager;
        _hubContext = hubContext;

        DisplayHub.RegisterDisplayManager(Name, displayManager);
        _displayManager.PowerStateHandlers += OnPowerStateChanged;
        _displayManager.OnInputChanged += OnInputChanged;
        _displayManager.OnVolumeChanged += OnVolumeChanged;
        _displayManager.OnAudioMuteChanged += OnAudioMuteChanged;
    }

    private async void OnPowerStateChanged(PowerState state) =>
        await _hubContext.Clients.Group(Name).OnPowerStateChanged(state);

    private async void OnInputChanged(Input input) =>
        await _hubContext.Clients.Group(Name).OnInputChanged(input);

    private async void OnVolumeChanged(int volume) =>
        await _hubContext.Clients.Group(Name).OnVolumeChanged(volume);

    private async void OnAudioMuteChanged(MuteState state) =>
        await _hubContext.Clients.Group(Name).OnAudioMuteChanged(state);

    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            LogInformation("Turning on display");
            _displayManager.PowerOn();
        }
    }

    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            LogInformation("Turning off display");
            _displayManager.PowerOff();
        }
    }
}
