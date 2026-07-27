using AVCoders.Core;

namespace AVCoders.Conference;

public abstract class CiscoRoomOsVideoConnector : SyncStatus
{
    public readonly int ConnectorId;
    private int _resolutionWidth;
    private int _resolutionHeight;
    private int _resolutionRefreshRate;

    protected CiscoRoomOsVideoConnector(string name, int connectorId, AVEndpointType type) : base(name, type)
    {
        ConnectorId = connectorId;
    }

    internal void SetResolutionWidth(int width)
    {
        _resolutionWidth = width;
        UpdateResolution();
    }

    internal void SetResolutionHeight(int height)
    {
        _resolutionHeight = height;
        UpdateResolution();
    }

    internal void SetResolutionRefreshRate(int refreshRate)
    {
        _resolutionRefreshRate = refreshRate;
        UpdateResolution();
    }

    internal void SetConnectionState(ConnectionState state)
    {
        if (state == ConnectionState.Disconnected)
        {
            _resolutionWidth = 0;
            _resolutionHeight = 0;
            _resolutionRefreshRate = 0;
            SetResolution(string.Empty);
        }
        UpdateConnectionState(state);
    }

    private void UpdateResolution() =>
        SetResolution(_resolutionWidth == 0 || _resolutionHeight == 0
            ? string.Empty
            : $"{_resolutionWidth}x{_resolutionHeight}@{_resolutionRefreshRate}");

    protected abstract void SetResolution(string resolution);

    protected abstract void UpdateConnectionState(ConnectionState state);

    internal abstract void SetHdcpStatus(HdcpStatus status);
}

public class CiscoRoomOsVideoInput(string name, int connectorId)
    : CiscoRoomOsVideoConnector(name, connectorId, AVEndpointType.Encoder)
{
    protected override void SetResolution(string resolution) => InputResolution = resolution;

    protected override void UpdateConnectionState(ConnectionState state) => InputConnectionStatus = state;

    internal override void SetHdcpStatus(HdcpStatus status) => InputHdcpStatus = status;
}

public class CiscoRoomOsVideoOutput(string name, int connectorId)
    : CiscoRoomOsVideoConnector(name, connectorId, AVEndpointType.Decoder)
{
    protected override void SetResolution(string resolution) => OutputResolution = resolution;

    protected override void UpdateConnectionState(ConnectionState state) => OutputConnectionStatus = state;

    internal override void SetHdcpStatus(HdcpStatus status) => OutputHdcpStatus = status;
}
