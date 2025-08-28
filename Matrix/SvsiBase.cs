using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class SvsiBase : AVoIPEndpoint
{
    public const ushort DefaultPort = 50002;
    public const ushort SerialPassthroughPort = 50004;
    public readonly Dictionary<string, string> StatusDictionary;
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

    public SvsiBase(string name, TcpClient tcpClient, AVEndpointType deviceType) : base(name, deviceType, tcpClient)
    {
        StatusDictionary = new Dictionary<string, string>();
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        
        PollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(45), true);
        PollWorker.Restart();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected) 
            return;
        Task.Delay(300).ContinueWith(_ =>
        {
            Poll(CancellationToken.None);
            PollWorker.Restart();
        });
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            response.Split('\r').ToList().ForEach(item =>
            {
                if (!item.Contains(':'))
                    return;
                var keyValuePairs = item.Split(':');
                var key = keyValuePairs[0].Trim(':');
                if (key.Length < 1)
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
    }
    
    protected abstract void UpdateVariablesBasedOnStatus();

    private Task Poll(CancellationToken token)
    {
        CommunicationClient.Send("\r");
        return Task.CompletedTask;
    }
}