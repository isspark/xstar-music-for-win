---
name: xstar-music
description: Build, run, test, and package the XSTAR Music for Windows WPF app (.NET 8). Use when asked to 编译/运行/测试/打包 this project, or when the task mentions Xstar.Music.sln, dotnet build/test/run/publish, installer.iss, Inno Setup, or dist\XstarMusic-Setup-*.exe.
---

# XSTAR Music for Windows — build, run, test, package

WPF music player. Solution `Xstar.Music.sln` at the repo root:
- `src/Xstar.Music.App` — WPF app (`net8.0-windows`, BASS + WPF-UI)
- `src/Xstar.Music.Core` — library (`net8.0`)
- `tests/Xstar.Music.Tests` — xUnit

Shell is **Windows PowerShell 5.1**. Run all commands from the repo root.
Do not use `&&`; chain with `cmd1; if ($?) { cmd2 }`.

## Build

```powershell
dotnet build .\Xstar.Music.sln
```

.NET 8 SDK (8.0.4xx) is on PATH. Expected result: **0 errors**. There are a few
**pre-existing** nullable warnings (`CS8602` around `MainWindow.xaml.cs` scan-progress
code, `CS8604` in `MusicDatabase.cs`) — ignore them.

## Test

```powershell
dotnet test .\Xstar.Music.sln
```

All tests must pass (currently 26). Provider tests use stub `HttpMessageHandler`s,
so no network is required.

## Run (development)

A running instance **locks** the Debug output DLLs and will make the next build fail
(`MSB3027/MSB3021: file is being used by another process`). Always stop it first:

```powershell
Get-Process -Name "Xstar.Music.App" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\Xstar.Music.sln
```

Then either:

```powershell
dotnet run --project .\src\Xstar.Music.App\Xstar.Music.App.csproj
```

or start the built exe and smoke-test that it stays alive:

```powershell
$exe = ".\src\Xstar.Music.App\bin\Debug\net8.0-windows\Xstar.Music.App.exe"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 7
if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) { "RUNNING PID=$($p.Id)" } else { "EXITED" }
```

The app is a GUI; it never exits on its own. Leave it running only if the user asked
to launch it, otherwise stop it when done with `Stop-Process -Force`.

## Package the installer

Inno Setup is required. If `ISCC.exe` is missing:

```powershell
winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
```

`installer.iss` expects a **self-contained** publish in `dist\publish` and packages it:

```powershell
Remove-Item -Recurse -Force .\dist\publish -ErrorAction SilentlyContinue
dotnet publish .\src\Xstar.Music.App\Xstar.Music.App.csproj -c Release -r win-x64 --self-contained true -o .\dist\publish
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "installer.iss"
```

Output: `dist\XstarMusic-Setup-<MyAppVersion>.exe`.

Notes:
- The installer uses `compiler:Languages\ChineseSimplified.isl`. Inno Setup 6.x does
  **not** ship it; if compilation fails with "Couldn't open include file", place the
  file in `<Inno Setup dir>\Languages\ChineseSimplified.isl`.
- Bump the version in **three** places: `installer.iss` (`#define MyAppVersion`),
  `src/Xstar.Music.App/Xstar.Music.App.csproj` and
  `src/Xstar.Music.Core/Xstar.Music.Core.csproj` (`<Version>`).
- The published exe is self-contained (bundles .NET 8 + BASS native DLLs), so the
  target machine needs no runtime.

## Architecture quick map

- `src/Xstar.Music.Core/Online/` — online music providers (`IOnlineMusicProvider`,
  `OnlineMusicService`, `GdStudioProvider`, `AudiusProvider`, `JamendoProvider`) and
  `OnlineHttp` (forces IPv4; the CDNs hang on broken IPv6 otherwise).
- `src/Xstar.Music.App/MainWindow.xaml(.cs)` — all UI + wiring (library overlay,
  online search/stream/download, settings).
- `src/Xstar.Music.App/BassPlayerEngine.cs` / `MediaPlayerEngine.cs` — playback engines;
  `Load` returns `bool` so callers can fall back to a cached local copy.
- Settings persist to `%APPDATA%\XstarMusic\settings.json`; the SQLite DB is
  `%APPDATA%\XstarMusic\xstar.db`.
