# ElApp.Watch.Wpf

Forecourt pump-monitoring capture station: a Windows desktop (WPF) client
that watches CCTV/webcam feeds per fuel pump, detects when a vehicle pulls
in and stops, captures a photo, attempts a number-plate read, and displays
a live "cameras online" dashboard. It is the `Station` capture app in
EagleLens's forecourt vehicle/plate verification design (see this
workspace's `ARCHITECTURE.md` §4 for the full product context).

This app runs standalone today — it has no network/API integration with
any EagleLens backend service yet.

## Solution layout

```
src/
  ElApp.Watch.Domain/   Pure pump stop-detection state machine (no OpenCV/ONNX/WPF dependency)
  ElApp.Watch.Vision/   OpenCvSharp/ONNX-backed motion analysis, vehicle detection, plate OCR
  ElApp.Watch.Wpf/      WPF host app: Views, ViewModels, Services, composition root
tests/
  ElApp.Watch.Domain.Tests/   xUnit characterization tests for the state machine
```

`ElApp.Watch.Domain` has no dependency on `ElApp.Watch.Vision` internals
beyond what it needs to be driven generically (it's generic over the frame
type); `ElApp.Watch.Vision` depends on `ElApp.Watch.Domain`;
`ElApp.Watch.Wpf` depends on `ElApp.Watch.Vision`.

## Build & run

```
dotnet build
dotnet test
dotnet run --project src/ElApp.Watch.Wpf
```

Requires the .NET 10 SDK and Windows (the app uses WPF and DirectShow-based
webcam capture).

## Configuration

`src/ElApp.Watch.Wpf/appsettings.json` holds the ONNX model file paths
(under `Vision`) and the snapshot output folder name (under `Snapshots`).
Captured photos are saved to a `Snapshots` folder next to the built
executable — this folder is git-ignored; it's runtime output, not source.

## Assets

`src/ElApp.Watch.Wpf/Assets/` contains the bundled ONNX models (vehicle
detection, plate detection, plate OCR — each under its own upstream
license, see `Assets/Models/LICENSE-*.txt` and `NOTICE.md`), sample CCTV
test videos, and static placeholder pump images used for filler tiles.
