using AVCoders.Core;
using Serilog;

namespace AVCoders.Camera;

public class LumensCL511 : CameraBase
{
    public static readonly SerialSpec DefaultSpec = new SerialSpec(SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public const ushort DefaultPort = 9997;
    
    private readonly bool _autoTuneAfterZoom;
    private CancellationTokenSource? _autoTuneCts;

    public LumensCL511(string name, CommunicationClient client, bool autoTuneAfterZoom) : base(name, client)
    {
        _autoTuneAfterZoom = autoTuneAfterZoom;
    }

    public override void PowerOn() => CommunicationClient.Send([0xA0, 0xB1, 0x01, 0x00, 0x00, 0xAF]);

    public override void PowerOff() => CommunicationClient.Send([0xA0, 0xB1, 0x00, 0x00, 0x00, 0xAF]);

    public override void ZoomStop()
    {
        _autoTuneCts?.Cancel();
        CommunicationClient.Send([0xA0, 0x11, 0x00, 0x00, 0x00, 0xAF]);

        if (!_autoTuneAfterZoom) 
            return;
        
        _autoTuneCts = new CancellationTokenSource();
        var token = _autoTuneCts.Token;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, token);
                AutoTune();
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public override void ZoomIn()
    {
        _autoTuneCts?.Cancel();
        CommunicationClient.Send([0xA0, 0x11, 0x23, 0x00, 0x00, 0xAF]);
    }

    public override void ZoomOut()
    {
        _autoTuneCts?.Cancel();
        CommunicationClient.Send([0xA0, 0x11, 0x33, 0x00, 0x00, 0xAF]);
    }

    public override void PanTiltStop() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltUp() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltDown() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltLeft() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void PanTiltRight() => AddEvent(EventType.Error, "This module doesn't support Pan / Tilt");

    public override void SetAutoFocus(PowerState state)
    {
        if (state == PowerState.On)
        {
            AddEvent(EventType.Error, "This module doesn't support autofocus, triggering a one-time focus instead of enabling it");
            OneTimeAutoFocus();
            return;
        }
        AddEvent(EventType.Error, "This module doesn't support autofocus, ignoring command");
    }

    public void OneTimeAutoFocus() => CommunicationClient.Send([0xA0, 0xA3, 0x01, 0x00, 0x00, 0xAF]);
    
    public void OneTimeAutoWhiteBalance() => CommunicationClient.Send([0xA0, 0x22, 0x01, 0x00, 0x00, 0xAF]);

    public void AutoTune() => CommunicationClient.Send([0xA0, 0x22, 0x00, 0x00, 0x00, 0xAF]);

    // Only supports preset numbers 1-8
    public override void DoRecallPreset(int presetNumber)
    {
        byte actualPresetNumber = (byte)(presetNumber + 1); // 0 is not supported
        CommunicationClient.Send([0xA0, 0x03, 0x00, 0x00, actualPresetNumber, 0xAF]);
    }

    // Only supports preset numbers 1-8
    public override void SavePreset(int presetNumber)
    {
        byte actualPresetNumber = (byte)(presetNumber + 1); // 0 is not supported
        CommunicationClient.Send([0xA0, 0x03, 0x00, 0x01, actualPresetNumber, 0xAF]);
    }
}