using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class SvsiBase : AVoIPEndpoint
{
    public const ushort DefaultPort = 50002;
    public const ushort SerialPassthroughPort = 50004;
    public readonly Dictionary<string, string> StatusDictionary;
    protected readonly TcpClient TcpClient;
    protected readonly ThreadWorker PollWorker;
    private uint _streamId;

    public uint StreamId
    {
        get => _streamId;
        protected set
        {
            if (value == _streamId)
                return;
            _streamId = value;
            StreamAddress = value.ToString();
        }
    }

    public SvsiBase(TcpClient tcpClient, int pollTime, AVoIPDeviceType deviceType) : base(deviceType)
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

    private Task Poll(CancellationToken token)
    {
        TcpClient.Send("\r");
        return Task.CompletedTask;
    }
}