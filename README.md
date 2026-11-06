# SRdeckPlay

SRdeckPlay is a standalone Windows x64 application for playing narrowband IQ WAV recordings created with SRdeck. It reads recording metadata, displays signal level and frequency history, and replays supported AM, FM, USB, and LSB signals.

The SRdeck application and SDR hardware are not required. Install the .NET 10 Desktop Runtime x64 to run a framework-dependent build.

## Build

From the repository root, run `build.bat` or:

```powershell
dotnet build SRdeckPlay.sln -c Release -p:Platform=x64
```

See [the Japanese guide](SRdeckPlay/README.ja.md) for input format, playback controls, and analysis details.
