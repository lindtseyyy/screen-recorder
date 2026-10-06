# Minimal Screen Recorder for Windows 11: Development Plan

A small, reliable Windows 11 desktop app that records a whole monitor or a rectangular region to MP4, with optional system sound and microphone.
It has a Record button, Pause/Resume and Stop, two audio toggles, and a save folder. Nothing else.

---

## 0. Scope

**In scope (v1)**

| Area | Feature |
|---|---|
| Capture | Full monitor, or a rectangular region on one monitor |
| Monitors | Auto-detected. One monitor: *Full screen / Region*. Several: one tile per monitor plus *Region* |
| Preview | The selected area is outlined on screen before recording starts |
| Audio | Two independent toggles: **System sound** and **Microphone**. A microphone picker appears only when more than one microphone is connected |
| Modes | Video only · Video + system sound · Video + microphone · Video + system sound + microphone |
| Controls | Start, Pause/Resume, Stop, plus a small floating indicator with elapsed time and active audio icons |
| Output | H.264 MP4, with one AAC audio track when audio is enabled. Saved to a folder the user picks (default `Videos\Screen Recordings`) |
| Settings | Frame rate (30/60) and show mouse cursor (on/off). That's all |

**Out of scope on purpose.** Each of these is a common feature request, but none is needed for v1:

- Audio extras: per-source volume sliders, level meters, separate audio tracks, noise suppression or echo cancellation, per-app audio capture, choosing an output device other than the Windows default.
- Editing or trimming, webcam overlay, annotations, GIF export, streaming, cloud upload, accounts.
- Window capture, regions that span two monitors, scheduled recording, quality presets, configurable hotkeys.

---

## 1. Technology / framework

**Recommendation: C# on .NET 10 (LTS) with WPF, using WPF's built-in Fluent (Windows 11) theme.**

| Option | Verdict | Reason |
|---|---|---|
| **WPF + .NET 10, Fluent theme** | ✅ **Chosen** | Mature and stable. `ThemeMode="System"` gives the Windows 11 look (Mica, rounded corners, light/dark) with no extra dependency. Transparent, borderless, click-through overlay windows, which the region selector and outline need, are easy to build. Full access to WinRT capture/media APIs and Win32 interop. |
| WinUI 3 (Windows App SDK) | ❌ | Has the most native look, but transparent and click-through overlay windows are awkward. It also needs the Windows App SDK runtime or a heavier self-contained deployment. |
| C++/Win32 or C++/WinRT | ❌ for v1 | Lowest overhead, but development is much slower. C# reaches the same native APIs, and every heavy step runs on the GPU or in Windows audio components anyway. |
| Electron / Tauri / Python | ❌ | Electron is heavy. Python is hard to package. All of them still need native capture code underneath. |
| Shelling out to FFmpeg | ❌ | Adds 80–100 MB, licensing questions and an external process to babysit, and pause/resume is awkward. |

If a specific Fluent control turns out to be missing from the built-in theme, the fallback is **WPF-UI** (`lepoco/wpfui`, MIT).

**Dependencies** (kept to a minimum):

- `Microsoft.Windows.CsWin32`: build-time source generator for Win32 P/Invoke (no runtime cost).
- `Vortice.Direct3D11` (MIT): a thin D3D11 wrapper for the device, textures and copies.
- `NAudio.Wasapi` + `NAudio.Core` (MIT): WASAPI loopback and microphone capture, device enumeration, device-change notifications, and a fallback resampler.
- Test project only: `xunit`.

**Target framework:** `net10.0-windows10.0.26100.0`, with `SupportedOSPlatformVersion = 10.0.22000.0` (Windows 11).
Newer APIs, such as `MinUpdateInterval` on Windows 11 24H2 and later, are guarded with `ApiInformation` checks at runtime.

---

## 2. Capture and encoding APIs

| Job | API | Why |
|---|---|---|
| Capture a monitor | **Windows.Graphics.Capture (WGC)**, item created with `IGraphicsCaptureItemInterop::CreateForMonitor(HMONITOR)` | The modern, supported API. Frames arrive as GPU textures, it is DPI-correct, it handles rotation and hybrid GPUs, and it can capture the cursor. No picker dialog is needed. |
| Hide our own UI from the recording | `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` | The outline, indicator and main window stay visible to the user but are not recorded. |
| Crop to a region | `ID3D11DeviceContext::CopySubresourceRegion` | A GPU-only copy of the selected rectangle. |
| Capture system sound | **WASAPI loopback** (shared mode) on the default output device, through NAudio's `WasapiLoopbackCapture` | The standard way to record "what you hear". No driver, no virtual cable, no permission prompt. |
| Capture the microphone | **WASAPI capture** (shared mode) on the selected input device, through NAudio's `WasapiCapture` | Low latency, works with USB, built-in and Bluetooth microphones. |
| List microphones and react to plug/unplug | `IMMDeviceEnumerator` + `IMMNotificationClient` (NAudio's `MMDeviceEnumerator`) | Same device list Windows Sound settings uses. |
| Encode and write MP4 | **`MediaTranscoder` + `MediaStreamSource`** (Windows.Media, built on Media Foundation) | Hardware H.264 (NVENC / Quick Sync / AMF / Qualcomm) with automatic software fallback. Built-in AAC encoder. Accepts D3D11 surfaces directly and does BGRA→NV12 conversion and scaling internally. |

Not chosen:

- DXGI Desktop Duplication: older and more code, and it needs a second code path for different adapters.
- GDI `BitBlt`: CPU-bound, slow, and misses some content.
- FFmpeg (see §1).

**Main design rules:**

- Video pixels never leave the GPU. No CPU readback means low CPU use and low memory use.
- Audio is small (48 kHz stereo is about 384 KB/s of float data) and is handled on the CPU.

---

## 3. Detecting monitors

`MonitorService` enumerates monitors and returns a `MonitorInfo` list:

```
MonitorInfo { HMONITOR Handle; string DeviceName;   // "\\.\DISPLAY2"
              RECT Bounds;     // physical pixels, virtual-desktop coordinates (can be negative!)
              bool IsPrimary;  uint Dpi;  int Number;  string Label; } // "Display 2 · 1920 × 1080"
```

- **Enumerate:** `EnumDisplayMonitors` → `GetMonitorInfoW` (`MONITORINFOEXW`: bounds, primary flag, device name) → `GetDpiForMonitor`.
- **Numbering:** primary first, then left to right, then top to bottom.
  - This does not always match the numbers in Windows Settings. That's fine, because selecting a tile outlines that monitor on screen (§4.3), which identifies it.
  - Optional polish: real model names (e.g. "DELL U2720Q") from `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo(GET_TARGET_NAME)`.
- **Changes:** re-enumerate on `SystemEvents.DisplaySettingsChanged` (or `WM_DISPLAYCHANGE`) and refresh the picker. If the selected monitor disappears, fall back to the primary monitor.
- **Single vs. multiple monitors:**
  - 1 monitor: the picker shows two options, **Full screen** and **Region**.
  - 2 or more: one tile per monitor plus **Region**. The default is the last-used monitor if it is still connected, otherwise the primary.

---

## 4. Full-screen and region capture

### 4.1 One shared model

Everything is described by a single type:

```
CaptureTarget { MonitorInfo Monitor; RECT CropRect; }   // CropRect is in the monitor's physical pixels, origin at its top-left
```

- Full screen is `CropRect = (0, 0, monitorWidth, monitorHeight)`.
- A region is a smaller `CropRect`.
- The capture pipeline is identical in both cases. Full screen skips the crop copy when the rectangle covers the whole frame.

### 4.2 Region selection

1. The user clicks **Region**. The main window hides, and one **overlay window per monitor** opens.
   - Each overlay is borderless, topmost, transparent, and dims its screen at about 40% black.
   - One window per monitor (rather than one spanning the whole desktop) keeps mixed-DPI setups correct.
2. The user drags a rectangle on any monitor. The selection is clear (no dimming inside it), has an accent-colored border, and shows a live **"1280 × 720"** size label. Dragging again replaces the selection.
3. **Enter** (or releasing the mouse plus a ✓ button) confirms. **Esc** cancels.
4. The rectangle is converted from WPF DIPs to physical pixels using the overlay's DPI (`PresentationSource…TransformToDevice`), then normalized:
   - clamped to that monitor (a region cannot span monitors);
   - width and height rounded **down to even numbers**, because H.264 4:2:0 requires it;
   - a minimum of 128 × 128 enforced, to stay within hardware encoder limits.
5. The app returns to the main window with *"Region · 1280 × 720 on Display 2"* selected.

### 4.3 Making the target clearly visible before recording

A `SelectionFrameWindow` draws a 2–3 px accent border around the current target:

- The window is click-through and topmost (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`).
- It is excluded from capture with `WDA_EXCLUDEFROMCAPTURE`.

Behavior:

- **Monitor target:** the border flashes around the monitor edges for about 1.5 s when the tile is selected.
- **Region target:** the border stays visible until recording starts. During recording it turns red and stays, so the user always knows what is being recorded.

### 4.4 Starting the WGC capture (sketch)

```csharp
var item    = CaptureHelper.CreateItemForMonitor(target.Monitor.Handle);    // IGraphicsCaptureItemInterop
var pool    = Direct3D11CaptureFramePool.CreateFreeThreaded(d3dDevice,
                  DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
var session = pool.CreateCaptureSession(item);
session.IsCursorCaptureEnabled = settings.ShowCursor;
if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, "IsBorderRequired"))
    session.IsBorderRequired = false;                                      // no yellow capture border
if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, "MinUpdateInterval"))
    session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / settings.Fps);   // Win11 24H2+: OS-side throttling
pool.FrameArrived += OnFrameArrived;
item.Closed       += OnCaptureItemClosed;                                  // monitor unplugged → stop & save
session.StartCapture();
```

---

## 5. Audio: system sound and microphone

### 5.1 UI and choices

The main window has one **Audio** row with two toggle chips. They are independent, so all four modes (video only, + system, + mic, + both) work:

```
Audio   [ 🔊 System sound ● ]   [ 🎤 Microphone ○ ]   Microphone (USB Audio) ▾
```

Microphone states:

| Situation | What the UI shows |
|---|---|
| 0 microphones | Mic chip disabled, tooltip *"No microphone found"* |
| 1 microphone | Device name shown as plain text, no picker |
| 2 or more | A small drop-down (ComboBox), shown only while the Mic chip is on |

Defaults and memory:

- First run: system sound **on**, microphone **off**. After that, the last choices are remembered.
- The selected microphone is remembered by its endpoint ID. If it's missing next time, fall back to the Windows default input device.
- Audio choices are locked while recording. Changing them requires a new recording, which keeps the pipeline simple.

### 5.2 Device detection

`AudioDeviceService`:

- **Enumerate:** `MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)`. Each entry has an ID and a friendly name such as *"Microphone (Realtek Audio)"*. The default is `GetDefaultAudioEndpoint(Capture, Role.Console)`.
- **Changes:** register an `IMMNotificationClient`. On device added, removed or default changed, refresh the list on the UI thread. The same callback also tells the recorder when the default **output** device changes during recording (§5.5).

### 5.3 Capture

Both sources are opened in **shared mode**, and both ask Windows for one common format: **48 kHz, stereo, 32-bit float**.

- Use `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY` so Windows does the sample-rate and channel conversion.
- If a device refuses that, capture in its native mix format and convert in code:
  - resample with NAudio's `WdlResampler`;
  - upmix mono to stereo by duplicating the channel.
- **Confirm in the Phase 4 spike** that auto-convert works together with the loopback flag.

How each source behaves:

- **System sound:** loopback on the *default render device*. It records everything the user hears, from all apps, and only from the default output device.
- **Microphone:** the selected capture device.
- Each source writes into its own small **jitter buffer** (about 1 s of capacity).

### 5.4 Mixing (deliberately basic) and keeping audio in sync

- The output is **one** AAC track, so every player plays both sources.
  - Many players only play the first track of a multi-track MP4, so separate tracks would confuse users.
  - Mixing is a fixed sum, `out = clamp(system + mic, -1, 1)`. There are no gain controls, no ducking and no effects.
- **The mixer is driven by the same `RecordingClock` as the video.** Every ~20 ms it works out how many samples *should* exist by now (`activeTime × 48000`), pulls exactly that many from each enabled buffer, sums them, converts to 16-bit PCM and queues one 20 ms chunk for the encoder.
- This one rule covers four problems:

| Problem | How the clock-driven mixer handles it |
|---|---|
| WASAPI loopback delivers **no data while nothing is playing** | The buffer underflows, so the mixer fills with silence. The audio timeline never gets shorter than the video |
| Microphone and sound-card clocks drift apart over a long recording | **Drift control.** Every second, each buffer's lowest level is checked. If it never fell below 40 ms, that surplus is pure delay rather than jitter cushion, so it is dropped down to 20 ms, one frame at a time, at most one per 1,000 frames (0.1 %, inaudible). That keeps each source within a few tens of ms of the video for any length (tests: 500 ppm fast for 10 min stays under 70 ms; without this it reached the 200 ms cap). A slow clock just underflows now and then. The 200 ms trim stays as a backstop for bursts |
| A short underflow mid-sound | The gap fades out from the last sample (×0.995 per frame, ~5 ms) instead of jumping to zero, which would click |
| Mic muted mid-recording | Mic input is discarded as it arrives while muted, and the buffer is cleared. Before this fix the muted mic kept filling its buffer to the 200 ms cap, so after unmuting ~0.2 s of audio from the muted time was played, and the mic stayed 0.2 s late |
| Pause | While paused, `activeTime` doesn't advance, so the mixer produces nothing and incoming audio is discarded. Buffers are cleared on resume |
| A source disappears mid-recording | Its buffer stays empty, so that source contributes silence and the recording continues |

- The mixer runs ~100 ms behind real time (jitter buffer). That's invisible in the output because timestamps come from the clock.
- A constant WASAPI latency offset of about 10–30 ms is acceptable. If sync tests show a consistent offset, correct it with the QPC position that WASAPI reports for each packet (`IAudioCaptureClient::GetBuffer` → `u64QPCPosition`).

### 5.5 Audio error handling

| Event | Behavior |
|---|---|
| Microphone blocked by Windows privacy settings (fails with access denied) | Recording does **not** start. Show *"Windows is blocking microphone access"* with an **Open settings** button (`ms-settings:privacy-microphone`) |
| Microphone in use exclusively by another app | Recording does not start. Show a clear message |
| Microphone unplugged during recording | Keep recording, with silence for the mic. Show a notice in the "Saved" banner |
| Default output device changes during recording (e.g. headphones plugged in) | Reopen loopback on the new default device. A gap of a few milliseconds is filled with silence by the mixer. Windows reports the change once per role, so only the Multimedia one (the endpoint loopback opens) triggers a reopen |
| No audio source enabled | No audio stream is created. The output is video only |

---

## 6. Recording, pause/resume and stop

### 6.1 State machine (`Recorder`)

```
            Start()                Pause()
  Idle ───────────────► Recording ─────────► Paused
   ▲                     │    ▲               │
   │                     │    └── Resume() ───┘
   │        Stop() / error / monitor lost     │
   └──────── Finalizing ◄─────────────────────┘
```

**Idle → Recording**, in this order:

1. Open the audio sources first. If an enabled source fails (e.g. microphone blocked), abort with a message before anything visible changes.
2. Create the capture session and encoder.
   - Starting takes a second or two. The UI counts as busy from the click: Record and settings are locked, and closing the window waits for the start, then stops and saves.
   - Stop is ignored while starting. Frames that arrive before the clock starts only refresh the "latest" texture, so the first video frame is the real screen.
3. Start the clock, then **clear the mixer's jitter buffers**. The sources have been feeding them since step 1. Kept, the audio would be trimmed to 200 ms and stay ~200 ms late for the whole recording. Measured in real logs before the fix: ~1 s of mic audio buffered at start.
4. Minimize the main window and show the indicator.
5. Block sleep with `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`.
   - The setting is per thread, so it is set and cleared on the UI thread. A stop that ends on a pool thread (low disk, monitor lost) would otherwise never clear it.

**Finalizing:** the recording bar closes and the main window comes back immediately with the saving screen (§6.6). Settings and Record stay disabled until the MP4 is flushed, closed and renamed.

### 6.2 Data flow

```
VIDEO
WGC FrameArrived (thread pool)
   │  Paused? → copy into "latest" texture only, release frame
   │  too soon for target fps? → drop (fallback throttle when MinUpdateInterval is unavailable)
   ▼
CopySubresourceRegion(cropRect) → free texture from a growable ring (4 → cap; at cap and encoder waiting → repeat last frame; else drop, never block)
   ▼
video queue (capacity 3) ─────────────┐
                                      │
AUDIO                                 ▼
WASAPI loopback ─► jitter buffer ─┐  MediaStreamSource ── SampleRequested(video | audio, with deferrals)
WASAPI mic      ─► jitter buffer ─┤      │  video: CreateFromDirect3D11Surface(tex, ts); Processed → return tex
                                  ▼      │  audio: CreateFromBuffer(20 ms PCM, ts)
          Mixer (clock-driven, 20 ms) ─► audio queue
                                         ▼
                              MediaTranscoder (H.264 HW + AAC) → "<name>.mp4.part"
```

Rules:

- Turn on D3D11 multithread protection (`ID3D11Multithread.SetMultithreadProtected(true)`). Capture copies and the encoder share one device from different threads.
- Always dispose `Direct3D11CaptureFrame` immediately. Leaked frames stall the frame pool.
- Answer `SampleRequested` with a **deferral** rather than by blocking the Media Foundation thread.
- **Keep every video `MediaStreamSample` alive until `Processed` (the real cause of "video freezes, audio continues").**
  - A texture returns to the ring only when its sample's `Processed` event fires. If nothing holds the sample, the GC can collect its .NET wrapper first, and then `Processed` is never raised. That texture is lost for good.
  - Measured: about 1 in 20 samples was lost this way, so frames held climbed ~1.2/s until the 32-texture cap (~26–36 s in). From then on every frame was a repeat of the last one: the video showed one still image while audio kept recording. That is what users saw.
  - Fix: the encoder keeps each video sample in a set until `Processed` fires. Measured after the fix: at most 3 frames held during 60 s and 5 min recordings, 0 repeats, and no frozen stretch in the saved video.
  - The earlier reading that the encoder "holds many frames (median 7–10, peaks 17–38)" was this leak, not the encoder. The ring and repeat logic below stay as a safety net. A one-time log warning marks the ring being full.
- **Texture ring sizing (safety net).**
  - The encoder may hold a few frames while it is busy, and it requests the next frame *before* releasing any.
  - With a fixed ring of 4 this deadlocks. Every texture is held, every new frame and heartbeat is dropped, and the video freezes for the rest of the recording while audio keeps going. A live 45 s capture delivered 49 frames instead of ~2,700.
  - Fix, part 1: the ring starts at 4 and grows on demand up to `768 MB / frame size`, clamped to 8–32 textures. That's 32 at 1080p/1440p (~265/470 MB at most) and 20 at 4096×2304. It is only reached when the encoder lags.
  - Fix, part 2: if the ring is at its cap and the encoder is waiting for a frame, resubmit the newest texture with the new timestamp instead of dropping. That looks the same as one dropped frame, costs no memory, and lets the encoder release textures, so the freeze can't happen at any cap. Slots are reference-counted because a texture can be in two samples.
  - Measured after the fix: 51 fps of 60 with 0 dropped at refresh-rate content. Even at a forced cap of 4, video kept moving (only with many repeats), which is why the ring also grows.
- **Video heartbeat:** if no new screen frame arrives within 0.5 s, re-send the last frame with a new timestamp.
  - WGC only delivers frames when the screen changes. Without the heartbeat, a static screen would stall the video stream while audio keeps flowing, and the MP4 muxer would have to buffer unbounded audio.
  - Re-encoding an unchanged frame costs almost nothing.
- **Video timestamps never go backwards.**
  - Frame callbacks run in parallel and can race a heartbeat stamped "now", so a frame whose time is not after the last one pushed gets the last time + 1 ms.
  - The final frame at Stop is skipped when a frame stamped later already went out.

### 6.3 Timestamps and pause

Video and audio share one `RecordingClock` based on QPC, the same clock as `frame.SystemRelativeTime` (`Stopwatch.GetTimestamp()`).

```
activeTime(t) = t - startTime - pausedTotal - sleptTotal
video ts      = activeTime(frame.SystemRelativeTime)
audio ts      = samplesEmitted / 48000            // mixer keeps samplesEmitted ≈ activeTime(now) × 48000
Pause():   pauseStartedAt = now;                      state = Paused     (mixer stops emitting)
Resume():  pausedTotal += now - pauseStartedAt;       state = Recording
           clear audio jitter buffers
           enqueue the "latest" video texture at activeTime(now)   // screen may have changed while paused
Elapsed (UI) = activeTime(now)  (frozen while paused)
```

**Sleep is left out like a pause.** QPC keeps counting while the PC sleeps (lid closed, sleep button, idle sleep). Blocking sleep can't stop a lid close. Counted, an hour asleep would reach the mixer at wake as one ~0.7 GB burst of silence (overnight: out of memory, crash, and the unfinished file is unplayable) and the video as one frozen frame for the whole hour.

- `sleptTotal` = growth of (QPC − `QueryUnbiasedInterruptTime`) since start. Both come from the same counter, and the second stops during sleep. It only grows, in steps of at least 0.5 s, so its ~15 ms tick noise never moves timestamps.
- All internal times (start, pause start) are in this sleep-free time, so a sleep while paused isn't subtracted twice.
- After waking, recording continues where it left off. The "Saved" message notes that the PC slept, and the log records for how long.
- Verified on this laptop: the computed sleep since boot (105.1 min) matched the system's sleep/wake events. The MF encoder kept writing normally after each of 3 sleeps during a long encode.

- The video is **variable frame rate**, capped at the selected fps, with the 0.5 s heartbeat. MP4 handles this fine.
- While paused, capture keeps running and data is discarded. Resuming is instant and there is no device or session teardown to go wrong.

### 6.4 Stop

1. Set state to `Finalizing`.
   - Enqueue the last video frame again at the stop time, so a static screen at the end doesn't shorten the video.
   - Run the mixer up to the stop time, so the audio ends at the same point.
2. Signal end of stream on **each** stream: in `SampleRequested`, set `args.Request.Sample = null`.
3. `await` the transcode task. Dispose the WGC session, frame pool, textures and WASAPI clients.
4. Flush the file to disk, then rename `<name>.mp4.part` to `<name>.mp4`. Only then replace the saving screen with *"Saved · Open · Show in folder"*, plus any notices from §5.5 and §6.5.
5. Release the sleep block.

### 6.5 Error paths (all end in a clean Idle state)

| Event | Behavior |
|---|---|
| Monitor unplugged (`item.Closed`) | Stop and save what was recorded, then tell the user |
| Monitor resolution or rotation changes mid-recording (`frame.ContentSize` changes) | Stop and save, then tell the user. Simplest and most reliable |
| Save drive nearly full (< 256 MB free, checked every 5 s) | Stop and save, then tell the user. A disk that actually fills fails the transcode before the MP4 index is written, losing the whole recording |
| Encoder or IO failure | Stop, keep the `.part` file if non-empty, show the error, and write it to the log. The 5 s check also notices a transcode that died mid-recording (GPU reset, drive unplugged) and stops then, instead of at the user's Stop |
| WGC unsupported (some VMs / RDP sessions) | Disable Record and explain why. Only when `GraphicsCaptureSession.IsSupported()` is false; any other capture start failure is a normal, retryable error |
| Display settings change (new monitor handle, DPI or position) | A selected region is rebound to the fresh monitor and stays selected. It is dropped only if that monitor is gone or changed size |
| Audio problems | See §5.5 |

### 6.6 Saving screen and long recordings

**Where saving time goes (measured).** Encoding happens live during recording, so Stop does not re-encode anything. Measured on the Media Foundation MP4 sink behind `MediaTranscoder`:

- It streams `mdat` straight into our file and appends `moov` at the end (`ftyp → mdat → moov`). It writes no temp file, makes no fast-start rewrite, and the only seek-back patches the 8-byte `mdat` size.
- Finalizing after end-of-stream took ~50 ms for 2 minutes of 1080p30. The index grows ~22 KB per minute (≈ 1.5 MB per hour), so even multi-hour files finalize quickly.

So the saving time that remains, in order, is:

1. Draining whatever the encoder hadn't consumed at Stop. That is ≤ 3 video frames plus any audio backlog, which can be larger if the encoder fell behind (e.g. software encode on a slow machine).
2. Writing the index.
3. Flushing to disk, which on slow USB or network drives can take seconds.

**UX.**

- On Stop (or any automatic stop), the recording bar closes, the elapsed time freezes at the stop time, and the main window comes back right away. It shows a saving card in place of the settings: *"Saving recording…"*, a percentage, a stage line, a progress bar and *"Length · size written · saving for m:ss"*.
- The taskbar button shows the same progress.
- *"Saved"* appears only after the file is flushed and renamed.
- Closing the window during a save waits for the save to finish, then closes.

**Progress is measured, never faked.** Every 200 ms the recorder samples the encoder: the media time handed to Media Foundation per stream, whether each stream has delivered end-of-stream, and bytes written. `SaveProgressTracker` maps that to one fraction that never goes backward:

| Stage | Range | Measured by |
|---|---|---|
| Encoding the last frames and audio | 0 → 90% | min over streams of (time handed − time handed at Stop) / (stop time − time handed at Stop) |
| Writing the video file to disk | holds at 90% | not measurable (index + flush), so it holds instead of creeping; the "saving for" clock and the size keep moving |
| Finishing up (rename) | 97% → 100% | n/a |

**Performance and resources for long recordings.**

- Nothing grows with recording length on our side. Video stays on the GPU (a texture ring that grows only as far as the encoder needs, capped by a memory budget; see §6.2), the video queue is capped at 3, and progress polling is O(1).
- Audio samples wrap the mixer's PCM array (`AsBuffer`) instead of copying it through a `DataWriter`. That saves one allocation and copy 50 times a second for the whole recording.
- The low-disk guard (§6.5) stops with room left for the index and flush, so a long recording is never lost to a full drive.
- **FAT32 guard.** FAT32 can't hold a file of 4 GB or more (~80 min at 1080p Medium). Writing past that fails the transcode before the index is written, which loses the whole recording. On FAT32 (2 GB on FAT16), recording stops and saves 64 MB before the limit, with a notice. NTFS, exFAT and ReFS have no guard because they need none.
- **Files over 4 GB (verified).** The MF sink switches to a 64-bit `mdat` size and `co64` chunk offsets. A 4.73 GB file (1080p, 80 Mbps, fed faster than real time) finalized in 158 ms, and both tracks' chunk offsets passed 4 GB. Windows decoded its last frame cleanly.
- The sleep block stays on until the save finishes.
- A larger `FileStream` write buffer to coalesce the sink's small writes was tried and dropped. The benefit wasn't measurable and its interaction with the WinRT stream adapter wasn't proven safe.

**Quality is untouched.** None of this changes the codec, bitrate, resolution, frame rate, audio format or the bytes the sink writes. There is no extra re-encode or remux at save time, and the saving path never drops frames or audio: the backlog is drained fully, not discarded.

---

## 7. Encoding and saving

### 7.1 Encoding profile

```csharp
var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
profile.Video.Width  = outW;  profile.Video.Height = outH;      // even numbers
profile.Video.FrameRate.Numerator = fps; profile.Video.FrameRate.Denominator = 1;
profile.Video.Bitrate = Clamp(outW * outH * fps * 0.1, 2_000_000, 40_000_000);
// ≈ 6 Mbps at 1080p30, ≈ 22 Mbps at 1440p60. Screen text stays sharp, files stay reasonable.
profile.Audio = audioEnabled
    ? AudioEncodingProperties.CreateAac(48000, 2, 192000)      // AAC-LC, 48 kHz stereo, 192 kbps
    : null;                                                     // video-only recording
transcoder.HardwareAccelerationEnabled = true;
```

- **Video input stream:** `VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, cropW, cropH)`.
- **Audio input stream** (only when a source is enabled): `AudioEncodingProperties.CreatePcm(48000, 2, 16)`. The source is `new MediaStreamSource(videoDescriptor, audioDescriptor)`, or video only.
- Set `MediaStreamSource.BufferTime = TimeSpan.Zero`.
- **Size limit:** many hardware H.264 encoders max out around **4096 × 2304**. If the crop is larger (for example a 5120 × 1440 ultrawide), set `outW/outH` to fit inside that box, keeping the aspect ratio, and the transcoder scales on the GPU.
- Known v1 limitation: if the process crashes mid-recording, the `.part` file is unplayable, because standard MP4 writes its index at the end. That is acceptable for v1. If it becomes a problem, switch to fragmented MP4 through `IMFSinkWriter`.

### 7.2 Save location and file naming

- The main window shows **Save to: `…\Videos\Screen Recordings`** with a **Change** button that opens WPF `OpenFolderDialog`.
- The default comes from `KnownFolders.Videos` + `Screen Recordings`, the same folder Snipping Tool uses.
- File name: `Recording 2026-10-05 20-43-12.mp4`. Add ` (2)` on a collision.
- Before starting, check that the folder is writable. If not, ask the user to choose another one.

### 7.3 Settings

Stored at `%LOCALAPPDATA%\ScreenRecorder\settings.json`:

```json
{
  "saveFolder": "C:\\Users\\…\\Videos\\Screen Recordings",
  "fps": 30,
  "showCursor": true,
  "systemAudio": true,
  "microphone": false,
  "microphoneId": "{0.0.1.00000000}.{…}"
}
```

### 7.4 Logging

A plain text log goes to `%LOCALAPPDATA%\ScreenRecorder\logs\` (keep the last 5 files). It records:

- start/stop, target, output size and the encoder used;
- audio devices and formats, and buffer underflow/overflow corrections (counts only);
- device changes and errors.

It exists only so failures can be diagnosed.

---

## 8. Architecture and components

```
ScreenRecorder.sln
├─ src/ScreenRecorder/
│  ├─ App.xaml(.cs)                  startup, ThemeMode="System", global exception logging
│  ├─ app.manifest                   PerMonitorV2 DPI awareness, Windows 10/11 supportedOS
│  ├─ NativeMethods.txt              CsWin32 list (EnumDisplayMonitors, SetWindowDisplayAffinity, …)
│  ├─ UI/
│  │  ├─ MainWindow.xaml             source tiles, Audio row, Save-to row, Record button, ⚙ flyout
│  │  ├─ MainViewModel.cs            binds UI ↔ services/Recorder/Settings (plain INotifyPropertyChanged)
│  │  ├─ RegionSelectorWindow.xaml   per-monitor dimmed overlay, drag-to-select
│  │  ├─ SelectionFrameWindow.cs     click-through outline around the target
│  │  └─ RecordingBar.xaml           ● 00:01:23 System sound on [Mute Mic] [Pause] [Stop]   (topmost, draggable, excluded from capture)
│  ├─ Capture/
│  │  ├─ MonitorService.cs           enumerate + change notifications
│  │  ├─ CaptureTarget.cs            monitor + crop rect, normalization (even, clamp, min size)
│  │  ├─ CaptureHelper.cs            CreateItemForMonitor, D3D device ↔ WinRT IDirect3DDevice interop
│  │  └─ FrameSource.cs              WGC session, frame pool, crop copy, texture ring, heartbeat
│  ├─ Audio/
│  │  ├─ AudioDeviceService.cs       list microphones, default devices, IMMNotificationClient
│  │  ├─ AudioSource.cs              one WASAPI client (loopback or mic) → 48 kHz float stereo → jitter buffer
│  │  └─ AudioMixer.cs               clock-driven sum + clamp → 20 ms PCM chunks (pure logic, unit-tested)
│  ├─ Encoding/
│  │  └─ Mp4Encoder.cs               MediaStreamSource (video [+ audio]) + MediaTranscoder, finalize/rename
│  ├─ Recording/
│  │  ├─ Recorder.cs                 state machine; orchestrates FrameSource, AudioSources, AudioMixer, Mp4Encoder
│  │  └─ RecordingClock.cs           shared timestamp/pause logic (unit-tested)
│  └─ Infrastructure/
│     ├─ SettingsStore.cs            JSON load/save
│     └─ Log.cs
└─ tests/ScreenRecorder.Tests/       xUnit: clock, mixer, CaptureTarget, bitrate, monitor ordering, state machine
```

**Rules:**

- The UI layer only talks to `Recorder`, `MonitorService` and `AudioDeviceService`.
- Capture, audio and encoding classes know nothing about the UI.
- `RecordingClock` is the **single source of time** for both video and audio.
- No dependency-injection container and no MVVM framework. The app is too small to need them.

### UI sketch

```
┌────────────────────────────────────────────────────┐
│ Screen Recorder                            ─  □  ✕ │   Mica background, light/dark follows Windows
│                                                    │
│  What to record                                    │
│  ┌───────────┐ ┌───────────┐ ┌───────────┐         │
│  │  ▭  1     │ │  ▭  2     │ │  ⬚        │         │   1 monitor →  [ Full screen ] [ Region ]
│  │ Display 1 │ │ Display 2 │ │ Region    │         │
│  │ 2560×1440 │ │ 1920×1080 │ │ Select…   │         │
│  └───────────┘ └───────────┘ └───────────┘         │
│                                                    │
│  Audio   [🔊 System sound ●]  [🎤 Microphone ●]    │   chips toggle independently
│          Microphone (USB Audio)              ▾     │   picker only if mic on AND >1 mic
│                                                    │
│  Save to   …\Videos\Screen Recordings      Change  │
│                                                    │
│              ┌──────────────────────┐              │
│              │   ●  Record          │              │   accent-colored, largest element
│              └──────────────────────┘            ⚙ │   ⚙ → Frame rate 30/60 · Show cursor
└────────────────────────────────────────────────────┘

Recording bar (top-center of the recorded monitor, draggable):
   ( ● 00:01:23  System sound on  [Mute Mic] [Pause] [Stop] )
   paused →  ( ● Paused 00:01:23  System sound on  [Mute Mic] [Play] [Stop] )
   "System sound on" is a plain status label (smaller, dimmer than the buttons, not clickable), shown only when
   system sound is being recorded. "Mute Mic" appears only when the mic was on at the start.
   Flat matte buttons (no gradients or bright colors); color marks state:
     Mute Mic / Pause → neutral gray · Unmute Mic (mic off) → muted amber · Play (paused) → muted green
     Stop → muted red · dot: muted red while recording, amber (with "Paused") while paused
   Hover lightens and press darkens each button slightly.
```

Also, while recording, a red overlay icon on the taskbar button (`TaskbarItemInfo.Overlay`). It's one line of code and gives clear feedback.

---

## 9. Development phases

Estimates are for one developer.

| Phase | Work | Done when |
|---|---|---|
| **0. Setup** (½ day) | Install the .NET 10 SDK (it is **not installed on this machine yet**) and Visual Studio 2022/2026 or VS Code + C# Dev Kit. Create the WPF project, manifest, CsWin32, Vortice and NAudio. Set up the git repo. | An empty Fluent-themed window builds and runs. |
| **1. Video capture spike** (2–3 days) | Hard-coded: capture the primary monitor with WGC and encode 10 s to MP4 through MediaTranscoder. This tests the riskiest video part first. | The MP4 plays in Media Player. ffprobe shows the right resolution and duration. CPU is low. |
| **2. Monitors and targets** (3–4 days) | `MonitorService`, picker tiles (1-monitor and multi-monitor layouts), monitor outline flash, region overlay and DPI conversion, `CaptureTarget` normalization, crop copy. | Can record any monitor or any region on any monitor, including mixed-DPI and negative-coordinate layouts. |
| **3. Controls** (2–3 days) | `Recorder` state machine, `RecordingClock`, Pause/Resume, Stop with the final-frame fix, video heartbeat, recording bar, exclude-from-capture on all app windows, minimize/restore, sleep block. | Pausing 5 s in a 15 s session gives a 10 s file. The app's own UI never appears in recordings. |
| **4. Audio** (4–5 days) | `AudioDeviceService`, `AudioSource` (loopback + mic, format negotiation), `AudioMixer`, audio stream in `Mp4Encoder`, the Audio row in the UI, privacy and device-loss handling. | All four modes produce correct files. Audio stays in sync across pauses and silence. Mic picker appears only when needed. |
| **5. Saving and settings** (1–2 days) | Save-to row and folder picker, file naming, `.part` → rename, "Saved · Open · Show in folder" banner, settings flyout and JSON (including audio choices), logging. | Settings survive a restart. Files land in the chosen folder. |
| **6. Hardening** (4–6 days) | All error paths in §5.5 and §6.5, display and audio hot-plug, encoder size cap, long-run leak and A/V drift testing, the full test matrix in §11. | The whole test matrix passes. A 60-min recording has stable memory and stays in sync. |
| **7. Packaging** (1–2 days) | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` (and `win-arm64`), optional simple installer, code signing if possible. | Runs on a clean Windows 11 machine with nothing preinstalled. |

**Total: about 4–5 weeks.**

The build is usable as a video-only recorder after Phase 3 and becomes feature-complete after Phase 4. Phases 5–7 make it dependable.

---

## 10. Windows 11 considerations and permissions

1. **Screen-capture permissions:**
   - An **unpackaged** desktop app needs no permission prompt or capability to use WGC with `CreateForMonitor`. Ship unpackaged (single exe or a simple installer) to keep it that way.
   - If it's ever packaged as MSIX, declare the `graphicsCapture` and `microphone` capabilities and re-test, including Windows 11's *Privacy & security → Screenshot borders / Screenshot access* settings.
2. **Microphone privacy:**
   - *Settings → Privacy & security → Microphone* has a global switch and a **"Let desktop apps access your microphone"** switch. If either is off, mic capture fails, so handle it as described in §5.5 with a button to `ms-settings:privacy-microphone`.
   - Windows 11 shows a microphone-in-use icon in the taskbar while recording. That's expected.
   - System-sound loopback is **not** covered by the microphone privacy setting and needs no permission.
3. **Yellow capture border:** set `IsBorderRequired = false` (Windows 11). If it's ignored for any reason, call `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)` first.
4. **DPI:**
   - The manifest must declare **PerMonitorV2**.
   - Do all capture math in physical pixels, and convert from WPF DIPs per window.
   - Never assume 100% scaling, or that every monitor has the same scaling.
5. **Negative coordinates:** a monitor placed left of or above the primary has negative virtual-desktop coordinates. All rectangle code must handle this.
6. **The app's own windows:** `WDA_EXCLUDEFROMCAPTURE` (Windows 10 2004+) on the main window, outline and recording bar.
7. **Content that will not be captured:**
   - DRM-protected video (Netflix, some browsers' protected playback) records as black. Its audio may still be captured, or may be silent, depending on the app.
   - The secure desktop (UAC prompts, lock screen) is not captured, so the video shows a frozen frame for that period.
   - Document both in the README.
8. **Bluetooth headsets:**
   - Opening a Bluetooth headset's microphone switches the headset to the hands-free profile. Playback, and so the recorded system sound, drops to low-quality mono for the duration of the recording.
   - This is Windows/Bluetooth behavior, not a bug. Note it in the README and suggest a separate or built-in mic for the best system-sound quality.
9. **Echo with speakers:** with system sound and microphone both on and speakers (not headphones) in use, the mic picks up the speakers. There's no echo cancellation in v1 (out of scope). Note it in the README and recommend headphones.
10. **Spatial audio / exclusive mode:**
    - Loopback records the default output device's shared-mode mix.
    - Apps playing in WASAPI *exclusive* mode, which is rare, aren't captured.
    - A mic held in exclusive mode by another app can't be opened (§5.5).
11. **HDR displays:** BGRA8 capture of an HDR monitor may look washed out. Test it. If it's bad, documenting it as a v1 limitation is acceptable.
12. **Sleep and power:**
    - Block sleep and screen-off during recording (`SetThreadExecutionState`).
    - Test on battery. If Windows power throttling ("efficiency mode") causes dropped frames or audio glitches while the app is minimized, opt out during recording with `SetProcessInformation(ProcessPowerThrottling)`.
13. **Hybrid GPUs (laptops):** WGC handles cross-adapter capture. Still test external monitors attached to the discrete GPU.
14. **OneDrive:** the Videos folder may be redirected to OneDrive, which then syncs large files. It works, but mention it in the README and test it.
15. **Windows on ARM:** ship a native `win-arm64` build. Hardware encoders and WASAPI work the same way there.
16. **SmartScreen:** an unsigned exe triggers *"Windows protected your PC"*. Code-sign releases if possible, or tell users about it.

---

## 11. Testing

### 11.1 Automated (xUnit, runs on every build)

- `RecordingClock`: timestamps with no pause, one pause, many pauses, pause at start, stop while paused.
- `AudioMixer`:
  - sums and clamps correctly;
  - fills underflow with silence (loopback silence case);
  - trims overflow beyond 200 ms (burst backstop);
  - drift control keeps a fast device within ~70 ms over 10 simulated minutes, drops nothing with jitter alone, and keeps a slow device in sync;
  - fades out on underflow instead of jumping to silence;
  - discards muted mic audio, so none of it plays after unmuting;
  - emits nothing while paused;
  - single-source and two-source modes;
  - the sample count matches `activeTime × 48000` after many pause/resume cycles.
- Format conversion fallback: mono→stereo, 44.1/16 kHz→48 kHz sample counts.
- `CaptureTarget` normalization: even rounding, clamping to the monitor, minimum size, regions at a monitor's negative-coordinate edges.
- DIP → pixel conversion at 100 / 125 / 150 / 175 / 200%.
- Monitor ordering and labels from **fake** monitor lists: 1, 2 and 3 monitors; left, above and stacked layouts.
- Microphone UI state from fake device lists: 0, 1 and 3 microphones; a saved mic that's missing.
- Output size: bitrate formula and the 4096 × 2304 fit with odd and ultrawide sizes.
- `Recorder` state transitions, including invalid calls (e.g. Pause while Idle).

### 11.2 Integration (scripted, on a dev machine)

A small test harness records and checks each file with **ffprobe** (dev-only tool, never shipped):

| Test | Expected |
|---|---|
| 10 s, each of the **four audio modes** | codec h264 and expected size. Audio stream **absent** for video-only. AAC 48 kHz stereo otherwise. Video and audio durations within ±0.1 s of each other and ±0.3 s of 10 s |
| Record 5 s, pause 5 s, record 5 s | Duration ≈ 10 s for both streams |
| 10 s of an unchanging screen and no sound playing | Duration ≈ 10 s. Audio track present and silent (heartbeat and silence fill work) |

### 11.3 Manual test matrix

**Video:**

| # | Setup | Check |
|---|---|---|
| 1 | Single 1920×1080 at 100% | Simple picker (Full screen / Region), basic flow |
| 2 | Single 4K at 150% and 200% | Region pixels are exact. Output is 3840×2160 and plays smoothly |
| 3 | Two monitors, **different DPI** (e.g. laptop 150% + external 100%) | Region selection is accurate on both. The outline lines up exactly |
| 4 | Secondary monitor **left of** and **above** the primary | Negative coordinates, correct monitor captured |
| 5 | Three monitors | All listed, correctly numbered and outlined |
| 6 | Portrait (rotated) monitor | Correct orientation in the output |
| 7 | Ultrawide 5120×1440 / 3440×1440 | Scaled to fit the encoder limit, aspect ratio kept |
| 8 | Mixed refresh rates (60 + 144 Hz), 60 fps setting | Smooth output, frame rate capped |
| 9 | Unplug a monitor during recording, and while idle | Recording saves cleanly. The picker refreshes |
| 10 | Change resolution or scaling during recording | Stops and saves, with a message |
| 11 | Lock screen / UAC prompt / sleep attempt during recording | No crash. Frozen frame for the secure desktop. No sleep |

**Audio:**

| # | Setup | Check |
|---|---|---|
| 12 | **A/V sync:** record a sync-test clip (flash + beep) playing on screen, with system sound on | Offset < ~50 ms at the start, and still < ~50 ms at the end of a 60-min recording |
| 13 | Mic only: say "now" each time you click a visible on-screen button | Voice lines up with the clicks and is at a clear level |
| 14 | No mic / 1 mic / 3 mics (USB + built-in + Bluetooth) | Chip disabled / name shown / drop-down shown. The selected mic is the one actually recorded |
| 15 | Unplug the USB mic mid-recording | Recording continues, mic silent afterwards, notice shown |
| 16 | Plug in headphones (default output changes) mid-recording | System sound continues from the new device |
| 17 | Mic privacy switch off | Clear message with Open settings. Video-only and system-only still work |
| 18 | Another app holds the mic exclusively | Clear message, no crash |
| 19 | Bluetooth headset as mic and output | Works. Quality drop matches the README note |
| 20 | Different device sample rates (44.1 / 48 / 96 kHz output, 16 kHz mic) | Correct pitch and speed in the output |
| 21 | Pause while music plays, resume | No audio from the paused period, no click or gap at the seam beyond a few ms |

**General:**

| # | Setup | Check |
|---|---|---|
| 22 | 60-minute recording, both audio sources | Memory flat, no growing frame drops, file plays to the end, still in sync |
| 23 | Save folder: OneDrive-redirected, read-only, nearly full disk | Clear errors, no data loss |
| 24 | Laptop with hybrid GPU, on battery and plugged in | Works on both GPUs' outputs, no throttling drops or audio glitches |
| 25 | Dark and light mode, high contrast | UI readable in all |
| 26 | Playback | Windows Media Player, VLC, a browser, and upload to a common video site. Audio audible in all |

**No spare monitors or microphones?**

- Most monitor cases can be simulated with an open-source **virtual display driver** (IddCx-based "Virtual Display Driver") that adds fake monitors with any resolution and DPI.
- Windows Settings can change scaling, orientation, position, and each audio device's sample rate (*Sound → device → Format*).
- A cheap USB microphone plus the built-in one covers the multiple-microphone cases.
- Still do at least one pass with real mixed-DPI hardware before release.

### 11.4 Resource targets (measured with Task Manager / PerfMon)

| Scenario | Target |
|---|---|
| Idle (main window open) | ~0% CPU, < 80 MB RAM |
| Recording 1080p30 with both audio sources, hardware encode | < 5–10% CPU on a mid-range laptop. Audio adds about 1–2% |
| Memory during a 60-min recording | Stable, no growth |
| Paused | Near-idle CPU |

---

## 12. v1 definition of done

- [ ] Detects 1–N monitors and shows the simple picker for 1 and the tile picker for 2 or more.
- [ ] Records a full monitor or a region; the target is outlined on screen before recording starts.
- [ ] System sound and microphone toggle independently; all four modes (video only, + system, + mic, + both) produce correct files.
- [ ] Microphones are detected automatically; the picker appears only when more than one is connected; the choice is remembered.
- [ ] Start / Pause / Resume / Stop work, with a small indicator showing elapsed time and active audio sources; the app's UI never appears in the video.
- [ ] Saves an H.264 MP4 (+ AAC when audio is on) to a user-chosen folder; durations and A/V sync are correct after pauses and silence.
- [ ] Handles monitor unplug, resolution change, mic unplug, output-device change, mic privacy block, full disk and lock screen without crashing or losing what was already recorded.
- [ ] Passes the §11 matrix; resource use is within the §11.4 targets.
- [ ] Ships as a single self-contained exe for x64 and arm64.
