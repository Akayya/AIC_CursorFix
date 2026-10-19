#Cursor Fix

**English** | [简体中文](README.md)

A **BepInEx 5** plugin for *Alice in Cradle* that keeps the mouse pointer visible while the BetterExperience configuration GUI (F1) is open.

Current version **v1.2.0** (built 2026-10-17).

---

## The problem

Pressing F1 opens the BetterExperience GUI, and the mouse pointer disappears — you have to navigate the window blind.

## Root cause (confirmed by decompiling the game)

The static class `XX.CURS` in `AliceInCradle_Data\Managed\unsafeAssem.dll` is the game's own cursor system. While you play, it sets `Cursor.visible = false` to hide the OS cursor and instead draws a pointer itself, in world space, using a mesh (`MeshDrawer` / `RenderTexture`).

The mod GUI, however, is IMGUI: it is drawn on top of everything, covering that mesh pointer. And since the OS cursor is hidden, the result is no pointer at all.

Verified: neither `BetterExperience.dll` nor `UnityModBase.dll` contains a single `UnityEngine.Cursor` call, and every cursor write in the game lives inside `XX.CURS` (zero in `Assembly-CSharp.dll`). So this is not a BetterExperience bug — it is the game's own cursor fighting an IMGUI overlay.

## How the plugin works

1. It watches UnityModBase's `GuiHostBase` (the base class of the F1 config window, the F2 log window and the F3 control window) to decide whether a mod window is currently shown.
2. While a window is visible it calls the game's own helper `XX.CURS.setTemporaryNormalMouse(true)` — the switch the game already provides for situations that need a normal mouse. It performs `Cursor.SetCursor(null)` + `Cursor.visible = true` and hides the self-drawn mesh cursor.
3. Because the game re-hides the cursor from `fine()` / `fineMouse2()` / `fineCursorVisible()`, the plugin re-asserts its state after those methods run, in every `LateUpdate`, and once more at the end of every frame (`WaitForEndOfFrame`).
4. When the window closes it restores the `Cursor.visible` / `lockState` values and the mesh cursor state that were in effect before opening, so the in-game pointer is drawn by the game again.

## Requirements

- BepInEx 5.4.x (tested with 5.4.23.5, game built on Unity 2022.3.62)
- BetterExperience installed (the plugin declares a hard dependency on `com.buele.betterexperience`, which guarantees UnityModBase loads first)

## Installation

Extract the `BepInEx` folder over your game root so that you end up with:

```
<game root>\BepInEx\plugins\CursorFix.dll
<game root>\BepInEx\config\aic.cursorfix.cfg
```

The config file is optional — it is generated on first launch. Then start the game and press F1. A ready-to-extract zip is available on the [Releases](../../releases) page.

> **Upgrading from the old file name**: before the rename, the plugin shipped as `BetterExperienceCursorFix.dll` with `aic.betterexperience.cursorfix.cfg` (GUID `aic.betterexperience.cursorfix`). Delete those two old files first, otherwise both copies load at the same time.

## Configuration

File: `BepInEx\config\aic.cursorfix.cfg`

| Option | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Master switch. Set to `false` to disable the plugin's behaviour. |
| `AlsoUnlockCursor` | `true` | Also force `Cursor.lockState = None` while the GUI is open. |
| `HideGameCursor` | `true` | Also hide the game's own mesh pointer while the GUI is open, so you do not see two pointers. |

## Verifying the log

Search `BepInEx\LogOutput.log` for `CursorFix`:

```
CursorFix: patches installed = 13 (guiHost=7, gameCursor=6)  patches applied
CursorFix: GUI opened (visible=False, ... window=UnityModBase.HConfigGUI.GuiHost)
                                                             GUI detected
CursorFix: override on -> cursor.visible=True                cursor taken over
CursorFix: GUI closed (...)                                  original state restored
```

## Uninstalling

Delete `BepInEx\plugins\CursorFix.dll`. The plugin never modifies the game or any other mod's DLL, so nothing else is left behind.

## Building from source

The plugin is C# 5 and can be built with the .NET Framework 4 `csc.exe` that ships with Windows — no Unity project needed.

```powershell
# src/CursorFix.cs   plugin source
# src/build.ps1      build script; -Root points at your game root
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\src\build.ps1 -Root "D:\Games\AliceInCradle_ver030"
```

The script references `BepInEx.dll` / `0Harmony.dll` from `<Root>\BepInEx\core` and the Unity assemblies from `<Root>\AliceInCradle_Data\Managed`, and writes `CursorFix.dll` into `<Root>\BepInEx\plugins`.

## Known unrelated issue

`UnityModBase\UnityModBase.cfg` ships with `ConfigGuiHeight = 1008`, which is larger than the window client area on a 1080p display (about 997), so the bottom of the config window gets clipped. Lower it to around 900 to see the whole window.

## License

No license file is included in this repository. Please ask the author before redistributing.
