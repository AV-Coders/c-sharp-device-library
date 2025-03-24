using AVCoders.Core;

namespace AVCoders.Matrix;

public class ExtronDtpCpxx : VideoMatrix
{
    private readonly CommunicationClient _communicationClient;

    public ExtronDtpCpxx(CommunicationClient communicationClient, int numberOfOutputs, string name) : base(numberOfOutputs, name)
    {
        _communicationClient = communicationClient;
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
            Error(e.Message);
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    public override void RouteAV(int input, int output)
    {
        if (output == 0)
        {
            SendCommand(String.Format("{0}*!", input));
        }
        else
        {
            SendCommand(String.Format("{0}*{1}!", input, output));
        }
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
        if (output == 0)
        {
            SendCommand(String.Format("{0}*%", input));
        }
        else
        {
            SendCommand(String.Format("{0}*{1}%", input, output));
        }
    }

    public override void RouteAudio(int input, int output)
    {
        if (output == 0)
        {
            SendCommand(String.Format("{0}*$", input));
        }
        else
        {
            SendCommand(String.Format("{0}*{1}$", input, output));
        }
    }

    public void SetSyncTimeout(int seconds, int output)
    {
        if (seconds < 502)
        {
            SendCommand(String.Format("\u001bT{0}*{1}SSAV\u0027", seconds, output));
        }
    }
}