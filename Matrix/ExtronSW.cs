using AVCoders.Core;
using Serilog;

namespace AVCoders.Matrix;

public class ExtronSw : VideoMatrix
{
    private readonly int _numberOfInputs;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    public ExtronSw(CommunicationClient communicationClient, int numberOfInputs, string name) 
        : base(1, communicationClient, name)
    {
        _numberOfInputs = numberOfInputs;
    }

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
        {
            SendCommand($"{input}!");
            AddEvent(EventType.Input, $"Switched to input {input}");
        }
        else
        {
            AddEvent(EventType.Input, $"Not switching to input {input} as it is out of range, must be between 1 and {_numberOfInputs}");
            Log.Error("Not switching to input {Input} as it is out of range, must be between 1 and {NumberOfInputs}", input, _numberOfInputs);
        }
    }
}