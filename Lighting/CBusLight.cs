namespace AVCoders.Lighting;

public class CBusLight : Light
{
    private readonly byte _group;
    private readonly CBusRampTime _defaultRampTime;
    private readonly CBusSerialInterface _interface;
    private readonly byte _powerOff = 0x01;
    private readonly byte _powerOn = 0x79;
    private readonly byte _recallPreset = 0x65;

    public CBusLight(string name, CBusSerialInterface @interface, byte group, CBusRampTime defaultRampTime) : base(name)
    {
        _group = group;
        _defaultRampTime = defaultRampTime;
        _interface = @interface;
    }

    protected override void DoRecallPreset(int preset)
    {
        _interface.SendPointToMultipointPayload(
            CBusSerialInterface.SceneApplication,
            [_group, (byte)preset], true
            );
    }

    protected override void DoPowerOn()
    {
        _interface.SendPointToMultipointPayload(
            CBusSerialInterface.LightingApplication,
            [_powerOn, _group], true);
    }

    protected override void DoPowerOff()
    {
        _interface.SendPointToMultipointPayload(
            CBusSerialInterface.LightingApplication,
            [_powerOff, _group], true);
    }

    protected override void DoSetLevel(int level)
    {
        byte levelValue = (byte)(level * 2.55);
        _interface.SendPointToMultipointPayload(
            CBusSerialInterface.LightingApplication,
            [(byte)_defaultRampTime, _group, levelValue], true);
    }

    public void SetLevel(int level, CBusRampTime rampTime)
    {
        Brightness = (uint) level;
        byte levelValue = (byte)(level * 2.55);
        _interface.SendPointToMultipointPayload(
            CBusSerialInterface.LightingApplication,
            [(byte)rampTime, _group, levelValue], true);
    }

    public void StopRamping()
    {
        _interface.SendPointToMultipointPayload(
            CBusSerialInterface.LightingApplication,
            [0x09, _group], false);
    }
}