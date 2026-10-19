using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Motor.Tests;

public class ScreenTechnicsConnectTest
{
    private readonly ScreenTechnicsConnect _motor;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient(ScreenTechnicsConnect.DefaultPort);

    public ScreenTechnicsConnectTest()
    {
        _motor = new ScreenTechnicsConnect("Projector Screen", _mockClient.Object, RelayAction.Lower, 1, 2);
    }
    
    [Fact]
    public void Raise_SendsTheCommand()
    {
        _motor.Raise();
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);
        
        _mockClient.Verify(x => x.Send("30 1\r"));
    }
    
    [Fact]
    public void Lower_SendsTheCommand()
    {
        _motor.Lower();
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);
        
        _mockClient.Verify(x => x.Send("33 1\r"));
    }
    
    [Fact]
    public void Stop_SendsTheCommand()
    {
        _motor.Stop();
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);
        
        _mockClient.Verify(x => x.Send("36 1\r"));
    }

    [Fact]
    public void QueuedMessage_IsSentUnderTheEnqueuersTriggerId()
    {
        string? sentUnder = null;
        _mockClient.Setup(x => x.Send("30 1\r")).Callback(() => sentUnder = Trigger.Id);
        var raise = Trigger.Start();
        _motor.Raise();

        Trigger.Start();
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);

        Assert.Equal(raise, sentUnder);
    }

    [Fact]
    public void QueuedMessage_EnqueuedWithNoTriggerId_IsSentUnderTheConnectionEventsTriggerId()
    {
        string? sentUnder = null;
        _mockClient.Setup(x => x.Send("30 1\r")).Callback(() => sentUnder = Trigger.Id);
        Assert.Null(Trigger.Id);
        _motor.Raise();

        var connected = Trigger.Start();
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);

        Assert.Equal(connected, sentUnder);
    }

    [Fact]
    public void Client_DisconnectsAfterMoveTime()
    {
        _motor.Stop();
        Thread.Sleep(2100);
        _mockClient.Verify(x => x.Disconnect(), Times.Exactly(2));
    }

    [Fact]
    public void Client_ConnectsOnCommand()
    {
        _motor.Stop();
        _mockClient.Verify(x => x.Connect(), Times.Once);
    }
}