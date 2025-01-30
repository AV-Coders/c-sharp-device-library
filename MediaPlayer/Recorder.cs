using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public abstract class Recorder : MediaPlayer
{
    public TimestampHandler? TimestampHandlers;
    
    public void SetRecordState(TransportState desiredState)
    {
        switch (desiredState)
        {
            case TransportState.Recording:
                Record();
                break;
            case TransportState.RecordingPaused:
                Pause();
                break;
            case TransportState.Stopped:
                Stop();
                break;
        }
    }

    public void Record()
    {
        DesiredTransportState = TransportState.Recording;
        DoRecord();
    }

    public void Pause()
    {
        if (TransportState is TransportState.Recording or TransportState.PreparingToRecord)
            DesiredTransportState = TransportState.RecordingPaused;
        if (DesiredTransportState is TransportState.Playing)
            DesiredTransportState = TransportState.Paused;
        DoPause();
    }

    public void Stop()
    {
        DesiredTransportState = TransportState.Stopped;
        DoStop();
    }

    protected abstract void DoRecord();
    protected abstract void DoPause();
    protected abstract void DoStop();

}