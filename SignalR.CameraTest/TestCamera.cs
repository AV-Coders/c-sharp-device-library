using AVCoders.Camera;
using AVCoders.Core;
using AVCoders.Core.Tests;

namespace AVCoders.SignalR.Camera.Tests;

/// <summary>
/// Concrete <see cref="CameraBase"/> used by the SignalR.Camera tests so we can drive
/// state changes (PowerState, LastRecalledPreset) from outside the class.
/// </summary>
public class TestCamera : CameraBase
{
    public int PowerOnCallCount;
    public int PowerOffCallCount;
    public int DoZoomStopCallCount;
    public int ZoomInCallCount;
    public int ZoomOutCallCount;
    public int DoPanTiltStopCallCount;
    public int PanTiltUpCallCount;
    public int PanTiltDownCallCount;
    public int PanTiltLeftCallCount;
    public int PanTiltRightCallCount;
    public int DoRecallPresetCallCount;
    public int LastDoRecallPresetArg;
    public int SavePresetCallCount;
    public int LastSavePresetArg;
    public PowerState? LastSetAutoFocus;

    public TestCamera(string name = "TestCam", Dictionary<int, string>? presets = null)
        : base(name, TestFactory.CreateCommunicationClient().Object,
            presets ?? new Dictionary<int, string> { { 0, "Home" }, { 1, "Lectern" } })
    {
    }

    public void SetPowerStateForTest(PowerState state) => PowerState = state;
    public void SetLastRecalledPresetForTest(int preset) => LastRecalledPreset = preset;

    public override void PowerOn() => PowerOnCallCount++;
    public override void PowerOff() => PowerOffCallCount++;

    protected override void DoZoomStop() => DoZoomStopCallCount++;
    public override void ZoomIn() => ZoomInCallCount++;
    public override void ZoomOut() => ZoomOutCallCount++;

    protected override void DoPanTiltStop() => DoPanTiltStopCallCount++;
    public override void PanTiltUp() => PanTiltUpCallCount++;
    public override void PanTiltDown() => PanTiltDownCallCount++;
    public override void PanTiltLeft() => PanTiltLeftCallCount++;
    public override void PanTiltRight() => PanTiltRightCallCount++;

    public override void DoRecallPreset(int presetNumber)
    {
        DoRecallPresetCallCount++;
        LastDoRecallPresetArg = presetNumber;
    }

    public override void SavePreset(int presetNumber)
    {
        SavePresetCallCount++;
        LastSavePresetArg = presetNumber;
    }

    public override void SetAutoFocus(PowerState state) => LastSetAutoFocus = state;
}
