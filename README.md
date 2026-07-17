# BeamForge

BeamForge is an offline Windows application for safely composing music-reactive DMX scenes and converting simple raster artwork into ILDA frames for compatible SD-card laser projectors.

The current hardware target is the SALA LM-002. Its fixture profile is intentionally provisional: live output is blocked until the exact DMX chart, blackout values, laser classification, USB-DMX protocol, and original SD samples are verified.

## Current capabilities

- Simulator-first DMX mixer with explicit arm and blackout controls
- JSON fixture profiles, scenes, and show projects
- Microphone or Windows system-audio analysis using beat, energy, and frequency bands
- PNG, JPEG, BMP, and GIF tracing with an ordered-path preview
- ILDA format 5 read/write and guarded SD-card export

## Build

```powershell
dotnet build BeamForge.sln -c Release
dotnet test BeamForge.sln -c Release --no-build
```

Create a self-contained Windows package with:

```powershell
dotnet publish src\BeamForge.App\BeamForge.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\BeamForge-win-x64
```

## OpenWiki

OpenWiki 0.2.0 is pinned as a development dependency. Install it and complete the interactive ChatGPT login with:

```powershell
npm install
npm run docs:init
```

The npm scripts select the ChatGPT provider and disable OpenWiki telemetry. Generated content must follow `openwiki/INSTRUCTIONS.md` and may edit `AGENTS.md` only inside its marked block.

BeamForge is not safety-rated equipment. Read [SAFETY.md](SAFETY.md) before connecting any laser.
