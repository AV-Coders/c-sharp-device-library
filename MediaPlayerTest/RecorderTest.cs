using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.MediaPlayer.Tests;

public class TestRecorder : Recorder
{
    public TestRecorder(CommunicationClient comms) : base("TestRecorder", comms)
    {
    }

    public void SetTransportState(TransportState state) => TransportState = state;
    public TransportState GetDesiredTransportState() => DesiredTransportState;
    public void SetDesiredTransportState(TransportState state) => DesiredTransportState = state;

    public override void PowerOn() { }
    public override void PowerOff() { }
    public override void AddMarker() { }
    public bool UpdateTransportStateOnAction { get; set; }
    protected override void DoRecord() { if (UpdateTransportStateOnAction) TransportState = TransportState.Recording; }
    protected override void DoPause() { if (UpdateTransportStateOnAction) TransportState = TransportState.RecordingPaused; }
    protected override void DoStop() { if (UpdateTransportStateOnAction) TransportState = TransportState.Stopped; }
}

public class RecorderTest
{
    private readonly TestRecorder _recorder;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly Mock<TransportStateHandler> _transportStateHandler = new();

    public RecorderTest()
    {
        _recorder = new TestRecorder(_mockClient.Object);
        _recorder.TransportStateHandlers += _transportStateHandler.Object;
    }

    [Fact]
    public void Record_FiresTransportStateChangingWithRecordingBeforeTheStateChanges()
    {
        TransportState? received = null;
        TransportState? capturedDuring = null;
        _recorder.TransportStateChanging += (_, state) =>
        {
            received = state;
            capturedDuring = _recorder.TransportState;
        };

        _recorder.Record();

        Assert.Equal(TransportState.Recording, received);
        Assert.NotNull(capturedDuring);
        Assert.NotEqual(TransportState.Recording, capturedDuring);
    }

    [Fact]
    public void Stop_FiresTransportStateChangingWithStoppedBeforeTheStateChanges()
    {
        TransportState? received = null;
        TransportState? capturedDuring = null;
        _recorder.TransportStateChanging += (_, state) =>
        {
            received = state;
            capturedDuring = _recorder.TransportState;
        };

        _recorder.Stop();

        Assert.Equal(TransportState.Stopped, received);
        Assert.NotNull(capturedDuring);
        Assert.NotEqual(TransportState.Stopped, capturedDuring);
    }

    [Fact]
    public void Pause_FiresTransportStateChangingWhenRecording()
    {
        _recorder.SetTransportState(TransportState.Recording);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.Pause();

        Assert.Equal(TransportState.RecordingPaused, received);
    }

    [Fact]
    public void Pause_DoesNotFireTransportStateChangingWhenStopped()
    {
        _recorder.SetTransportState(TransportState.Stopped);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.Pause();

        Assert.Null(received);
    }

    [Fact]
    public void TransportStateHandlers_FiresWhenTransportStateChanges()
    {
        _recorder.SetTransportState(TransportState.Recording);

        _transportStateHandler.Verify(x => x.Invoke(TransportState.Recording));
    }

    [Fact]
    public void TransportStateHandlers_DoesNotFireWhenStateIsUnchanged()
    {
        _recorder.SetTransportState(TransportState.Unknown);

        _transportStateHandler.Verify(x => x.Invoke(It.IsAny<TransportState>()), Times.Never);
    }

    [Theory]
    [InlineData(TransportState.Recording)]
    [InlineData(TransportState.RecordingPaused)]
    [InlineData(TransportState.Stopped)]
    public void SetRecordState_CallsTheMatchingTransportMethod(TransportState desiredState)
    {
        _recorder.SetTransportState(TransportState.PreparingToRecord);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.SetRecordState(desiredState);

        Assert.Equal(desiredState, received);
    }

    [Fact]
    public void Pause_FiresTransportStateChangingWhenPreparingToRecord()
    {
        _recorder.SetTransportState(TransportState.PreparingToRecord);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.Pause();

        Assert.Equal(TransportState.RecordingPaused, received);
    }

    [Fact]
    public void Pause_FiresTransportStateChangingWithPausedWhenPlaying()
    {
        _recorder.SetDesiredTransportState(TransportState.Playing);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.Pause();

        Assert.Equal(TransportState.Paused, received);
    }

    [Fact]
    public void Record_SetsDesiredTransportStateToRecording()
    {
        _recorder.Record();

        Assert.Equal(TransportState.Recording, _recorder.GetDesiredTransportState());
    }

    [Fact]
    public void Stop_SetsDesiredTransportStateToStopped()
    {
        _recorder.Stop();

        Assert.Equal(TransportState.Stopped, _recorder.GetDesiredTransportState());
    }

    [Fact]
    public void Pause_SetsDesiredTransportStateToRecordingPausedWhenRecording()
    {
        _recorder.SetTransportState(TransportState.Recording);

        _recorder.Pause();

        Assert.Equal(TransportState.RecordingPaused, _recorder.GetDesiredTransportState());
    }

    [Fact]
    public void Record_DoesNotFireTransportStateChangingWhenAlreadyRecording()
    {
        _recorder.SetTransportState(TransportState.Recording);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.Record();

        Assert.Null(received);
    }

    [Fact]
    public void Stop_DoesNotFireTransportStateChangingWhenAlreadyStopped()
    {
        _recorder.SetTransportState(TransportState.Stopped);
        TransportState? received = null;
        _recorder.TransportStateChanging += (_, state) => received = state;

        _recorder.Stop();

        Assert.Null(received);
    }

    [Fact]
    public void TransportStateChanging_FiresBeforeTransportStateHandlers()
    {
        _recorder.UpdateTransportStateOnAction = true;
        var order = new List<string>();
        _recorder.TransportStateChanging += (_, _) => order.Add("changing");
        _recorder.TransportStateHandlers += _ => order.Add("changed");

        _recorder.Record();

        Assert.Equal(["changing", "changed"], order);
    }
}
