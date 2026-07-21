namespace AVCoders.SignalR.Source.Tests;

internal sealed record TestSourceDefinition(string Name, string Subtitle, string SourceId, string Icon)
    : SourceDefinition(Name, Subtitle, SourceId, Icon)
{
    public void SetIsConnected(bool value) => IsConnected = value;
    public void SetPreviewUrl(string value) => PreviewUrl = value;
}
