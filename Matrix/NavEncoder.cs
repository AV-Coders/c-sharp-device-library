using AVCoders.Core;

namespace AVCoders.Matrix;

public class NavEncoder : NavDeviceBase
{
    public NavEncoder(string name, string ipAddress, Navigator navigator)
        : this(name, ipAddress, navigator, null)
    {
    }

    internal NavEncoder(string name, string ipAddress, Navigator navigator, TimeProvider? timeProvider = null)
        : base(name, AVEndpointType.Encoder, ipAddress, navigator, timeProvider)
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
            var parts = response.Split(' ');
            if (parts.Length < 2)
            {
                LogWarning("The input status {Response} has no value", response);
                return;
            }
            InputConnectionStatus = parts[1] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected;

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