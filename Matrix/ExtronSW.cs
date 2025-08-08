using AVCoders.Core;
using Serilog;

namespace AVCoders.Matrix;

public class ExtronSw : VideoMatrix
{
    private readonly CommunicationClient _communicationClient;
    private readonly int _numberOfInputs;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    public ExtronSw(CommunicationClient communicationClient, int numberOfInputs, string name) : base(1, name)
    {
        _communicationClient = communicationClient;
        _numberOfInputs = numberOfInputs;
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

    public override void PowerOn() {    }

    public override void PowerOff() {    }

    public override void RouteVideo(int input, int output)
    {
        Log.Error("This device doesn't support video breakaway");
    }

    public override void RouteAudio(int input, int output)
    {
        Log.Error("This device doesn't support audio breakaway");
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand($"{input}!");
    }
}