# Offline subtitle latency upgrade after Update-17

Implemented in the existing .NET 10 / WPF application. The optional **Fast
Subtitles** profile uses an 80 ms capture cadence, 150 ms initial unpunctuated
stability and 50 ms completed-line confirmation. Short complete dialogue is
eligible immediately. Original profile defaults remain available.

## Measured result

On 2026-10-13, an interactive Windows 10.0.26300 desktop with an Intel i7-8700
(12 logical processors) ran actual Windows Graphics Capture, Windows OCR,
installed English-to-Arabic Bergamot, SQLite and the application's OverlayWindow.
The source was a controlled 1280×720 WPF window with moving backgrounds and a
1280×160 outlined-caption region. Eight lines were repeated three times. Both
variants were prewarmed and started with fresh fixture cache databases. Baseline
algorithms are frozen from `e99fd23` (Update-17), with instrumentation only.

| Controlled source appearance → Arabic composition boundary | Update-17 | Fast Subtitles |
| --- | ---: | ---: |
| P50 | 414.800 ms | 139.127 ms |
| P95 | 698.577 ms | 316.568 ms |
| Maximum | 1068.016 ms | 319.326 ms |
| Correct recognized source, ignoring case/typographic equivalents | 24/24 | 24/24 |
| Short exclamations, nine observations | — | 119.556–139.127 ms |
| Supplied unpunctuated lyric, three observations | — | 305.760–319.326 ms |

P50 improved 66.5% and P95 improved 54.7%. The under-500 ms target was achieved
for all 24 controlled samples. The 200–300 ms preference was achieved for many
lines, but not consistently for unpunctuated lyrics. These results **do not
demonstrate actual game or YouTube/browser performance**. A single ordered run,
small corpus, warm model, repeated captions and background workloads affect the
results; this is not a general latency guarantee.

The source appearance marker is taken after two WPF rendering events following
the source update, and the endpoint after two events following overlay update.
This is a composition-boundary proxy, not first luminous pixels or physical
scanout. Production diagnostics cannot know external source onset: their total
starts at the first capture request that observes a candidate. Frame arrival
latency, arbitrary playback/capture phase, monitor refresh, browser rendering,
exclusive fullscreen and network delays are not separately established here.

## Per-stage evidence

Values are **P50 / P95 milliseconds (sample count)**. Stages overlap, so adding
percentiles is invalid. GPU callback work in Update-17 occurs independently of
the capture-call scope. Recovery includes its nested preprocessing/OCR.
Stabilization includes both immediate short lines and longer settling lines.
Missing metrics mean not observed/not instrumented, not zero.

| Stage | Update-17 | Fast Subtitles |
| --- | ---: | ---: |
| Capture | 0.984 / 2.403 (174) | 0.715 / 1.115 (162) |
| FrameWait | — | 0.000 / 0.001 (162) |
| GpuCopyReadback | 1.926 / 2.946 (512) | 0.622 / 0.975 (159) |
| Fingerprint | 1.861 / 2.887 (174) | — |
| TextChangeDetection | — | 0.816 / 1.213 (162) |
| Preprocess, including SoftwareBitmap conversion | 7.505 / 12.581 (174) | 0.328 / 5.501 (158) |
| WindowsOcr, native recognition call | 6.023 / 8.125 (174) | 5.425 / 7.406 (158) |
| OcrRecovery | 7.443 / 10.843 (5) | — |
| Stabilization | 0.114 / 381.721 (35) | 0.064 / 185.020 (36) |
| QueueWait | 0.070 / 0.238 (35) | 0.058 / 0.404 (36) |
| CacheLookup | 0.251 / 10.423 (35) | 4.141 / 11.197 (36) |
| ModelInitialization | 103.280 / 103.280 (1) | 124.968 / 124.968 (1) |
| NativeWarmup | 162.900 / 162.900 (1) | 185.667 / 185.667 (1) |
| NativeInference | 8.615 / 23.658 (29) | 8.101 / 25.194 (10) |
| OverlayDispatch, completion to fixture publication request | — | 10.938 / 14.054 (24) |
| OverlayPublication, UI apply | 6.809 / 10.999 (24) | 6.142 / 10.739 (24) |
| OverlayRender, request to composition boundary | 14.822 / 34.675 (24) | 13.259 / 29.335 (24) |
| ControlledAppearanceToOverlayRender | 414.800 / 698.577 (24) | 139.127 / 316.568 (24) |

Initialization and warmup each have only one sample, not a cold-start percentile
study. They occurred before subtitle onset. Native decoding speed itself did not
materially improve; lower stabilization/polling overhead, cheaper readback and
cache reuse produced the improvement. Offline model weights, decoding and two
native worker threads were preserved. The upgraded cache retained repeated-line
translations across irrelevant history changes: only ten actual inference calls
were recorded versus 29 in the baseline. Queue counts include transient/canceled
candidates and cannot be interpreted as a cache hit ratio.

The upgraded visual signature caused 158 OCR calls over 162 capture turns in
this particular animated fixture. Background edges/antialiasing often changed
the mask, so it did **not** eliminate most OCR work here. The deterministic
scheduling regression independently confirms five-to-six OCR calls over ten
unchanged-glyph animated frames, periodic revalidation and immediate glyph edit
detection. Capturing/OCR at 80 ms remains a deliberate correctness tradeoff.

## Resource results

| Metric | Update-17 | Fast Subtitles |
| --- | ---: | ---: |
| GPU readbacks | 930 | 159 |
| CPU readback bytes | 3,428,352,000 | 130,252,800 |
| Staging allocations | 930 | 1 |
| Test workload wall time | 18.881 s | 11.738 s |
| Test process CPU seconds | 24.000 s | 10.797 s |
| CPU percentage of all logical processors, P50 / P95 | 10.37 / 15.24% | 7.68 / 10.51% |
| Process busiest GPU engine, P50 / P95 | 5.86 / 7.07% | 4.55 / 5.22% |
| Working set, P50 / P95 | 398.07 / 404.04 MiB | 399.93 / 402.29 MiB |
| Private memory, P50 / P95 | 591.57 / 596.51 MiB | 591.16 / 594.56 MiB |

Readback bytes fell 96.2%. CPU/GPU figures include the test host, source animation,
overlay, loaded model and benchmark instrumentation. They are not isolated app
or whole-device GPU measurements. Baseline ran first; both ran in the same
process with collection between runs. Memory remained comparable, not a proven
memory improvement. Resources were sampled at most once per second (16 baseline
and 11 upgraded CPU/GPU samples). GPU measurements use available process PDH
engine counters and can be missing on other machines. The bounded GPU timing
buffer retains the last 512 of 930 baseline readbacks. Full counts/bytes are
separate counters.

Raw synthetic-corpus evidence: [comparison JSON](benchmarks/update17-offline-latency.json).

## Translation quality limitation

All 24 Arabic outputs matched Update-17 exactly. That establishes regression
parity for this corpus, **not natural Arabic accuracy**. In particular, the
installed model translated `Cr-cr-cry since the day we parted` (OCR capitalization
`Cr-cr-Cry`) as **`Cr-cr-Cry منذ اليوم الذي افترى`**. It leaves the English stutter
and mistranslates the separation meaning. The natural Arabic requirement remains
unmet for this example. No lyric-specific replacement or unsupported character,
subject, gender or plot fact was inserted to hide the model error.

Story context, DeepL context/glossary support, explicit per-game terms and speaker
names remain supported. Bergamot does not consume dialogue history. Its glossary
support is exact whole-input matching, not inflection-aware terminology enforcement
inside arbitrary sentences. Better model/decoder evidence and Arabic-speaking
review of a varied story corpus are required before claiming natural MSA quality.

## Changed files and behavior

All paths below are relative to the repository root.

- `Services/Capture/WindowsGraphicsWindowCapturer.cs`: latest-frame ownership in
  the callback, worker-only GPU ROI copy/readback, retained staging and CPU ROI,
  static-frame reuse, disposal and resize handling. No full-window bitmap copy.
- `Services/Capture/CaptureCrop.cs`: physical ROI bounds validation, including
  negative multi-monitor coordinates. `ScreenCaptureService.cs`: initializes
  free-threaded WGC on the detection worker without a synchronous UI hop.
- `Services/Capture/SubtitleVisualSignature.cs`: packed light/dark stroke hint
  scans every pixel; cached dimensions avoid per-pixel GDI queries.
- `Services/Capture/TranslationPipeline.cs`: measured scheduling, independent OCR
  confirmation, periodic revalidation, immediate result event before persistence,
  bounded latest-work queue, sequence/cancellation checks, offline cache identity.
- `Services/Capture/DialogueTracker.cs`, `Utils/OcrTextCanonicalizer.cs`: optional
  fast timing and conservative typography normalization; meaningful pronouns,
  negation, apostrophes, stutters and punctuation remain distinct. True prefix
  growth resets stability; reused OCR never fabricates fresh confidence.
- `Services/Ocr/WindowsOcrEngine.cs`, `OcrImagePreprocessor.cs`: reusable engines,
  direct original-pixel first pass, backoff for expensive empty-image recovery,
  outlined bright-stroke recovery after other passes fail. Its full-height bar
  suppression is a recovery heuristic; tightly cropped text needs real validation.
- `Services/Translation/OfflineBergamotTranslationProvider.cs`: resident services,
  explicit prewarm, short-line path and installed-model requirement during playback.
  Missing models never trigger automatic download or cloud fallback while playing.
- `Services/Translation/ITranslationProvider.cs`, `RoutingTranslationProvider.cs`,
  `StoryTranslationContext.cs`: offline identity excludes unused history, scene
  and speaker facts while retaining game, language, terms and name mappings.
  Namespace `offline-v4` safely coexists with existing entries. No database schema
  changes or deletion; prior profiles deserialize new booleans as false.
- `Services/Diagnostics/PerformanceDiagnostics.cs`, `GpuUsageSampler.cs`,
  `OverlayRenderMeasurement.cs`: optional monotonic, numeric-only bounded diagnostics
  and process resources; cancel/timeout rendering subscriptions.
- `Models/StorySettings.cs`, `PipelineResult.cs`, `ViewModels/PlayViewModel.cs`,
  `Views/MainWindow.xaml` and `.xaml.cs`: profile toggles, shared diagnostics,
  prewarm before playback, cadence includes capture/OCR work, Render-priority
  publication and rejection of older pending snapshots.
- `tests/CortexTransl.Tests/LatencyRegressionTests.cs`, `WindowsOcrTests.cs`,
  `TestDoubles.cs`, `.csproj`, `Benchmarks/`: timing, semantic changes, lyric,
  typewriter, animated masks, queue/cache publication, no-network, real model
  cancellation/recovery, challenging native OCR and opt-in actual WGC benchmarks.
- `.github/workflows/windows-ci.yml`: Windows build/regression tests on pushes and
  PRs; optional interactive runner benchmark with preinstalled models and JSON/TRX
  artifacts. Hosted-runner skips are visible, never treated as game evidence.

Application source paths are under `src/CortexTransl.App/`. Menu assembly, DeepL
HTTP parameters, overlay controls, hotkeys, presets and model decoding were not
rewritten. Existing tests for these behaviors still pass.

## Verification and reproduction

Local Release build succeeded without warnings. Full standard run: **78 passed,
two opt-in Windows tests skipped**, zero failed. Separate opted-in run: **two
passed**, zero failed. In total, all 80 tests were exercised successfully. The
regular suite includes actual installed native Bergamot tests and native Windows
OCR tests on this machine. It checks cancellation after actual native inference
starts, discarded result and resident-service recovery. Slow-provider pipeline
tests separately verify ongoing detection and latest-result publication.

The WGC integration verifies the visible Arabic overlay does not enter the
selected-window OCR image, unchanged-size staging reuse, static dialogue retention,
crop location invalidation and new staging allocation on ROI resize. Synthetic
OCR checks include six outlined/glowing busy-background captions and 18 short
dialogue font/background cases. No user screenshots were supplied or evaluated.

```powershell
dotnet restore CortexTransl.sln
dotnet build CortexTransl.sln -c Release --no-restore
dotnet test CortexTransl.sln -c Release --no-build --logger trx

# Run from a signed-in interactive Windows desktop with English OCR installed.
# This folder contains en-ar/config.yml and the matching complete model files.
$env:CORTEX_TRANSL_TEST_MODELS = 'C:\Models'
$env:CORTEX_TRANSL_RUN_WINDOWS_BENCHMARK = '1'
$env:CORTEX_TRANSL_BENCHMARK_OUTPUT = Join-Path $pwd 'artifacts/latency-benchmark'
dotnet test CortexTransl.sln -c Release --no-build --filter 'FullyQualifiedName~ControlledWindowsBenchmark' --logger trx
```

In the app, enable **Fast subtitles** and optionally **Performance diagnostics**
in the story/preset controls. Install the required model explicitly before
playback. **Save P50/P95 report** replaces one bounded JSON at
`%LOCALAPPDATA%\Cortex Transl\logs\subtitle-performance.json`. Diagnostics retain
at most 512 numeric samples per fixed stage. They contain no source/Arabic text,
screenshots, window titles, identities or API keys. The benchmark JSON contains
only its explicitly known synthetic corpus outputs. Model startup before profile
diagnostics are enabled will not retroactively appear in a production report.

## Remaining Windows/real-world work

- Actual YouTube/browser window and real story game validation are outstanding;
  no video URL, screenshots or running game were supplied. Use the same source
  corpus and collect multiple sessions before making performance/accuracy claims.
- Validate game-specific stylized, colored/low-contrast text, motion through
  bright/dark thresholds, OCR case/spelling oscillation, sub-80 ms lines, HDR,
  fullscreen, window resizing, mixed DPI and multi-monitor movement on real hardware.
  Coordinate math was unit-tested; that is not a physical-monitor validation.
- Native Bergamot inference cannot be interrupted midway by its blocking API.
  Detection continues; cancellation discards the result on return. A newer line
  can wait behind one in-flight native call. Parallel model instances were avoided
  to preserve resource limits. The under-500 ms target is not guaranteed here.
- Recovery has up to four justified passes only after empty native OCR, with
  repeated-failure backoff up to one second. An unchanged weak caption that fails
  all passes can still be missed/delayed. Meaningful spelling/case oscillation
  still resets candidates conservatively; arbitrary fuzzy equivalence is unsafe.
- Composition timing does not prove physical display latency. Real onset latency
  needs an externally synchronized source/recording or game-side test harness,
  without injecting into games or modifying their files.
- The selected-window privacy boundary remains mandatory in dialogue mode: loss
  of the bound window clears/cancels work without desktop fallback. Explicit menu
  capture retains its existing selected-target fallback. No game injection or
  game-file changes were used. Current production capture keeps Windows' capture
  indicator; it does not request borderless capture permission. The frozen
  Update-17 benchmark still retains its historical best-effort border call.

GPU copy contract reference: [Microsoft CopySubresourceRegion documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copysubresourceregion).
