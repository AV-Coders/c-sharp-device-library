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
            // HdcpI0
            // HdcpI1
            InputHdcpStatus = response.Remove(0, 5) == "1" ? HdcpStatus.Active : HdcpStatus.NotSupported;
        }
        
    }

    protected override void ProcessConcatenatedResponse(string response)
    {
        if (!response.Contains('I'))
            return;
        var kvp = response.Split('I');
        switch (kvp[0])
        {
            case "Res":
                if (kvp[1].Contains("NOT DETECTED"))
                {
                    InputConnectionStatus = ConnectionState.Disconnected;
                    InputResolution = string.Empty;
                }
                else
                {
                    InputConnectionStatus = ConnectionState.Connected;
                    InputResolution = kvp[1];
                }
                break;
        }
    }
}