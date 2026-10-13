# Capture checks

Run `dotnet run --project tools/CaptureChecks -c Release` on Windows.
The tool uses a software D3D11 WARP device to check WinRT texture interop,
known offline menu terminology without network access, wrapped OCR bounds,
and game-scoped offline cache identity. It uses direct internal contracts,
so stale method names fail at build time rather than at reflection time.

This does not test NVIDIA recording, real window capture, or translation latency.
Use the opt-in tests in `tests/CortexTransl.Tests/Benchmarks` on an interactive
desktop for real WGC/OCR/Bergamot/overlay validation. Overlay placement and
privacy/lifecycle contracts are covered by the regression suite.
