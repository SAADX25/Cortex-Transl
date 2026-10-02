<div align="center">

# 🎮 Cortex Transl

**Real-time game dialogue translation overlay for Windows — fully offline**

[![Version](https://img.shields.io/badge/version-1.5.0-6366f1?style=for-the-badge)](https://github.com/SAADX25/Cortex-Transl/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=for-the-badge&logo=windows)](https://www.microsoft.com/windows)
[![Framework](https://img.shields.io/badge/.NET-10.0-512bd4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Private-ff4444?style=for-the-badge)](#)

---

*Cortex Transl captures in-game dialogue text from the screen, translates it to Arabic in real time, and displays it directly over the game — no file patching, no API key required.*

</div>

---

## ✨ Features

| Feature | Details |
|---------|---------|
| 🌐 **Offline Translation** | Bergamot Arabic model runs fully locally — no internet needed |
| 🔍 **Smart OCR** | Reads on-screen text via the Windows OCR Engine |
| 🪟 **Transparent Overlay** | Translation is displayed directly on top of the game window |
| ⚡ **Fast & Responsive** | Detects frame changes before re-translating to avoid lag |
| 💾 **Smart Cache** | Saves translations in SQLite to skip repeated lookups |
| 🎨 **Customizable** | Font size, color, opacity, and overlay position |
| 🧪 **TEST Mode** | Evaluate translation quality with per-chunk analysis |
| 📋 **Presets** | Save a game setup and load it with one click |
| 🔑 **DeepL (optional)** | Context-aware Arabic for story dialogue and game menus |

---

## 🚀 Quick Start

```
1. Launch the app — the Arabic model downloads once (~30 MB)
2. Run your game in Borderless Windowed mode
3. Press F9 and drag around the dialogue box
4. Press F8 or click Start to begin translating

For offline translation, choose the game's language explicitly. DeepL can detect the source language automatically.
```

Offline English menus use Arabic UI terminology for common game commands and desktop labels, and preserve known product names such as Steam and Brave. Known captions wrapped across two or three lines are translated as one label. Unknown labels still use the offline translation model; this does not guarantee accurate terminology for every game. Menu results use a separate cache version so earlier literal translations are not reused.

The translation overlay is recordable, including by NVIDIA recording. Text is captured from the underlying window without Windows capture-protection flags. Desktop icons also support direct capture from Explorer's icon view, keeping translated labels over the original names. Menu mode never moves labels away from their source: if direct capture is unavailable, it retains the last translation and reports a capture error. Dialogue mode can use a desktop fallback that places the translation beside the selected region without repeatedly hiding it. Direct capture restores the original placement when it becomes available again.

### ⌨️ Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `F8` | Start / Stop translation |
| `F9` | Select dialogue region |
| `F10` | Show / Hide the app window |

These shortcuts require the function key alone. Modified combinations such as NVIDIA's Alt+F9 do not run Cortex's region-selection handler.

---

## 🏗️ Project Structure

```
Cortex Transl/
│
├── 📄 CortexTransl.sln                 # Solution file
├── 📄 pack.ps1                          # Installer build script
├── 📄 README.md
│
├── 📁 src/
│   └── 📁 CortexTransl.App/             # Main project (WPF / .NET 10)
│       │
│       ├── 📁 Data/                     # Database layer (SQLite)
│       │   ├── DatabaseMigrator.cs      # DB initialization and migrations
│       │   └── SqliteConnectionFactory.cs
│       │
│       ├── 📁 Models/                   # Data models
│       │   ├── AppSettings.cs           # Application settings
│       │   ├── CaptureRegion.cs         # Screen capture region
│       │   ├── GameProfile.cs           # Game preset data
│       │   ├── OcrTextBlock.cs          # OCR text block
│       │   ├── OverlaySettings.cs       # Overlay display settings
│       │   ├── PipelineResult.cs        # Translation cycle result
│       │   ├── TestChunkResult.cs       # Per-chunk test result
│       │   └── TranslatedBlock.cs       # Translated text block
│       │
│       ├── 📁 Services/                 # Service layer
│       │   │
│       │   ├── 📁 Cache/                # Translation cache
│       │   │   ├── ITranslationCacheRepository.cs
│       │   │   └── SqliteTranslationCacheRepository.cs
│       │   │
│       │   ├── 📁 Capture/              # Screen capture & processing
│       │   │   ├── TranslationPipeline.cs            # Main orchestrator (OCR→Translate→Display)
│       │   │   ├── ScreenCaptureService.cs            # Screen capture service
│       │   │   ├── WindowsGraphicsWindowCapturer.cs   # Direct game-window capture
│       │   │   ├── CaptureWindowTarget.cs             # Find the window below the overlay
│       │   │   ├── GdiScreenCapture.cs                # GDI fallback
│       │   │   ├── RegionSelectionService.cs          # Capture region picker
│       │   │   ├── ScreenCoordinates.cs               # DPI/coordinate helpers
│       │   │   └── ImageFingerprint.cs                # Frame-change detection
│       │   │
│       │   ├── 📁 Hotkeys/              # Global keyboard shortcuts
│       │   │
│       │   ├── 📁 Ocr/                  # Text recognition engine
│       │   │   ├── IOcrEngine.cs
│       │   │   ├── WindowsOcrEngine.cs          # Windows.Media.Ocr wrapper
│       │   │   └── OcrImagePreprocessor.cs      # Pre-processing before OCR
│       │   │
│       │   ├── 📁 Overlay/              # Transparent overlay window
│       │   │   ├── IOverlayService.cs
│       │   │   └── OverlayService.cs
│       │   │
│       │   ├── 📁 Profiles/             # Game preset management
│       │   │   ├── IGameProfileRepository.cs
│       │   │   └── SqliteGameProfileRepository.cs
│       │   │
│       │   ├── 📁 Settings/             # App settings & theming
│       │   │   ├── AppSettingsService.cs
│       │   │   └── ThemeService.cs
│       │   │
│       │   └── 📁 Translation/          # Translation providers
│       │       ├── ITranslationProvider.cs
│       │       ├── OfflineBergamotTranslationProvider.cs   # Bergamot (offline)
│       │       ├── DeepLTranslationProvider.cs             # DeepL API
│       │       ├── RoutingTranslationProvider.cs           # Provider selector
│       │       ├── OfflineModelInstaller.cs                # Arabic model downloader
│       │       ├── TranslationContentKind.cs               # Dialogue/menu translation context
│       │       └── TranslationProviderSettings.cs
│       │
│       ├── 📁 Utils/                    # Helper utilities
│       │   ├── DialogueTextAssembler.cs     # Assembles dialogue text blocks
│       │   ├── TranslationChunker.cs        # Splits long text into chunks
│       │   ├── TextSimilarity.cs            # Detects repeated/unchanged text
│       │   ├── TextNormalizer.cs            # Cleans raw OCR output
│       │   ├── TextHasher.cs                # SHA-256 for cache keys
│       │   ├── AsyncRelayCommand.cs         # Async MVVM command
│       │   ├── RelayCommand.cs              # MVVM command
│       │   └── ObservableObject.cs          # INotifyPropertyChanged base
│       │
│       ├── 📁 ViewModels/
│       │   └── PlayViewModel.cs             # Main ViewModel (MVVM)
│       │
│       └── 📁 Views/                    # WPF XAML views
│           ├── MainWindow.xaml              # Main application window
│           ├── OverlayWindow.xaml           # In-game overlay window
│           ├── RegionSelectorWindow.xaml    # Region selection window
│           └── 📁 Styles/                  # WPF styles & resource dictionaries
│
└── 📁 installer/
    └── CortexTransl.iss                 # Inno Setup 6 installer script
```

---

## 🔄 How It Works

```
┌──────────────┐    ┌──────────────┐    ┌──────────────────────┐
│    Screen    │───▶│  Windows OCR │───▶│  DialogueTextAssemb  │
│   Capture    │    │   Engine     │    │  ler (text assembly) │
└──────────────┘    └──────────────┘    └──────────────────────┘
                                                    │
                                                    ▼
                                        ┌───────────────────────┐
                                        │   Translation Cache   │
                                        │       (SQLite)        │
                                        └──────────┬────────────┘
                                        HIT ◀──────┘  MISS
                                                       │
                                                       ▼
                                        ┌───────────────────────┐
                                        │  TranslationChunker   │
                                        │   (split long text)   │
                                        └──────────┬────────────┘
                                                   │
                                        ┌──────────▼────────────┐
                                        │  Bergamot (offline)   │◀── en-ar model
                                        │    or DeepL API       │
                                        └──────────┬────────────┘
                                                   │
                                                   ▼
                                        ┌───────────────────────┐
                                        │    Overlay Window     │
                                        │  (displayed in-game)  │
                                        └───────────────────────┘
```

---

## ⚙️ System Requirements

- **OS:** Windows 10 (Build 19041+) or Windows 11
- **Architecture:** x64
- **.NET:** Bundled with the self-contained installer — no separate install needed
- **Internet:** Required once to download the Arabic translation model (~30 MB)

---

## 🛠️ Build & Development

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) *(installer only)*
- Windows 10/11 x64

### Run in development

```powershell
git clone https://github.com/SAADX25/Cortex-Transl.git
cd "Cortex Transl"
dotnet restore
dotnet run --project src/CortexTransl.App
```

### Build the installer (v1.5.0)

```powershell
# Publishes, packages, and compiles Setup.exe
powershell -ExecutionPolicy Bypass -File pack.ps1
# Output: dist\CortexTransl-1.5.0-Setup.exe
```

### Publish without installer

```powershell
dotnet publish src/CortexTransl.App/CortexTransl.App.csproj -c Release -p:PublishProfile=Win64
# Output: dist\win-x64\
```

> ⚠️ **Note:** Do not use Single File publish — `bergamot.dll` must remain alongside the executable.

---

## 📦 NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [`BergamotTranslatorSharp`](https://github.com/mozilla/bergamot-translator) | 0.5.1 | Offline translation engine |
| [`Microsoft.Data.Sqlite`](https://docs.microsoft.com/en-us/dotnet/standard/data/sqlite/) | 10.0.9 | Cache & settings storage |
| [`SQLitePCLRaw.bundle_e_sqlite3`](https://github.com/ericsink/SQLitePCL.raw) | 3.0.3 | Native SQLite provider |
| [`Vortice.Direct3D11`](https://github.com/amerkoleci/Vortice.Windows) | 3.8.3 | Windows Graphics Capture API |

---

## 📂 App Data Location

The app stores its data at:
```
%LOCALAPPDATA%\Cortex Transl\
├── models\en-ar\                    # Arabic translation model files
│   ├── config.yml
│   ├── model.enar.intgemm.alphas.bin
│   ├── lex.50.50.enar.s2t.bin
│   └── vocab.enar.spm
├── cortex.db                        # Cache + settings + presets database
└── app.log                          # Application log
```

---

<div align="center">

Built with ❤️ for Arabic gamers — **Cortex Transl v1.5.0**

</div>
