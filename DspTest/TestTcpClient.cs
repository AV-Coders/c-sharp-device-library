using AVCoders.Core;

namespace AVCoders.Dsp.Tests;

internal class TestTcpClient() : TcpClient("host", TelnetPort, "Test", CommandStringFormat.Ascii)
{
    private const ushort TelnetPort = 23;
    private readonly object _sentLock = new();
    private readonly List<string> _sent = [];
    public List<string> Sent { get { lock (_sentLock) return [.._sent]; } }
    public int Reconnects;
    public override void Send(string message) { lock (_sentLock) _sent.Add(message); }
    public override void Send(byte[] bytes) { lock (_sentLock) _sent.Add(System.Text.Encoding.ASCII.GetString(bytes)); }
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
}
