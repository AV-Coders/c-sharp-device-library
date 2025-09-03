using System.Text;
using AVCoders.Core;

namespace AVCoders.Matrix;

public class ExtronIn18Xx : VideoMatrix
{
    private readonly int _numberOfInputs;
    private const string EscapeHeader = "\x1b";
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    public ExtronIn18Xx(CommunicationClient communicationClient, int numberOfInputs, string name) : base(1, communicationClient, name)
    {
        _numberOfInputs = numberOfInputs;
        PowerState = PowerState.Unknown;
        UpdateCommunicationState(CommunicationState.NotAttempted);
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
            SendCommand($"{input}*1!");
    }

    public void RouteAV(int input, List<int> outputs)
    {
        if (input <= 0 || input > _numberOfInputs)
            return;
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}!"));
        sb.Append('\r');
        SendCommand(sb.ToString());
    }

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand($"{input}*1%");
    }

    public void RouteVideo(int input, List<int> outputs)
    {
        if (input <= 0 || input > _numberOfInputs)
            return;
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}%"));
        sb.Append('\r');
        SendCommand(sb.ToString());
    }

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand($"{input}*1$");
    }

    public void RouteAudio(int input, List<int> outputs)
    {
        if (input <= 0 || input > _numberOfInputs)
            return;
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}$"));
        sb.Append('\r');
        SendCommand(sb.ToString());
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds < 502)
        {
            SendCommand($"\u001bT{seconds}SSAV\u0027");
        }
    }
}