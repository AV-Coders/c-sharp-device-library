namespace AVCoders.Core;

public class SerialSpec
{
    public SerialBaud BaudRate { get; }
    public SerialParity Parity { get; }
    public SerialDataBits DataBits { get; }
    public SerialStopBits StopBits { get; }
    public SerialProtocol Protocol { get; }

    public SerialSpec(
        SerialBaud baudRate,
        SerialParity parity,
        SerialDataBits dataBits,
        SerialStopBits stopBits,
        SerialProtocol protocol)
    {
        BaudRate = baudRate;
        Parity = parity;
        DataBits = dataBits;
        StopBits = stopBits;
        Protocol = protocol;
    }
}