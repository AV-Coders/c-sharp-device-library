namespace AVCoders.Core;

public interface IDevice
{
    PowerState PowerState { get; }

    PowerState DesiredPowerState { get; }

    void PowerOn();

    void PowerOff();
}
