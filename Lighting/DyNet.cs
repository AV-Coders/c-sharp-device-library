using AVCoders.Core;

namespace AVCoders.Lighting;

public class DyNet : DeviceBase
{
    public static readonly ushort DefaultPort = 50000;
    private readonly byte _syncByteLogicalAddressingScheme = 0x1c;
    private const byte Broadcast = 0xFF;
    private const int MinimumPreset = 1;
    private const int MaximumPreset = 8;
    private const int MinimumLevel = 0;
    private const int MaximumLevel = 100;
    //From https://docs.dynalite.com/system-builder/latest/quick_start/dynet_opcodes.html
    private static readonly byte[] PresetOpcodes = [0x00, 0x01, 0x02, 0x03, 0x0A, 0x0B, 0x0C, 0x0D];

    private readonly List<byte> _gather = new();

    public DyNet(CommunicationClient commsClient, string name) : base(name, commsClient)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties())
        {
            _gather.AddRange(response);

            while (true)
            {
                while (_gather.Count > 0 && _gather[0] != _syncByteLogicalAddressingScheme)
                    _gather.RemoveAt(0);

                if (_gather.Count < 8)
                    return;

                byte[] frame = _gather.Take(8).ToArray();
                if (CalculateChecksum(frame.Take(7).ToArray()) != frame[7])
                {
                    // A sync byte mid-frame, or a corrupt frame - realign on the next candidate
                    _gather.RemoveAt(0);
                    continue;
                }

                _gather.RemoveRange(0, 8);
                ProcessFrame(frame);
            }
        }
    }

    private void ProcessFrame(byte[] frame)
    {
        byte area = frame[1];
        byte bank = frame[5];
        switch (frame[3])
        {
            case <= 0x03:
                AddEvent(EventType.Preset, $"Area {area} recalled preset {bank * 8 + frame[3] + 1}");
                break;
            case >= 0x0A and <= 0x0D:
                AddEvent(EventType.Preset, $"Area {area} recalled preset {bank * 8 + frame[3] - 0x0A + 5}");
                break;
            case 0x63:
                AddEvent(EventType.Preset, $"Current preset requested for area {area}");
                break;
            case 0x76:
                AddEvent(EventType.Level, frame[2] == Broadcast
                    ? $"Area {area} stopped fading"
                    : $"Area {area} channel {frame[2] + 1} stopped fading");
                break;
            case 0x79:
                // Levels are inverted on the wire - 0x01 is 100%, 0xFF is 0%
                int percentage = (int)Math.Round((255 - frame[2]) / 2.54);
                double seconds = (frame[5] << 8 | frame[4]) * 20 / 1000.0;
                AddEvent(EventType.Level, $"Area {area} fading to {percentage}% over {seconds}s");
                break;
        }
    }

    public void SelectCurrentPreset(byte area, byte preset, byte rampTimeIn100thsOfASecond = 0x64)
    {
        if (preset is < MinimumPreset or > MaximumPreset)
        {
            using (PushProperties())
                LogWarning("Not recalling preset {Preset} for area {Area} as it is out of range, must be between {MinimumPreset} and {MaximumPreset}", preset, area, MinimumPreset, MaximumPreset);
            return;
        }

        byte fadeLow = rampTimeIn100thsOfASecond;
        byte fadeHigh = 0x00;
        byte bank = 0x00;
        
        Send([
            _syncByteLogicalAddressingScheme,
            area,
            fadeLow,
            PresetOpcodes[preset - MinimumPreset],
            fadeHigh,
            bank,
            Broadcast
        ]);

    }

    public void PowerOffArea(byte area, byte rampTimeIn100thsOfASecond = 0x64)
    {
        byte fadeLow = rampTimeIn100thsOfASecond;
        Send([
            _syncByteLogicalAddressingScheme,
            area,
            Broadcast,                        // All Channels in area
            0x68,
            0x00,
            fadeLow,
            Broadcast
        ]);
        AddEvent(EventType.Power, $"Powered off area {area}");
    }

    public void PowerOnArea(byte area, byte rampTimeIn100thsOfASecond = 0x64)
    {
        byte fadeLow = rampTimeIn100thsOfASecond;
        Send([
            _syncByteLogicalAddressingScheme,
            area,
            Broadcast,                        // All Channels in area
            0x69,
            0x00,
            fadeLow,
            Broadcast
        ]);
        AddEvent(EventType.Power, $"Powered on area {area}");
    }

    public void RampAreaToLevel(byte area, int level, byte rampTimeIn100thsOfASecond = 0x64)
    {
        if (level is < MinimumLevel or > MaximumLevel)
        {
            using (PushProperties())
                LogWarning("Not ramping area {Area} to level {Level} as it is out of range, must be between {MinimumLevel} and {MaximumLevel}", area, level, MinimumLevel, MaximumLevel);
            return;
        }

        Send([
            _syncByteLogicalAddressingScheme,
            area,
            Broadcast,                        // All Channels in area
            0x71,
            GetLevelFromPercentage(level),
            rampTimeIn100thsOfASecond,
            Broadcast
        ]);
        AddEvent(EventType.Level, $"Ramped area {area} to level {level}%");
    }

    private byte GetLevelFromPercentage(int level)
    {
        // Levels are inverted on the wire - 0x01 is 100%, 0xFF is 0%
        return (byte)Math.Round(255 - level * 2.54);
    }

    private void Send(byte[] messageWithoutChecksum)
    {
        byte[] messageWithChecksum = new byte[messageWithoutChecksum.Length + 1];
        Array.Copy(messageWithoutChecksum, messageWithChecksum, messageWithoutChecksum.Length);
        messageWithChecksum[messageWithoutChecksum.Length] = CalculateChecksum(messageWithoutChecksum);
        CommunicationClient.Send(messageWithChecksum);
    }

    public static byte CalculateChecksum(byte[] message)
    {
        int checksum = 0;
        foreach (byte b in message)
            checksum += b;

        checksum = ~checksum;
        checksum++;
        return (byte)(checksum & 0xFF);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
}