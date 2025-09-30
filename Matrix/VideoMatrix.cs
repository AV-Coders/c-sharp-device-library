using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class VideoMatrix(int numberOfOutputs, CommunicationClient client, string name)
    : DeviceBase(name, client)
{
    protected List<int> Sources = new(numberOfOutputs);

    public abstract void RouteVideo(int input, int output);
    public abstract void RouteAudio(int input, int output);
    public abstract void RouteAV(int input, int output);
}