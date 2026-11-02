using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Motor.Tests;

public class ScreenTechnicsConnectTest
{
    private const int MoveSeconds = 2;
    private static readonly TimeSpan ConnectedSettleTime = TimeSpan.FromMilliseconds(500);
    private readonly FakeTimeProvider _time = new();
    private readonly ScreenTechnicsConnect _motor;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient(ScreenTechnicsConnect.DefaultPort);

    public ScreenTechnicsConnectTest()
    {
        _motor = new ScreenTechnicsConnect("Projector Screen", _mockClient.Object, RelayAction.Lower, 1, MoveSeconds, _time);
    }

    private async Task Connect()
    {
        var sends = Sends();
        await Task.Run(() => _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected));
        await Wait.UntilAsync(_time, ConnectedSettleTime, () => Sends() > sends, "The queued command was never sent after connecting");
    }

    private int Sends() => _mockClient.Invocations.Count(invocation => invocation.Method.Name == nameof(TcpClient.Send));

    private int Disconnects() => _mockClient.Invocations.Count(invocation => invocation.Method.Name == nameof(TcpClient.Disconnect));

    [Fact]
    public async Task Raise_SendsTheCommand()
    {
        _motor.Raise();
        await Connect();

        _mockClient.Verify(x => x.Send("30 1\r"));
    }

    [Fact]
    public async Task Lower_SendsTheCommand()
    {
        _motor.Lower();
        await Connect();

        _mockClient.Verify(x => x.Send("33 1\r"));
    }

    [Fact]
    public async Task Stop_SendsTheCommand()
    {
        _motor.Stop();
        await Connect();

        _mockClient.Verify(x => x.Send("36 1\r"));
    }

    [Fact]
    public async Task QueuedMessage_IsSentUnderTheEnqueuersTriggerId()
    {
        string? sentUnder = null;
        _mockClient.Setup(x => x.Send("30 1\r")).Callback(() => sentUnder = Trigger.Id);
        var raise = Trigger.Start();
        _motor.Raise();

        Trigger.Start();
        await Connect();

        Assert.Equal(raise, sentUnder);
    }

    [Fact]
    public async Task QueuedMessage_EnqueuedWithNoTriggerId_IsSentUnderTheConnectionEventsTriggerId()
    {
        string? sentUnder = null;
        _mockClient.Setup(x => x.Send("30 1\r")).Callback(() => sentUnder = Trigger.Id);
        Assert.Null(Trigger.Id);
        _motor.Raise();

        var connected = Trigger.Start();
        await Connect();

        Assert.Equal(connected, sentUnder);
    }

    [Fact]
    public async Task Client_DisconnectsAfterMoveTime()
    {
        _motor.Stop();

        await Wait.UntilAsync(_time, TimeSpan.FromSeconds(MoveSeconds), () => Disconnects() == 2,
            "The client was never disconnected after the move");
        _mockClient.Verify(x => x.Disconnect(), Times.Exactly(2));
    }

    [Fact]
    public async Task Client_DisconnectFailingAfterTheMove_IsRecordedAsAnError()
    {
        var failure = new InvalidOperationException("The socket is gone");
        _mockClient.Setup(x => x.Disconnect()).Throws(failure);
        _motor.Stop();

        await Wait.UntilAsync(_time, TimeSpan.FromSeconds(MoveSeconds), () => _motor.Errors.Any(),
            "The failed disconnect was never recorded");

        Assert.Same(failure, Assert.Single(_motor.Errors).Exception);
    }

    [Fact]
    public void Client_ConnectsOnCommand()
    {
        _motor.Stop();
        _mockClient.Verify(x => x.Connect(), Times.Once);
    }

    [Fact]
    public async Task CommandQueuedWhileTheQueuedOneIsSent_IsKeptForTheNextConnection()
    {
        _mockClient.Setup(x => x.Send("30 1\r")).Callback(() => _motor.Lower());
        _motor.Raise();

        await Connect();
        _mockClient.Verify(x => x.Send("33 1\r"), Times.Never);
        await Connect();

        _mockClient.Verify(x => x.Send("33 1\r"), Times.Once);
    }

    [Fact]
    public async Task QueuedCommand_IsSentTheSettleTimeAfterConnectingWithoutBlockingTheConnectionHandler()
    {
        _motor.Raise();

        await Task.Run(() => _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected));
        _time.Advance(ConnectedSettleTime - TimeSpan.FromTicks(1));
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        _time.Advance(TimeSpan.FromTicks(1));

        await Wait.UntilAsync(() => Sends() == 1, "The queued command was never sent after the settle time");
        _mockClient.Verify(x => x.Send("30 1\r"));
    }

    [Fact]
    public async Task QueuedCommand_SentOnConnection_SetsTheCommunicationStateToOkay()
    {
        _motor.Raise();

        await Connect();

        Assert.Equal(CommunicationState.Okay, _motor.CommunicationState);
    }

    [Fact]
    public void CommunicationClient_IsTheTcpClient()
    {
        Assert.Same(_mockClient.Object, _motor.CommunicationClient);
    }
}
