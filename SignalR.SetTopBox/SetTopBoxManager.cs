using AVCoders.Core;
using AVCoders.MediaPlayer;

namespace AVCoders.SignalR.SetTopBox;

/// <summary>
/// Wraps a <see cref="AVCoders.MediaPlayer.MediaPlayer"/> that implements <see cref="ISetTopBox"/>
/// and folds its power, comms and active-source state into a single <see cref="SetTopBoxState"/>.
/// </summary>
public class SetTopBoxManager : DeviceBase
{
    private readonly AVCoders.MediaPlayer.MediaPlayer _device;
    private readonly ISetTopBox _setTopBox;

    public string SourceId { get; }
    public IReadOnlyCollection<RemoteButton> SupportedButtons => _setTopBox.SupportedButtons;
    public event Action<SetTopBoxState>? StateChanged;

    /// <param name="name">The group name UIs join; also the manager's device name.</param>
    /// <param name="device">The driver. Must implement <see cref="ISetTopBox"/>.</param>
    /// <param name="sourceId">The source-hub <c>SourceId</c> this box belongs to.</param>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an <see cref="ISetTopBox"/>.</exception>
    public SetTopBoxManager(string name, AVCoders.MediaPlayer.MediaPlayer device, string sourceId)
        : base(name, CommunicationClient.None)
    {
        if (device is not ISetTopBox setTopBox)
            throw new ArgumentException($"{device.Name} ({device.GetType().Name}) does not implement {nameof(ISetTopBox)}", nameof(device));

        _device = device;
        _setTopBox = setTopBox;
        SourceId = sourceId;

        _device.PowerStateHandlers += state =>
        {
            PowerState = state;
            RaiseStateChanged();
        };
        _device.DesiredPowerStateHandlers += _ => RaiseStateChanged();
        _device.CommunicationStateHandlers += _ => RaiseStateChanged();

        // Core has no active-source abstraction yet; the Apple TV is the only driver that reports it.
        if (_device is AppleTvCec appleTv)
            appleTv.ActiveSourceHandlers += _ => RaiseStateChanged();
    }

    public SetTopBoxState State => new(
        Name,
        SourceId,
        _setTopBox.SupportedButtons.Select(button => button.ToString()).ToArray(),
        _device.PowerState,
        _device.DesiredPowerState,
        _device is AppleTvCec { IsActiveSource: true },
        _device.CommunicationState);

    public void SendRemoteButton(RemoteButton button) => _setTopBox.SendIRCode(button);

    public override void PowerOn() => _device.PowerOn();

    public override void PowerOff() => _device.PowerOff();

    private void RaiseStateChanged() => StateChanged?.Invoke(State);
}
