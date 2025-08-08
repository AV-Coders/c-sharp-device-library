using System.Diagnostics;
using AVCoders.Core;

namespace AVCoders.Matrix;

public class ExtronIn16Xx : VideoMatrix
{
    private readonly CommunicationClient _communicationClient;
    private readonly int _numberOfInputs;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs, string name) : base(1, name)
    {
        _communicationClient = communicationClient;
        _numberOfInputs = numberOfInputs;
        PowerState = PowerState.Unknown;
        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

    private void SendCommand(string command)
    {
        try
        {
            _communicationClient.Send(command);
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
}