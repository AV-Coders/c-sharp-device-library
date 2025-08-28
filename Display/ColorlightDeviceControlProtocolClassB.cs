using AVCoders.Core;
using Serilog;

namespace AVCoders.Display;

public class ColorlightDeviceControlProtocolClassB : Display
{
    public const ushort TcpPort = 9999;
    public const ushort UdpPort = 9099;
    private readonly byte[] _heartbeatResponse = [0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];
    private readonly byte[] _heartbeatRequest = [0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];
    private uint _brightness;


    public ColorlightDeviceControlProtocolClassB(IpComms comms, string name) : base(new List<Input>(), name, Input.Unknown, comms, 1)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        PollWorker.Stop();
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties())
        {
            if (response.Take(8).ToArray().SequenceEqual(_heartbeatRequest))
            {
                CommunicationClient.Send(_heartbeatResponse);
            }
        }
    }

    public void RecallPresetOnZ8(int preset) // Limit of 16 presets
    {
        CommunicationClient.Send([0x02, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)preset, 0x00]);
    }

    public void RecallPreset(int preset) // Limit of 16 presets
    {
        CommunicationClient.Send([0x07, 0x10, 0x03, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)preset, 0x00]);
    }

    protected override Task DoPoll(CancellationToken token)
    {
        PollWorker.Stop();
        return Task.CompletedTask;
    }

    protected override void DoPowerOn()
    {
        CommunicationClient.Send([0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
    }

    protected override void DoPowerOff()
    {
        CommunicationClient.Send([0x10, 0x10, 0x00, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01]);
    }

    protected override void DoSetInput(Input input) => Log.Error("ColorlightDeviceControlProtocolClassB does not support input select");

    protected override void DoSetVolume(int percentage) => Log.Error("ColorlightDeviceControlProtocolClassB does not support volume");

    protected override void DoSetAudioMute(MuteState state) => Log.Error("ColorlightDeviceControlProtocolClassB does not support audio mute");

    public void SetBrightness(uint percentage)
    {
        if (percentage > 100)
        {
            Log.Error("The brightness can't go over 100%");
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
        if (_brightness - amount > 100)
        {
            SetBrightness(0);
            return;
        }
        SetBrightness(_brightness - amount);
    }
    
    protected override void HandleConnectionState(ConnectionState connectionState) { }
}