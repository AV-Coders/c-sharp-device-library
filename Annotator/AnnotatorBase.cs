using AVCoders.Core;

namespace AVCoders.Annotator;

public enum DrawingTool
{
    Eraser,
    Pointer,
    Freehand,
    Highlighter,
    VectorLine,
    ArrowLine,
    Ellipse,
    Rectangle,
    Text,
    Spotlight,
    Zoom,
    Pan
}

public delegate void DrawingToolHandler(DrawingTool tool);

public abstract class AnnotatorBase(string name, CommunicationClient client)
    : DeviceBase(name, client)
{
    public ActionHandler? UsbSavedHandlers;
    public ActionHandler? InternalMemorySavedHandlers;
    public StringHandler? UsbFileSavedHandlers;
    public StringHandler? InternalMemoryFileSavedHandlers;
    public DrawingToolHandler? OnDrawingToolChanged;
    public abstract void Clear();
    public abstract void SaveToInternalMemory();
    public abstract void SaveToUsb();

    public abstract void SaveToNetworkShare(string folderPath);

    public abstract void Save();
    public abstract void SetVideoMute(MuteState state);
    public abstract void SetTool(DrawingTool tool);
}