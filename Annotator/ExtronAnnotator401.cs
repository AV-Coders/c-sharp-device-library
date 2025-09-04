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

public class ExtronAnnotator401 : DeviceBase
{
    private readonly string _fileprefix;
    private const string EscapeHeader = "\x1b";
    public static readonly ushort DefaultPort = 22023;
    private readonly ThreadWorker _pollWorker;
    private readonly Annotator401Outputs _annotationOutputs;
    private readonly Annotator401Outputs _cursorOutputs;

    public ExtronAnnotator401(CommunicationClient client, string name, string fileprefix, Annotator401Outputs annotationOutputs = Annotator401Outputs.All, Annotator401Outputs cursorOutputs = Annotator401Outputs.All) : base(name, client)
    {
        _fileprefix = fileprefix;
        _annotationOutputs = annotationOutputs;
        _cursorOutputs = cursorOutputs;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        CommunicationClient.ResponseHandlers += HandleResponse;
    }

    private void HandleResponse(string value)
    {
        
    }

    public void Clear() => WrapAndSendCommand("0EDIT");

    public void StartCalibration() => WrapAndSendCommand("1PCAL");

    public void StopCalibration() => WrapAndSendCommand("0PCAL");

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState != ConnectionState.Connected)
            return;
        WrapAndSendCommand("3CV");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        WrapAndSendCommand("ASHW");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        WrapAndSendCommand($"P{_fileprefix}CFMT");
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

    public override void PowerOn()
    {
        SetAnnotationOutput(_annotationOutputs);
        SetCursorOutput(_cursorOutputs);
    }

    public override void PowerOff()
    {
        SetAnnotationOutput(Annotator401Outputs.None);
        SetCursorOutput(Annotator401Outputs.None);
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
    
    public void SaveToInternalMemory() => WrapAndSendCommand("0*/annotated/MF");

    public void SaveToUSB() => WrapAndSendCommand("1*/annotated/MF");
    
    public void SetAnnotationOutput(Annotator401Outputs output) => WrapAndSendCommand($"{(int)output}ASHW");
    
    public void SetCursorOutput(Annotator401Outputs output) => WrapAndSendCommand($"{(int)output}CSHW");
}