
using AVCoders.Core;

namespace AVCoders.Matrix;

public class BlustreamAmf41W : VideoMatrix
{
    public static readonly ushort DefaultPort = 23;
    private const string SuccessReply = "[SUCCESS]";
    private const string FailureReply = "[FAIL]";

    public BlustreamAmf41W(TcpClient tcpClient, string name)
        : base(1, tcpClient, name)
    {
        tcpClient.ResponseHandlers += HandleResponse;

        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
    }

    private void HandleResponse(string response)
    {
        if (response.Contains(SuccessReply))
            CommunicationState = CommunicationState.Okay;
        else if (response.Contains(FailureReply))
            CommunicationState = CommunicationState.Error;
    }

    private void SourceSelect(int source, int window = 0)
    {
        var command = window == 0
            ? $"config --source-select hdmi{source}\r"
            : $"config --source-select hdmi{source} {window}\r";
        try
        {
            CommunicationClient.Send(command);
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    public override void PowerOn()
    {
    }

    public override void PowerOff()
    {
    }

    public override int NumberOfOutputs => 1;
    public override int NumberOfInputs => 4;
    public override bool RequiresOutputSpecification => false;
    public override bool SupportsVideoBreakaway => false;

    public override void RouteVideo(int input, int output) => SourceSelect(input, output);

    public override bool SupportsAudioBreakaway => false;

    public override void RouteAudio(int input, int output) => SourceSelect(input, output);

    public override void RouteAV(int input, int output) => SourceSelect(input, output);
    public override List<SyncStatus> GetInputs() => new();

    public override List<SyncStatus> GetOutputs() => new();
}