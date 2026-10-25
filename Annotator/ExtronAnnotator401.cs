using System.Globalization;
using AVCoders.Core;

namespace AVCoders.Annotator;

public enum Annotator401Outputs : int
{
    All = 0,
    Output1 = 1,
    Output2 = 2,
    None = 3
}

public class ExtronAnnotator401 : AnnotatorBase
{
    private readonly string _fileprefix;
    private const string EscapeHeader = "\x1b";
    public static readonly ushort DefaultPort = 22023;
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OutputStepGap = TimeSpan.FromMilliseconds(200);
    private readonly ThreadWorker _pollWorker;
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;
    private readonly Annotator401Outputs _annotationOutputs;
    private readonly Annotator401Outputs _cursorOutputs;
    public DrawingTool? CurrentTool { get; private set; }

    private static readonly Dictionary<DrawingTool, int> ToolCodes = new()
    {
        { DrawingTool.Eraser, 0 },
        { DrawingTool.Pointer, 1 },
        { DrawingTool.Freehand, 2 },
        { DrawingTool.Highlighter, 3 },
        { DrawingTool.VectorLine, 4 },
        { DrawingTool.ArrowLine, 5 },
        { DrawingTool.Ellipse, 6 },
        { DrawingTool.Rectangle, 7 },
        { DrawingTool.Text, 8 },
        { DrawingTool.Spotlight, 9 },
        { DrawingTool.Zoom, 10 },
        { DrawingTool.Pan, 11 }
    };

    private static readonly Dictionary<int, DrawingTool> ToolsByCode =
        ToolCodes.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public ExtronAnnotator401(CommunicationClient client, string name, string fileprefix, Annotator401Outputs annotationOutputs = Annotator401Outputs.All, Annotator401Outputs cursorOutputs = Annotator401Outputs.All)
        : base(name, client)
    {
        _fileprefix = fileprefix;
        _annotationOutputs = annotationOutputs;
        _cursorOutputs = cursorOutputs;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        CommunicationClient.ResponseHandlers += HandleResponse;
        HandleConnectionState(CommunicationClient.ConnectionState);
    }

    private void HandleResponse(string response)
    {
        if (response.StartsWith("Ims1*"))
        {
            OnFileSaved?.Invoke(response.Substring(5).TrimEnd());
        }
        if (response.StartsWith("Ims0*"))
        {
            OnFileSaved?.Invoke(response.Substring(5).TrimEnd());
        }
        if (response.StartsWith("Draw"))
        {
            ProcessDraw(response);
        }
    }

    private void ProcessDraw(string response)
    {
        if (response.Length < 6
            || !int.TryParse(response.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int toolValue)
            || !ToolsByCode.TryGetValue(toolValue, out DrawingTool tool))
        {
            using (PushProperties())
                LogWarning("Unknown drawing tool in {Response}", response);
            return;
        }
        CurrentTool = tool;
        OnDrawingToolChanged?.Invoke(tool);
    }

    public override void Clear() => QueueCommands(("0EDIT", TimeSpan.Zero));

    public void StartCalibration() => WrapAndSendCommand("1PCAL");

    public void StopCalibration() => WrapAndSendCommand("0PCAL");

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState != ConnectionState.Connected)
            return;
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(_ => Initialise(), TaskScheduler.Default).Unwrap();
    }

    private async Task Initialise()
    {
        try
        {
            WrapAndSendCommand("3CV");
            await Task.Delay(ConnectStepGap);
            WrapAndSendCommand("ASHW");
            await Task.Delay(ConnectStepGap);
            WrapAndSendCommand($"P{_fileprefix}CFMT");
            await Task.Delay(ConnectStepGap);
            QueryTool();
        }
        catch (Exception e)
        {
            LogException(e, "Initialising the annotator failed");
        }
    }

    private Task Poll(CancellationToken arg)
    {
        using (PushProperties("Poll"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return Task.CompletedTask;

            WrapAndSendCommand("0TC");
        }
        return Task.CompletedTask;
    }

    public override void PowerOn() => QueueCommands(
        ($"{(int)_annotationOutputs}ASHW", OutputStepGap),
        ($"{(int)_cursorOutputs}CSHW", OutputStepGap));

    public override void PowerOff() => QueueCommands(
        ($"{(int)Annotator401Outputs.None}ASHW", OutputStepGap),
        ($"{(int)Annotator401Outputs.None}CSHW", OutputStepGap));

    private void QueueCommands(params (string Command, TimeSpan SpacingAfter)[] commands)
    {
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(_ => SendSpaced(commands), TaskScheduler.Default).Unwrap();
    }

    private async Task SendSpaced((string Command, TimeSpan SpacingAfter)[] commands)
    {
        try
        {
            foreach (var (command, spacingAfter) in commands)
            {
                WrapAndSendCommand(command);
                await Task.Delay(spacingAfter);
            }
        }
        catch (Exception e)
        {
            LogException(e, "Sending to the annotator failed");
        }
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }
    
    public override void SaveToInternalMemory() => SendCommand("W0*/graphics/MF|");
    public override void SaveToUsb() => SendCommand($"W1*/graphics/{_fileprefix}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.pngMF|");
    
    public override void SaveToNetworkShare(string folderPath) => SendCommand($"W0*/shares/{folderPath.Trim('/')}/{_fileprefix}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.pngMF|");
    public override void Save() => SendCommand("W9MF|");

    public override void SetTool(DrawingTool tool) => WrapAndSendCommand($"{ToolCodes[tool]}DRAW");

    public void QueryTool() => WrapAndSendCommand("DRAW");

    public void SetAnnotationOutput(Annotator401Outputs output) => QueueCommands(($"{(int)output}ASHW", TimeSpan.Zero));

    public void SetCursorOutput(Annotator401Outputs output) => QueueCommands(($"{(int)output}CSHW", TimeSpan.Zero));

    public void SetVideoMute(Annotator401Outputs output, MuteState state)
    {
        int outputNumber = output switch
        {
            Annotator401Outputs.Output1 => 1,
            Annotator401Outputs.Output2 => 2,
            Annotator401Outputs.All => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(output), output, null)
        };
        
        SendCommand(state == MuteState.On ? $"{outputNumber}*2B" : $"{outputNumber}*0B");
    }

    public override void SetVideoMute(MuteState state)
    {
        SendCommand(state == MuteState.On ? "0*2B" : "0*0B");
    }

    public void Reboot() => QueueCommands(("1boot", TimeSpan.Zero));
}