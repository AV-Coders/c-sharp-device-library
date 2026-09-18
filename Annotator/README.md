# AVCoders.Annotator

Annotation device drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Annotator
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Annotator). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ExtronAnnotator401`

The annotator's HDMI sync and HDCP status is reported separately by `ExtronAnnotator401VideoMatrix` in
[`AVCoders.Matrix`](../Matrix/README.md), which shares the same communication client so the ports can be seen
on a standard video matrix page.

## Usage

Drivers derive from `AnnotatorBase` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
