using AVCoders.Core;

namespace AVCoders.Annotator;

public abstract class AnnotatorBase(string name, CommunicationClient client, CommandStringFormat commandStringFormat)
    : DeviceBase(name, client, commandStringFormat)
{
    public ActionHandler? UsbSavedHandlers;
    public ActionHandler? InternalMemorySavedHandlers;
    public StringHandler? UsbFileSavedHandlers;
    public StringHandler? InternalMemoryFileSavedHandlers;
    public abstract void Clear();
    public abstract void SaveToInternalMemory();
    public abstract void SaveToUsb();
    public abstract void SetVideoMute(MuteState state);
}