# CotL Mod Manager

Small Windows (.NET 8 WinForms) mod manager for Cult of the Lamb BepInEx plugins.

- Lists installed plugins with their BepInPlugin / BepInDependency metadata (read via System.Reflection.Metadata, no loading)
- Enable / disable mods (disabled mods move to `BepInEx\plugins_disabled\`)
- Installs mods from archives (extracted with Windows `tar.exe`)
- Game path is stored in `%APPDATA%\CotLModManager\settings.json`

## Build
```bat
build.bat
```
Produces `CotLModManager.exe` in the project folder (requires the .NET 8 SDK; the exe needs the .NET 8 Desktop Runtime).

Headless test: `CotLModManager.exe --test <gameDir> <report.txt> [sources...]`

## Workflow
Edit here, commit, then `.\deploy.ps1` to build and mirror into `%USERPROFILE%\source\CotLModManager`.
