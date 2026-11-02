namespace AVCoders.SignalR.Source.Tests;

public class SourceDefinitionTest
{
    [Fact]
    public void IsConnected_RepeatedTransitionFiresOncePerChange()
    {
        var source = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");
        var handler = new Mock<Action<bool>>();
        source.OnConnectedChanged += handler.Object;

        source.SetIsConnected(true);
        source.SetIsConnected(true);
        source.SetIsConnected(false);

        handler.Verify(h => h.Invoke(true), Times.Once);
        handler.Verify(h => h.Invoke(false), Times.Once);
    }
}
