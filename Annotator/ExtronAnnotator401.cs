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
    public static readonly ushort DefaultPort = 22023;
    private readonly ThreadWorker _pollWorker;
    private const string EscapeHeader = "\x1b";

    public ExtronAnnotator401(CommunicationClient client, string name, string fileprefix) : base(name, client)
    {
        _fileprefix = fileprefix;
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

    public override void PowerOn() { }

    public override void PowerOff() { }
    
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
    
    public void SaveToInternalMemory() => WrapAndSendCommand($"0*/graphics/{_fileprefix}-{DateTime.Now}.jpgMF");

    public void SaveToUSB() => WrapAndSendCommand($"1*/graphics/{_fileprefix}-{DateTime.Now}.jpgMF");
    
    public void SetAnnotationOutput(Annotator401Outputs output) => WrapAndSendCommand($"{(int)output}ASHW");
    
    public void SetCursorOutput(Annotator401Outputs output) => WrapAndSendCommand($"{(int)output}CSHW");
}