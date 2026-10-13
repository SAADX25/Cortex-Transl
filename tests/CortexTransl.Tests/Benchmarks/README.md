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
