﻿namespace AVCoders.Matrix;

public class NavEncoder : NavDeviceBase
{
    public NavEncoder(string name, string ipAddress, Navigator navigator)
        : base(name, AVoIPDeviceType.Encoder, ipAddress, navigator)
    {
    }

    protected override Task Poll(CancellationToken arg)
    {
        Send($"I");
        return Task.CompletedTask;
    }

    protected override void HandleResponse(string response)
    {
        Debug($"Device response - {response}");
        if (response.Contains('*'))
        {
            var responses = response.Split('*');
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
                    InputConnectionStatus = ConnectionStatus.Disconnected;
                    InputResolution = String.Empty;
                }
                else
                {
                    InputConnectionStatus = ConnectionStatus.Connected;
                    InputResolution = kvp[1];
                }
                break;
        }
    }
}