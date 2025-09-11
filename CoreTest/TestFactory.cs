using Moq;

namespace AVCoders.Core.Tests;

public static class TestFactory
{
    public static Mock<TcpClient> CreateTcpClient(ushort port = 23) => new ("host", port, "TcpClient", CommandStringFormat.Ascii);
    
    public static Mock<UdpClient> CreateUdpClient(ushort port = 23) => new ("host", port, "UdpClient", CommandStringFormat.Ascii);
    
    public static Mock<SerialClient> CreateSerialClient(ushort port = 1) => new ("SerialClient", "host", port, CommandStringFormat.Hex);
    
    public static Mock<SshClient> CreateSshClient(ushort port = 22) => new ("host", port, "SshClient", CommandStringFormat.Ascii);
    
    public static Mock<CommunicationClient> CreateCommunicationClient() => new ("CommunicationClient", "host", (ushort)22, CommandStringFormat.Ascii);
}