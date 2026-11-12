using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace AVCoders.CommunicationClients.Tests;

public enum ConnectTimeouts
{
    AsSet,
    AtOnce,
    Never
}

public class TimerTrackingClock : FakeTimeProvider
{
    private readonly ConcurrentDictionary<object, TimeSpan> _armed = new();

    public ICollection<TimeSpan> ArmedTimers => _armed.Values;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var key = new object();
        _armed[key] = dueTime;
        var timer = base.CreateTimer(firing =>
        {
            _armed.TryRemove(key, out _);
            callback(firing);
        }, state, dueTime, period);
        return new ArmedTimer(timer, () => _armed.TryRemove(key, out _));
    }

    private sealed class ArmedTimer(ITimer timer, Action disarm) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            disarm();
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            disarm();
            return timer.DisposeAsync();
        }
    }
}

public sealed class ConnectTimeoutClock : TimerTrackingClock
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public volatile ConnectTimeouts ConnectTimeouts;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        base.CreateTimer(callback, state, dueTime != ConnectTimeout ? dueTime : ConnectTimeouts switch
        {
            ConnectTimeouts.AtOnce => TimeSpan.Zero,
            ConnectTimeouts.Never => Timeout.InfiniteTimeSpan,
            _ => dueTime
        }, period);
}

public static class TestNetwork
{
    public static readonly TimeSpan ClockStep = TimeSpan.FromMilliseconds(250);
    private const int ReadBufferSize = 65536;
    // Windows retries a refused SYN for about 2 s; SIO_TCP_INITIAL_RTO without SYN retransmissions fails it at once.
    private const int SioTcpInitialRto = unchecked((int)0x98000011);
    private static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

    public static TcpListener ListenOn(ConnectTimeoutClock time, ushort port)
    {
        time.ConnectTimeouts = ConnectTimeouts.AsSet;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    public static System.Net.Sockets.TcpClient RefusedAtOnce()
    {
        var client = new System.Net.Sockets.TcpClient(AddressFamily.InterNetwork);
        if (OperatingSystem.IsWindows())
            client.Client.IOControl(SioTcpInitialRto, NoSynRetransmissions, null);
        return client;
    }

    public static ushort GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
        return Encoding.ASCII.GetString(buffer, 0, bytesRead);
    }

    public static Task UntilAsync(FakeTimeProvider time, Func<bool> condition, string failureMessage) =>
        Wait.UntilAsync(time, ClockStep, condition, failureMessage);

    public static async Task<T> AdvanceUntilDone<T>(FakeTimeProvider time, Task<T> task, string failureMessage)
    {
        await UntilAsync(time, () => task.IsCompleted, failureMessage);
        return await task;
    }

    public static async Task Advance(FakeTimeProvider time, TimeSpan span)
    {
        for (var advanced = TimeSpan.Zero; advanced < span; advanced += ClockStep)
            await Task.Run(() => time.Advance(ClockStep));
    }

    public static Task<System.Net.Sockets.TcpClient> AcceptAsync(TcpListener listener, FakeTimeProvider time) =>
        AdvanceUntilDone(time, listener.AcceptTcpClientAsync(), "No TCP connection arrived");

    public static async Task<string> ReadStringAsync(System.Net.Sockets.TcpClient client, FakeTimeProvider time)
    {
        var buffer = new byte[ReadBufferSize];
        var bytesRead = await AdvanceUntilDone(time, client.GetStream().ReadAsync(buffer, 0, buffer.Length), "No TCP data arrived");
        return Encoding.ASCII.GetString(buffer, 0, bytesRead);
    }

    public static async Task<string> ReadUntilAsync(System.Net.Sockets.TcpClient client, FakeTimeProvider time, string ending)
    {
        var received = new StringBuilder();
        while (!received.ToString().EndsWith(ending))
        {
            var read = await ReadStringAsync(client, time);
            Assert.NotEqual("", read);
            received.Append(read);
        }
        return received.ToString();
    }

    public static List<ThreadWorker> Workers(IpComms client)
    {
        var workers = new List<ThreadWorker>();
        foreach (var name in new[] { "ReceiveThreadWorker", "ConnectionStateWorker", "SendQueueWorker" })
        {
            var field = typeof(IpComms).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            workers.Add((ThreadWorker)field.GetValue(client)!);
        }
        return workers;
    }

    public static int SendQueueCount(IpComms client)
    {
        var field = client.GetType().GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((ICollection)field.GetValue(client)!).Count;
    }

    public static ThreadWorker SendQueueWorker(IpComms client) =>
        (ThreadWorker)typeof(IpComms).GetField("SendQueueWorker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
}
