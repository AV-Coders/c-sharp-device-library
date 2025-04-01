using AVCoders.Core;

namespace AVCoders.Display;

public class ColorlightDeviceControlProtocolClassB : Display
{
    public const ushort TcpPort = 9999;
    public const ushort UdpPort = 9099;
    private readonly IpComms _comms;
    private readonly int? _powerOnPreset;
    private readonly int? _powerOffPreset;
    private readonly byte[] _heartbeatResponse = [0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];
    private readonly byte[] _heartbeatRequest = [0x99, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];


    public ColorlightDeviceControlProtocolClassB(IpComms comms, string name, int? powerOnPreset, int? powerOffPreset) : base(new List<Input>(), name, Input.Unknown, 1)
    {
        _comms = comms;
        _comms.ResponseByteHandlers += HandleResponse;
        _powerOnPreset = powerOnPreset;
        _powerOffPreset = powerOffPreset;
        PollWorker.Stop();
    }

    private void HandleResponse(byte[] response)
    {
        if (response.Take(8).ToArray().SequenceEqual(_heartbeatRequest))
        {
            _comms.Send(_heartbeatResponse);
        }
    }

    public void RecallPresetOnZ8(int preset) // Limit of 16 presets
    {
        _comms.Send([0x02, 0x10, 0x00, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)preset, 0x00]);
    }

    public void RecallPreset(int preset) // Limit of 16 presets
    {
        _comms.Send([0x07, 0x10, 0x03, 0x13, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)preset, 0x00]);
    }

    protected override Task Poll(CancellationToken token)
    {
        PollWorker.Stop();
        return Task.CompletedTask;
    }

    protected override void DoPowerOn()
    {
        if (_powerOnPreset != null)
            RecallPreset(_powerOnPreset.Value);
    }

    protected override void DoPowerOff()
    {
        if(_powerOffPreset != null)
            RecallPreset(_powerOffPreset.Value);
    }

    protected override void DoSetInput(Input input) => Debug("This module does not support input select");

    protected override void DoSetVolume(int percentage) => Debug("This device does not support volume");

    protected override void DoSetAudioMute(MuteState state) => Debug("This device does not support audio mute");
}