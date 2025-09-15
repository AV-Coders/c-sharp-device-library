using AVCoders.Core;

namespace AVCoders.Lighting;

public class DyNet
{
    public static readonly ushort DefaultPort = 50000;
    private readonly TcpClient _tcpClient;
    private readonly byte _syncByteLogicalAddressingScheme = 0x1c;
    
    public DyNet(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
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

        // Join: broadcast
        const byte join = 0xFF;

        Send([
            _syncByteLogicalAddressingScheme, // 0x1C
            area,                             // Area
            fadeLow,                          // Fade low byte
            presetInBank,                     // Preset within bank (0..7 => P1..P8)
            fadeHigh,                         // Fade high byte
            bank,                             // Preset bank (0 => P1-8, 1 => P9-16, ...)
            join                              // Join (0xFF broadcast)
        ]);
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
        _tcpClient.Send(messageWithChecksum);
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
}