# AV Coders Climate

Climate and HVAC drivers for the AV Coders device library.

## Devices

- Temperzone UC8 unit controller (`TemperzoneUc8`) over Modbus RTU: power, heat/cool/dry/fan-only modes, indoor fan speed, capacity, quiet/dry/economy modes, supply air targets, de-ice monitoring and control, lockout detection and reset, fault reporting via the issues API, and on-change history recording with configurable retention, disk persistence and CSV export (`TemperzoneUc8History`).
  - Shows the UC8 ID code and software version (registers 601 and 602) as details, read on each identity check after connecting
  - Shows the cooling, heating and de-ice run times, cycle counts, HP and LP trips, power-on resets and sensor fault counts (registers 1003-1032) as details, read every 100 polls

## Usage

Construct with a `ModbusClient` (see `AvCodersModbusRtuClient` in AVCoders.CommunicationClients) and optionally a history directory:

```csharp
var transport = new AvCodersSerialClient(comPort, TemperzoneUc8.DefaultSerialSpec, "UC8 RS485", CommandStringFormat.Hex);
var modbus = new AvCodersModbusRtuClient(transport, "UC8 Modbus");
var hvac = new TemperzoneUc8("Level 1 HVAC", modbus, historyDirectory: "/user/hvac-history");
```

The driver is monitoring-first: no control-enable bits are written to the unit until a control method is called, so a monitoring-only deployment never arms the UC8's 5-minute BMS watchdog.
