using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// Decodes the status values Extron's SIS <c>HdcpI</c> / <c>HdcpO</c> responses carry. The meaning is the
/// same across the SIS devices in this package, so the drivers share these tables.
/// </summary>
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
