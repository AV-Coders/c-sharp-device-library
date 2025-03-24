namespace AVCoders.Lighting;

public class CBusLight : Light
{
    private readonly byte _group;
    private readonly CBusRampTime _defaultRampTime;
    private readonly CBusInterface _interface;
    private readonly byte _powerOff = 0x01;
    private readonly byte _powerOn = 0x79;

    public CBusLight(string name, CBusInterface @interface, byte group, CBusRampTime defaultRampTime) : base(name)
    {
        _group = group;
        _defaultRampTime = defaultRampTime;
        _interface = @interface;
    }

    protected override void DoPowerOn()
    {
        _interface.SendPointToMultipointPayload(
            CBusInterface.LightingApplication,
            [_powerOn, _group]);
    }

    protected override void DoPowerOff()
    {
        _interface.SendPointToMultipointPayload(
            CBusInterface.LightingApplication,
            [_powerOff, _group]);
    }

    protected override void DoSetLevel(int level)
    {
        byte levelValue = (byte)(level * 2.55);
        _interface.SendPointToMultipointPayload(
            CBusInterface.LightingApplication,
            [(byte)_defaultRampTime, _group, levelValue]);
    }

    public void SetLevel(int level, CBusRampTime rampTime)
    {
        Level = level;
        byte levelValue = (byte)(level * 2.55);
        _interface.SendPointToMultipointPayload(
            CBusInterface.LightingApplication,
            [(byte)rampTime, _group, levelValue]);
    }

    public void StopRamping()
    {
        _interface.SendPointToMultipointPayload(
            CBusInterface.LightingApplication,
            [0x09, _group]);
    }
}