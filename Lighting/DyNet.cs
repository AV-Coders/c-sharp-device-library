using AVCoders.Core;

namespace AVCoders.Lighting;

public class DyNet : DeviceBase
{
    public static readonly ushort DefaultPort = 50000;
    private readonly byte _syncByteLogicalAddressingScheme = 0x1c;
    private const byte Broadcast = 0xFF;
    //From https://docs.dynalite.com/system-builder/latest/quick_start/dynet_opcodes.html
    
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

            if (_gather.Count > 1024)
            {
                LogWarning("Gather buffer exceeded 1024 bytes, clearing");
                _gather.Clear();
                return;
            }

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
        if(preset < 1 || preset > 8)
            throw new ArgumentOutOfRangeException(nameof(preset), "Preset must be between 1 and 8.");
        
        byte zeroBased = (byte)(preset - 1);
        byte fadeLow = rampTimeIn100thsOfASecond;
        byte fadeHigh = 0x00;
        byte bank = 0x00;
        
        Send([
            _syncByteLogicalAddressingScheme,
            area,
            fadeLow,
            GetByteForPreset(zeroBased),
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
        if(level < 0 || level > 100)
            throw new ArgumentOutOfRangeException(nameof(level), "Level must be between 0 and 100.");
        // Levels are inverted on the wire - 0x01 is 100%, 0xFF is 0%
        return (byte)Math.Round(255 - level * 2.54);
    }

    private byte GetByteForPreset(int preset)
    {
        return preset switch
        {
            0 => 0x00,
            1 => 0x01,
            2 => 0x02,
            3 => 0x03,
            4 => 0x0A,
            5 => 0x0B,
            6 => 0x0C,
            7 => 0x0D,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), "Preset must be >= 1 and <= 8.")
        };
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