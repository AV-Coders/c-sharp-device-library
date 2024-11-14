using AVCoders.Core;

namespace AVCoders.Motor;

public abstract class Motor : DeviceBase
{
    public readonly string Name;
    private readonly Action _powerOnAction;
    private readonly Action _powerOffAction;
    protected readonly int MoveSeconds;
    protected Guid CurrentMoveId;
    protected RelayAction CurrentMoveAction = RelayAction.None;

    protected Motor(string name, RelayAction powerOnAction, int moveSeconds)
    {
        Name = name;
        MoveSeconds = moveSeconds;
        CurrentMoveId = Guid.Empty;
        _powerOnAction = powerOnAction == RelayAction.Raise ? Raise : Lower;
        _powerOffAction = powerOnAction == RelayAction.Raise ? Lower : Raise;
    }

    protected void CreateMoveTimer(Guid moveId) => new Thread(x =>
        {
            Thread.Sleep(MoveSeconds * 1000);
            Log($"Move timer event, move id {moveId}, current move: {CurrentMoveId}");
            if (CurrentMoveId == moveId)
            {
                CurrentMoveId = Guid.Empty;
                CurrentMoveAction = RelayAction.None;
            }
        }
    ).Start();

    public abstract void Raise();

    public abstract void Lower();

    public abstract void Stop();

    public override void PowerOn() => _powerOnAction.Invoke();

    public override void PowerOff() => _powerOffAction.Invoke();

}