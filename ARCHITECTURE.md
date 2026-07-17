# Architecture

BeamForge is one WPF executable plus one test project. It has no database, server, dependency-injection container, or runtime AI service.

## Control path

Microphone or WASAPI loopback audio is normalized and converted into RMS energy, spectral-flux beats, and low/mid/high band values. Automatic mappings, saved scenes, and live mixer changes all produce the same logical channel values. Every value passes through `SafetyController`, which applies arming state, the intensity ceiling, channel bounds, and verified-profile rules before a complete DMX universe reaches `IDmxOutput`.

The simulator records output frames for the UI and tests. A hardware implementation is deliberately absent until the USB-DMX protocol is identified.

## Artwork path

WPF decodes PNG, JPEG, BMP, and GIF frames. OpenCV converts frames to grayscale, thresholds line art or applies Canny edges to photographs, extracts contours, simplifies them, and orders them with a bounded nearest-neighbor pass. BeamForge adds blanking and dwell points, enforces a point budget, previews the actual path, and writes ILDA format 5.

SD export requires a user-approved preview, a removable destination, and a filename that does not already exist. Compatibility with the LM-002 remains provisional until original samples are parsed.

## Persistence

Fixture profiles and show projects are versioned JSON files. They are human-readable and validated at load time. Application settings live under `%LocalAppData%\BeamForge`; user projects remain where the user saves them.
