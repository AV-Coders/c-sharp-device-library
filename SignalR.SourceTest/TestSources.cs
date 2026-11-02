namespace AVCoders.SignalR.Source.Tests;

public static class TestSources
{
    public static readonly SourceDefinition None = new("None", "Not off, not on", "None", "off");
    public static readonly SourceDefinition Laptop = new("Laptop", "Lectern HDMI", "laptop", "laptop");
    public static readonly SourceDefinition Wireless = new("Wireless", "Wireless Presenter", "wireless", "wifi");
}
