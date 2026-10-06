# Screen Recorder

A small, reliable Windows 11 desktop app that records a whole monitor or a
rectangular region to MP4, with optional system sound and microphone. See
[PLAN.md](PLAN.md) for the full development plan.

- **Capture:** full monitor or a region on one monitor; the target is outlined
  on screen before recording starts.
- **Audio:** independent **System sound** and **Microphone** toggles (all four
  modes); a microphone picker appears only when more than one mic is connected.
- **Controls:** Start, Pause/Resume, Stop, plus a floating bar (excluded
  from the recording) with elapsed time, a mic mute toggle, and
  pause/stop buttons.
- **Output:** H.264 MP4 (+ one AAC track when audio is on) in a folder you pick
  (default `Videos\Screen Recordings`).
- **Saving:** after Stop, a saving screen (and taskbar progress) shows each
  stage until the file is flushed to disk; only then does it say *Saved*.
  Recording stops and saves on its own if the save drive is nearly full.
- **Settings:** frame rate (30/60) and mouse cursor on/off. That's all.

## Requirements

- Windows 11 (x64 or ARM64) to run.
- [.NET 10 SDK](https://aka.ms/dotnet/download) to build.

## Build, test, run

```powershell
dotnet build ScreenRecorder.sln
dotnet test ScreenRecorder.sln
dotnet run --project src/ScreenRecorder
```

Publish a self-contained single file (runs on a clean Windows 11 machine):

```powershell
dotnet publish src/ScreenRecorder -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/ScreenRecorder -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true
```

## Layout

```
ScreenRecorder.sln
├─ src/ScreenRecorder/
│  ├─ UI/            MainWindow, region selector, selection outline, recording bar
│  ├─ Capture/       MonitorService, CaptureTarget, WGC FrameSource, D3D interop
│  ├─ Audio/         WASAPI sources, clock-driven mixer, device detection
│  ├─ Encoding/      MediaStreamSource + MediaTranscoder MP4 encoder
│  ├─ Recording/     Recorder state machine, shared RecordingClock
│  └─ Infrastructure/ settings (JSON), log, window helpers
└─ tests/ScreenRecorder.Tests/   xUnit: clock, mixer, converter, targets,
                                 monitor ordering, mic UI, bitrate, state machine
```

Settings live at `%LOCALAPPDATA%\ScreenRecorder\settings.json`, logs at
`%LOCALAPPDATA%\ScreenRecorder\logs\` (last 5 kept).

## Notes and limitations

- **DRM-protected video** (Netflix, some protected browser playback) records as
  black; its audio may or may not be captured, depending on the app.
- **The secure desktop** (UAC prompts, lock screen) is not captured; the video
  shows a frozen frame for that period.
- **Bluetooth headsets:** opening a Bluetooth mic switches the headset to the
  hands-free profile, so playback (and the recorded system sound) drops to
  low-quality mono while recording. Use a separate or built-in mic for the
  best system-sound quality.
- **Echo:** with system sound + microphone both on and speakers (not
  headphones) in use, the mic picks up the speakers. There is no echo
  cancellation in v1 — headphones are recommended.
- **OneDrive:** the Videos folder may be redirected to OneDrive, which then
  syncs large recordings. It works, but be aware of the upload.
- **HDR displays:** capture is BGRA8; HDR content may look washed out.
- **Crash mid-recording:** the `.mp4.part` file is unplayable, because standard
  MP4 writes its index at the end.
- Apps playing in WASAPI **exclusive mode** (rare) are not captured by
  system-sound loopback, and a mic held exclusively by another app can't be
  opened.
- Screen capture is unavailable in some **VMs and remote sessions**; the app
  disables recording and explains why.
- Unsigned builds trigger Windows **SmartScreen** ("Windows protected your
  PC"). Code-sign releases if possible.

## Implementation notes (deviations from PLAN.md)

- `AudioSource` asks for 48 kHz float stereo and falls back to the device mix
  format with in-code conversion. The `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM`
  optimization was not forced through NAudio's fixed stream flags; conversion
  in code always works.
- Format fallback uses a small built-in linear resampler
  (`AudioFormatConverter`, unit-tested) instead of NAudio's `WdlResampler`,
  keeping the conversion pure and testable.
- Full-screen frames also go through the GPU crop copy (uniform path, negligible
  cost) instead of skipping the copy; this keeps frame lifetimes simple with a
  2-buffer pool.
