using AVCoders.Core;
using Moq;

namespace AVCoders.Motor.Tests;

public class ScreenTechnicsConnectTest
{
    private readonly ScreenTechnicsConnect _motor;
    private readonly Mock<TcpClient> _mockClient;

    public ScreenTechnicsConnectTest()
    {
        _mockClient = new Mock<TcpClient>("foo", ScreenTechnicsConnect.DefaultPort, "bar");
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