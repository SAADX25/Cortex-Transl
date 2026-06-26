# Cortex Transl

Cortex Transl is a Windows desktop MVP for translating dialogue from offline, single-player story games using screen capture only.

## Scope

- WPF desktop UI on .NET 10+
- User-selected screen region capture
- Windows OCR implementation behind an OCR abstraction
- Placeholder Arabic translation provider behind a translation abstraction
- SQLite cache for successful translations and game profiles
- Transparent topmost overlay
- Global `F8` hotkey for capture and translate

## Safety Boundaries

This project does not inject code, hook game processes, modify game files, bypass anti-cheat systems, or inspect game memory. It only captures the user-selected rectangle of the screen.

## Build

Install the .NET 10 SDK or newer, then run:

```powershell
dotnet restore
dotnet build
```

The runtime database is stored under `%LOCALAPPDATA%\Cortex Transl\cortex-transl.db`.
