namespace AVCoders.MediaPlayer;

public enum RecordState
{
    Unknown,
    Recording,
    RecordingPaused,
    Stopped,
    PreparingToRecord
}

public delegate void RecordStateHandler(RecordState state);

public delegate void TimestampHandler(string timestamp);