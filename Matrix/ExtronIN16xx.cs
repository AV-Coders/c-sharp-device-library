using System.Diagnostics;
using AVCoders.Core;

namespace AVCoders.Matrix;

public class ExtronIn16Xx : VideoMatrix
{
    private readonly CommunicationClient _communicationClient;
    private readonly int _numberOfInputs;

    public ExtronIn16Xx(CommunicationClient communicationClient, int numberOfInputs) : base(1)
    {
        _communicationClient = communicationClient;
        _numberOfInputs = numberOfInputs;
        PowerState = PowerState.Unknown;
        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

    private void SendCommand(String command)
    {
        try
        {
            _communicationClient.Send(command);
            UpdateCommunicationState(CommunicationState.Okay);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"ExtronIN16xx - Communication error: {e.Message}");
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    public override void RouteAV(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand(String.Format("{0}!", input));
    }

    public override void PowerOn()
    {
        throw new NotImplementedException();
    }

    public override void PowerOff()
    {
        throw new NotImplementedException();
    }

    public override void RouteVideo(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand(String.Format("{0}%", input));
    }

    public override void RouteAudio(int input, int output)
    {
        if (input > 0 && input <= _numberOfInputs)
            SendCommand(String.Format("{0}$", input));
    }

    public void SetSyncTimeout(int seconds)
    {
        if (seconds < 502)
        {
            SendCommand(String.Format("\u001bT{0}SSAV\u0027", seconds));
        }
    }
}