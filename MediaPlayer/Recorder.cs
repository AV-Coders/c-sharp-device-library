namespace AVCoders.MediaPlayer;

public abstract class Recorder : MediaPlayer
{
    public RecordStateHandler? RecordStateHandlers;
    public TimestampHandler? TimestampHandlers;
    private RecordState _recordState = RecordState.Unknown;
    
    public RecordState RecordState
    {
        get => _recordState;
        protected set
        {
            if (_recordState == value)
                return;
            _recordState = value;
            RecordStateHandlers?.Invoke(value);
        }
    }
}