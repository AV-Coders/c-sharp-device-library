using AVCoders.Core;

namespace AVCoders.Lighting;

public abstract class Light : DeviceBase
{
    private uint _brightness;
    public UintHandler? BrightnessChangeHandlers;

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
    }

    public override void PowerOn()
    {
        DoPowerOn();
    }

    protected abstract void DoPowerOn();

    protected abstract void DoPowerOff();

    protected abstract void DoSetLevel(int level);
}