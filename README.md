<p align="center">
  <img src="src/VoiceWin/Assets/vwtrans-crop.png" alt="VoiceWin" width="128" height="128">
</p>

<h1 align="center">VoiceWin</h1>

<p align="center">
  <strong>Native Windows Speech-to-Text Transcription App</strong><br>
  Press a key, speak, release — your words appear instantly.
</p>

<p align="center">
  <a href="https://github.com/safzanpirani/voicewin/releases">Download</a> •
  <a href="#quick-start">Quick Start</a> •
  <a href="#features">Features</a>
</p>

---

## Features

- **Hold Right Alt** to record, release to transcribe and paste
- **Tap Right Alt** to toggle recording (configurable)
- **Hybrid mode** — hold for quick recordings, tap to toggle for longer ones
- **Real-time Streaming** — see transcriptions appear as you speak (Deepgram)
- **Status Overlay** — animated waveform bars with REC/LIVE indicator, shows Recording/Listening feedback
- **Voice Activity Detection** — automatically trims silence, auto-stops streaming after extended silence
- **AI Enhancement** — polish transcriptions with LLM-powered rewriting
- **20+ Languages** — supports English, Spanish, French, German, Japanese, Chinese, and more
- **Sound Feedback** — audio cues when recording starts/stops
- **Tray Icon Status** — visual indicator shows recording (red), processing (orange), or ready (green)
- Supports **Groq Whisper API** (fast, free tier available)
- Supports **Deepgram nova-3** (high quality, non-streaming or streaming)
- System tray app — runs in background
- Auto-paste transcribed text to focused window
- Single portable EXE — no installation required

## Requirements

- Windows 10/11 x64
- Or Linux (Arch/CachyOS and others) — see [Linux](#linux)

## Linux

A native Linux port lives in `src/VoiceWin.Linux` — a full Avalonia GUI port with feature parity: the same settings window, animated recording overlay (waveform bars + REC/LIVE indicator), tray icon with status dot, hold/toggle/hybrid hotkey modes, custom hotkey recording, Deepgram streaming with silence auto-stop, AI enhancement, and sound feedback. It shares the transcription/LLM services and settings format with the Windows app (`~/.config/VoiceWin/settings.json`).

**Build** (requires `dotnet-sdk` 8+):

```bash
dotnet publish src/VoiceWin.Linux -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux-x64
```

This produces a single self-contained `publish/linux-x64/voicewin` executable — no .NET runtime needed.

**Add to your application launcher** (installs the binary to `~/.local/bin`, the icon, and a `.desktop` entry — no root needed):

```bash
./install-linux.sh
```

VoiceWin then appears in your desktop's app launcher / menu under *Utilities*.

**Runtime dependencies** (Arch/CachyOS package names): `pipewire-pulse` (mic capture via `parecord`), `wl-clipboard` (clipboard), and for auto-paste `ydotool` (preferred) or `wtype`. Optional: `mpv` or `ffmpeg` (`ffplay`) for sound feedback.

**Auto-paste:** Linux defaults to **Ctrl+Shift+V** for terminal inputs, with **Ctrl+V** (desktop inputs) and **Shift+Insert** selectable under **Paste shortcut**. Shift+Insert updates both the regular clipboard and PRIMARY selection, because different applications read different selections. VoiceWin verifies the transcript with `wl-paste` before injecting a paste shortcut and cancels automatic paste if verification fails. Choose the shortcut your target app accepts. Clipboard writes and keystrokes run in order so streaming chunks cannot overwrite one another before paste. VoiceWin prefers `ydotool` via `/dev/uinput` and can start its own daemon when permissions allow it; `wtype` is a fallback for compatible compositors (not KWin). Failures appear in status and stderr; when copying succeeds, the latest chunk remains available for manual paste.

**Live dictation:** choose **Deepgram Streaming**, configure a Deepgram key, and use **Tap to Toggle** so the recording modifier is released while text is pasted. Focus the target input, tap the hotkey, speak, then tap again to stop. Finalized phrases append during recording, including final results on stop. Interim guesses are not inserted because they can change. Linux streaming skips AI enhancement to avoid delaying or reordering phrases; batch transcription still supports enhancement.

**Global hotkey:** the hotkey listener reads `/dev/input` (works on both X11 and Wayland) and requires membership in the `input` group:

```bash
sudo usermod -aG input $USER   # then log out and back in
```

Without it, recording can still be toggled by clicking the tray icon or by binding a desktop-environment global shortcut to `voicewin --toggle`.

## Quick Start

1. Download `VoiceWin.exe` from [Releases](https://github.com/safzanpirani/voicewin/releases)
2. Run it — no installation needed
3. Add your API key (Groq or Deepgram) in the settings window
4. Press **Right Alt** to record, release to transcribe

## API Keys

### Groq (Recommended — Fast & Free)
1. Go to [console.groq.com](https://console.groq.com)
2. Create an account and get an API key
3. Paste in the "Groq API Key" field

### Deepgram
1. Go to [console.deepgram.com](https://console.deepgram.com)
2. Create an account and get an API key
3. Paste in the "Deepgram API Key" field

## Usage

1. Press and hold **Right Alt** to start recording
2. Speak into your microphone
3. Release **Right Alt** to stop and transcribe
4. Text is automatically pasted into the focused text field

## Status Overlay

A minimalist overlay appears during recording:

- **Animated waveform** — 5 bars that react to your microphone audio level
- **REC indicator** (red) — shown during batch recording mode
- **LIVE indicator** (green) — shown during streaming mode
- **Recording/Listening** — real-time feedback showing when speech is detected
- **Configurable position** — top or bottom of screen

The overlay automatically hides after transcription completes.

## Voice Activity Detection (VAD)

Powered by Silero VAD for intelligent silence handling:

### Batch Mode
- Automatically trims silence from the beginning, middle, and end of recordings
- Reduces API costs by sending only speech segments
- Shows "No speech detected" if recording contains only silence

### Streaming Mode
- Auto-stops recording after configurable silence timeout (default: 60 seconds)
- Prevents overnight API charges if you forget to stop recording
- Configurable in settings

| Setting | Default | Description |
|---------|---------|-------------|
| VAD Enabled | On | Toggle silence detection |
| Silence Timeout | 60s | Auto-stop streaming after this much silence |

## Hotkey Modes

| Mode | Description |
|------|-------------|
| **Hold to Record** | Hold the key while speaking, release to transcribe |
| **Tap to Toggle** | Tap once to start, tap again to stop |
| **Hybrid** | Hold for quick recordings (≥250ms), or tap to toggle for longer sessions |

## Transcription Providers

### Non-Streaming (Groq, Deepgram)
Records audio, then transcribes after you stop. Fast and reliable.

### Streaming (Deepgram)
Transcribes in real-time as you speak — text appears immediately. Best used with **Toggle** hotkey mode.

> **Note:** There's a ~1 second delay while connecting to the Deepgram WebSocket. The sound plays once connected — wait for it before speaking.

> **Note:** Some apps (terminals, code editors) may strip trailing whitespace between transcript chunks. If words run together, try non-streaming mode or adjust your app's settings.

## Language Support

VoiceWin supports 20+ languages with auto-detection:

| Language | Code | Language | Code |
|----------|------|----------|------|
| Auto-detect (Multi) | `multi` | Japanese | `ja` |
| English | `en` | Korean | `ko` |
| Spanish | `es` | Russian | `ru` |
| French | `fr` | Portuguese | `pt` |
| German | `de` | Italian | `it` |
| Chinese | `zh` | Dutch | `nl` |
| Hindi | `hi` | Polish | `pl` |
| Arabic | `ar` | Turkish | `tr` |

Select your preferred language in settings, or use "Auto-detect" for multilingual transcription.

## AI Enhancement

Enable AI Enhancement to polish transcriptions with Groq's LLM:

- Fixes grammar, spelling, and punctuation
- Removes filler words and stutters
- Improves sentence structure and clarity
- Preserves your original meaning and tone

Customize the enhancement prompt in settings.

## Tray Icon Status

| Color | Status |
|-------|--------|
| 🟢 Green | Ready |
| 🔴 Red | Recording |
| 🟠 Orange | Processing |

## Settings

Settings are stored at:
```
%APPDATA%\VoiceWin\settings.json
```

| Setting | Default | Description |
|---------|---------|-------------|
| Transcription Provider | Groq | Groq, Deepgram, or Deepgram Streaming |
| Hotkey Mode | Hold | Hold, Toggle, or Hybrid |
| Language | Auto-detect | 20+ languages supported |
| Overlay Position | Bottom | Top or Bottom of screen |
| VAD Enabled | On | Silence detection and trimming |
| VAD Silence Timeout | 60s | Auto-stop streaming after silence |
| AI Enhancement | Off | LLM-powered text cleanup |
| Sound Feedback | On | Audio cues for recording |

## Build from Source

Requires [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Development build
dotnet restore
dotnet build src/VoiceWin -c Release

# Self-contained single-file EXE
dotnet publish src/VoiceWin -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Output: `publish/VoiceWin.exe` (~560MB due to bundled ONNX Runtime for VAD)

> **Note:** The large file size is due to ONNX Runtime CUDA libraries bundled for Silero VAD. The app works without GPU acceleration.

## License

MIT

### Linux regression checks

Run `dotnet run --project tests/VoiceWin.Regression` with .NET 8 installed (or set `DOTNET_ROLL_FORWARD=Major` to use a newer runtime). The dependency-free harness uses fake clipboard/keyboard tools and a local WebSocket server; it does not type into applications or contact Deepgram. It covers ordered Unicode paste, shortcut selection, error reporting, copied/ordered audio buffers, fragmented responses, and final transcripts on stop.

### Deepgram model choices

The list is intentionally small: **Flux General English** (default), **Flux General Multilingual**, and **Nova-3 General** (batch or live fallback). New installations default to Deepgram Streaming and Tap to Toggle. Flux requires a Deepgram API key and the streaming provider; choose Nova-3 for batch transcription.

Flux uses `/v2/listen` and appends confirmed turns during recording. On stop, VoiceWin preserves the last pending update, since Flux CloseStream does not emit EndOfTurn. Multilingual mode leaves language detection to Flux; a specific language is sent as a language hint. Language availability is model-dependent. See [Flux API](https://developers.deepgram.com/reference/speech-to-text/listen-flux) and [closing a Flux stream](https://developers.deepgram.com/docs/flux/close-stream).

### Live transcript overlay

During streaming, a transcript panel below the REC/LIVE recording badge displays the session so far, including interim recognition updates. Revised words replace the current phrase; confirmed phrases accumulate. The panel wraps and scrolls to the newest text, stays within the screen work area, clears for the next recording, and remains visible for 1.5 seconds after transcription finishes. Preview updates do not paste interim guesses or take keyboard focus from your target application.
