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

    public void RecallPreset(byte area, byte preset, byte rampTimeIn100thsOfASecond = 0x14)
    {
        
        Send([
                _syncByteLogicalAddressingScheme,
                area,
                0x00,
                preset,
                0x00,
                rampTimeIn100thsOfASecond,
                0xFF
            ]
        );
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
        {
            checksum += b;
        }

        checksum = ~ checksum;
        checksum++;
        // I'm pretty sure the byte cast is enough...
        return (byte) (checksum & 0xFF);
    }
}