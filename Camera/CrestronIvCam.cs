using AVCoders.Core;

namespace AVCoders.Camera;

public class CrestronIvCam : SonyVisca
{
    public const ushort DefaultPort = 5500;
    public const ushort DefaultRtspPort = 554;
    protected const int OsdMenuPreset = 95;
    protected const int RebootPreset = 99;

    private static readonly int[] ReservedPresets = [OsdMenuPreset, RebootPreset];

    private readonly ushort _rtspPort;

    protected virtual IReadOnlyCollection<int> PresetsBlockedOnRecall => ReservedPresets;

    protected virtual IReadOnlyCollection<int> PresetsBlockedOnSave => ReservedPresets;

    public CrestronIvCam(CommunicationClient client, string name, Dictionary<int, string> presetNames, byte cameraId = 1, int pollTime = 30, ushort rtspPort = DefaultRtspPort)
        : base(client, false, name, presetNames, cameraId, pollTime)
    {
        _rtspPort = rtspPort;
    }

    public string MainStreamUrl() => $"rtsp://{CommunicationClient.Host}:{_rtspPort}/1.h264";

    public override void RecallPreset(int presetNumber)
    {
        if (PresetsBlockedOnRecall.Contains(presetNumber))
        {
            using (PushProperties("RecallPreset"))
            {
                LogWarning("Preset {presetNumber} is reserved and can't be recalled", presetNumber);
                AddEvent(EventType.Error, $"Preset {presetNumber} is reserved and can't be recalled");
            }
            return;
        }
        base.RecallPreset(presetNumber);
    }

    public override void SavePreset(int presetNumber)
    {
        if (PresetsBlockedOnSave.Contains(presetNumber))
        {
            using (PushProperties("SavePreset"))
            {
                LogWarning("Preset {presetNumber} is reserved and can't be saved", presetNumber);
                AddEvent(EventType.Error, $"Preset {presetNumber} is reserved and can't be saved");
            }
            return;
        }
        base.SavePreset(presetNumber);
    }
}
