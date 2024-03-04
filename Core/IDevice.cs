namespace AVCoders.Core;

public interface IDevice
{
    void PowerOn();

    void PowerOff();

    PowerState GetCurrentPowerState();

    CommunicationState GetCurrentCommunicationState();
}