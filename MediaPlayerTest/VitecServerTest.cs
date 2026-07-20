using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AVCoders.MediaPlayer.Tests;

public class VitecServerTest : IDisposable
{
    private const string DeviceMac = "00:11:22:33:44:55";
    private const string ChannelsJson =
        """
        [
          {"channelid":"c1","number":1,"name":"One","uri":"uri-1"},
          {"channelid":"c2","number":2,"name":"Two","uri":"uri-2"},
          {"channelid":"c3","number":3,"name":"Three","uri":"uri-3"}
        ]
        """;

    private readonly HttpListener _listener;
    private readonly ushort _port;
    private readonly List<string> _posts = [];
    private readonly object _postsLock = new();

    public VitecServerTest()
    {
        // HttpListener cannot bind port 0, so probe for a free one.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                _port = port;
                _ = Task.Run(ServeRequests);
                return;
            }
            catch (HttpListenerException)
            {
                // port was taken between probe and bind - try another
            }
        }

        throw new InvalidOperationException("Could not find a free port for HttpListener");
    }

    private static ushort GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return (ushort)((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { /* test teardown */ }
    }

    private async Task ServeRequests()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                lock (_postsLock)
                    _posts.Add(body);
            }
            else
            {
                var responseBytes = Encoding.UTF8.GetBytes(ChannelsJson);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes);
            }
            context.Response.Close();
        }
    }

    private int PostCount { get { lock (_postsLock) return _posts.Count; } }

    private string LastPost { get { lock (_postsLock) return _posts[^1]; } }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(25);
        }
    }

    private VitecServer CreateServer() => new($"127.0.0.1:{_port}", "Test server");

    /// <summary>Waits for the startup channel poll to land, then tunes to the given channel.</summary>
    private async Task<VitecServer> CreateServerTunedTo(int channelNumber)
    {
        var server = CreateServer();
        await WaitUntilAsync(() =>
        {
            server.SetChannel(channelNumber, DeviceMac);
            return PostCount > 0;
        }, "The channel list was never fetched");

        // The retry loop above may have fired several posts; wait until they have all
        // landed so the next post a test observes is its own.
        var stableCount = PostCount;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow - stableSince < TimeSpan.FromMilliseconds(300))
        {
            await Task.Delay(25);
            if (PostCount == stableCount)
                continue;
            stableCount = PostCount;
            stableSince = DateTime.UtcNow;
        }
        return server;
    }

    [Fact]
    public async Task Server_FetchesTheChannelListOnStartup_AndSetChannelUsesIt()
    {
        var server = await CreateServerTunedTo(2);

        Assert.Contains("\"uri-2\"", LastPost);
        Assert.Contains(DeviceMac, LastPost);
    }

    [Fact]
    public async Task ChannelUp_MovesToTheNextChannel()
    {
        var server = await CreateServerTunedTo(1);
        var before = PostCount;

        server.ChannelUp(DeviceMac);

        await WaitUntilAsync(() => PostCount > before, "ChannelUp never posted");
        Assert.Contains("\"uri-2\"", LastPost);
    }

    [Fact]
    public async Task ChannelUp_WrapsFromTheLastChannelToTheFirst()
    {
        var server = await CreateServerTunedTo(3);
        var before = PostCount;

        server.ChannelUp(DeviceMac);

        await WaitUntilAsync(() => PostCount > before, "ChannelUp never posted");
        Assert.Contains("\"uri-1\"", LastPost);
    }

    [Fact]
    public async Task ChannelDown_WrapsFromTheFirstChannelToTheLast()
    {
        var server = await CreateServerTunedTo(1);
        var before = PostCount;

        server.ChannelDown(DeviceMac);

        await WaitUntilAsync(() => PostCount > before, "ChannelDown never posted");
        Assert.Contains("\"uri-3\"", LastPost);
    }

    [Fact]
    public void ChannelChanges_WithAnEmptyChannelList_DoNotThrow()
    {
        // Port 1 on loopback has no listener, so the channel poll fails and the list stays empty.
        var server = new VitecServer("127.0.0.1:1", "Unreachable server");

        Assert.Null(Record.Exception(() => server.ChannelUp(DeviceMac)));
        Assert.Null(Record.Exception(() => server.ChannelDown(DeviceMac)));
    }

    [Fact]
    public void SetChannel_WithAnUnknownNumber_DoesNotThrow()
    {
        var server = new VitecServer("127.0.0.1:1", "Unreachable server");

        Assert.Null(Record.Exception(() => server.SetChannel(99, DeviceMac)));
        Assert.Equal(0, PostCount);
    }
}
