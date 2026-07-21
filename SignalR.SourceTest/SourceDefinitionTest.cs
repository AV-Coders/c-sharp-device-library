namespace AVCoders.SignalR.Source.Tests;

public class SourceDefinitionTest
{
    [Fact]
    public void IsConnected_TransitionFiresEventWithNewValue()
    {
        var source = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");
        var handler = new Mock<Action<bool>>();
        source.OnConnectedChanged += handler.Object;

        source.SetIsConnected(true);

        handler.Verify(h => h.Invoke(true), Times.Once);
    }

    [Fact]
    public void IsConnected_NoOpDoesNotFireEvent()
    {
        var source = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");
        var handler = new Mock<Action<bool>>();
        source.OnConnectedChanged += handler.Object;

        source.SetIsConnected(false);

        handler.Verify(h => h.Invoke(It.IsAny<bool>()), Times.Never);
    }

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

    [Fact]
    public void PreviewUrl_DefaultsToEmpty()
    {
        var source = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");

        Assert.Equal(string.Empty, source.PreviewUrl);
    }

    [Fact]
    public void IsConnected_DefaultsToFalse()
    {
        var source = new TestSourceDefinition("Laptop", "Lectern HDMI", "laptop", "laptop");

        Assert.False(source.IsConnected);
    }
}
