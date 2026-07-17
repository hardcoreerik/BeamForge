# Laser Safety

BeamForge is not a certified safety system. Software blackout cannot replace a physical shutter, interlock, emergency stop, or accessible power cutoff.

## Non-negotiable rules

- Use only a private indoor, terminated projection surface.
- Never scan people, animals, windows, mirrors, reflective objects, roads, vehicles, or aircraft.
- Keep an accessible switched power cutoff within reach.
- Do not connect live output until the laser class, output powers, DMX mode, and blackout values are verified.
- Begin every session disconnected, disarmed, and blacked out.
- Treat software intensity limits as convenience controls, not exposure controls.

## First hardware smoke test

1. Secure the projector and terminate its entire possible scan area.
2. Confirm the physical power cutoff works without BeamForge.
3. Connect BeamForge while output remains disarmed.
4. Verify the documented blackout value first.
5. Test one channel and one narrow value range at a time.
6. Record every observed value and revert to blackout between tests.
7. Stop immediately if the device response differs from the manual.

Application shutdown, silence, audio failure, and device disconnect trigger best-effort blackout, but a crash or USB fault can leave a fixture holding its last DMX state.
