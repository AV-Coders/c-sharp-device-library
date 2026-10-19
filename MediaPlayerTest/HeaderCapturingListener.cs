using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;

namespace AVCoders.MediaPlayer.Tests;

public sealed class HeaderCapturingListener : IDisposable
{
    private const int PortAttempts = 10;

    private readonly HttpListener _listener;
    public ushort Port { get; }

    public HeaderCapturingListener()
    {
        for (var attempt = 0; attempt < PortAttempts; attempt++)
        {
            var port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                return;
            }
            catch (HttpListenerException)
            {
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

    public async Task<NameValueCollection> NextRequestHeadersAsync()
    {
        var context = await _listener.GetContextAsync();
        var headers = new NameValueCollection(context.Request.Headers);
        context.Response.Close();
        return headers;
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { }
    }
}
