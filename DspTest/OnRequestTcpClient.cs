using AVCoders.Core;

namespace AVCoders.Dsp.Tests;

internal sealed class OnRequestTcpClient : TestTcpClient
{
    public int Connects;

    public override void Send(string message)
    {
        if (DesiredConnectionState == ConnectionState.Disconnected)
        {
            ReconnectForSend(() => base.Send(message));
            return;
        }
        base.Send(message);
    }

    public override void Connect()
    {
        RecordRequestedConnect();
        Connects++;
        DesiredConnectionState = ConnectionState.Connected;
    }

    public override void Disconnect()
    {
        lock (ConnectionRequestLock)
        {
            RecordRequestedDisconnect();
            DesiredConnectionState = ConnectionState.Disconnected;
            ConnectionState = ConnectionState.Disconnected;
        }
    }
}
