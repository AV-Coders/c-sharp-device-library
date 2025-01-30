using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public class ExtronSmp351 : Recorder
{
    private readonly CommunicationClient _communicationClient;
    public static readonly ushort DefaultPort = 22023;
    private readonly ThreadWorker _pollWorker;
    private readonly Regex _responseParser;
    private readonly ulong _memoryLowKBytes;
    private readonly ulong _memoryFullKBytes;
    public const string EscapeHeader = "\x1b";

    

    public ExtronSmp351(CommunicationClient communicationClient, ulong memoryLowKBytes, ulong memoryFullKBytes, int pollTime = 1000)
    {
        _communicationClient = communicationClient;
        _memoryLowKBytes = memoryLowKBytes;
        _memoryFullKBytes = memoryFullKBytes;
        _communicationClient.ResponseHandlers += HandleResponse;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromMilliseconds(pollTime));
        _pollWorker.Restart();

        string responsePattern = "<([^>]*)>";
        _responseParser = new Regex(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    public void Record() => _communicationClient.Send($"{EscapeHeader}Y1RCDR\r");

    public void Stop() => _communicationClient.Send($"{EscapeHeader}Y0RCDR\r");
    
    public void Pause() => _communicationClient.Send($"{EscapeHeader}Y2RCDR\r");
    
    
    private void SetRecordState(RecordState desiredState)
    {
        switch (desiredState)
        {
            case RecordState.Recording:
                Record();
                break;
            case RecordState.RecordingPaused:
                Pause();
                break;
            case RecordState.Stopped:
                Stop();
                break;
        }
    }

    private void HandleResponse(string response)
    {
        var matches = _responseParser.Matches(response);
        ProcessRecordingState(matches[1].Groups[1].Value);
        if (RecordState is RecordState.Recording or RecordState.RecordingPaused)
        {
            TimestampHandlers?.Invoke(matches[4].Groups[1].Value);
        }
    }

    private void ProcessRecordingState(string state)
    {
        RecordState = state switch
        {
            "recording" => RecordState.Recording,
            "paused" => RecordState.RecordingPaused,
            "stopped" => RecordState.Stopped,
            "setup" => RecordState.PreparingToRecord,
            _ => RecordState.Unknown
        };
    }

    private Task Poll( CancellationToken token)
    {
        if(_communicationClient.GetConnectionState() == ConnectionState.Connected)
        {
            _communicationClient.Send("I");
        }
        return Task.CompletedTask;
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
}