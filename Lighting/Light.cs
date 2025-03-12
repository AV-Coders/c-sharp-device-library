using AVCoders.Core;

namespace AVCoders.Lighting;

public abstract class Light : DeviceBase
{
    private int _level;
    public IntHandler? LevelChangeHandlers;

    public int Level
    {
        get => _level;
        protected set
        {
            if(_level == value) 
                return;
            _level = value;
            LevelChangeHandlers?.Invoke(value);
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