namespace AVCoders.Dsp.Tests;

public static class TesiraReplies
{
    public const string Version = "+OK \"value\":\"5.2.0.8\"";

    public const string SerialNumber = "+OK \"value\":\"04351854\"";

    public const string DeviceInfo = """+OK "info":{"deviceModel":"TesiraFORTÉ AVB VT" "deviceRevision":"Rev. A" "serialNumber":"04351854" "firmwareVersion":"5.2.0.8" "ipAddress":"10.1.3.105"}""";

    public const string NetworkStatus = """+OK "value":{"schemaVersion":2 "hostname":"TesiraForte04351854" "defaultGatewayStatus":"0.0.0.0" "networkInterfaceStatusWithName":[{"interfaceId":"control" "networkInterfaceStatus":{"macAddress":"78:45:01:0a:ee:9f" "linkStatus":LINK_1_GB "addressSource":DHCP "ip":"10.1.3.105" "netmask":"255.255.252.0" "dhcpLeaseObtainedDate":"Mon Oct 12 15:36:17 AUS Eastern Standard Time 2026" "dhcpLeaseExpiresDate":"Tue Oct 13 15:36:17 AUS Eastern Standard Time 2026" "gateway":"10.1.0.1"}} {"interfaceId":"media_avb_0" "networkInterfaceStatus":{"macAddress":"78:45:01:0a:ee:a0" "linkStatus":LINK_DOWN "addressSource":DHCP "ip":"169.254.7.24" "netmask":"255.255.0.0" "dhcpLeaseObtainedDate":"Unknown" "dhcpLeaseExpiresDate":"Unknown" "gateway":"0.0.0.0"}}] "dnsStatus":{"primaryDNSServer":"10.1.0.1" "secondaryDNSServer":"" "domainName":"millpark.acarbone.com"} "mDNSEnabled":true "telnetDisabled":false "sshDisabled":false "networkPortMode":PORT_MODE_SEPARATE "rstpEnabled":false "httpsEnabled":false "igmpEnabled":false "switchPortMode":SWITCH_PORT_MODE_CONTROL_AND_MEDIA "volteraSwitchPortMode":SWITCH_PORT_MODE_VLT_CONTROL_AND_MEDIA}""";

    public const string NoFaults = """+OK "value":[{"id":INDICATOR_NONE_IN_DEVICE "name":"No fault in device" "faults":[] "serialNumber":"04351854"}]""";

    public const string DiscoveredServers = """+OK "value":[{"ip":"10.1.3.105" "hostname":"TesiraForte04351854" "sleeping":false "deviceCategory":AUDIO_SERVER "deviceType":11}]""";

    public const string Aliases = """+OK "list":["Aec1" "AecInput1" "AecRef1" "DEVICE" "InputLevels" "Mixer1" "Mixer2" "Output1"]""";

    public const string UnsupportedDeviceAttribute = "-ERR 'serialNumbr' is not supported by Device::TextAttributes";

    public const string UnsupportedSessionAttribute = "-ERR 'detailedAliases' is not supported by TextSession::Attributes";

    public const string AttributeNotSupported = "-ERR ATTRIBUTE_NOT_SUPPORTED";
}
