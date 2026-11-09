using AVCoders.Core;

namespace AVCoders.Matrix;

// HdcpI / HdcpO codes as the IN1806/1808, Annotator 401 and DTP2 CrossPoint 82 report them (1 = no HDCP, 2 = HDCP).
// IN16xx, SW HD 4K PLUS and first-generation DTP CrossPoint use the opposite codes and keep their own tables.
internal static class ExtronSisHdcp
{
    public static (ConnectionState Connection, HdcpStatus Hdcp) DecodeInput(string value) => value switch
    {
        "0" => (ConnectionState.Disconnected, HdcpStatus.Unknown),
        "1" => (ConnectionState.Connected, HdcpStatus.NotSupported),
        "2" => (ConnectionState.Connected, HdcpStatus.Active),
        _ => (ConnectionState.Unknown, HdcpStatus.Unknown)
    };

    public static (ConnectionState Connection, HdcpStatus Hdcp) DecodeOutput(string value) => value switch
    {
        "0" => (ConnectionState.Disconnected, HdcpStatus.Unknown),
        "1" => (ConnectionState.Connected, HdcpStatus.NotSupported),
        "2" => (ConnectionState.Connected, HdcpStatus.Available),
        "3" => (ConnectionState.Connected, HdcpStatus.Active),
        _ => (ConnectionState.Unknown, HdcpStatus.Unknown)
    };
}
