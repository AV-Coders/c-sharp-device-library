using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public enum RecordState
{
    Unknown,
    Recording,
    RecordingPaused,
    Stopped
}

public delegate void RecordStateHandler(RecordState state);

public delegate void TimestampHandler(string timestamp);

public class ExtronSmp351
{
    private readonly CommunicationClient _communicationClient;
    public static readonly ushort DefaultPort = 22023;
    private readonly ThreadWorker _pollWorker;
    public RecordStateHandler? RecordStateHandlers;
    public TimestampHandler? TimestampHandlers;
    private RecordState _recordState;
    private readonly Regex _responseParser;
    private readonly ulong _memoryLowKBytes;
    private readonly ulong _memoryFullKBytes;

    public ExtronSmp351(CommunicationClient communicationClient, ulong memoryLowKBytes, ulong memoryFullKBytes, int pollTime = 1000)
    {
        _communicationClient = communicationClient;
        _memoryLowKBytes = memoryLowKBytes;
        _memoryFullKBytes = memoryFullKBytes;
        _communicationClient.ResponseHandlers += HandleResponse;
        _recordState = RecordState.Unknown;
        _pollWorker = new ThreadWorker(PollRecorderThreadFunction, TimeSpan.FromMilliseconds(pollTime));
        _pollWorker.Restart();

        string responsePattern = "<([^>]*)>";
        _responseParser = new Regex(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private void HandleResponse(string response)
    {
        RecordStateHandlers?.Invoke(RecordState.Stopped);
        var matches = _responseParser.Matches(response);
        ProcessRecordingState(matches[1].Groups[1].Value);
        if (_recordState is RecordState.Recording or RecordState.RecordingPaused)
        {
            TimestampHandlers?.Invoke(matches[4].Groups[1].Value);
        }
    }

    private void ProcessRecordingState(string state)
    {
        RecordState currentState = state switch
        {
            "recording" => RecordState.Recording,
            "paused" => RecordState.RecordingPaused,
            "stopped" => RecordState.Stopped,
            _ => RecordState.Unknown
        };

        if (currentState == _recordState)
            return;
        
        RecordStateHandlers?.Invoke(currentState);
        _recordState = currentState;
    }

    private void PollRecorderThreadFunction( CancellationToken token)
    {
        if(_communicationClient.GetConnectionState() == ConnectionState.Connected)
        {
            _communicationClient.Send("I");
        }
    }
}