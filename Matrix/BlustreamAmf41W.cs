
using AVCoders.Core;

namespace AVCoders.Matrix;

public class BlustreamAmf41W : VideoMatrix
{
    public static readonly ushort DefaultPort = 23;

    public BlustreamAmf41W(TcpClient tcpClient, string name) : base(1, tcpClient, name)
    {
        tcpClient.SetPort(DefaultPort);
        tcpClient.ResponseHandlers += HandleResponse;

        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
    }

    private void HandleResponse(string response)
    {
        CommunicationState = response.Contains("[SUCCESS]") ? CommunicationState.Okay : CommunicationState.Error;
    }

    private void SourceSelect(int source, int window = 0)
    {
        if (window == 0)
        {
            CommunicationClient.Send($"config --source-select hdmi{source}\r");
        }
        else
        {
            CommunicationClient.Send($"config --source-select hdmi{source} {window}\r");
        }
    }

    public override void PowerOn()
    {
    }

    public override void PowerOff()
    {
    }

    public override void RouteVideo(int input, int output)
    {
        SourceSelect(input, output);
    }

    public override void RouteAudio(int input, int output)
    {
        SourceSelect(input, output);
    }

    public override void RouteAV(int input, int output)
    {
        SourceSelect(input, output);
    }
}