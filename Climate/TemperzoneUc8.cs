using AVCoders.Core;

namespace Climate;

public enum TemperzoneUc8FanSpeed : int
{
    Off = 0, 
    Low = 100,
    Medium = 550,
    High = 1000,
}

public class TemperzoneUc8
{
    public const int DefaultDeviceId = 44;
    public FloatHandler? SupplyAirTemperatureHandler;
    public FloatHandler? ReturnAirTemperatureHandler;
    public IntHandler? IndoorFanSpeedHandler;
    public IntHandler? OutdoowFanSpeedHandler;
    private readonly ModbusRtuClient _client;

    public static readonly SerialSpec DefaultSerialSpec = new SerialSpec(SerialBaud.Rate19200, SerialParity.Even,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs485);
    
    private readonly ThreadWorker _keepAliveWorker;
    private int _pollcount;

    // Registers
    // Temperature Sensor Registers
    private const int OutdoorCoilTemperature = 1;
    private const int IndoorCoilTemperature = 2;
    private const int OutdoorAmbientTemperature = 3;
    private const int SuctionLineTemperature = 4;
    private const int DischargeLineTemperature = 5;
    private const int DeIceSensorTemperature = 6;
    private const int EvaporatingTemperature = 7;
    private const int CondensingTemperature = 8;
    private const int ControllerTemperature = 9;
    private const int SuctionSideSuperheat = 10;
    private const int DischargeSideSuperhead = 11;
    private const int SensorUnavailable = -10000;

    // Control registers
    private const int ControlEnable = 101; // To allow this module to control the device.
    private const int CompressorPower = 102;
    private const int HeatingOrCooling = 103; // 0 is cool, 1 is heat / reverse valve state
    private const int RemoteOnOff = 104;
    private const int IndoorFanMode = 105;
    private const int IndoorFanSpeed = 108; // Look at chapter 9
    private const int OutdoorCoilDeIce = 110;
    private const int QuiteMode = 111;
    private const int DryMode = 112;
    private const int Economy = 113;
    private const int CoolingSupplyAirTempTarget = 118; // Look at chapter 11
    private const int HeatingSupplyAirTempTarget = 119; // Look at Chapter 11
    
    // Feedback Registers
    private const int OutdoorFanSpeedFb = 401;
    private const int IndoorFanSpeedFb = 402;
    private const int UnitModeFb = 407;
    private const int SupplyAirTemp = 1205;
    private const int ReturnAirTemp = 1206;

    private const int FaultBank1 = 901;
    private const int FaultBank2 = 902;
    private const int FaultBank3 = 903;

    public TemperzoneUc8(ModbusRtuClient client)
    {
        _keepAliveWorker = new ThreadWorker(KeepAlive, TimeSpan.FromSeconds(10));
        _keepAliveWorker.Restart();
        _client = client;
    }

    private Task KeepAlive(CancellationToken arg)
    {
        if (_pollcount > 50)
        {
            _client.WriteRegister(1401, 8821);
            _pollcount = 0;
        }
        _client.ReadRegisters([IndoorCoilTemperature, OutdoorAmbientTemperature, IndoorFanSpeedFb, UnitModeFb, SupplyAirTemp, ReturnAirTemp ]);

        _pollcount++;
        return Task.CompletedTask;
    }
}