using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AVCoders.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    private readonly TaskCompletionSource _channelListLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Uri _channelUri;
    private readonly Uri _getChannelsUri;

    public VitecServer(string host, string name = "Server") : base(name)
    {
        _channelUri = new Uri($"http://{host}/api/public/control/devices/commands/channel", UriKind.Absolute);
        _getChannelsUri = new Uri($"http://{host}/api/public/control/channels", UriKind.Absolute);
        _pollChannelsWorker = new ThreadWorker(PollChannels, TimeSpan.FromHours(2));
        _pollChannelsWorker.Restart();
    }

    public Task ChannelListLoaded => _channelListLoaded.Task;

    private Task PollChannels(CancellationToken token) => Get(_getChannelsUri);

    private bool ValidateCertificate(object arg1, X509Certificate? arg2, X509Chain? arg3,
        SslPolicyErrors arg4) => true;

    private async Task Post(Uri uri, string content)
    {
        using SocketsHttpHandler handler = new();
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
        handler.ActivityHeadersPropagator = null;

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
        using SocketsHttpHandler handler = new();
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
        handler.ActivityHeadersPropagator = null;

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
        if (!response.IsSuccessStatusCode)
        {
            LogWarning("The channel list request failed: {StatusCode} {ReasonPhrase}", (int)response.StatusCode,
                response.ReasonPhrase);
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        List<VitecServerChannel>? vitecServerChannels = JsonConvert.DeserializeObject<List<VitecServerChannel>>(responseBody);
        if (vitecServerChannels == null)
        {
            LogWarning("Ignoring a channel list that is not a JSON array: {Body}", responseBody);
            return;
        }
        // Built aside and swapped so readers never see a half-populated map, and a
        // duplicate channel number updates the entry instead of throwing.
        var newChannelMap = new Dictionary<int, string>();
        foreach (var channelInfo in vitecServerChannels)
        {
            if (string.IsNullOrEmpty(channelInfo.Uri))
            {
                LogWarning("Skipping channel {Number} ({Name}), it has no URI", channelInfo.Number, channelInfo.Name);
                continue;
            }
            newChannelMap[channelInfo.Number] = channelInfo.Uri;
        }
        _channelMap = newChannelMap;
        _channelListLoaded.TrySetResult();
    }

    private static string ChannelCommand(string channelUri, string deviceMac) => new JObject
    {
        ["devices"] = new JArray(deviceMac),
        ["uri"] = channelUri,
        ["isFullScreen"] = 0,
        ["params"] = new JObject()
    }.ToString(Formatting.None);

    public void SetChannel(string channelUri, string deviceMac)
    {
        Post(_channelUri, ChannelCommand(channelUri, deviceMac));
    }

    public void SetChannel(int channelNumber, string deviceMac)
    {
        if (!_channelMap.TryGetValue(channelNumber, out var channelUri))
        {
            using (PushProperties())
                LogError("Channel {ChannelNumber} is not in the channel list", channelNumber);
            return;
        }
        Post(_channelUri, ChannelCommand(channelUri, deviceMac));
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