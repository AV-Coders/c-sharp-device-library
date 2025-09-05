using AVCoders.Core;

namespace AVCoders.Annotator;

public abstract class AnnotatorBase(string name, CommunicationClient client) : DeviceBase(name, client)
{
    public abstract void Clear();
    public abstract void SaveToInternalMemory();
    public abstract void SaveToUsb();
}