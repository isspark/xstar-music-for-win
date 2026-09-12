# XSTAR Music for Windows

XSTAR Music for Windows is a local-first Windows music player.

## Current Status

The repository contains a working WPF application, verified by build and tests:

- .NET 8 solution with `Xstar.Music.App` (WPF), `Xstar.Music.Core` (library) and `Xstar.Music.Tests` (xUnit).
- Single-window dark UI with album background, song/artist text and compact playback controls.
- Folder persistence under `%APPDATA%\\XstarMusic\\settings.json`.
- Recursive audio file scanning for common formats, with incremental file-system watching.
- TagLib# metadata reading, embedded artwork cache and SQLite indexing (`xstar.db`).
- Playback via BASS/BASS.NET (`BassPlayerEngine`) with automatic fallback to WPF `MediaPlayer`; both implement `IPlayerEngine`.
- Favorites, play history and custom playlists stored in SQLite.
- Local `.lrc` loading plus online lyrics from LRCLIB with on-disk caching (`%APPDATA%\\XstarMusic\\lyrics`).
- Online search / download of free Creative Commons music via Jamendo (requires your own free `client_id`).
- Global media keys and a system tray icon (play/pause, previous, next, stop, exit).
- Folder settings dialog, keyword filtering, song/album/artist/favorites/history/playlist views and lyric style menu.
- Dark Mode (OLED) immersive UI: near-black surfaces, blurred now-playing artwork with per-quality glow, green play CTA on indigo accents, unified component styles and icon font.

Verified locally: `dotnet build .\Xstar.Music.sln` (0 warnings, 0 errors) and `dotnet test .\Xstar.Music.sln` (17/17 passing). The .NET 8.0.425 SDK is installed at `C:\Users\Mrxuz\.dotnet`.

### Native audio libraries

BASS native binaries live in `src/Xstar.Music.App/native/win-x64` (`bass.dll` plus FLAC/Opus/ALAC/APE add-ons) and are copied next to the executable on build. They are distributed under the BASS license (free for non-commercial use); see `native/win-x64/BASS-LICENSE.txt`. AAC/M4A/WMA use the Windows Media Foundation codecs provided by the OS.

### Online music configuration

Open the settings dialog and enter a Jamendo `client_id` (free registration at <https://developer.jamendo.com>). Without it, local playback, favorites, playlists and lyrics all still work; only online search is disabled.

## Build

With the .NET 8 SDK on `PATH`, run:

```powershell
dotnet build .\Xstar.Music.sln
dotnet test  .\Xstar.Music.sln
dotnet run   --project .\src\Xstar.Music.App\Xstar.Music.App.csproj
```

## Documentation

- [Requirements](docs/requirements.md)
- [Technical Design](docs/tech-design.md)
- [HTML UI Prototype](docs/ui-prototype.html)
- [Design System](design-system/xstar-music/MASTER.md)
