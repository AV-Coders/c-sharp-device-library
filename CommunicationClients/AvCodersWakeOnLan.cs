using System.Net;
using Microsoft.Extensions.Logging;
using UdpClient = System.Net.Sockets.UdpClient;

namespace AVCoders.CommunicationClients;

public class AvCodersWakeOnLan : IWakeOnLan
{
    private const int Rounds = 3;
    private const int EchoPort = 7;
    private const int DiscardPort = 9;
    private static readonly TimeSpan RoundGap = TimeSpan.FromMilliseconds(300);
    private readonly ILogger _logger = LogBase.LoggerFactory.CreateLogger<AvCodersWakeOnLan>();

    public void Wake(string mac)
    {
        byte[] wolPacket;
        try
        {
            wolPacket = BuildMagicPacket(ParseMacAddress(mac));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "'{Mac}' is not a valid MAC address, no Wake-on-LAN packets were sent", mac);
            return;
        }
        _ = Task.Run(() => SendRounds(wolPacket, mac));
    }

    private async Task SendRounds(byte[] wolPacket, string mac)
    {
        try
        {
            using var client = new UdpClient();
            for (int i = 0; i < Rounds; i++)
            {
                client.Send(wolPacket, new IPEndPoint(IPAddress.Broadcast, EchoPort));
                client.Send(wolPacket, new IPEndPoint(IPAddress.Broadcast, DiscardPort));
                await Task.Delay(RoundGap);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to send the Wake-on-LAN packets to '{Mac}'", mac);
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
