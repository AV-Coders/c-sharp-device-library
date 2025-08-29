using System.Net;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersWakeOnLan : IWakeOnLan
{
    public void Wake(string mac)
    {
        var wolPacket = BuildMagicPacket(ParseMacAddress(mac));
        using var client = new UdpClient();
        for (int i = 0; i < 3; i++)
        {
            client.Send(wolPacket, new IPEndPoint(IPAddress.Broadcast, 7));
            client.Send(wolPacket, new IPEndPoint(IPAddress.Broadcast, 9));
            Thread.Sleep(300);
        }
    }
    
    private byte[] BuildMagicPacket(byte[] macAddress)
    {
        if (macAddress.Length != 6) throw new ArgumentException("The MAC is invalid");

        List<byte> magic = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];

        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                magic.Add(macAddress[j]);
            }
        }
        return magic.ToArray();
    }

    private static byte[] ParseMacAddress(string text)
    {
        string[] tokens = text.Split(':', '-');

        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(tokens[i], 16);
        }
        return bytes;
    }
}