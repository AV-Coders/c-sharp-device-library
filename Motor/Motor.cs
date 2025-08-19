using AVCoders.Core;
using Serilog;

namespace AVCoders.Motor;

public abstract class Motor : DeviceBase
{
    private readonly Action _powerOnAction;
    private readonly Action _powerOffAction;
    protected readonly int MoveSeconds;
    protected Guid CurrentMoveId;
    protected RelayAction CurrentMoveAction = RelayAction.None;
    private CancellationTokenSource _cancellationTokenSource = new ();

    protected Motor(string name, RelayAction powerOnAction, int moveSeconds) : base(name)
    {
        MoveSeconds = moveSeconds;
        CurrentMoveId = Guid.Empty;
        _powerOnAction = powerOnAction == RelayAction.Raise ? Raise : Lower;
        _powerOffAction = powerOnAction == RelayAction.Raise ? Lower : Raise;
    }

    ~Motor()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    protected void CreateMoveTimer(Guid moveId, Action? completeAction = null)
    {
        using (PushProperties("CreateMoveTimer"))
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            new Task(() =>
                {
                    Task.Delay(TimeSpan.FromSeconds(MoveSeconds), _cancellationTokenSource.Token)
                        .Wait(_cancellationTokenSource.Token);
                    Log.Verbose("Move timer event, move id {MoveId}, current move: {CurrentMoveId}", moveId, CurrentMoveId);
                    CurrentMoveId = Guid.Empty;
                    CurrentMoveAction = RelayAction.None;
                    completeAction?.Invoke();
                }
            ).Start();
        }
    }

    public abstract void Raise();

    public abstract void Lower();

    public abstract void Stop();

    public override void PowerOn() => _powerOnAction.Invoke();

    public override void PowerOff() => _powerOffAction.Invoke();

}