using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AVCoders.Core;
using Newtonsoft.Json;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace AVCoders.MediaPlayer;

/*
 * {
    "channelid": "string",
    "redundancy": "string",
    "language": "string",
    "number": 0,
    "name": "string",
    "groups": "string",
    "icon": "string",
    "type": "string",
    "interface": "string",
    "uri": "string",
    "definition": "string",
    "address": {},
    "stream": {},
    "source": {},
    "group": [
      "string"
    ],
    "bandwidth": "string"
  }
 */
public record VitecServerChannel(
    [property: JsonProperty("channelid")] string ChannelId,
    [property: JsonProperty("number")] int Number,
    [property: JsonProperty("name")] string Name,
    [property: JsonProperty("uri")] string Uri
);

public class VitecServer
{
    private readonly Dictionary<int, string> _channelMap = new ();
    public LogHandler? LogHandlers;
    private ThreadWorker _pollChannelsWorker;
    private readonly Dictionary<string, int> _currentChannelMap = new();

    private readonly Uri _channelUri;
    private readonly Uri _getChannelsUri;

    public VitecServer(string host)
    {
        _channelUri = new Uri($"http://{host}/api/public/control/devices/commands/channel", UriKind.Absolute);
        _getChannelsUri = new Uri($"http://{host}/api/public/control/channels", UriKind.Absolute);
        _pollChannelsWorker = new ThreadWorker(PollChannels, TimeSpan.FromHours(2));
        PollChannels(default);
    }

    private Task PollChannels(CancellationToken token)
    {
        Get(_getChannelsUri);
        return Task.CompletedTask;
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
            Log(e.StackTrace?? "No stack trace available");
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace?? "No stack trace available");
        }
    }

    private async Task Get(Uri uri)
    {
        using HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using HttpClient httpClient = new HttpClient(handler);
        try
        {
            Log($"Sending request: {uri}");
            HttpResponseMessage response = await httpClient.GetAsync(uri);
            await HandleResponse(response);
        }
        catch (Exception e)
        {
            Log(e.Message);
            Log(e.StackTrace?? "No stack trace available");
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace?? "No stack trace available");
        }
    }

    private async Task HandleResponse(HttpResponseMessage response)
    {
        Log($"Response status code: {response.StatusCode.ToString()}");
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            Log(responseBody);
            List<VitecServerChannel>? vitecServerChannels = JsonConvert.DeserializeObject<List<VitecServerChannel>>(responseBody);
            if (vitecServerChannels == null)
            {
                Log("Response is not a list of channels");
                return;
            }
            Log("Response is a list of channels");
            _channelMap.Clear();

            vitecServerChannels.ForEach(channelInfo =>
            {
                _channelMap.Add(channelInfo.Number, channelInfo.Uri);
            });
        }
    }

    public void SetChannel(string channelUri, string deviceMac)
    {
        Post(_channelUri,$"{{\n  \"devices\": [\n    \"{deviceMac}\"\n  ],\n  \"uri\": \"{channelUri}\",\n  \"isFullScreen\": 0,\n  \"params\": {{}}\n}}");
    }

    public void SetChannel(int channelNumber, string deviceMac)
    {
        Post(_channelUri,$"{{\n  \"devices\": [\n    \"{deviceMac}\"\n  ],\n  \"uri\": \"{_channelMap[channelNumber]}\",\n  \"isFullScreen\": 0,\n  \"params\": {{}}\n}}");
        _currentChannelMap[deviceMac] = channelNumber;
    }

    public void ChannelUp(string deviceMac)
    {
        int newChannelIndex = 0;
        List<int> channels = _channelMap.Keys.ToList();
        
        if (_currentChannelMap.TryGetValue(deviceMac, out var currentChannel))
        {
            newChannelIndex = channels.IndexOf(currentChannel) + 1;
            if (newChannelIndex < -1 || newChannelIndex > channels.Count)
                newChannelIndex = 0;
        }
        
        SetChannel(channels[newChannelIndex], deviceMac);
    }

    public void ChannelDown(string deviceMac)
    {
        int newChannelIndex = 0;
        List<int> channels = _channelMap.Keys.ToList();
        
        if (_currentChannelMap.TryGetValue(deviceMac, out var currentChannel))
        {
            newChannelIndex = channels.IndexOf(currentChannel) - 1;
            if (newChannelIndex < -1 || newChannelIndex > channels.Count)
                newChannelIndex = channels.Count - 1;
        }
        
        SetChannel(channels[newChannelIndex], deviceMac);
    }
   
    protected void Log(string message)
    {
        LogHandlers?.Invoke($"VitecServer - {message}");
    }
}