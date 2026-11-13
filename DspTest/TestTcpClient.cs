using AVCoders.Core;

namespace AVCoders.Dsp.Tests;

internal class TestTcpClient() : TcpClient("host", TelnetPort, "Test", CommandStringFormat.Ascii)
{
    private const ushort TelnetPort = 23;
    private readonly object _sentLock = new();
    private readonly List<string> _sent = [];
    private readonly List<string> _queued = [];
    public List<string> Sent { get { lock (_sentLock) return [.._sent]; } }
    public int Reconnects;
    public override void Send(string message) => Hand(message);
    public override void Send(byte[] bytes) => Hand(System.Text.Encoding.ASCII.GetString(bytes));
    public override void Connect() { }
    public override void Reconnect() => Reconnects++;
    public override void Disconnect()
    {
        DesiredConnectionState = ConnectionState.Disconnected;
        ConnectionState = ConnectionState.Disconnected;
    }
    protected override Task ProcessSendQueue(CancellationToken token) => Task.CompletedTask;
    protected override Task CheckConnectionState(CancellationToken token) => Task.CompletedTask;
    protected override Task Receive(CancellationToken token) => Task.CompletedTask;
    public void SetConnectionState(ConnectionState state) => ConnectionState = state;
    public void Reply(string response) => InvokeResponseHandlers(response, System.Text.Encoding.UTF8.GetBytes(response));
    public void ReplyBytes(byte[] response) => InvokeResponseHandlers(System.Text.Encoding.Latin1.GetString(response), response);

    public void WriteQueued()
    {
        List<string> queued;
        lock (_sentLock)
        {
            queued = [.._queued];
            _queued.Clear();
        }
        queued.ForEach(Written);
    }

    public void DropQueued()
    {
        lock (_sentLock)
            _queued.Clear();
    }

    private void Hand(string message)
    {
        var connected = ConnectionState == ConnectionState.Connected;
        lock (_sentLock)
        {
            _sent.Add(message);
            if (!connected)
                _queued.Add(message);
        }
        if (connected)
            Written(message);
    }

    private void Written(string message)
    {
        InvokeRequestHandlers(Bytes.FromString(message));
        InvokeRequestHandlers(message);
    }
}
