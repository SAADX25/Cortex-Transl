# Cortex Transl

**Cortex Transl** is a Windows desktop application for real-time screen translation. It is designed for games, visual novels, videos, and dense in-game UI panels where text appears on screen.

The app is screen-capture based only. It does not inject into games, hook game processes, click game UI, automate gameplay, or interact with the game.

## Translation Modes

### Subtitle Mode

Subtitle Mode is the default mode. It is best for:

- Dialogue
- Cutscenes
- Visual novels
- Videos
- Repeating subtitle areas

In this mode, Cortex Transl captures the selected dialogue region, translates the detected text, and shows the result in a stable subtitle-style overlay.

### Menu / Screen Mode

Menu / Screen Mode is designed for dense game UI text, including:

- Settings windows
- Inventory panels
- Quest logs
- Skill descriptions
- Option menus
- MMORPG interface panels

In this mode, Cortex Transl reads the whole selected region, detects multiple OCR lines, translates the lines together in a batch, and shows a structured side-by-side result with original text and Arabic translations.

## Features

- **Screen-capture only:** No DLL injection, DirectX hooks, process hooks, anti-cheat bypassing, game UI clicking, or gameplay automation.
- **Simple Mode:** Translation mode, theme, provider status, region selection, and the right action for the current mode.
- **Advanced Mode:** OCR preset, language/provider controls, overlay settings, profiles, timing metrics, and debug details.
- **Small Text OCR Presets:** Normal, Small Text, and High Contrast Text preprocessing for tiny or low-contrast UI text.
- **Batch Menu Translation:** Menu lines are sent together when possible instead of one request per line.
- **Line Cache:** Repeated menu text can load from cache instead of being translated again.
- **DeepL Support:** Uses a user-provided DeepL API key, stored locally with Windows DPAPI encryption.
- **Dark First Launch:** New installs start in Dark mode. Saved user theme choices are respected.

## Quick Start

### Subtitle Mode

1. Choose **Subtitle Mode**.
2. Press `F9` and select the dialogue/subtitle area.
3. Press `F8` to start auto translation.
4. Press `F8` again to stop auto translation and hide the overlay.

### Menu / Screen Mode

1. Choose **Menu / Screen Mode**.
2. Press `F9` and select the whole game menu or panel.
3. Press `F10` or **Translate Screen Region**.
4. Read the translated menu lines in the result panel.

For the best overlay experience in Subtitle Mode, run games in borderless windowed mode when possible.

## Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| `F8` | Toggle Auto Translate in Subtitle Mode |
| `F9` | Select Region |
| `F10` | Capture Once / Translate Region |

## Requirements

- Windows 10 or Windows 11
- .NET 10 Runtime or newer
- DeepL API key for DeepL translations

## Building From Source

Install the .NET 10 SDK or newer, then run:

```powershell
git clone https://github.com/your-username/Cortex-Transl.git
cd Cortex-Transl
dotnet restore
dotnet build
```

From this repository, you can also run:

```powershell
.\Cortex_Dev.bat
```

The development script restores packages, builds Debug, and starts the app.

## Troubleshooting

### The Overlay Flickers Or Does Not Appear During Recording

Use **Recording Compatibility Mode** in Advanced Mode overlay settings. If needed, capture the entire desktop instead of a specific game window.

### Region Selection Does Not Work In A Game

If the game uses exclusive fullscreen, choose **Fullscreen Game** in the setup wizard or use **Menu / Screen Mode**. Cortex Transl will use screenshot-based region selection for those scenarios.

### Small Menu Text Is Missed

Switch to **Menu / Screen Mode** and use the **Small Text** or **High Contrast Text** OCR preset in Advanced Mode.

## Privacy And Safety

Cortex Transl is screen-capture based only and does not interact with the game.

DeepL API keys are stored locally and encrypted with Windows DPAPI.
