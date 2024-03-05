using AVCoders.Core;

namespace AVCoders.Matrix;

public class ExtronDtpCpxx : VideoMatrix
{
    private readonly CommunicationClient _communicationClient;

    public ExtronDtpCpxx(CommunicationClient communicationClient, int numberOfOutputs) : base(numberOfOutputs)
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
            Log($"ExtronDtpcPxx - Communication error: {e.Message}");
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    private new void Log(string message)
    {
        LogHandlers?.Invoke($"ExtronDtpCpxx - {message}");
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