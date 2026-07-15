using System.Net;
using System.Net.Sockets;

namespace AVCoders.CommunicationClients.Tests;

/// <summary>
/// Helpers for transport tests. All waits poll a condition with a hard timeout —
/// never a fixed sleep — so tests stay deterministic on slow CI runners.
/// </summary>
public static class TestNetwork
{
    public static ushort GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out after {timeoutSeconds}s: {failureMessage}");
    }

    public static async Task<System.Net.Sockets.TcpClient> AcceptAsync(TcpListener listener, int timeoutSeconds)
    {
        var acceptTask = listener.AcceptTcpClientAsync();
        var completed = await Task.WhenAny(acceptTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.True(completed == acceptTask, $"Timed out after {timeoutSeconds}s waiting for a TCP connection");
        return await acceptTask;
    }

    public static async Task<string> ReadStringAsync(System.Net.Sockets.TcpClient client, int timeoutSeconds)
    {
        var buffer = new byte[2048];
        var readTask = client.GetStream().ReadAsync(buffer, 0, buffer.Length);
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.True(completed == readTask, $"Timed out after {timeoutSeconds}s waiting for TCP data");
        var bytesRead = await readTask;
        return System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);
    }
}
