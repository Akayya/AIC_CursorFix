#Cursor Fix

[English](README.en.md) | 简体中文

爱丽丝与摇篮（Alice in Cradle）的 **BepInEx 5** 插件：按 F1 打开 BetterExperience 配置界面后，鼠标指针不再消失。

当前版本 **v1.2.0**（构建日期 2026-10-17）。

---

## 解决的问题

按 F1 打开 BetterExperience 配置界面后，鼠标指针消失，只能盲操作。

## 原因（已通过反编译确认）

游戏本体 `AliceInCradle_Data\Managed\unsafeAssem.dll` 里的静态类 `XX.CURS` 是游戏自己的光标系统：游戏进行时它会执行 `Cursor.visible = false` 把系统光标藏起来，改为自己在世界空间用网格（`MeshDrawer` / `RenderTexture`）绘制一个指针。

而 mod 的配置界面是 IMGUI，绘制在所有内容之上，把那个网格指针挡住了；系统光标又处于隐藏状态，于是画面上看不到任何指针。

已核查：`BetterExperience.dll` 与 `UnityModBase.dll` 中没有任何 `UnityEngine.Cursor` 调用；整个游戏里所有光标写入点都在 `XX.CURS` 内（`Assembly-CSharp.dll` 里 0 处）。所以这不是 BetterExperience 的 bug，而是游戏光标与 IMGUI 窗口的叠加问题。

## 本插件的做法

1. 监听 UnityModBase 的 `GuiHostBase`（F1 配置界面、F2 日志界面、F3 控制界面都继承它），判定「是否有 mod 窗口正在显示」。
2. 窗口显示时调用游戏自带的开关 `XX.CURS.setTemporaryNormalMouse(true)`：该函数本来就是游戏给「需要正常鼠标」的场合用的，会 `Cursor.SetCursor(null)` + `Cursor.visible = true` + 隐藏自绘的网格光标。
3. 因为游戏会在 `fine()` / `fineMouse2()` / `fineCursorVisible()` 里反复把光标藏回去，插件在这些方法执行后、每帧 `LateUpdate`、以及每帧结束时（`WaitForEndOfFrame`）各确认一次，确保指针一直在。
4. 窗口关闭时恢复打开前的 `Cursor.visible` / `lockState` 和网格光标状态，游戏内指针照旧由游戏自己绘制。

## 安装要求

- BepInEx 5.4.x（已在 5.4.23.5 验证，游戏为 Unity 2022.3.62）
- 已安装 BetterExperience（本插件声明了对 `com.buele.betterexperience` 的硬依赖，因此 UnityModBase 一定先加载）

## 安装

把 `BepInEx` 文件夹解压并覆盖到游戏根目录，得到：

```
<游戏根目录>\BepInEx\plugins\BetterExperienceCursorFix.dll
<游戏根目录>\BepInEx\config\aic.betterexperience.cursorfix.cfg
```

（cfg 可选，首次运行会自动生成）

启动游戏，按 F1 验证即可。也可以直接在 [Releases](../../releases) 下载打包好的 zip。

## 配置

文件：`BepInEx\config\aic.betterexperience.cursorfix.cfg`

| 配置项 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 总开关。改成 `false` 等于停用本插件功能。 |
| `AlsoUnlockCursor` | `true` | 打开界面时同时把 `Cursor.lockState` 设为 `None`。 |
| `HideGameCursor` | `true` | 打开界面时同时隐藏游戏自绘的网格指针，避免界面上出现两个指针。 |

## 日志确认

`BepInEx\LogOutput.log` 里搜索 `CursorFix`：

```
CursorFix: patches installed = 13 (guiHost=7, gameCursor=6)  挂钩成功
CursorFix: GUI opened (visible=False, ... window=UnityModBase.HConfigGUI.GuiHost)
                                                             检测到界面打开
CursorFix: override on -> cursor.visible=True                已接管光标
CursorFix: GUI closed (...)                                  已恢复原状态
```

## 卸载

删除 `BepInEx\plugins\BetterExperienceCursorFix.dll` 即可。本插件不修改游戏或任何现有 mod 的 DLL，不会留下残留。

## 从源码编译

插件源码是 C# 5，可用系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 编译，无需 Unity 工程。

```powershell
# src/CursorFix.cs   插件源码
# src/build.ps1      构建脚本，-Root 指定游戏根目录
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\src\build.ps1 -Root "D:\Games\AliceInCradle_ver030"
```

构建脚本会引用 `<Root>\BepInEx\core` 下的 `BepInEx.dll` / `0Harmony.dll`，以及 `<Root>\AliceInCradle_Data\Managed` 下的 Unity 程序集，输出到 `<Root>\BepInEx\plugins\BetterExperienceCursorFix.dll`。

## 已知无关问题

`UnityModBase\UnityModBase.cfg` 里 `ConfigGuiHeight = 1008` 超过 1080p 下窗口客户区高度（约 997），因此配置窗口底部会被裁掉。想完整显示可把该值改成 900 左右。

## 许可

仓库中未附带许可证文件；如需二次分发，请先与作者确认。
