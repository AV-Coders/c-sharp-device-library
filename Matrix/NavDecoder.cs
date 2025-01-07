namespace AVCoders.Matrix;

public class NavDecoder : NavDeviceBase
{
    public NavDecoder(string name, string ipAddress, Navigator navigator) 
        : base(name, AVoIPDeviceType.Decoder, ipAddress, navigator)
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
        if (response.StartsWith("In"))
        {
            var streamId = response.Split(' ')[0][2..];
            StreamAddress = streamId;
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
                    UpdateOutputStatus(ConnectionStatus.Disconnected, String.Empty);
                else
                    UpdateOutputStatus(ConnectionStatus.Connected, kvp[1]);
                break;
        }
    }
}