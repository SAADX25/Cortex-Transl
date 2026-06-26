# Cortex Transl

Cortex Transl is a Windows desktop MVP for translating dialogue from offline, single-player story games using screen capture only.

## Current Status

`v0.1-alpha` is the stable MVP checkpoint. It focuses on the manual capture workflow only:

- Select a dialogue rectangle on screen.
- Capture only that selected region.
- Run Windows OCR.
- Translate to Arabic through the offline placeholder provider.
- Display original and translated text in the app.
- Show translated text in a transparent topmost overlay.
- Trigger capture with the `Capture & Translate` button or global `F8`.
- Cache successful translations in SQLite.
- Save and reload basic game profiles.
- Show debug indicators for capture time, OCR time, cache hit/miss/skipped, translation time, and overlay update time.

## Scope

- WPF desktop UI on .NET 10+
- User-selected screen region capture
- Windows OCR implementation behind an OCR abstraction
- Placeholder Arabic translation provider behind a translation abstraction
- SQLite cache for successful translations and game profiles
- Transparent topmost overlay
- Global `F8` hotkey for capture and translate

## Manual Test Results

Latest stable MVP test artifact:

`artifacts/manual-test-20260626-115333/manual-test-result.json`

Validated workflow:

- No selected region shows a clear warning.
- Region selection worked on a multi-monitor setup.
- Saved profile reused coordinates `X 90, Y 410, W 750, H 105`.
- `Capture & Translate` detected high-contrast dialogue text.
- `F8` captured repeatedly without visible overlap crashes.
- Overlay appeared above the dialogue target and updated.
- Repeated unchanged capture skipped OCR and translation.
- Repeated text after changing away and back loaded from SQLite cache.
- Invalid off-screen capture is handled with a friendly message.

Observed OCR results:

- High-contrast text: `WHERE ARE WE?` -> accurate.
- High-contrast text: `HELLO` -> accurate.

Observed timings on the controlled test target:

- Initial capture path: capture `14 ms`, OCR `94 ms`, cache miss, translation `0 ms`, overlay `46 ms`.
- Cache hit path: capture `6 ms`, OCR `10 ms`, cache hit, overlay `11 ms`.

## Safety Boundaries

This project does not inject code, hook game processes, modify game files, bypass anti-cheat systems, or inspect game memory. It only captures the user-selected rectangle of the screen.

## Known Limitations

- The only translation provider in `v0.1-alpha` is the placeholder provider.
- Windows OCR quality depends heavily on contrast, font size, and selected region accuracy.
- There is no installer yet.
- There is no auto-capture mode yet.
- API-backed providers, advanced AI modes, and story/context translation are intentionally not included in this checkpoint.
- API keys and provider-specific settings are not persisted yet.

## Build

Install the .NET 10 SDK or newer, then run:

```powershell
dotnet restore
dotnet build
```

The runtime database is stored under `%LOCALAPPDATA%\Cortex Transl\cortex-transl.db`.

## Next Milestone

`v0.2` will focus on real-world testing, one real translation provider behind the existing abstraction, OCR preprocessing improvements, and preserving the stable manual capture -> OCR -> cache -> overlay workflow.
