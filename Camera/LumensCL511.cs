using AVCoders.Core;
using Serilog;

namespace AVCoders.Camera;

public class LumensCL511 : CameraBase
{
    public static readonly SerialSpec DefaultSpec = new SerialSpec(SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public const ushort DefaultPort = 9997;
    
    private readonly bool _autoTuneAfterZoom;
    private readonly ThreadWorker _autoTuneWorker;

    public LumensCL511(string name, CommunicationClient client, bool autoTuneAfterZoom) : base(name, client)
    {
        _autoTuneAfterZoom = autoTuneAfterZoom;
        _autoTuneWorker = new ThreadWorker(TriggerAutoTune, TimeSpan.FromSeconds(1));
    }

    private Task TriggerAutoTune(CancellationToken arg)
    {
        AutoTune();
        Task.Run(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            return _autoTuneWorker.Stop();
        }, arg);
        return Task.CompletedTask;
    }

    public override void PowerOn() => CommunicationClient.Send([0xA0, 0xB1, 0x01, 0x00, 0x00, 0xAF]);

    public override void PowerOff() => CommunicationClient.Send([0xA0, 0xB1, 0x00, 0x00, 0x00, 0xAF]);

    public override void ZoomStop()
    {
        CommunicationClient.Send([0xA0, 0x11, 0x00, 0x00, 0x00, 0xAF]);
        if (_autoTuneAfterZoom)
            _autoTuneWorker.Restart();
    }

    public override void ZoomIn()
    {
        CommunicationClient.Send([0xA0, 0x11, 0x23, 0x00, 0x00, 0xAF]);
        _autoTuneWorker.Stop();
    }

    public override void ZoomOut()
    {
        CommunicationClient.Send([0xA0, 0x11, 0x33, 0x00, 0x00, 0xAF]);
        _autoTuneWorker.Stop();
    }

    public override void PanTiltStop() => Log.Error("LumensCL511 module doesn't support Pan / Tilt");

    public override void PanTiltUp() => Log.Error("LumensCL511 module doesn't support Pan / Tilt");

    public override void PanTiltDown() => Log.Error("LumensCL511 module doesn't support Pan / Tilt");

    public override void PanTiltLeft() => Log.Error("LumensCL511 module doesn't support Pan / Tilt");

    public override void PanTiltRight()
    {
        Log.Error("LumensCL511 module doesn't support Pan / Tilt");
    }

    public override void SetAutoFocus(PowerState state)
    {
        if (state == PowerState.On)
        {
            Log.Information("LumensCL511 module doesn't support autofocus, triggering a one-time focus instead");
            OneTimeAutoFocus();
        }
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