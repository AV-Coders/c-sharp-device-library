using AVCoders.Core;

namespace AVCoders.Motor;

public class ScreenTechnicsConnect : Motor
{
    public static readonly ushort DefaultPort = 3001;
    private readonly TcpClient _client;
    private readonly int _moduleId;
    private string _queuedMessage = String.Empty;
    private Guid _currentConnectionKeepAlive = Guid.Empty;

    public ScreenTechnicsConnect(string name, TcpClient client, RelayAction powerOnAction,int moduleId = 1, int moveSeconds = 36)
        : base(name, powerOnAction, moveSeconds)
    {
        _client = client;
        _moduleId = moduleId;
        _client.Disconnect();
        _client.ConnectionStateHandlers += ConnectionStateHandlers;
    }

    private void ConnectionStateHandlers(ConnectionState connectionState)
    {
        Debug($"Connection State Change - {connectionState.ToString()}");
        if (connectionState != ConnectionState.Connected)
            return;
        if (_queuedMessage != String.Empty)
        {
            Thread.Sleep(500); // You need a slight pause because the device doesn't accept commands when the event is triggered.
            Debug($"Sending queued message {_queuedMessage}");
            _client.Send(_queuedMessage);
            _queuedMessage = String.Empty;
        }
    }

    private void Send(string message)
    {
        Guid thisConnectionKeepAlive = Guid.NewGuid();
        _currentConnectionKeepAlive = thisConnectionKeepAlive;
        
        if(_client.GetConnectionState() == ConnectionState.Connected)
            _client.Send(message);
        else
        {
            Debug("Queuing Message");
            _queuedMessage = message;
            _client.Connect();
        }

        new Thread(_ =>
        {
            Thread.Sleep(MoveSeconds * 1000);
            CurrentMoveAction = RelayAction.None;
            TryDisconnect(thisConnectionKeepAlive);
        }).Start();
    }

    private void TryDisconnect(Guid thisConnectionKeepAlive)
    {
        if (_currentConnectionKeepAlive != thisConnectionKeepAlive)
            return;
        _client.Disconnect();
    }

    public override void Raise()
    {
        Send($"30 {_moduleId}\r");
        CurrentMoveAction = RelayAction.Raise;
        Debug("Screen technics will Raise");
    }

    public override void Lower()
    {
        Send($"33 {_moduleId}\r");
        CurrentMoveAction = RelayAction.Lower;
        Debug("Screen technics will Lower");
    }

    public override void Stop()
    {
        Send($"36 {_moduleId}\r");
        CurrentMoveAction = RelayAction.None;
        Debug("Screen technics will Stop");
    }
}