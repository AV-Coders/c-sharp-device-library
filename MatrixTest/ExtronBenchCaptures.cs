namespace AVCoders.Matrix.Tests;

internal static class ExtronBenchCaptures
{
    public static readonly string[] Sw2ConnectSent = ["\u001b3CV\r", "1I", "\u001bLS\r", "\u001bIHDCP\r", "\u001bOHDCP\r", "!"];

    public static readonly string[] Sw2ConnectReplies =
    [
        "(c) Copyright 2024, Extron Electronics, SW2 HD 4K PLUS, V1.03, 60-1603-01",
        "Sun, 01 Nov 2026 10:50:35",
        "Vrb3",
        "Inf01*SW2 HD 4K PLUS",
        "Sig\"0 1*0\"",
        "HdcpI0 2 ",
        "HdcpO0",
        "In1 All"
    ];

    public static readonly string[] Sw2IdentitySent = ["Q", "N", "\u001bCN\r", "\u001bCH\r"];

    public static readonly string[] Sw2IdentityReplies = ["Ver01*1.03", "Pno60-1603-01", "Ipn ROOM-VID-SWI01", "Iph 00-05-A6-00-00-01"];

    public static readonly string[] Sw2PollReplies = ["Sig\"0 1*0\"", "HdcpI0 2 ", "HdcpO0", "In1 All"];

    public static readonly string[] In1808ConnectReplies =
    [
        "(c) Copyright 2024, Extron Electronics, IN1808, V1.09, 60-1615-01",
        "Sun, 01 Nov 2026 10:50:29",
        "Vrb3",
        "Inf01*IN1808"
    ];

    public static readonly string[] In1808IdentityReplies = ["Bld1.09.0006", "Pno60-1615-01", "Ipn ROOM-VID-SWI01", "Iph 00-05-A6-00-00-02"];

    public static readonly string[] In1808StatusReplies =
    [
        "In00 0*1*0*1*0*0*0*0",
        "HdcpI1*0", "HdcpI2*1", "HdcpI3*0", "HdcpI4*2", "HdcpI5*0", "HdcpI6*0", "HdcpI7*0", "HdcpI8*0",
        "HdcpO1*0", "HdcpO2*0"
    ];

    public const string In1808TemperatureReply = "20Stat 46";

    public const string Dtp2Cp82Banner =
        "\r\n(c) Copyright 2020, Extron Electronics, DTP2 CrossPoint 82, V1.04, 60-1812-01\r\nSun, 01 Nov 2026 04:11:07\r\n";

    public static readonly string[] Dtp2Cp82ConnectReplies = ["Vrb3\r\n", "Inf00*DTP2 CrossPoint 82\r\n"];

    public static readonly string[] Dtp2Cp82IdentityReplies =
        ["Bld1.04.0004\r\n", "Pno60-1812-01\r\n", "Ipn ROOM-VP01\r\n", "Iph 00-05-A6-00-00-03\r\n"];

    public static readonly string[] Dtp2Cp82StatusReplies =
    [
        "VnamI1*--\r\nHdcpI1*0\r\nVnamI2*Codec\r\n",
        "HdcpI2*0\r\n",
        "VnamI3*Blu-Ray Player\r\n",
        "HdcpI3*0\r\n",
        "VnamI4*Wireless Presenter\r\n",
        "HdcpI4*1\r\n",
        "VnamI5*Doc Cam\r\n",
        "HdcpI5*0\r\n",
        "VnamI6*Members PC\r\n",
        "HdcpI6*0\r\n",
        "VnamI7*Bar HDMI\r\n",
        "HdcpI7*0\r\n",
        "VnamI8*Video Input 8\r\n",
        "HdcpI8*0\r\n",
        "VnamO1*Codec Content\r\n",
        "HdcpO1*1\r\n",
        "VnamO2*Bench Output\r\n",
        "HdcpO2*2\r\n",
        "VnamO3*--\r\n",
        "HdcpO3*0\r\n",
        "In1*1 Vid\r\n",
        "In1*1 Aud\r\n",
        "In6*2 Vid\r\n",
        "In1*2 Aud\r\n",
        "Lout1\r\n",
        "In00 0*0*0*1*0*0*0*0\r\n"
    ];

    public const string Dtp2Cp82TemperatureReply = "20Stat 34\r\n";

    public const string Dtp2Cp82SignalPresenceReply = "In00 0*0*0*1*0*0*0*0\r\n";

    public const string DtpCp108Banner = "(c) Copyright 2025, Extron Electronics, DTPCP108, V2.04, 60-1381-01";

    public static readonly string[] DtpCp108ConnectReplies = [DtpCp108Banner, "Sun, 01 Nov 2026 11:50:29", "Vrb3", "Inf00*DTPCP108"];

    public static readonly string[] DtpCp108PortReplies =
    [
        "Nmi1,Codec Output 1", "HdcpI01*0",
        "Nmi2,Codec Output 2", "HdcpI02*0",
        "Nmi3,Bench Laptop", "HdcpI03*0",
        "Nmi4,Judicial Laptop", "HdcpI04*0",
        "Nmi5,Doc Cam", "HdcpI05*0",
        "Nmi6,Blu-Ray Player", "HdcpI06*1",
        "Nmi7,PC", "HdcpI07*0",
        "Nmi8,Bar Left", "HdcpI08*0",
        "Nmi9,Bar Right", "HdcpI09*0",
        "Nmi10,Witness Laptop", "HdcpI10*0",
        "Nmo1,Output 1", "HdcpO1*0", "E13", "E13",
        "Nmo2,Bench Monitor", "HdcpO2*1", "E13", "E13",
        "Nmo3,Output 3", "HdcpO3*0", "E13", "E13",
        "Nmo4,Bar Monitors", "HdcpO4*0", "E13", "E13",
        "Nmo5,HD Strip", "E13", "HdcpO5A*0", "HdcpO5B*0",
        "Nmo6,AFM Monitor", "E13", "HdcpO6A*0", "HdcpO6B*0",
        "Nmo7,Left LCD", "HdcpO7*0", "E13", "E13",
        "Nmo8,Right LCD", "HdcpO8*0", "E13", "E13"
    ];

    public static readonly string[] DtpCp108TieReplies =
    [
        "Out1 In0 Vid", "Out2 In0 Vid", "Out3 In0 Vid", "Out4 In0 Vid",
        "Out5 In0 Vid", "Out6 In0 Vid", "Out7 In1 Vid", "Out8 In2 Vid"
    ];

    public const string DtpCp108PollReply = "Pti0*00030";
}
