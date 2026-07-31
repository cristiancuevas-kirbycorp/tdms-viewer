# TDMS Viewer

A modern Windows desktop app to open, defragment, graph, and analyze National Instruments **TDMS** files — without LabVIEW or DIAdem. Built with **C# / WPF** and **[ScottPlot](https://scottplot.net/)**.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6)

## Features

- **Open & auto-defragment** TDMS files in place (only when fragmented), so large logs load fast.
- **Data Portal** (DIAdem-style): browse groups/channels with units, a live properties table, and a preview graph.
  - Multi-word filter that searches **group + channel** names together (e.g. `AFE voltage`), with match highlighting, plus Expand/Collapse all.
  - Tick, double-click, or **drag** a channel onto the graph.
- **Multi-axis plotting** with first-class, named **Y-scales** — assign plots to scales, set Left/Right side, Auto or fixed min/max.
- **Reports & Pages** organization, savable/loadable as a `.tvproj` project file.
- **Calculated channels** from math expressions (e.g. `[A1 BMS/Bus current] * 2 + 5`) via a channel-picker builder.
- **Time-focused interaction**: scroll and left-drag zoom/pan the time (X) axis only; box zoom; per-axis Y zoom.
- **Cursors**: two draggable time cursors with a shaded band and a readout of each plot's value at **A/B**, the **delta**, and **Min/Max/Avg between the cursors**.
- **Per-series styling**: color, line width, line style, and markers.
- **Recent Files** history, **Print/Save** the graph to PNG.

## Requirements

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build (not needed to run the self-contained exe)

## Build & run

```powershell
git clone https://github.com/cristiancuevas-kirbycorp/tdms-viewer.git
cd tdms-viewer
dotnet run --project TdmsViewer/TdmsViewer.csproj
```

Or open the folder in VS Code and press **F5** (a launch config and build task are included).

## Publish a standalone executable

Produces a single self-contained `.exe` that runs on any 64-bit Windows PC with no .NET install:

```powershell
dotnet publish TdmsViewer/TdmsViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `TdmsViewer/bin/Release/net8.0-windows/win-x64/publish/TdmsViewer.exe`

## Tech stack

| Concern | Library |
|---|---|
| UI | WPF (.NET 8), MVVM (CommunityToolkit.Mvvm) |
| TDMS read | [TDMSReader](https://github.com/mikeobrien/TDMSReader) (MIT) |
| Plotting | [ScottPlot](https://scottplot.net/) (MIT) |
| Formulas | [Jace.NET](https://github.com/pieterderycke/Jace) (MIT) |

## License

[MIT](LICENSE)
