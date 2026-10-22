# FastClip

A tiny, low-overhead replay-buffer clipper for Windows. Press a hotkey (default **F24**) to save the last 2 minutes of your screen and audio.

## Why it's light
- **Capture:** DXGI Desktop Duplication (`ddagrab`). Frames stay on the GPU.
- **Encode:** hardware H.264 (NVENC, AMF or QuickSync), with x264 as a fallback.
- **Buffer:** a fixed ring of 2-second segments on disk that never grows.
- **Save:** stream-copy concat with no re-encode, so it's near instant.
- **Game audio:** WASAPI loopback of any output device (e.g. your audio interface), plus an optional mic.
- **Priority:** the recorder gets raised GPU scheduling priority so a game at 100% GPU doesn't starve capture.

## Requirements
- Windows 10/11
- [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) on PATH (`winget install Gyan.FFmpeg`) or `ffmpeg.exe` next to `FastClip.exe`

## Build
Run `build.bat`. It uses the C# compiler that ships with Windows, so no SDK is needed. The output is `FastClip.exe`.

## Use
Run `FastClip.exe`. The window has these controls:
- **Save clip** saves the buffered clip. The hotkey does the same in-game.
- **Stop/Start recording** pauses or resumes the buffer.
- **Settings** cover clip length, hotkey, FPS, bitrate, monitor, encoder, game audio, mic and output folder.
- **Start with Windows** launches FastClip minimized to the tray at login.

Clips go to `Videos\Clips` by default. Settings are stored in `settings.ini` next to the exe.

`FastClip.ps1` is the original script version. It needs no compile step.
