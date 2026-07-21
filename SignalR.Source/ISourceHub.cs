namespace AVCoders.SignalR.Source;

public interface ISourceHub
{
    Task UpdateSourceIndex(int index);
    Task UpdateSourceList(List<SourceDefinition> sources);
}
