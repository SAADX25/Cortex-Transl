# Controlled Windows latency benchmark

`Update17/` freezes the seven relevant source files from commit `e99fd23`
(Update-17). Namespace changes, imports of unchanged application interfaces,
numeric diagnostic scopes and readback counters are the only benchmark changes.
The old algorithms, callback readback, OCR recovery, cache identity and polling
behavior are retained. The baseline uses its own fingerprint and preprocessor.
Do not refactor these frozen files alongside production changes.

`AnimatedSubtitleFixture` owns a visible WPF source window and the application's
real `OverlayWindow`. It draws outlined English captions over moving backgrounds.
`ControlledWindowsBenchmark` runs actual WGC, Windows OCR, SQLite and installed
English-to-Arabic Bergamot models. Both variants are warmed before playback and
use fresh fixture databases. The network handler throws on every request.
The corpus is public synthetic test text, never captured user content.

There are 24 samples (eight lines repeated three times), plus steady-text ticks.
This is a small controlled comparison, not a game/browser benchmark or linguistic
accuracy study. Source onset and publication use WPF composition events, not
physical monitor scanout. Timing thresholds are reported instead of asserted
because machine-dependent CI timing is not a reliable correctness assertion.

See [the latency report](../../../docs/OFFLINE_SUBTITLE_LATENCY.md) for commands,
measurements, attribution limits and real-world verification still required.

The additional lifecycle integration test exercises production `ScreenCaptureService`
stop/restart, minimized-window loss, recovery on the same selected target, and
idempotent disposal. It supplies a fixture-only source selector because production
selection deliberately excludes windows belonging to Cortex's own process.

For an Update-19 cleanup comparison, compare the `upgraded` sections from runs at
`117b32d` and the cleaned checkout. The `baseline` section in each JSON remains
Update-17; it must not be mislabeled Update-19.
