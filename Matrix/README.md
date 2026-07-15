# AVCoders.Matrix

Matrix switcher and AV-over-IP drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Matrix
```

Published to GitHub Packages — add the `https://nuget.pkg.github.com/AV-Coders/index.json` source first. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ExtronIn16Xx`, `ExtronIn18Xx`, `ExtronSw`, `ExtronDtpCpxx`
- `SvsiEncoder`, `SvsiDecoder`
- `BlustreamAmf41W`
- `Navigator`, `NavEncoder`, `NavDecoder`
- `AVoIPEndpoint`

## Usage

Drivers derive from `VideoMatrix` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
