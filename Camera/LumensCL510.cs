using AVCoders.Core;

namespace AVCoders.Camera;

// Commands from Lumens RS078 - CL510,V01 RS-232 command set v1.2
public class LumensCL510 : CameraBase
{
    public static readonly SerialSpec DefaultSpec = new SerialSpec(SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);

    private const byte Tele = 0x00;
    private const byte Wide = 0x01;
    private const int MaxPresets = 8;

    private readonly bool _autoFocusAfterZoom;
    private CancellationTokenSource? _autoFocusCts;

    public LumensCL510(string name, CommunicationClient client, bool autoFocusAfterZoom, Dictionary<int, string> presetNames)
        : base(name, client, presetNames)
    {
        _autoFocusAfterZoom = autoFocusAfterZoom;
        // This driver never reads a reply, so state is always assumed on send.
        DeviceSendsResponses = false;
    }

    protected override void OnDeviceSendsResponsesChanged()
    {
        if (!DeviceSendsResponses)
            return;
        LogWarning("This driver never reads replies, so DeviceSendsResponses stays false");
        DeviceSendsResponses = false;
    }

    private void Send(byte command, byte p1 = 0x00, byte p2 = 0x00, byte p3 = 0x00) =>
        CommunicationClient.Send([0xA0, command, p1, p2, p3, 0xAF]);

    // The device has no power feedback, so the state is set optimistically.
    public override void PowerOn()
    {
        Send(0xB1, 0x01);
        DesiredPowerState = PowerState.On;
        PowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        Send(0xB1, 0x00);
        DesiredPowerState = PowerState.Off;
        PowerState = PowerState.Off;
    }

    protected override void DoZoomStop()
    {
        CancelPendingAutoFocus();
        Send(0x10);

        if (!_autoFocusAfterZoom)
            return;

        _autoFocusCts = new CancellationTokenSource();
        var token = _autoFocusCts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token);
                OneTimeAutoFocus();
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    // Zoom Start (No AF).  The speed parameter was removed in v1.2 of the command set, so it's always 0.
    public override void ZoomIn()
    {
        CancelPendingAutoFocus();
        Send(0x11, Tele);
    }

    public override void ZoomOut()
    {
        CancelPendingAutoFocus();
        Send(0x11, Wide);
    }

    private void CancelPendingAutoFocus()
    {
        _autoFocusCts?.Cancel();
        _autoFocusCts?.Dispose();
        _autoFocusCts = null;
    }

    protected override void DoPanTiltStop() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltUp() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltDown() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltLeft() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltRight() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void SetAutoFocus(PowerState state)
    {
        if (state == PowerState.On)
        {
            AddEvent(EventType.Error, "This module doesn't support continuous autofocus, triggering a one-time focus instead");
            OneTimeAutoFocus();
            return;
        }
        AddEvent(EventType.Error, "This module doesn't support continuous autofocus, ignoring command");
    }

    public void OneTimeAutoFocus() => Send(0xA3, 0x01);

    public void AutoTune() => Send(0x22, 0x00);

    public void OneTimeAutoWhiteBalance() => Send(0x22, 0x01);

    // Confirmed on send whatever the flag says: there is no reply handler to confirm it later.
    public override void RecallPreset(int presetNumber)
    {
        if (!IsValidPreset(presetNumber))
            return;
        DoRecallPreset(presetNumber);
        LastRecalledPreset = presetNumber;
    }

    // Presets are zero-based here and 1-8 on the device
    public override void DoRecallPreset(int presetNumber)
    {
        if (!IsValidPreset(presetNumber))
            return;
        Send(0x03, 0x00, 0x00, (byte)(presetNumber + 1));
    }

    public override void SavePreset(int presetNumber)
    {
        if (!IsValidPreset(presetNumber))
            return;
        Send(0x03, 0x00, 0x01, (byte)(presetNumber + 1));
    }

    private bool IsValidPreset(int presetNumber)
    {
        if (presetNumber is >= 0 and < MaxPresets)
            return true;
        AddEvent(EventType.Error, $"Preset {presetNumber} is out of range, this module supports presets 0-{MaxPresets - 1}");
        return false;
    }
}
