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

`v0.1-alpha` was tagged after a clean Release build as commit `61b0573`.

`v0.2` is now in progress. It keeps the same MVP workflow and adds:

- DeepL as the first real translation provider behind the existing provider abstraction.
- The existing offline Placeholder provider for deterministic testing.
- A simple DeepL API key field and Free/Pro endpoint toggle.
- Provider status in the debug area.
- Friendly provider errors for missing API key, rejected key, network failure, rate limit, quota exceeded, and empty/invalid translation response.
- Conservative OCR preprocessing for the selected region: modest upscaling for small text, grayscale conversion, contrast boost, and alpha compositing over white.

## Scope

- WPF desktop UI on .NET 10+
- User-selected screen region capture
- Windows OCR implementation behind an OCR abstraction
- Placeholder Arabic translation provider and DeepL provider behind a translation abstraction
- SQLite cache for successful translations and game profiles
- Transparent topmost overlay
- Global `F8` hotkey for capture and translate

## Manual Test Results

Latest stable MVP checkpoint artifact:

`artifacts/manual-test-20260626-115333/manual-test-result.json`

Latest v0.2 controlled manual workflow artifact:

`artifacts/v0.2-manual-test-20260626-123208/manual-test-result.json`

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

v0.2 controlled dialogue-source pass:

- Selected region was saved and reloaded as `X 95, Y 125, W 750, H 167`.
- `Capture & Translate` button worked on high-contrast English text.
- Rapid `F8` presses reused unchanged OCR/translation state without overlapping failures.
- Profile reload reused the saved region and loaded the repeated translation from SQLite cache.
- Overlay appeared above the dialogue target and updated with Arabic placeholder translations.
- DeepL missing-key flow showed a friendly UI message and did not crash.

v0.2 OCR observations:

- High contrast: `WHERE ARE WE?` -> exact.
- Small text: `HELLO.` -> exact after preprocessing.
- Stylized/game-like font: `GAME OVER` -> exact.

v0.2 observed timings:

- Initial high-contrast capture: capture `15 ms`, OCR `230 ms`, cache miss, translation `0 ms`, overlay `48 ms`.
- Rapid unchanged `F8`: capture `5-9 ms`, OCR skipped, translation skipped, overlay `2-3 ms`.
- Profile reload cache hit: capture `13 ms`, OCR `125 ms`, cache hit, overlay `51 ms`.
- Small text: capture `9 ms`, OCR `40 ms`, cache miss, overlay `20 ms`.
- Stylized text: capture `11 ms`, OCR `37 ms`, cache miss, overlay `17 ms`.
- DeepL missing-key path: capture `4 ms`, OCR `39 ms`, cache miss, provider error in `42 ms`.

## Safety Boundaries

This project does not inject code, hook game processes, modify game files, bypass anti-cheat systems, or inspect game memory. It only captures the user-selected rectangle of the screen.

## Known Limitations

- DeepL is wired in `v0.2`, but live translation requires a user-provided API key.
- Windows OCR quality depends heavily on contrast, font size, and selected region accuracy.
- There is no installer yet.
- There is no auto-capture mode yet.
- API-backed providers, advanced AI modes, and story/context translation are intentionally not included in this checkpoint.
- API keys and provider-specific settings are not persisted yet.
- The v0.2 OCR pass used controlled desktop dialogue-source windows, not a real game executable.
- Network, rate-limit, and quota provider paths are handled in code but were not live-tested because no DeepL API key was available.

## Build

Install the .NET 10 SDK or newer, then run:

```powershell
dotnet restore
dotnet build
```

The runtime database is stored under `%LOCALAPPDATA%\Cortex Transl\cortex-transl.db`.

For DeepL testing, set `CORTEX_TRANSL_DEEPL_API_KEY` before launch or paste the key into the app. Keep the Placeholder provider selected for offline testing.

## Next Milestone

Continue `v0.2` with live DeepL validation using a real key, more real game/video samples, and targeted OCR preprocessing tweaks only where the current selected-region workflow needs them.
