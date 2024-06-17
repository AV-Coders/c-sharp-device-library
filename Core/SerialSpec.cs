namespace AVCoders.Core;

public record SerialSpec(
    SerialBaud BaudRate,
    SerialParity Parity,
    SerialDataBits DataBits,
    SerialStopBits StopBits,
    SerialProtocol Protocol);