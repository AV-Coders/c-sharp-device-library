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

public class VitecServer : LogBase
{
    private Dictionary<int, string> _channelMap = new ();
    private ThreadWorker _pollChannelsWorker;
    private readonly Dictionary<string, int> _currentChannelMap = new();

    private readonly Uri _channelUri;
    private readonly Uri _getChannelsUri;

    public VitecServer(string host, string name = "Server") : base(name)
    {
        _channelUri = new Uri($"http://{host}/api/public/control/devices/commands/channel", UriKind.Absolute);
        _getChannelsUri = new Uri($"http://{host}/api/public/control/channels", UriKind.Absolute);
        _pollChannelsWorker = new ThreadWorker(PollChannels, TimeSpan.FromHours(2));
        _pollChannelsWorker.Restart();
    }

    private Task PollChannels(CancellationToken token) => Get(_getChannelsUri);

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
            await httpClient.PostAsync(uri, new StringContent(content, Encoding.Default, "application/json"));
        }
        catch (Exception e)
        {
            LogException(e);
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
            HttpResponseMessage response = await httpClient.GetAsync(uri);
            await HandleResponse(response);
        }
        catch (Exception e)
        {
            LogException(e);
        }
    }

    private async Task HandleResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            List<VitecServerChannel>? vitecServerChannels = JsonConvert.DeserializeObject<List<VitecServerChannel>>(responseBody);
            if (vitecServerChannels == null)
            {
                return;
            }
            // Built aside and swapped so readers never see a half-populated map, and a
            // duplicate channel number updates the entry instead of throwing.
            var newChannelMap = new Dictionary<int, string>();
            vitecServerChannels.ForEach(channelInfo => newChannelMap[channelInfo.Number] = channelInfo.Uri);
            _channelMap = newChannelMap;
        }
    }

    public void SetChannel(string channelUri, string deviceMac)
    {
        Post(_channelUri,$"{{\n  \"devices\": [\n    \"{deviceMac}\"\n  ],\n  \"uri\": \"{channelUri}\",\n  \"isFullScreen\": 0,\n  \"params\": {{}}\n}}");
    }

    public void SetChannel(int channelNumber, string deviceMac)
    {
        if (!_channelMap.TryGetValue(channelNumber, out var channelUri))
        {
            using (PushProperties())
                LogError("Channel {ChannelNumber} is not in the channel list", channelNumber);
            return;
        }
        Post(_channelUri,$"{{\n  \"devices\": [\n    \"{deviceMac}\"\n  ],\n  \"uri\": \"{channelUri}\",\n  \"isFullScreen\": 0,\n  \"params\": {{}}\n}}");
        _currentChannelMap[deviceMac] = channelNumber;
    }

    public void ChannelUp(string deviceMac)
    {
        List<int> channels = _channelMap.Keys.ToList();
        if (channels.Count == 0)
        {
            using (PushProperties())
                LogWarning("Ignoring channel up, the channel list is empty");
            return;
        }

        int newChannelIndex = 0;
        if (_currentChannelMap.TryGetValue(deviceMac, out var currentChannel))
        {
            newChannelIndex = channels.IndexOf(currentChannel) + 1;
            if (newChannelIndex >= channels.Count)
                newChannelIndex = 0;
        }

        SetChannel(channels[newChannelIndex], deviceMac);
    }

    public void ChannelDown(string deviceMac)
    {
        List<int> channels = _channelMap.Keys.ToList();
        if (channels.Count == 0)
        {
            using (PushProperties())
                LogWarning("Ignoring channel down, the channel list is empty");
            return;
        }

        int newChannelIndex = 0;
        if (_currentChannelMap.TryGetValue(deviceMac, out var currentChannel))
        {
            newChannelIndex = channels.IndexOf(currentChannel) - 1;
            if (newChannelIndex < 0)
                newChannelIndex = channels.Count - 1;
        }

        SetChannel(channels[newChannelIndex], deviceMac);
    }
}