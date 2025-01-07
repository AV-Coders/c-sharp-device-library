namespace AVCoders.Matrix;

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
        Log($"Device response - {response}");
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
                if(kvp[1].Contains("NOT DETECTED"))
                    UpdateInputStatus(ConnectionStatus.Disconnected, String.Empty);
                else
                    UpdateInputStatus(ConnectionStatus.Connected, kvp[1]);
                break;
        }
    }
}