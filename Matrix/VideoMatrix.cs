using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class VideoMatrix(int numberOfOutputs, CommunicationClient client, string name)
    : DeviceBase(name, client)
{
    protected List<int> Sources = new(numberOfOutputs);
    public abstract int NumberOfOutputs { get; }
    public abstract int NumberOfInputs { get; }
    public abstract bool RequiresOutputSpecification { get; }
    public abstract bool SupportsVideoBreakaway { get; }
    public abstract void RouteVideo(int input, int output);
    public abstract bool SupportsAudioBreakaway { get; }
    public abstract void RouteAudio(int input, int output);
    public abstract void RouteAV(int input, int output);
}