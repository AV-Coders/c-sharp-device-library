using AVCoders.Core;

namespace AVCoders.Matrix;

public class SvsiEncoder : SvsiBase
{
    public SvsiEncoder(string name, TcpClient tcpClient) : this(name, tcpClient, null)
    {
    }

    internal SvsiEncoder(string name, TcpClient tcpClient, TimeProvider? timeProvider)
        : base(name, tcpClient, AVEndpointType.Encoder, timeProvider)
    {
    }

    protected override void UpdateVariablesBasedOnStatus()
    {
        if (StatusDictionary.TryGetValue("INPUTRES", out var resolution) &&
            StatusDictionary.TryGetValue("DVIINPUT", out var connected))
        {
            InputConnectionStatus =
                connected == "connected" ? ConnectionState.Connected : ConnectionState.Disconnected;
            InputResolution = resolution;
        }
    }
}