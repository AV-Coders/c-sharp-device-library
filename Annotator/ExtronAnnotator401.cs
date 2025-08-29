using AVCoders.Core;

namespace AVCoders.Annotator;

public class ExtronAnnotator401 : DeviceBase
{
    public static readonly ushort DefaultPort = 22023;
    private readonly ThreadWorker _pollWorker;
    private const string EscapeHeader = "\x1b";

    public ExtronAnnotator401(CommunicationClient client, string name) : base(name, client)
    {
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

    public void SaveToNetworkDrive() => WrapAndSendCommand("3MCAP");

    public void SaveToInternalMemory() => WrapAndSendCommand("0MCAP");

    public void SaveToIQC() => WrapAndSendCommand("1MCAP");

    public void SaveToUSB() => WrapAndSendCommand("2MCAP");
}