using AVCoders.Core;

namespace AVCoders.Lighting;

public class DyNet : DeviceBase
{
    public static readonly ushort DefaultPort = 50000;
    private readonly byte _syncByteLogicalAddressingScheme = 0x1c;
    private const byte Broadcast = 0xFF;
    //From https://docs.dynalite.com/system-builder/latest/quick_start/dynet_opcodes.html
    
    public DyNet(CommunicationClient commsClient, string name) : base(name, commsClient)
    {
    }

    public void RecallPresetInBank(byte area, int preset, byte rampTimeIn100thsOfASecond = 0x64)
    {
        if (preset < 1)
            throw new ArgumentOutOfRangeException(nameof(preset), "Preset must be >= 1.");

        // Compute bank and index-within-bank (0..7 for P1..P8)
        int zeroBased = preset - 1;
        byte bank = (byte)(zeroBased / 8);
        byte presetInBank = GetByteForPreset(zeroBased % 8);

        // Fade time as 16-bit little-endian (here high byte is 0 because parameter is a byte)
        byte fadeLow = rampTimeIn100thsOfASecond;
        byte fadeHigh = 0x00;

        Send([
            _syncByteLogicalAddressingScheme,
            area,
            fadeLow,
            presetInBank,
            fadeHigh,
            bank,
            Broadcast
        ]);
        AddEvent(EventType.Preset, $"Recalled preset {preset} (by bank) in area {area}");
    }

    public void RecallPresetLinear(byte area, int preset, byte rampTimeIn20Ms = 0x64)
    {
        if (preset < 1 || preset > 256)
            throw new ArgumentOutOfRangeException(nameof(preset), "Preset must be between 1 and 255.");

        byte zeroBased = (byte)(preset - 1);

        // Fade time as 16-bit little-endian (here high byte is 0 because parameter is a byte)
        byte fadeLow = rampTimeIn20Ms;
        byte fadeHigh = 0x00;

        Send([
            _syncByteLogicalAddressingScheme,
            area,
            zeroBased,
            0x65,
            fadeLow,
            fadeHigh,
            Broadcast
        ]);
        AddEvent(EventType.Preset, $"Recalled preset {preset} (linear) in area {area}");
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
        if(level > 100)
            throw new ArgumentOutOfRangeException(nameof(level), "Level must be between 0 and 100.");
        return (byte)(level * 2.55);
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