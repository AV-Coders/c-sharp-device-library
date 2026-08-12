using AVCoders.Core;

namespace AVCoders.Matrix;

public class NavEncoder : NavDeviceBase
{
    public NavEncoder(string name, string ipAddress, Navigator navigator)
        : base(name, AVEndpointType.Encoder, ipAddress, navigator)
    {
    }

    protected override Task Poll(CancellationToken arg)
    {
        Send($"I");
        return Task.CompletedTask;
    }

    protected override void HandleResponse(string response)
    {
        if (response.StartsWith("In00"))
        {
            InputConnectionStatus = response.Split(' ')[1] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected;

            switch (InputConnectionStatus)
            {
                case ConnectionState.Connected:
                    Poll(CancellationToken.None);
                    break;
                case ConnectionState.Disconnected:
                    InputHdcpStatus = HdcpStatus.Unknown;
                    InputResolution = string.Empty;
                    break;
            }
        }
        else if (response.StartsWith("HdcpI"))
        {
            InputHdcpStatus = ParseSourceHdcpStatus(response.Remove(0, 5));
        }

    }

    protected override void ProcessConcatenatedResponse(string response)
    {
        if (response.StartsWith("SigI"))
            InputConnectionStatus = response[4..] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected;
        else if (response.StartsWith("HdcpI"))
            InputHdcpStatus = ParseSourceHdcpStatus(response[5..]);
        else if (response.StartsWith("HdcpO"))
            OutputHdcpStatus = ParseSinkHdcpStatus(response[5..]);
        else if (response.StartsWith("ResI"))
        {
            var resolution = response[4..];
            if (resolution.Contains("NOT DETECTED") || resolution.StartsWith("0x0"))
            {
                InputConnectionStatus = ConnectionState.Disconnected;
                InputResolution = string.Empty;
            }
            else
            {
                InputConnectionStatus = ConnectionState.Connected;
                InputResolution = resolution;
            }
        }
    }
}