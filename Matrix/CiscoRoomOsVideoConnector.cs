using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// A video connector on a Cisco codec. Connection state, resolution, HDCP and name changes are
/// recorded in the connector's <see cref="LogBase.Events"/> history.
/// </summary>
public abstract class CiscoRoomOsVideoConnector : SyncStatus
{
    public readonly int ConnectorId;
    private readonly string _defaultName;
    private int _resolutionWidth;
    private int _resolutionHeight;
    private int _resolutionRefreshRate;

    protected CiscoRoomOsVideoConnector(string name, int connectorId, AVEndpointType type) : base(name, type)
    {
        ConnectorId = connectorId;
        _defaultName = name;
    }

    /// <summary>Applies the name configured on the codec; an empty name reverts to the default.</summary>
    internal void SetName(string name)
    {
        var newName = name.Length == 0 ? _defaultName : name;
        if (newName == Name)
            return;
        Name = newName;
        AddEvent(EventType.Other, $"Name Changed to {newName}");
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
        if (state is ConnectionState.Disconnected or ConnectionState.Unknown)
        {
            _resolutionWidth = 0;
            _resolutionHeight = 0;
            _resolutionRefreshRate = 0;
            SetResolution(string.Empty);
            OnSignalLost();
        }
        UpdateConnectionState(state);
    }

    /// <summary>Forgets everything learnt from the codec, used when the connection to it is lost.</summary>
    internal void Reset()
    {
        SetConnectionState(ConnectionState.Unknown);
        SetHdcpStatus(HdcpStatus.Unknown);
    }

    private void UpdateResolution() =>
        SetResolution(_resolutionWidth == 0 || _resolutionHeight == 0
            ? string.Empty
            : $"{_resolutionWidth}x{_resolutionHeight}@{_resolutionRefreshRate}");

    protected virtual void OnSignalLost() { }

    protected abstract void SetResolution(string resolution);

    protected abstract void UpdateConnectionState(ConnectionState state);

    internal abstract void SetHdcpStatus(HdcpStatus status);
}

/// <summary>
/// A codec video input. Its <see cref="LogBase.Name"/> follows the connector name configured on the
/// codec (xConfiguration Video Input Connector n Name).
/// </summary>
public class CiscoRoomOsVideoInput(string name, int connectorId)
    : CiscoRoomOsVideoConnector(name, connectorId, AVEndpointType.Encoder)
{
    protected override void SetResolution(string resolution) => InputResolution = resolution;

    protected override void UpdateConnectionState(ConnectionState state) => InputConnectionStatus = state;

    internal override void SetHdcpStatus(HdcpStatus status) => InputHdcpStatus = status;
}

/// <summary>
/// A codec video output. The codec has no configurable output name, so <see cref="LogBase.Name"/> stays
/// "Output n". The display name the codec reads from the sink's EDID
/// (xStatus Video Output Connector n ConnectedDevice Name) is exposed as <see cref="ConnectedDeviceName"/>,
/// recorded in the event history when it changes and cleared when the display disconnects.
/// </summary>
public class CiscoRoomOsVideoOutput(string name, int connectorId)
    : CiscoRoomOsVideoConnector(name, connectorId, AVEndpointType.Decoder)
{
    public string ConnectedDeviceName { get; private set; } = string.Empty;
    public StringHandler? ConnectedDeviceNameChangedHandlers;

    internal void SetConnectedDeviceName(string deviceName)
    {
        if (deviceName == ConnectedDeviceName)
            return;
        ConnectedDeviceName = deviceName;
        AddEvent(EventType.Connection, deviceName.Length == 0
            ? "Connected Device Removed"
            : $"Connected Device Changed to {deviceName}");
        ConnectedDeviceNameChangedHandlers?.Invoke(deviceName);
    }

    protected override void OnSignalLost() => SetConnectedDeviceName(string.Empty);

    protected override void SetResolution(string resolution) => OutputResolution = resolution;

    protected override void UpdateConnectionState(ConnectionState state) => OutputConnectionStatus = state;

    internal override void SetHdcpStatus(HdcpStatus status) => OutputHdcpStatus = status;
}
