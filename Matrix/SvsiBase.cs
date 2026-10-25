using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class SvsiBase : AVoIPEndpoint
{
    public const ushort DefaultPort = 50002;
    public const ushort SerialPassthroughPort = 50004;
    public readonly Dictionary<string, string> StatusDictionary;
    protected readonly ThreadWorker PollWorker;
    private readonly object _partialLineLock = new();
    private string _partialLine = string.Empty;
    private uint? _streamId;

    public uint StreamId
    {
        get => _streamId ?? 0;
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
        HandleConnectionState(CommunicationClient.ConnectionState);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        DeviceConnectionState = connectionState;
        if (connectionState != ConnectionState.Connected)
        {
            lock (_partialLineLock)
                _partialLine = string.Empty;
            return;
        }
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
            string[] lines;
            lock (_partialLineLock)
            {
                lines = (_partialLine + response).Split('\r');
                _partialLine = lines[^1];
            }
            foreach (var line in lines[..^1])
            {
                if (line.Split(':', 2) is not [var key, var value] || key.Length == 0)
                    continue;
                StatusDictionary[key] = value;
            }
            UpdateStreamId();
            UpdateVariablesBasedOnStatus();
        }
    }

    private void UpdateStreamId()
    {
        if (!StatusDictionary.TryGetValue("STREAM", out var streamId))
            return;
        if (uint.TryParse(streamId, out var parsedStreamId))
            StreamId = parsedStreamId;
        else
            LogWarning("The stream id {StreamId} is not a number", streamId);
    }

    protected abstract void UpdateVariablesBasedOnStatus();

    private Task Poll(CancellationToken token)
    {
        CommunicationClient.Send("\r");
        return Task.CompletedTask;
    }
}