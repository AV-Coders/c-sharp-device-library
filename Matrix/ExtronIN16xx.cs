using AVCoders.Core;

namespace AVCoders.Matrix;

public class ExtronIn16Xx : VideoMatrix
{
    private readonly int _numberOfInputs;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    
    private readonly ThreadWorker _pollWorker;
    private const string EscapeHeader = "\x1b";

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name) 
        : base(1, communicationClient, name, CommandStringFormat.Ascii)
    {
        _numberOfInputs = numberOfInputs;
        PowerState = PowerState.Unknown;
        UpdateCommunicationState(CommunicationState.NotAttempted);
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
    }
    
    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private Task Poll(CancellationToken arg)
    {
        if(CommunicationClient.ConnectionState == ConnectionState.Connected)
            WrapAndSendCommand("CV");
        return Task.CompletedTask;
    }

    private void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command);
            UpdateCommunicationState(CommunicationState.Okay);
        }
        catch (Exception e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand($"{input}!");
    }

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand($"{input}%");
    }

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand($"{input}$");
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds < 502)
        {
            SendCommand($"\u001bT{seconds}SSAV\u0027");
        }
    }

    public void SetVideoMute(MuteState state)
    {
        SendCommand(state == MuteState.On ? "1*2B" : "1*0B");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        SendCommand(state == MuteState.On ? "2*2B" : "2*0B");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        SendCommand(state == MuteState.On ? "3*2B" : "3*0B");
    }
}