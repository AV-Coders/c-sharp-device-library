using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class VideoMatrix : DeviceBase
{
    protected List<int> Sources;
    protected PowerState PowerState;
    protected CommunicationState CommunicationState;
    public CommunicationStateHandler? CommunicationStateHandlers;

    protected VideoMatrix(int numberOfOutputs, string name) : base(name)
    {
        Sources = new List<int>(numberOfOutputs);
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.Unknown;
    }

    public PowerState GetCurrentPowerState() => PowerState;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState;

    protected void UpdateCommunicationState(CommunicationState state)
    {
        CommunicationState = state;
        CommunicationStateHandlers?.Invoke(state);
    }

    public abstract void RouteVideo(int input, int output);
    public abstract void RouteAudio(int input, int output);
    public abstract void RouteAV(int input, int output);
}