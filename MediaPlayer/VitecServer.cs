using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AVCoders.Core;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace AVCoders.MediaPlayer;

public class VitecServer
{
    private readonly Dictionary<int, string> _channelMap;
    public LogHandler? LogHandlers;

    private readonly Uri _channelUri;

    public VitecServer(string host, Dictionary<int, string> channelMap)
    {
        _channelMap = channelMap;
        _channelUri = new Uri($"http://{host}/api/public/control/devices/commands/channel", UriKind.Absolute);
    }

    private bool ValidateCertificate(HttpRequestMessage arg1, X509Certificate2? arg2, X509Chain? arg3,
        SslPolicyErrors arg4) => true;

    private async Task Post(Uri uri, string content)
    {
        using HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using HttpClient httpClient = new HttpClient(handler);
        try
        {
            Log($"Sending request: {uri}");
            await httpClient.PostAsync(uri, new StringContent(content, Encoding.Default, "application/json"));
        }
        catch (Exception e)
        {
            Log(e.Message);
            Log(e.StackTrace);
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace);
        }
    }

    public void SetChannel(string channelUri, string deviceMac)
    {
        Post(_channelUri,$"{{\n  \"devices\": [\n    \"{deviceMac}\"\n  ],\n  \"uri\": \"{channelUri}\",\n  \"isFullScreen\": 0,\n  \"params\": {{}}\n}}");
    }

    public void SetChannel(int channelNumber, string deviceMac)
    {
        Post(_channelUri,$"{{\n  \"devices\": [\n    \"{deviceMac}\"\n  ],\n  \"uri\": \"{_channelMap[channelNumber]}\",\n  \"isFullScreen\": 0,\n  \"params\": {{}}\n}}");
    }
   
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"VitecServer - {message}");
    }
}