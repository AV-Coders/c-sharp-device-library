using AVCoders.Core;

namespace AVCoders.Matrix;

public delegate StreamChangeHandler StreamChangeHandler(uint streamId);

public abstract class SvsiBase : InputOutputStatus
{
    public const ushort DefaultPort = 50002;
    public readonly Dictionary<string, string> StatusDictionary;
    public StreamChangeHandler? StreamChangeHandlers;
    protected readonly TcpClient TcpClient;
    protected readonly ThreadWorker PollWorker;
    public uint StreamId;

    public SvsiBase(TcpClient tcpClient, int pollTime)
    {
        TcpClient = tcpClient;
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        StatusDictionary = new Dictionary<string, string>();
        TcpClient.ResponseHandlers += HandleResponse;
    }

    private void HandleResponse(string response)
    {
        response.Split('\r').ToList().ForEach(item =>
        {
            var keyValuePairs = item.Split(':');
            var key = keyValuePairs[0].Trim(':');
            if(key.Length < 1)
                return;

            if (key == "MAC")
            {
                StatusDictionary[key] = item.Remove(0, 4);
                return;
            }
            StatusDictionary[key] = keyValuePairs[1];
        });
        UpdateVariablesBasedOnStatus();
    }
    
    protected abstract void UpdateVariablesBasedOnStatus();

    protected void UpdateStreamId(uint streamId)
    {
        if (streamId == StreamId)
            return;
        
        StreamId = streamId;
        StreamChangeHandlers?.Invoke(streamId);
    }

    private void Poll(CancellationToken token) => TcpClient.Send("\r");
}