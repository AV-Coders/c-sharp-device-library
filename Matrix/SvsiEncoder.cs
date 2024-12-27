using AVCoders.Core;

namespace AVCoders.Matrix;

public class SvsiEncoder : SvsiBase
{
    public SvsiEncoder(TcpClient tcpClient, int pollTime = 10) : base(tcpClient, pollTime, AVoIPDeviceType.Encoder)
    {
    }

    protected override void UpdateVariablesBasedOnStatus()
    {
        if(StatusDictionary.TryGetValue("STREAM", out var streamId))
            StreamId = uint.Parse(streamId);

        if (StatusDictionary.TryGetValue("INPUTRES", out var resolution) &&
            StatusDictionary.TryGetValue("DVIINPUT", out var connected))
            UpdateInputStatus(connected == "connected"? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                resolution,
                1);
    }
}