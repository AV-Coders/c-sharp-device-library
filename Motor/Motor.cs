using AVCoders.Core;

namespace AVCoders.Motor;

public abstract class Motor : IDevice
{
    public readonly string Name;
    private readonly Action _powerOnAction;
    private readonly Action _powerOffAction;
    protected readonly int MoveSeconds;
    protected Guid CurrentMoveId;
    protected RelayAction CurrentMoveAction = RelayAction.None;
    public LogHandler? LogHandlers;

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

    public void PowerOn() => _powerOnAction.Invoke();

    public void PowerOff() => _powerOffAction.Invoke();
    
    public PowerState GetCurrentPowerState() => PowerState.Unknown;

    public CommunicationState GetCurrentCommunicationState() => CommunicationState.Okay;
    
    protected void Log(string message) => LogHandlers?.Invoke($"{DateTime.Now} - {Name} - Motor - {message}");

}