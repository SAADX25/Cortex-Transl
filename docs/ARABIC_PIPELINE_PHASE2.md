# Arabic pipeline hardening — 2026-10-14

Translation cache rows now carry `CacheVersion`. Startup adds the column to old
databases and deletes rows older than `TranslationCacheVersion.Current` (2).
Reads require the current version; writes stamp it explicitly. Repeated startup
preserves current translations and all game profiles. Future incompatible OCR or
translation changes should bump this single constant. `TextHasher.TranslationKey`
hashes canonical, merged text; general SHA-256 hashing remains exact for context
JSON and executable identities.

OCR now samples locked pixel buffers into a stack histogram, caches dimensions
outside pixel loops, sharpens directly from a native bitmap copy without two
large managed arrays, and pools caption column counts. Recovery scaling draws
the source crop directly. White and yellow caption strokes survive the binary
mask while dark outlines and full-height scene bars are suppressed. The normal
first pass still sends original pixels directly to Windows OCR. Stroke recovery
precedes the more expensive enlarged recovery pass.

Capture preparation and release run off the WPF dispatcher. Stop cancels and
hides immediately; restart waits for the previous session's release. Cancellation
sources and the run semaphore outlive their users. Async disposal waits for the
play loop and translation worker before releasing the provider and diagnostics.
Overlay publications are coalesced and posted asynchronously. GPU creation,
resize and event-unsubscription failure paths retain deterministic cleanup.

## Release preprocessing measurements

Same Windows host and 1280×180 synthetic bitmap containing white/yellow text and
a full-height bright bar. Each preset had 10 warmups and 40 measured frames.
The benchmark collection excludes parallel test work. Values include preprocessing
and disposal, and exclude capture, Windows OCR, translation and UI rendering.
Managed allocation counts exclude native GDI+/WinRT memory. These measurements
do not establish an end-to-end latency or overlay frame rate.

| Preset | Median before → after (ms) | P95 before → after (ms) | Managed bytes/frame before → after |
| --- | --- | --- | --- |
| Original copy | 6.21 → 6.07 | 8.95 → 8.66 | 192 → 136 |
| Adaptive | 16.57 → 6.46 | 19.75 → 6.60 | 1,288 → 232 |
| Small text + sharpening | 124.42 → 36.29 | 130.30 → 43.30 | 7,373,224 → 320 |
| Caption strokes | 16.24 → 6.89 | 26.73 → 7.04 | 288 → 232 |

Adaptive median time fell 61%; small-text median time fell 71%, with its two
full-frame managed arrays eliminated. The original-copy path is largely unchanged.
Raw reports are generated under `artifacts/phase2/` (ignored build artifacts).

```powershell
$env:CORTEX_TRANSL_PREPROCESS_REPORT = "$PWD/artifacts/phase2/preprocess-after.json"
dotnet test tests/CortexTransl.Tests/CortexTransl.Tests.csproj -c Release -m:1 `
  --filter FullyQualifiedName~MeasurePreprocessingLatencyAndManagedAllocations
Remove-Item Env:CORTEX_TRANSL_PREPROCESS_REPORT
dotnet build CortexTransl.sln -m:1
dotnet test CortexTransl.sln -m:1
```

Regression coverage includes stale-cache purging and canonical keys, final-only
typewriter requests in both speed profiles, wrapped subtitle assembly, Arabic
placeholder mutations, mixed-script punctuation, white/yellow masks, real Windows
OCR fixtures, and stop/restart/dispose while capture release or translation is
blocked. Native Bergamot tests need installed models; controlled desktop capture
benchmarks remain opt-in. DeepL tests use synthetic HTTP responses.
