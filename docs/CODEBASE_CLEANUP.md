# Update-19 codebase cleanup and recording investigation

Baseline: `117b32d` (Update-19), repository `SAADX25/Cortex-Transl`.
Work and local verification: 2026-10-13, Windows x64, .NET SDK 10.0.400 /
runtime 10.0.11, NVIDIA GeForce GTX 1050 Ti (4 GiB), driver 582.66.

The cleanup retains selected-window capture, GPU ROI readback and staging reuse,
bounded OCR/translation scheduling, resident offline prewarming, stale-result
rejection, cancellation, Arabic terms/names, game profiles, Fast Subtitles,
diagnostics, SQLite migration compatibility, global hotkeys, and both providers.
No model binaries, native decoder parameters, or game files were changed.

## Audit coverage and removal rules

The audit covered application C#, all WPF XAML/resource dictionaries, application
and publish manifests, project/solution/NuGet configuration, tests and frozen
benchmarks, the local capture-check tool, development launcher, packaging script,
Inno Setup installer, GitHub Actions, README, and existing engineering reports.
Generated `bin`, `obj`, published binaries, model payloads and test artifacts are
outputs, not candidates for source cleanup. `.agents` contained no applicable
`AGENTS.md`; the checkout started clean at the requested commit.

Candidate discovery used repository-wide symbol/resource searches, field-write
inspection, compiler validation, XAML resource/binding and event inspection,
constructor/composition paths, serialization models, P/Invoke and WinRT interface
contracts, tests, and installer/runtime consumers. Low reference counts alone
were not a removal decision. Tests and frozen Update-17 comparison sources were
preserved. No useful whole source file or feature was deleted.

| Area | Finding and disposition |
| --- | --- |
| View model | Remove unbound option lists; extract playback and test evaluation; fix saved placement and language readiness |
| WPF views/styles | Preserve named controls, bindings, converters and event handlers; remove only keys with no resource/string consumers |
| Capture/interop | Remove dead placement restoration; serialize target/session ownership; release on stop/loss; retain native ABI slots |
| Pipeline/context | Preserve queue, history, scopes and publication rules; add explicit capture stop; normalize a glossary lookup source once |
| OCR/preprocessing | Preserve passes, pixel/coordinate transforms and recovery heuristics; remove unused preset metadata and small repeated allocations |
| Bergamot/DeepL | Preserve decoding, routing, glossary/XML behavior and HTTP payloads; improve cleanup and test-page eligibility |
| SQLite/settings/profiles | Preserve schema/cache namespaces; fix initialization and path construction; reuse serializer options and expose cache failures |
| Diagnostics/hotkeys | Preserve opt-in numeric bounds and modifier checks; expose dispatch failures; remove a provably unreachable local key branch |
| Tests/benchmarks | Retain all original tests and frozen algorithms; add regression and actual capture lifecycle coverage |
| Tooling/installer/CI | Repair stale reflection checks, compile them in CI, validate Release publishing and version propagation |
| Documentation | Correct download, storage, privacy, recording, capture-border, OCR-pass and cadence claims |

## Dead code removed, with evidence

| Removed code | Evidence |
| --- | --- |
| `_capturePlacementRegion`, `_lastPlacement`, `ResetCapturePlacement`, `RestoreCapturePlacement` | The nullable capture-placement field had no assignment of a region: declaration and assignment to `null` only. Restoration required equality with a non-null captured region, so its move branch could never execute. `_lastPlacement` only fed that dead branch. All callers in WGC/GDI capture and overlay hiding were removed. Neither field was bound, reflected, serialized or registered. |
| `TranslationModeOptions`, `OverlayPlacementOptions`, `OverlayTextSizeOptions` | Only declarations existed. XAML uses explicit radio choices with converter parameters; the source-language/engine option collections remain bound and retained. |
| `TestChunkResult.StatusColor` | The test page binds `StatusIcon`, text and index, not this property. Chunk results are transient UI data, not persisted settings/profile records. |
| `AppDataPaths.LogPath`, `DebugCaptureDirectory`, creation of `debug-captures` | No reader existed. The declared log path was not used by `AppLog`, which writes `logs/errors.log`. No debug screenshot writer existed. Existing user directories/files are not removed. |
| `GetMonitorWorkArea`, `GetMonitorInfo`, `MonitorInfo` | The work-area helper had no callers; its native declaration and structure were used only by that helper. Virtual-screen and DPI functions remain. |
| Production `OcrPreprocessedImage.PresetName` and its constructor parameter | The parameter only populated an unread property. Scale/offsets remain; preprocessing option names still distinguish reduced-text interpolation. The frozen benchmark's own type is unchanged. |
| Production `IsEnglishArabicReady` | Readiness now checks the selected language pair; no remaining production/test/tool consumer needs an English-only readiness shortcut. The frozen benchmark version remains unchanged. |
| Unused `RunTurnAsync` boolean / loop `skipped` | The loop assigned but never consumed the result. Returning no value preserves all publication and cadence behavior. |
| `Key.System` check inside the local F10 branch | The preceding guard already returns for `Key.System`; plain F10 behavior and modified-key rejection remain. |
| Best-effort `HideYellowCaptureBorder` | The unpackaged application had no borderless capture capability/consent path. The call could silently fail or be ignored. Production now retains the Windows indicator. This is unsupported optional behavior removal, not a claim that it caused NVIDIA failures. |

Removed XAML styles: `MiniOverlayMock`, `StatusPill`, `SmallMutedText`, `TextPanel`.
Removed from both themes: `DangerPressedBrush`, `StatusPillTextBrush`, `SuccessBrush`,
`SurfaceBrush`, `CardBrush`, `BorderBrushSoft`, `BorderBrushMedium`, `TextPrimary`,
`TextSecondary`, `TextMuted`, `TranslationPanelBackgroundBrush`,
`StatusPillBackgroundBrush`. No `StaticResource`, `DynamicResource`, code lookup,
or test/tool lookup consumed these keys. Compiled dictionaries are loaded by the
WPF regression test. Implicit styles and template parts were retained.

Intentionally retained: `IGraphicsCaptureItemInterop.CreateForMonitor` is a COM
vtable slot, despite no managed caller; `_item` keeps the capture item rooted;
P/Invoke declarations, generated regex methods, WPF event handlers/converters,
serialization fields, migration columns, tests, and benchmark source snapshots
are runtime contracts. The active selected-shell `PrintWindow` path is retained.
Similar-looking primary/secondary button templates have different hover/pressed
appearance; they were not collapsed at the expense of UI behavior.

## Consolidation and refactoring

- `PlayViewModel.Playback.cs` groups region selection, playback cadence and result/
  overlay publication within the same binding-compatible partial view model.
  This is organization of shared session state, not a new controller abstraction.
- `TranslationTestRunner` independently evaluates ordered chunk results, empty/
  missing/extra output, completeness and cancellation. The view model owns only
  test-page state and presentation. Completeness is not an Arabic accuracy score.
- `OverlaySettings.CalculatePlacement` makes the existing cover/above/below and
  screen-edge calculation independently testable. Its pixel rules are unchanged.
- `ScreenCaptureService` now uses one ownership lock for selection, synchronous
  readback, stop and disposal. Creation/reset/retry logic no longer traverses
  several nested locking/exception paths. Frame callbacks remain independent.
- `ResourceCleanup` centralizes best-effort disposal with error logging so one
  failing native resource does not suppress the rest of shutdown.
- Settings reuse one `JsonSerializerOptions`; glossary lookup normalizes the
  source once; recovery border checking avoids 32 tiny edge arrays; each capture
  turn no longer copies provider settings already maintained by setters/startup.
- The existing local `tools/CaptureChecks` helper reflected into deleted
  `CaptureSafePlacement` and `GetCacheProviderId` methods. It is now a versioned,
  compile-checked project using direct internal contracts. Existing menu/interop
  checks remain; current placement/cache tests replace abandoned contracts.

## Bugs fixed and error visibility

1. F8 stop previously left WGC recording the selected window. Stop now releases
   its session/resources, suspends capture, and retains target identity for restart.
   Loss of the bound source also releases the session without choosing another window.
2. Capture creation/readback could race rebind or disposal. One service lock now
   protects the complete synchronous capture operation. Cancellation after readback
   disposes the returned bitmap before rejecting it.
3. A callback failure with no new frame was treated as an ordinary empty frame;
   it now reaches capture recovery. Creation failures are logged instead of hidden.
4. Startup overwrote persisted `below` placement with `cover`. It now respects the
   saved choice. WPF tests load both themes and the common dictionary; theme paths
   are qualified to the application assembly rather than the entry/test assembly.
5. Model preparation used mutable source-language state after awaits. It now uses
   a snapshot, ignores obsolete progress/completion/error updates, reevaluates a
   newly selected language, and cancels pending preparation on disposal.
6. The test command could become enabled for DeepL, then refuse to run. It now
   requires a ready offline pair, and eligibility updates when readiness/engine changes.
7. SQLite's interlocked initialization flag was published before native setup
   finished. `Lazy` publishes completion safely. A connection-string builder also
   supports valid paths containing semicolons.
8. Disposal failure could leave later D3D/hotkey/capture resources unvisited.
   Cleanup now attempts each resource and logs failures. Native inference remains
   blocking until its current call returns; stale/canceled output is still rejected.
9. `pack.ps1 -Version` previously changed its expectation while publish metadata
   and installer filename/version stayed hardcoded. The version now flows through
   MSBuild and Inno Setup; invalid versions are rejected before output cleanup.

Cache failures remain nonfatal and are logged at most once per five seconds per
repository instance. Overlay affinity, dispatch, topmost maintenance and native
cleanup failures are observable. API-key encryption failures log only their type,
never key/ciphertext/exception details. Logging itself retains its last-resort
catch. Cleanup-and-rethrow handlers and expected shutdown cancellation are retained.

## Source size

Counts include physical lines (comments/blank lines included) in `.cs`, `.xaml`
and `.csproj` under `src`/`tests`. Generated outputs and binary assets are excluded.
Adding tests/validation files is intentional; file count is not an optimization goal.

| Scope | Baseline files / lines | Cleaned files / lines |
| --- | ---: | ---: |
| Application | 87 / 10,422 | 90 / 10,204 |
| Tests, including frozen benchmark | 20 / 3,205 | 22 / 3,519 |
| Versioned capture-check tool | 0 / 0 (local ignored helper) | 2 / 143 |

| File | Before | After |
| --- | ---: | ---: |
| `PlayViewModel.cs` | 1,504 | 1,054 |
| `PlayViewModel.Playback.cs` | 0 | 342 |
| `OverlayWindow.xaml.cs` | 485 | 418 |
| `ScreenCaptureService.cs` | 189 | 150 |
| `WindowsGraphicsWindowCapturer.cs` | 279 | 282 |
| `TranslationPipeline.cs` | 345 | 351 |
| `MainWindow.xaml` | 751 | 751 |

The view model still has substantial settings/navigation state. Its combined
partial size is 1,396 lines; moving code alone is not presented as deletion.
Capture/pipeline code gained explicit lifetime/error handling where needed.
The main window layout was inspected and preserved; unused shared resources were
removed without introducing a new page/view hierarchy.

## NVIDIA recording investigation

**Root cause of the user's reported recording stop: not established.** NVIDIA
App 11.0.5.420 was installed. Its overlay switch appeared enabled, and its overlay
opened while Cortex translation was stopped. The user pressed Escape to stop
computer use before a recording/Instant Replay reproduction was performed. No
further NVIDIA interaction, configuration change, recording, or game launch was
performed. No NVIDIA compatibility claim or workaround is supplied.

Verified source findings:

- There is no NVIDIA SDK/NVAPI recording integration, injection, special provider,
  compatibility toggle, or setting mutation in Cortex. NVIDIA names in the menu
  dictionary are ordinary Arabic label translations.
- WGC creates an HWND capture item, a hardware D3D11 device and a free-threaded
  two-frame pool. GPU readback copies only the selected ROI, and staging is reused.
  Sessions survive static content and are recreated for size changes/recovery.
- The separate topmost/no-activate/click-through WPF overlay requests
  `WDA_EXCLUDEFROMCAPTURE`. Its exclusion from a selected-window OCR image passed
  real WGC integration. That is not proof that every recording API excludes it,
  nor proof that this flag stops NVIDIA recording.
- Capture exclusion can explain missing Arabic text in a recording. Microsoft's
  [display-affinity contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
  defines the requested exclusion. It does not establish the reported recorder-stop cause.
- NVIDIA documents that Desktop capture recordings may fail to start or stop
  when protected content is detected in an open application. This is a possible
  independent cause to check against an actual notification, not a diagnosis of
  this user's case. See [NVIDIA's protected-content recording article](https://nvidia.custhelp.com/app/answers/detail/a_id/5602).
- Windows border removal requires capability and consent, as described in the
  [IsBorderRequired documentation](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired).
  Current sessions set `IsBorderRequired = false` before starting capture,
  with the borderless-access consent request running in the background
  so it cannot stall OCR. Windows consent and system policy determine whether
  the request is honored; rejection leaves capture operational with its border.
- Menu stability compares normalized labels with a four-pixel rectangle tolerance.
  One-pixel OCR jitter no longer cancels pending translations; larger movements
  still update overlay positions. The real menu integration check covers WGC,
  Windows OCR, offline Arabic menu terminology, and rendered WPF label chips.
- F8/F9/F10 are registered without modifiers. Both the global dispatch and local
  handlers reject modified chords such as Alt+F9. Physical coexistence with NVIDIA
  shortcuts still needs testing; registration failure is logged.

| Requested recording case | This session's result |
| --- | --- |
| NVIDIA recording without Cortex | Not recorded/tested |
| Cortex open, translation stopped | NVIDIA App/overlay opened; recording not tested |
| Bergamot translation running | Controlled Cortex capture/translation passed; NVIDIA recording not tested |
| Instant Replay during translation | Not tested |
| Local non-DRM video | Not tested with recording |
| Regular YouTube video | Not tested with recording |
| Windowed/borderless story game | Not tested with recording |
| Translation overlay visible/hidden | WGC source exclusion tested; NVIDIA visible/hidden recording matrix not tested |

Reproduction protocol for completing the matrix:

1. Record NVIDIA App/driver versions, recording mode (Game or Desktop), shortcut
   settings, output location and any exact error notification. Confirm a recording
   without Cortex produces a playable file with increasing duration.
2. Repeat with Cortex open/stopped, then start already-installed Bergamot with F8.
   Check immediately and after 30 seconds, then stop translation and repeat.
3. Repeat the before/during/after sequence for Instant Replay. Record whether the
   recorder stops, replay switches off, overlay fails to open, only translated
   text disappears, or a setting was already disabled. These are distinct outcomes.
4. Run the same sequence on a local non-DRM video, an ordinary YouTube clip, and
   an available borderless story game. Test visible versus hidden Cortex overlay.
   Compare Game/Desktop modes only where already configured/available; any NVIDIA
   privacy/security-setting change is a user action, not a Cortex workaround.
5. If NVIDIA reports protected content, record that evidence and use an allowed
   source for the control test. Do not bypass DRM/capture protection, change
   NVIDIA security settings, or inject into a game. If recording already fails in
   the Cortex-free control, that is not evidence of a Cortex-triggered failure.

Cortex's session-lifetime and documentation fixes are independently justified.
They are **not** an unverified NVIDIA recording fix. Capture exclusion is retained.

## Automated and packaging verification

Baseline regular suite: 78 passed, two opt-in tests skipped. Baseline interactive
run: both opt-in tests passed. Intermediate tests caught and resolved WPF test-host
resource/dispatcher setup issues; failed/aborted intermediate runs are not counted
as successful validation.

Final Release build: **zero warnings and zero errors** (`-warnaserror`).
Final complete Windows run with `CORTEX_TRANSL_RUN_WINDOWS_BENCHMARK=1`:
**95 passed, zero failed, zero skipped**, including the three real capture tests.
Evidence: `TestResults/cleanup-final.trx`. The final run fixed a test-order issue:
the WPF test host remains alive for the test process so its shutdown cannot prevent
later capture fixtures from creating windows. Earlier failed/aborted runs remain
intermediate diagnostics, not claimed passes.

Coverage includes actual English Windows OCR, installed native Bergamot and
cancellation/recovery; short/negated/typewriter/outlined dialogue; Fast Subtitles;
latest-work bounds; cache/context/game isolation; DeepL synthetic HTTP failures
and protected XML terms; legacy SQLite migration; settings and encrypted-key
round-trip; saved overlay placement; language changes during preparation; WPF
themes; failed-resource cleanup; actual WGC ROI/staging/static-frame behavior;
and actual selected-window stop/restart/minimize/recovery/idempotent disposal.

The repaired tool passed software D3D11 WARP/WinRT texture interop, 22 known
offline menu cases without network, wrapped labels/bounds, and offline game scope.
Release publish and Inno Setup 6.7.3 compilation produced
`dist/CortexTransl-1.6.0-Setup.exe`, with `bergamot.dll`, native SQLite, bundled
English-Arabic models and self-contained runtime. Installation/uninstallation
against an existing user install was not performed. Packaging cleans only a
validated directory under `dist`, rejects linked output directories, and checks
the compiler before cleanup. `-Version` is passed to both executable metadata and
installer metadata/output name.

CI now builds the capture-check project with the solution, executes the tool,
and validates self-contained publishing. Local CI-equivalent restore/build/test/
tool/publish steps ran. Hosted GitHub Actions and the self-hosted interactive job
were not remotely dispatched; this report does not label them green.

## Update-19 latency and resource comparison

The final matched latency-only pair measured:

| Metric | Update-19 P50 / P95 | Cleaned P50 / P95 |
| --- | ---: | ---: |
| Controlled appearance → overlay render (ms) | 208.80 / 285.33 | 192.36 / 293.04 |
| Capture (ms) | 0.725 / 1.232 | 0.736 / 1.386 |
| GPU ROI readback (ms) | 0.629 / 1.123 | 0.633 / 1.254 |
| Windows OCR (ms) | 5.410 / 7.885 | 4.987 / 7.836 |
| Native inference (ms) | 9.003 / 23.045 | 9.678 / 25.913 |
| CPU (% of all logical processors) | 6.70 / 11.48 | 7.28 / 10.75 |
| Busiest process GPU engine (%) | 4.36 / 5.17 | 4.24 / 5.19 |
| Working set (MiB) | 399.52 / 403.97 | 402.57 / 405.16 |
| Private memory (MiB) | 591.36 / 595.14 | 592.54 / 596.21 |

P50 latency was 16.44 ms lower; P95 was 7.70 ms higher in this pair. An earlier
filtered pair measured 205.20 / 304.69 ms before and 205.53 / 286.68 ms afterward.
These opposite tail movements show run variability; no consistent tail improvement
or regression is established. Both matched runs used one staging allocation.
Readbacks were 161 → 160; ROI bytes were 131,891,200 → 131,072,000. CPU time was
10.484 → 10.609 seconds over 11.607 → 11.563 wall seconds. The approximately 3 MiB
working-set and 1.2 MiB private-memory median differences are reported, not hidden
or claimed to prove increased steady-state production memory. Stopping production
capture now releases the session instead of keeping it alive; its stopped-state
resource saving has not been separately quantified.

The checked-in [numeric comparison](benchmarks/update19-cleanup.json) retains the
per-stage statistics and resource counters. The benchmark's embedded `baseline`
variant remains Update-17. For this report, Update-19 is the `upgraded` variant
run from `117b32d`, compared with `upgraded` from the cleaned checkout. Both use
the same warmed models, fresh SQLite fixture caches, 24 known synthetic captions,
real WGC/OCR/overlay, and a network handler that rejects every request.

Timing is WPF controlled caption appearance to overlay composition, not physical
display scanout or an actual game's latency. CPU/GPU/memory metrics describe the
test process, which also owns the animated source fixture and legacy comparison,
not an isolated production/game process. Two early pairs were noisy; a final pair
ran the latency test alone on both trees to exclude unrelated suite allocations.
No statistically significant performance/translation-quality improvement or
universal no-regression guarantee is claimed from this small sample.

## Remaining limitations and risks

- Complete the NVIDIA manual matrix before diagnosing recorder stops or claiming
  compatibility. GPU hardware availability alone does not satisfy that matrix.
- Validate actual game/video playback, exclusive fullscreen/HDR, display changes,
  mixed DPI, resizing, multi-monitor movement and physical hotkey coexistence.
  Unit coordinate checks and controlled WPF capture do not replace these tests.
- Live DeepL credentials/latency were not exercised. Its API contract is covered
  by synthetic HTTP tests; fluent Arabic review of a representative story corpus
  remains required for linguistic quality.
- Native Bergamot inference cannot be interrupted mid-call. Cancellation rejects
  output afterward; disposing a provider can wait for an in-flight native call.
- API-key encryption/decryption still fails closed when Windows DPAPI is
  unavailable, now with a sanitized diagnostic. Corrupt settings/profile data and
  unusual driver/device failures retain conservative fallback/error paths.
- Keep the frozen benchmark sources and useful tests. Further splitting the view
  model or changing UI templates should follow a specific maintenance need.

Reproduce the complete interactive validation from an unlocked Windows desktop:

```powershell
dotnet restore CortexTransl.sln
dotnet build CortexTransl.sln -c Release --no-restore -warnaserror
$env:CORTEX_TRANSL_TEST_MODELS = 'C:\Models' # contains en-ar/config.yml and matching model files
$env:CORTEX_TRANSL_RUN_WINDOWS_BENCHMARK = '1'
$env:CORTEX_TRANSL_BENCHMARK_OUTPUT = Join-Path $pwd 'artifacts/cleanup-validation'
dotnet test CortexTransl.sln -c Release --no-build --logger trx
dotnet run --project tools/CaptureChecks -c Release --no-build
./pack.ps1 -Version 1.6.0
```
