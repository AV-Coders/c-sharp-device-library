using System.Runtime.InteropServices;
using AVCoders.Core;

namespace AVCoders.Display;

public class ColorlightDeviceControlProtocolClassB : Display
{
    public const ushort TcpPort = 9999;
    public const ushort UdpPort = 9099;
    private const int PresetCount = 16;
    private readonly byte[] _heartbeatResponse = [0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];
    private readonly byte[] _heartbeatRequest = [0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];
    private readonly List<byte> _gather = [];
    private uint _brightness;


    public ColorlightDeviceControlProtocolClassB(IpComms comms, string name) 
        : base([], name, Input.Unknown, comms, CommandStringFormat.Hex, 1)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties())
        {
            _gather.AddRange(response);
            int start;
            while ((start = CollectionsMarshal.AsSpan(_gather).IndexOf(_heartbeatRequest)) >= 0)
            {
                if (start > 0)
                    LogDebug("Unhandled reply {Reply}", Convert.ToHexString(_gather.GetRange(0, start).ToArray()));
                _gather.RemoveRange(0, start + _heartbeatRequest.Length);
                CommunicationState = CommunicationState.Okay;
                CommunicationClient.Send(_heartbeatResponse);
            }

            int unmatched = _gather.Count - (_heartbeatRequest.Length - 1);
            if (unmatched <= 0)
                return;
            LogDebug("Unhandled reply {Reply}", Convert.ToHexString(_gather.GetRange(0, unmatched).ToArray()));
            _gather.RemoveRange(0, unmatched);
        }
    }

    public void RecallPresetOnZ8(int preset)
    {
        if (RefuseInvalidPreset(preset))
            return;
        CommunicationClient.Send([0x02, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)preset, 0x00]);
    }

    public void RecallPreset(int preset)
    {
        if (RefuseInvalidPreset(preset))
            return;
        CommunicationClient.Send([0x07, 0x10, 0x03, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)preset, 0x00]);
    }

    private bool RefuseInvalidPreset(int preset)
    {
        if (preset is >= 0 and < PresetCount)
            return false;
        using (PushProperties("RefuseInvalidPreset"))
            LogWarning("Preset {Preset} is outside 0-{MaxPreset}", preset, PresetCount - 1);
        AddEvent(EventType.Error, $"Preset {preset} is outside 0-{PresetCount - 1}");
        return true;
    }

    protected override void RestartPolling() { }

    protected override Task DoPoll(CancellationToken token) => Task.CompletedTask;

    protected override void DoPowerOn()
    {
        CommunicationClient.Send([0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
    }

    protected override void DoPowerOff()
    {
        CommunicationClient.Send([0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01]);
    }

    protected override void DoSetInput(Input input) { using (PushProperties("DoSetInput")) LogWarning("ColorlightDeviceControlProtocolClassB does not support input select"); }

    public override bool SupportsLevel => false;

    public override bool SupportsMute => false;

    protected override void DoSetVolume(int percentage) { using (PushProperties("DoSetVolume")) LogWarning("ColorlightDeviceControlProtocolClassB does not support volume"); }

    protected override void DoSetAudioMute(MuteState state) { using (PushProperties("DoSetAudioMute")) LogWarning("ColorlightDeviceControlProtocolClassB does not support audio mute"); }

    public void SetBrightness(uint percentage)
    {
        if (percentage > 100)
        {
            using (PushProperties("SetBrightness"))
            LogWarning("The brightness can't go over 100%");
            AddEvent(EventType.Error, $"The brightness can't go over 100%, you've requested {percentage}");
            return;
        }
        _brightness = percentage;
        
        // Step 1: Convert percentage to a value between 0 and 10,000
        int scaledValue = (int)(percentage * 100);

        // Step 2: Get the high and low bytes
        byte highByte = (byte)(scaledValue >> 8); // Extract high byte
        byte lowByte = (byte)(scaledValue & 0xFF); // Extract low byte
        CommunicationClient.Send([0x50, 0x10, 0x00, 0x13,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00, 0x00, 0x00,  0x00, lowByte, highByte ]);
    }
    
    public void BrightnessUp(uint amount)
    {
        if (_brightness + amount > 100)
        {
            SetBrightness(100);
            return;
        }
        SetBrightness(_brightness + amount);
    }

    public void BrightnessDown(uint amount)
    {
        if (amount > _brightness)
        {
            SetBrightness(0);
            return;
        }
        SetBrightness(_brightness - amount);
    }
    
    protected override void HandleConnectionState(ConnectionState connectionState) { }
}