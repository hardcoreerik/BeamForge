# Project Truth

Last updated: 2026-07-16

## Verified locally

- BeamForge targets Windows x64 with .NET 10 WPF.
- The runtime is fully offline and uses NAudio 2.3.0, OpenCvSharp 4.13, and `System.Text.Json`.
- The repository contains no hardware driver; only the in-memory DMX simulator is enabled.
- Images and animations are exported as ILDA files. They are never transmitted over DMX.

## User-reported, not yet verified

- Target fixture: SALA Party Laser Light LM-002.
- The fixture accepts DMX through a generic USB-DMX cable.
- The fixture can play custom artwork from its original SD card.
- A third-party listing describes a 12-channel mode and ILDA playback.

## Explicitly untrusted

- The generic 18-channel white-label chart supplied during planning is discovery material only. It must not become a live fixture profile without device-specific evidence.

## Hardware gates

Live output remains unavailable until all of these are recorded:

1. Photos or scans of the LM-002 DMX pages and rear-panel mode display.
2. Laser class and per-color output powers from the product label or manual.
3. USB VID/PID, Windows driver, chipset, and a documented DMX transport protocol.
4. Original SD-card directory layout and sample ILDA headers.
5. Observed blackout behavior verified before any other channel.

## Runtime-observed results

- `dotnet test BeamForge.sln -c Release` passed all 9 tests with 0 failures on 2026-07-16.
- The Release WPF build launched on Windows, displayed the simulator-only interlock, connected in blackout, armed the simulator, and returned to blackout when `Escape` was pressed.
- No live laser, microphone/loopback capture, original SD-card, or USB-DMX test has been performed.
