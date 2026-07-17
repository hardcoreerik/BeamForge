# Roadmap

This document is the authority for milestone status.

| Milestone | Status | Exit gate |
| --- | --- | --- |
| 1. Truth and simulator | Complete | Canonical docs, validated profiles, safety state machine, mixer, scenes, and tests |
| 2. Hardware verification | Blocked on evidence | Exact manual, label, SD samples, adapter identity, and blackout observation |
| 3. Live DMX | Blocked on milestone 2 | Documented transport and verified LM-002 profile |
| 4. Music mode | Implemented; capture smoke pending | Microphone and loopback drive only bounded, automatable controls |
| 5. Graphics | Implemented; fixture compatibility blocked | Previewed PNG/JPEG/BMP/GIF conversion and sample-compatible ILDA export |
| 6. Release | Prerelease packaged; hardware acceptance blocked | Passing build/tests and self-contained win-x64 package; hardware acceptance remains blocked |

## Deferred

Cross-platform support, multiple fixtures or universes, Art-Net/sACN, real-time ILDA/IDN, SVG/video import, timelines, cloud services, runtime AI, and a broad fixture library are deferred until one fixture works end to end.
