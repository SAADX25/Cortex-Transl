# Story dialogue reliability upgrade

Implemented and verified locally on Windows on 2026-10-13. The application remains C# / .NET 10 / WPF, uses window capture rather than game injection, and retains Bergamot and DeepL routing.

## Behavior and configuration

- F9 binds the chosen region to its source window. Story capture stays on that HWND and process; it does not enumerate a replacement window every frame. A minimized, closed, cloaked, moved-out-of-region, or unavailable source clears the overlay. Story mode never falls back to desktop pixels. After moving/restarting a game, select its dialogue again with F9.
- Independent overlay windows are excluded by window capture. The Cortex overlay also requests `WDA_EXCLUDEFROMCAPTURE`. Desktop icon translation remains available through direct capture of an explicitly selected shell window in menu mode. No failed window capture falls back to desktop screen pixels, which could expose a newly opened private window.
- A serialized capture/OCR stage feeds one translation worker through a bounded channel with one pending item. New dialogue cancels the preceding request and replaces obsolete pending work. Work contains a sequence ID, immutable story-context snapshot, cache namespace, and cancellation token. Sequence/token checks precede publication, and WPF checks the sequence again before displaying a result.
- A source change clears the preceding Arabic subtitle while the new line settles or translates. Blank OCR gets a short grace period; OCR exceptions get a separate bounded grace period. Blank/failed OCR never becomes a successful frame fingerprint. Exact successful frames still get periodic OCR revalidation.
- Short complete lines, including `Run!`, `No!`, and `Help!`, are eligible immediately, irrespective of the preceding line's length. Longer complete lines confirm briefly. Unpunctuated/typewriter text waits for a quiet period. Exact equality is required to call dialogue unchanged; edits to negation, pronouns, words, or punctuation are not discarded as one-character noise.
- Original pixels are the first dialogue OCR pass. Empty recognition can trigger a reduced-size pass and then adaptive grayscale/polarity/enlargement with a crop only when the border is demonstrably uniform. At most three OCR passes run for a frame. Recovery is throttled on repeated failed images, while changed images can recover immediately. Crop offsets and scale are mapped back to source coordinates. Menu preprocessing retains its existing small-text first pass.

Save a named game preset, then open **Presets → Story context and subtitle timing**. Optional fields include the known current speaker, scene facts, game terms, Arabic character-name spellings, and an existing DeepL API glossary ID. Use one `English = Arabic` entry per line. Leave unknown speaker/scene facts blank; the application does not infer or manufacture identities.

| Setting | Default | Scope |
| --- | ---: | --- |
| Completed-text confirmation | 80 ms | Longer complete lines; short complete lines remain immediate |
| Unpunctuated text stability | 350 ms | Quiet period across at least two observations |
| Disappearance grace | 650 ms | Blank OCR |
| OCR exception grace | 1,500 ms | Temporary recognition failures |
| Maximum visibility | 15,000 ms | Prevents an unchanged line/frame from persisting indefinitely |
| Scene-history reset gap | 4,000 ms | Clears observed history after a text-free interval |
| Capture-loop delay | 100 ms | Delay between detection turns, in addition to capture/OCR work |
| Successful-frame OCR recheck | 400 ms | Hard-coded bound on fingerprint reuse |

Visibility maximum, disappearance grace, and unpunctuated stability are editable in the preset panel. All story settings persist in `appsettings.json` and each preset's `StorySettingsJson`. Additional timing values can be configured in that serialized `Story` object. Values used by the tracker/loop are clamped to supported ranges.

## Story context, Arabic terminology, and offline compatibility

`StoryTranslationContext` includes at most six recent eligible observed dialogue lines, optional explicit speaker and scene facts, terminology, speaker-name spellings, and game identity. Its DeepL representation bounds history/fact lengths and sends only the current utterance as translation text. A retry reuses the same context snapshot rather than adding its own utterance as preceding context.

DeepL receives the documented `context`, `split_sentences`, and `preserve_formatting` parameters. Existing API glossaries can use `glossary_id`, which requires an explicit matching source language. Arabic does not support DeepL's `custom_instructions` parameter; no unsupported Arabic instruction/formality parameters are sent. Natural MSA guidance in context is advisory, not a quality guarantee. See [DeepL's translation API reference](https://developers.deepl.com/api-reference/translate/request-translation).

For explicit English-to-Arabic local terms/names inside a sentence, XML `ignore_tags` protects the player's Arabic spelling. Source XML is escaped, terms match whole words/phrases, longer terms win, and returned markup is stripped. A missing or changed protected term rejects the response rather than displaying an inconsistent spelling. Full-line exact glossary matches work without a network request. Auto-detected/non-English source languages receive terminology as context but do not use this local English-to-Arabic protection.

Bergamot receives only the current utterance, split with the existing chunker when necessary. It does not receive history as text to translate. Exact full-line glossary/name overrides and exact menu-label overrides work offline; embedded-term constraints and contextual gender/relationship disambiguation are not available in the native engine. Runtime configuration files set two CPU threads and reference existing models with absolute paths; bundled model/configuration files are not rewritten or copied. An in-progress native translation cannot be interrupted mid-call; its obsolete result is discarded afterward.

## Cache and migration safety

Cache provider namespaces include translation behavior version (`story-v3`), engine/provider, content kind, game identity, and a SHA-256 context scope. The scope includes speaker/scene facts, recent history, sorted terminology/name entries, and glossary ID. Capture identity uses a hash of the selected executable's full path, not just its filename. If that path is inaccessible, capture uses an isolated session identity. Games sharing an executable, such as emulator titles, should use distinct named presets.

The migration only adds `GameProfiles.StorySettingsJson` with a default of `{}`; existing profile IDs, names, regions, preferences, and cache rows remain intact. It is idempotent. Legacy cache entries stay in the database, but changed dialogue behavior uses the new namespace rather than reusing incompatible entries. There is no destructive cache purge or database replacement.

## Automated validation

Run on Windows with .NET 10:

```powershell
dotnet restore CortexTransl.sln
dotnet build CortexTransl.sln -c Release --no-restore
dotnet test CortexTransl.sln -c Release --no-build --logger "trx;LogFileName=reliability.trx" --results-directory TestResults
```

The local Release build passed with **zero warnings and zero errors**. **57 tests passed, zero failed, zero skipped.** The Windows CI workflow restores, builds, tests, and uploads TRX results. The workflow is added; a hosted GitHub run has not been triggered or verified here.

Coverage includes:

- Utility/tracker and full-pipeline short-after-long cases; meaning-changing single edits; progressive text; complete-text confirmation; blank/failed OCR grace; maximum lifetime and reappearance.
- Capture continuing during a blocked translator; bounded pending work; cooperative cancellation; cancellation-ignoring obsolete responses; reset/new-scene invalidation even for identical source text; caller cancellation/recovery; selected-window loss and late-response rejection.
- Dynamic DeepL JSON payloads, cancellation, empty/mismatched responses, HTTP 403/429/456/503 failures and recovery, protected embedded terminology/name spellings, and altered-term rejection. These HTTP tests use a local synthetic handler, not a live DeepL account.
- Offline failure/recovery through a test provider; real bundled Bergamot inference; offline glossary and menu-label behavior with network access forbidden; cancellation before inference.
- Original-schema SQLite migration with pre-existing profile and cache records, repeated initialization, profile/story-settings round-trip, preservation of old rows, and separation of new cache namespaces.
- Exact full-frame fingerprints and one-pixel changes; adaptive polarity; recovery crop offsets/busy-border protection; actual Windows OCR over 18 rendered samples (`Run!`, `No!`, `Help!` × Segoe UI/Arial/Georgia × dark/light backgrounds), including safe coordinate remapping. OCR capitalization differences are accepted; missing words or punctuation are not.

Native Bergamot tests automatically discover `dist/win-x64/models`, or use `CORTEX_TRANSL_TEST_MODELS` pointing to a models directory containing `en-ar`. They are explicitly skipped on machines without a model. Real Windows OCR tests are explicitly skipped when the English Windows OCR feature is unavailable. Neither test downloads a model or sends dialogue to a cloud service.

Local results are in `TestResults/reliability.trx`; output includes measured timings and the native translation sample corpus. Native samples included `No! → لا!`, `Don't go! → لا تذهب!`, and `I can't help you. → لا أستطيع مساعدتك`. The native test checks nonempty Arabic/script output and two negation markers. This is a smoke corpus, not a human translation-quality evaluation or accuracy score.

## Measured performance

Measurements are from the local Release test run (.NET runtime 10.0.11, SDK 10.0.400, Windows x64). Tests execute concurrently, so timings include test-run load and are indicative rather than controlled benchmarks. Update the values from TRX when remeasuring on another machine.

| Measurement | Observed |
| --- | ---: |
| Synthetic detection while network is stalled | 0.500 ms mean; 1.039 ms maximum over three changing short lines |
| Exact 1280 × 180 frame fingerprint | 3.000 ms/frame over 100 iterations; 412 managed bytes/frame |
| Default original-pixel preprocessing, 1280 × 180 | 5.792 ms/frame over 30 iterations |
| Adaptive preprocessing, 1280 × 180 | 18.521 ms/frame over 20 iterations |
| Native Bergamot cold first utterance | 408.15 ms including model/service initialization |
| Six subsequent native utterances | 9.94–12.98 ms each |
| Outstanding translation bounds | One active worker and one pending job; queued jobs contain OCR/text/context, not bitmap frames |

Synthetic detector timing excludes real Windows capture/OCR and network latency. Preprocessing/fingerprint timings exclude translation. Bergamot timings exclude capture, OCR, stabilization, and presentation. Total application CPU percentage, GPU utilization, resident memory, frame rate impact, and live DeepL latency have **not** been measured in a real game.

## Remaining limits and real-game Windows verification

1. Validate Windows Graphics Capture on actual borderless/windowed games, fullscreen transitions, minimized/closed/restarted windows, alt-tab, resolution/DPI changes, multiple monitors, and GPU driver combinations. Confirm no desktop OCR/cloud request occurs after losing the bound game. Exclusive fullscreen/protected capture can remain unsupported; F9 reselection may be necessary.
2. Validate the overlay, WDA exclusion, region selection, F8/F9/F10, presets, and menu placement in the running WPF application. Native OCR vertical rectangles can be loose or clip glyphs; menu alignment needs visual verification. Dark/light rendered fonts are not representative of every outlined, animated, transparent, or textured subtitle background.
3. Evaluate a permissioned story-dialogue corpus with fluent Arabic reviewers. Check negation, emotional tone, pronouns/gender, character relationships/names, concise subtitle wording, and the protected XML path against a live DeepL account. No live DeepL quality or terminology-conformance claim is made here.
4. A typewriter pause longer than the configured quiet period can appear stable before the utterance is finished. Confirmation/visibility rules trade responsiveness against flicker and reading time. Visibility expiration is evaluated on detection turns. Extremely brief lines can occur between captures; slow translation can outlast a line's display time. Obsolete pending work is intentionally replaced, so bounded queues cannot guarantee display of every historic subtitle during a network stall.
5. Automatic scene boundaries use a text-free gap; scene changes without such a gap require updating explicit scene facts/resetting the session. Speaker identity is supplied by the player and should be blank when unknown. No automatic identity or plot inference is implemented.
6. Offline embedded glossary constraints and story-history disambiguation remain unavailable. A native call already executing can delay the latest translation, although capture/OCR continues and its stale result is rejected. Existing DeepL glossary IDs must support the chosen pair; invalid IDs/pairs produce an actionable service error.

## Changed files

The following inventory includes application changes, new extracted components, tests, CI, and documentation. Existing game files and model binaries were not modified.

- `CortexTransl.sln`
- `README.md`
- `src/CortexTransl.App/Data/DatabaseMigrator.cs`
- `src/CortexTransl.App/Models/AppSettings.cs`
- `src/CortexTransl.App/Models/GameProfile.cs`
- `src/CortexTransl.App/Models/PipelineResult.cs`
- `src/CortexTransl.App/Services/Capture/CaptureWindowTarget.cs`
- `src/CortexTransl.App/Services/Capture/GdiScreenCapture.cs`
- `src/CortexTransl.App/Services/Capture/IScreenCaptureService.cs`
- `src/CortexTransl.App/Services/Capture/ImageFingerprint.cs`
- `src/CortexTransl.App/Services/Capture/ScreenCaptureService.cs`
- `src/CortexTransl.App/Services/Capture/TranslationPipeline.cs`
- `src/CortexTransl.App/Services/Ocr/OcrImagePreprocessor.cs`
- `src/CortexTransl.App/Services/Ocr/WindowsOcrEngine.cs`
- `src/CortexTransl.App/Services/Profiles/SqliteGameProfileRepository.cs`
- `src/CortexTransl.App/Services/Translation/DeepLTranslationProvider.cs`
- `src/CortexTransl.App/Services/Translation/ITranslationProvider.cs`
- `src/CortexTransl.App/Services/Translation/OfflineBergamotTranslationProvider.cs`
- `src/CortexTransl.App/Services/Translation/OfflineModelInstaller.cs`
- `src/CortexTransl.App/Services/Translation/RoutingTranslationProvider.cs`
- `src/CortexTransl.App/Utils/DialogueTextAssembler.cs`
- `src/CortexTransl.App/Utils/TextSimilarity.cs`
- `src/CortexTransl.App/ViewModels/PlayViewModel.cs`
- `src/CortexTransl.App/Views/MainWindow.xaml`
- `src/CortexTransl.App/Views/OverlayWindow.xaml.cs`
- `.github/workflows/windows-ci.yml`
- `docs/STORY_TRANSLATION_RELIABILITY.md`
- `src/CortexTransl.App/Models/StorySettings.cs`
- `src/CortexTransl.App/Properties/AssemblyInfo.cs`
- `src/CortexTransl.App/Services/Capture/DialogueTracker.cs`
- `src/CortexTransl.App/Services/Capture/MenuTranslationProcessor.cs`
- `src/CortexTransl.App/Services/Translation/ProtectedStoryText.cs`
- `src/CortexTransl.App/Services/Translation/StoryTranslationContext.cs`
- `tests/CortexTransl.Tests/CortexTransl.Tests.csproj`
- `tests/CortexTransl.Tests/DatabaseTests.cs`
- `tests/CortexTransl.Tests/DeepLTests.cs`
- `tests/CortexTransl.Tests/DialogueTests.cs`
- `tests/CortexTransl.Tests/GlobalUsings.cs`
- `tests/CortexTransl.Tests/OcrAndPerformanceTests.cs`
- `tests/CortexTransl.Tests/OfflineTests.cs`
- `tests/CortexTransl.Tests/PipelineTests.cs`
- `tests/CortexTransl.Tests/TestDoubles.cs`
- `tests/CortexTransl.Tests/WindowsOcrTests.cs`

## Subsequent offline latency measurements

The [offline subtitle latency report](OFFLINE_SUBTITLE_LATENCY.md) documents the
Fast Subtitles changes and actual controlled Windows measurements after Update-17.
The results above describe the earlier reliability work and retain its original
verification limits.
