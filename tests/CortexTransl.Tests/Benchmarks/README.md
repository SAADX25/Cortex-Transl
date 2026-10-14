# Controlled Windows latency benchmark

The benchmark uses the production capture, OCR, pipeline, provider and overlay
classes for both the standard and fast subtitle profiles. Each profile is warmed
and uses a fresh fixture database. The network handler rejects every request.

`AnimatedSubtitleFixture` owns a visible WPF source window and the real overlay.
The opt-in benchmark requires an interactive desktop and installed en-ar models:
set `CORTEX_TRANSL_RUN_WINDOWS_BENCHMARK=1` and, when needed,
`CORTEX_TRANSL_TEST_MODELS` before running `dotnet test`.

There are 24 synthetic samples (eight lines repeated three times), plus stable
frames. Reports compare production profiles, not a frozen historical revision.
Timing is reported without machine-dependent latency thresholds. The lifecycle
checks cover stop/restart, window loss, recovery and staging-buffer reuse.
Historical measurements in `docs/OFFLINE_SUBTITLE_LATENCY.md` remain historical.
