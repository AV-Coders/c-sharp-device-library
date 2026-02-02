using AVCoders.Core;

namespace AVCoders.Lighting;

public record LightPreset(string Name);

public abstract class Light : DeviceBase
{
    private uint _brightness;
    public UintHandler? BrightnessChangeHandlers;
    public abstract List<LightPreset> Presets();

    protected Light(string name) 
        : base(name, CommunicationClient.None)
    {
    }
    
    public uint Brightness
    {
        get => _brightness;
        protected set
        {
            if (_brightness == value)
                return;
            _brightness = value;
            BrightnessChangeHandlers?.Invoke(value);
        }
    }
    
    public void SetLevel(int level)
    {
        if(level is >= 0 and <= 100)
            DoSetLevel(level);
    }

    public override void PowerOff()
    {
        DoPowerOff();
        AddEvent(EventType.Power, nameof(PowerState.Off));
    }

    public override void PowerOn()
    {
        DoPowerOn();
        AddEvent(EventType.Power, nameof(PowerState.On));
    }

    public void RecallPreset(int preset)
    {
        DoRecallPreset(preset);
        AddEvent(EventType.Preset, $"Preset {preset} recalled");
    }

    protected abstract void DoRecallPreset(int preset);

    protected abstract void DoPowerOn();

    protected abstract void DoPowerOff();

    protected abstract void DoSetLevel(int level);
}