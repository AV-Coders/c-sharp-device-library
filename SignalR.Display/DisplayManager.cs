using AVCoders.Core;
using AVCoders.Display;

namespace AVCoders.SignalR.Display;

public class DisplayManager : DeviceBase
{
    private readonly AVCoders.Display.Display _display;
    public List<Input> SupportedInputs => _display.SupportedInputs;
    public Input Input => _display.Input;
    public int Volume => _display.Volume;
    public MuteState AudioMute => _display.AudioMute;

    public event Action<Input>? OnInputChanged;
    public event Action<int>? OnVolumeChanged;
    public event Action<MuteState>? OnAudioMuteChanged;

    public DisplayManager(AVCoders.Display.Display display) : base(display.Name, display.CommunicationClient)
    {
        _display = display;
        _display.PowerStateHandlers += x => PowerState = x;
        _display.InputHandlers += x => OnInputChanged?.Invoke(x);
        _display.VolumeLevelHandlers += x => OnVolumeChanged?.Invoke(x);
        _display.MuteStateHandlers += x => OnAudioMuteChanged?.Invoke(x);
    }

    public override void PowerOn() => _display.PowerOn();
    public override void PowerOff() => _display.PowerOff();
    public void TogglePower() => _display.TogglePower();
    public void SetInput(Input input) => _display.SetInput(input);
    public void SetVolume(int volume) => _display.SetVolume(volume);
    public void LevelUp(int amount) => _display.LevelUp(amount);
    public void LevelDown(int amount) => _display.LevelDown(amount);
    public void SetAudioMute(MuteState state) => _display.SetAudioMute(state);
    public void ToggleAudioMute() => _display.ToggleAudioMute();
}
