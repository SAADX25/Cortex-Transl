<h1 align="center">Cortex Transl</h1>

<p align="center">
  <strong>A smart, simple, and safe real-time screen translation assistant.</strong>
</p>

---

## 🌟 Overview

**Cortex Transl** is a Windows desktop application designed to translate dialogue from offline, single-player story games, visual novels, and videos in real-time. It acts as a smart gaming assistant that brings language accessibility to your favorite media without complicated setups or technical jargon.

## ✨ Key Features

- **Smart and Simple:** No need to understand OCR engines, cache debugging, or overlay render modes. The app guides you through a setup wizard and uses smart defaults based on what you are trying to translate.
- **Safe and Secure:** Cortex Transl does not use DLL injection, DirectX hooks, or game process hooks. It will not bypass anti-cheat systems. It operates purely on screen capture.
- **Setup Wizard:** First-time users are guided through selecting their usage type (Game, Visual Novel, Video, or Fullscreen Game), their translation provider, and selecting a region.
- **Simple Mode vs Advanced Mode:** 
  - **Simple Mode:** Focuses on what matters: selecting regions, starting translation, choosing translation quality, and adjusting basic overlay settings.
  - **Advanced Mode:** Unlocks OCR engine selection, advanced render modes, click-through overlay toggles, profile management, and detailed debug metrics.
- **Smart Region Selection:** The app automatically selects the best way to capture your screen depending on your chosen Usage Type (e.g., using a Screenshot Selector for exclusive fullscreen games).
- **Subtitles-like Overlay:** By default, the overlay acts like a locked subtitle bar at the bottom center of your screen, appearing and disappearing smoothly as translations run or stop.
- **DeepL Support:** Uses DeepL for high-quality translations (requires an API key).

## 🚀 Quick Start

1. **Launch the App:** Run Cortex Transl. The first time you launch it, a friendly Setup Wizard will help you configure the basics.
2. **Select Your Target:** Choose whether you're translating a Windowed Game, a Visual Novel, or a Fullscreen Game.
3. **Select Region (`F9`):** Draw a rectangle around the area where dialogue usually appears.
4. **Auto Translate (`F8`):** Press `F8` to start auto-translating. The app will capture the region and display translations on a transparent overlay at the bottom of your screen. 
5. **Stop Translating (`F8`):** Press `F8` again to hide the subtitles and stop the translation engine.

> **💡 Pro Tip:** For the best experience, we strongly recommend running your games in **Borderless Windowed** mode. This allows the transparent overlay to render smoothly on top of your game and ensures reliable live region selection.

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `F8` | Toggle Auto Translate On / Off |
| `F9` | Open Region Selector |
| `F10` | Run Capture Once (Single frame translation) |

## 🛠️ Requirements & Setup

- **OS:** Windows 10 or Windows 11
- **Runtime:** .NET 10 Runtime (or newer)
- **DeepL API Key:** To use DeepL translations, you must provide your own API key in the app's settings. Your key is securely encrypted using Windows DPAPI before being saved to your local machine.

### Building from Source

Install the .NET 10 SDK or newer, then run:

```powershell
git clone https://github.com/your-username/Cortex-Transl.git
cd Cortex-Transl
dotnet restore
dotnet build
```

Double-click `Cortex_Dev.bat` from the repository root to restore, build Debug, and run the app.

## ⚠️ Troubleshooting

**The overlay is flickering or invisible during recording!**
If NVIDIA ShadowPlay or OBS is making the overlay flicker, enable **Recording Compatibility Mode** in the Overlay settings. Alternatively, capture your entire desktop instead of a specific game window.

**The app can't select a region in my game!**
If your game forces exclusive fullscreen, make sure to select "Fullscreen Game" in the setup wizard or usage type. The app will automatically switch to "Screenshot Selector" mode to help you safely select a region without minimizing your game.

---
<p align="center">
  <i>Built to make games accessible to everyone.</i>
</p>