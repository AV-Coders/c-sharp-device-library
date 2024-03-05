using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class VideoMatrix : IDevice
{
    protected List<int> Sources;
    protected PowerState PowerState;
    protected CommunicationState CommunicationState;
    public LogHandler? LogHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;

    protected VideoMatrix(int numberOfOutputs)
    {
        Sources = new List<int>(numberOfOutputs);
        this.PowerState = PowerState.Unknown;
        this.CommunicationState = CommunicationState.Unknown;
    }

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;
    
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"VideoMatrix - {message}");
    }

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    public abstract void PowerOn();

    public abstract void PowerOff();

    public abstract void RouteVideo(int input, int output);
    public abstract void RouteAudio(int input, int output);
    public abstract void RouteAV(int input, int output);
}