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

    protected override void DoRecord() => _communicationClient.Send($"{EscapeHeader}Y1RCDR\r");

    protected override void DoStop() => _communicationClient.Send($"{EscapeHeader}Y0RCDR\r");
    
    protected override void DoPause() => _communicationClient.Send($"{EscapeHeader}Y2RCDR\r");

    private void HandleResponse(string response)
    {
        var matches = _responseParser.Matches(response);
        ProcessRecordingState(matches[1].Groups[1].Value);
        if (TransportState is TransportState.Recording or TransportState.RecordingPaused)
        {
            TimestampHandlers?.Invoke(matches[4].Groups[1].Value);
        }
    }

    private void ProcessRecordingState(string state)
    {
        TransportState = state switch
        {
            "recording" => TransportState.Recording,
            "paused" => TransportState.RecordingPaused,
            "stopped" => TransportState.Stopped,
            "setup" => TransportState.PreparingToRecord,
            _ => TransportState.Unknown
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