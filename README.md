# LiteClip

A small, open-source clipping app for Windows. It quietly keeps the last few minutes of your screen and sound,
and when something cool happens you press a shortcut to save it. Then trim it, or shrink it for Discord.

No accounts. No uploads. No ads. No tracking. **No internet access at all.**

## The promise

LiteClip will never have:

- ads, sponsored "campaigns", or promoted games
- accounts, logins, or a cloud
- telemetry, analytics, or crash reports sent anywhere
- update pop-ups or anything else that phones home
- a social feed, "AI highlights", or anything that runs when you didn't ask for it

It records, it saves, it trims. That's it. Every line of code is here for you to check.

## Features

- **Replay buffer**: press **Alt+X** (or any shortcut you like) to save the last 15 s, 30 s, 1 min… any length up to 5 minutes.
- **Your own clip lengths**, each with its own optional shortcut (e.g. `Alt+F8` = last 20 s, `Alt+F10` = last 2 min).
- **Editor**: preview, drag the handles to trim, save the trimmed clip (your original stays untouched).
  Keys: `Space` play/pause · `←`/`→` step 1 s (`Shift` = 0.1 s) · `I`/`O` set start/end.
- **Copy** a clip and paste it straight into Discord, a chat or a folder.
- **Fit for Discord**: one click makes a copy under Discord's 10 MB limit (or Nitro's) and copies it.
- **Light**: about 4 MB of RAM for LiteClip in the tray, and about 60 MB for the recorder. Records on your graphics card
  (NVIDIA NVENC, AMD AMF, Intel Quick Sync, or Windows' encoder).
- **H.264, HEVC or AV1**, any frame rate (10–240), native or lower resolution, quality slider.
- **Game sound + microphone**, with volume for each, a **mute shortcut**, mixed or on **separate tracks** for video editors.
- **Smart file names** like `Counter-Strike 2 2026-11-02 21-40-47.mp4` (the game you were in is filled in automatically).
- **Any language**: translations are plain text files (Greek included).
- **Portable mode**: run it from a USB stick, leave nothing on the PC.
- **Remove LiteClip** button that deletes everything it made.
- **Auto-cleanup** of old clips (to the Recycle Bin), accent colour, save sound, tray icon, start with Windows.
- **Scriptable**: command-line options and a status file, so status bars, Stream Decks and scripts can use it.

## Why you can trust it

You don't have to take anyone's word for it. Everything can be checked:

1. **Read the code.** It's all in [`src/`](src), about 2,400 lines of plain C#:

   | File | What it does |
   | --- | --- |
   | `Program.cs` | Starts the app, command line, shortcuts, saving clips, memory, uninstall |
   | `Recorder.cs` | Runs FFmpeg to record the replay buffer, cut clips and make Discord-sized copies |
   | `AudioLoopback.cs` | Captures game sound / microphone with Windows' own audio API |
   | `Ui.cs` | Windows: clip library, editor, settings, pop-up, tray menu |
   | `Native.cs` | Shortcut keys, start-with-Windows shortcut, recycle bin, memory trimming |
   | `Settings.cs` | Reads/writes the plain-text settings file; every folder LiteClip uses |
   | `Lang.cs` | Translations |
   | `ui/*.xaml` | Layout and colours of the windows |

2. **See it built in public.** Every release is built by GitHub Actions from this code (the *Actions* tab shows the
   full log), with SHA-256 fingerprints and a [build attestation](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations)
   that proves the download came from this repository. See [SECURITY.md](SECURITY.md) for how to check it.

3. **Or build it yourself.** `build.ps1` compiles the source with the C# compiler that's already part of Windows
   (`csc.exe` from .NET Framework). No Visual Studio, no NuGet, no third-party code is downloaded into the app.

4. **FFmpeg is the only other program**, and it's verified. `build.ps1` downloads the official
   [BtbN FFmpeg build](https://github.com/BtbN/FFmpeg-Builds) (LGPL) and **refuses to use it unless its SHA-256
   checksum matches** the one published with that release. FFmpeg is the same open-source engine used by OBS,
   VLC and YouTube. *Settings → Advanced* shows the exact FFmpeg command LiteClip is running.

5. **No internet code.** GitHub checks every change for network code and refuses to build if it finds any. You can
   check too: search `src/` for `Http`, `WebRequest`, `WebClient`, `Socket` or `Download`. The only matches are
   harmless look-alikes: `hwdownload` (an FFmpeg filter that copies frames from the graphics card to memory) and the
   standard `http://schemas.microsoft.com/...` labels at the top of the `.xaml` files (names, not web addresses).
   For extra peace of mind, block `LiteClip.exe` and `tools\ffmpeg.exe` in Windows Firewall. It keeps working.

6. **Scan it.** Upload `LiteClip.exe` to [VirusTotal](https://www.virustotal.com). It's checked by ~70 antivirus engines.

LiteClip was built with the help of AI (Claude). All of the code is open, short and documented. Read it,
build it yourself, and verify it. See also [PRIVACY.md](PRIVACY.md).

### How much memory it uses

Measured on Windows 11 with an RTX 4070 Super, recording 1440p at 60 fps (Task Manager's "Memory" column):

| | RAM |
| --- | --- |
| LiteClip in the tray | ~4 MB |
| LiteClip with its window open | ~30 MB (given back when you close it) |
| Recorder (FFmpeg + NVIDIA encoder) | ~50–65 MB, depending on *Encoder frames in memory* |

Most of the recorder's memory is the graphics driver and the frames the encoder works on (about 11 MB per frame at
1440p). Screen capture alone is about 15 MB.

### What LiteClip writes to your PC

| Where | What |
| --- | --- |
| `%TEMP%\LiteClip\buffer` | The rolling recording (small 2-second files, overwritten constantly). Changeable in Settings. |
| `%TEMP%\LiteClip\status.txt` | A tiny status file for status bars / scripts (see below) |
| `Videos\LiteClip` | Your saved clips (changeable in Settings) |
| `%APPDATA%\LiteClip\settings.ini` | Settings, plain text |
| `%LOCALAPPDATA%\LiteClip\thumbs` | Clip thumbnails for the library |
| `Startup\LiteClip.lnk` | Only if "Start with Windows" is on |

Nothing else: no registry changes, no services, no drivers, no admin rights needed.
In **portable mode** all of these (except your clips) live in a `data` folder next to `LiteClip.exe` instead.

## Customize everything

Everything below can be changed in **LiteClip → Settings**, or by editing `settings.ini`
(Settings → *Open settings file*; restart LiteClip after editing it by hand).

### Clips and shortcuts

| Setting | Default | What it does |
| --- | --- | --- |
| `ClipLengths` | `15, 30, 60` | The lengths you can pick (5–300 s). `2m` = 2 minutes. `20@Alt+F8` gives 20 s its own shortcut. |
| `ClipSeconds` | `30` | The length the main shortcut, tray and buttons save |
| `Hotkey` | `Alt+X` | Save a clip |
| `HotkeyPause` | *(none)* | Pause / resume recording |
| `HotkeyMic` | *(none)* | Mute / unmute the microphone |
| `HotkeyOpen` | *(none)* | Open LiteClip |

Shortcuts look like `Alt+X`, `Ctrl+Shift+F9`, `Win+Alt+C`, or just an F-key like `F10`.

### Video

| Setting | Default | Options |
| --- | --- | --- |
| `Monitor` | `0` | 0 = first monitor, 1 = second, … |
| `Fps` | `60` | 10–240 |
| `Resolution` | `Native` | `Native` (lightest), `1440p`, `1080p`, `720p` (smaller files, uses some CPU) |
| `Codec` | `H.264` | `H.264` (plays everywhere), `HEVC` (smaller), `AV1` (smallest, newer GPUs and players) |
| `Encoder` | `Auto` | `Auto`, `NVIDIA`, `AMD`, `Intel`, `Windows` |
| `QualityCq` | `23` | 15 (best, biggest) … 40 (smallest) |
| `ShowCursor` | `True` | Show the mouse cursor in clips |

### Audio

| Setting | Default | What it does |
| --- | --- | --- |
| `RecordAudio` | `True` | Record game / system sound |
| `GameVolume` | `100` | Percent, 0–300 |
| `RecordMic` | `False` | Record your microphone (off by default for privacy) |
| `MicVolume` | `100` | Percent, 0–300 |
| `MicDevice` | *(empty)* | Empty = Windows' default microphone; or pick one in Settings |
| `MicMuted` | `False` | Mic stays connected but records silence (the mute shortcut flips this) |
| `SeparateTracks` | `False` | Game and mic as two audio tracks instead of mixed |

### Files

| Setting | Default | What it does |
| --- | --- | --- |
| `ClipsFolder` | `Videos\LiteClip` | Where clips are saved |
| `FileName` | `{app} {date} {time}` | Tokens: `{app}` (game/app you were in), `{date}`, `{time}`, `{length}` |
| `KeepDays` | `0` | Move clips older than this to the Recycle Bin (0 = never) |
| `MaxClipsGB` | `0` | Keep the clips folder under this size, oldest go first (0 = no limit) |
| `BufferFolder` | *(empty)* | Where the rolling recording lives (empty = Windows temp) |
| `ShareLimitMB` | `10` | Size for *Fit for Discord*: 10 = Discord free, 50 = Nitro Basic, 500 = Nitro |

### Look, feel and advanced

| Setting | Default | What it does |
| --- | --- | --- |
| `ShowToast` | `True` | Pop-up when a clip is saved |
| `PlaySound` | `False` | Short sound when a clip is saved |
| `Accent` | `#F2F2F2` | Colour of buttons and highlights, any `#RRGGBB` |
| `Language` | `English` | `English`, or the name of a file in the `lang` folder (e.g. `Greek`) |
| `AfterSaveCommand` | *(empty)* | Command to run after each save; `{file}` = the clip. Example: `explorer /select,{file}`. Only use commands you trust. |
| `GpuFrames` | `2` | Frames the NVIDIA encoder keeps ready, 1–4. ~11 MB of RAM each at 1440p. 1 = lightest, 4 = safest in very heavy games. |
| `CustomEncoderArgs` | *(empty)* | For FFmpeg experts: replaces LiteClip's video encoder options, e.g. `-c:v hevc_nvenc -preset p7 -cq 20` |

### Portable mode

Put an empty file named `portable.txt` next to `LiteClip.exe` (or use *Settings → Advanced → Portable mode*).
Settings, thumbnails and the recording buffer then live in a `data` folder next to the app, so you can run
LiteClip from a USB stick and leave nothing behind. Folders inside LiteClip's folder are saved as relative paths,
so a different drive letter still works.

### Languages

Translations are plain text files in the `lang` folder, one line per text:

```
Save clip => Αποθήκευση κλιπ
```

To add your language: copy `lang\template.txt` (it lists every text in the app), name it after your language
(e.g. `Spanish.txt`), fill in the translations and pick it in *Settings → Look and feel*. Missing lines stay in
English, so a half-done translation still works. Pull requests with new languages are very welcome.

### Command line

While LiteClip is running, other programs (scripts, Stream Deck, AutoHotkey, status bars) can control it:

```
LiteClip.exe                  open the window
LiteClip.exe --save           save a clip (your chosen length)
LiteClip.exe --save 45        save the last 45 seconds
LiteClip.exe --pause          pause recording
LiteClip.exe --resume         resume recording
LiteClip.exe --toggle-pause   pause / resume
LiteClip.exe --mute-mic       mute the microphone (also --unmute-mic, --toggle-mic)
LiteClip.exe --background     start without opening the window
```

### Status file (for status bars)

`%TEMP%\LiteClip\status.txt` (portable mode: `data\temp\status.txt`) is rewritten every second:

```
recording=1        1 while recording
paused=0
buffered=60        seconds ready to save
clip=30            current clip length
hotkey=Alt+X
mic=0              1 if the microphone is being recorded
micmuted=0         1 if it's muted
saving=0           1 while a clip is being saved
saved=3            clips saved since LiteClip started (watch it change to show "saved!")
last=C:\...\clip.mp4
```

## Build and run

Requirements: Windows 10/11 (64-bit). That's it.

```powershell
git clone https://github.com/gurtspec/LiteClip.git
cd LiteClip
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\dist\LiteClip.exe
```

`package.ps1` then makes the release zip (the same one GitHub publishes).

The first run opens the window and turns on "Start with Windows" (not in portable mode; you can switch it off in Settings).
After that LiteClip lives in the tray (white ring with a red dot): left-click to open, right-click for the menu.

Want to change something or add a feature? See [CONTRIBUTING.md](CONTRIBUTING.md).

## Uninstall

*Settings → Remove LiteClip* deletes everything LiteClip made (settings, thumbnails, temporary files, the
recording buffer, the Startup shortcut and its program files) and keeps your clips. Windows doesn't let a running
app delete itself, so the last step is deleting `LiteClip.exe` yourself.

## Limits

- Some games in *exclusive* fullscreen can show black. Use borderless/windowed fullscreen.
- Anti-cheat systems normally allow screen capture like this (it's the same Windows API as OBS and Discord),
  but no tool can promise that for every game.
- Tested on NVIDIA. AMD / Intel / Windows encoders use the same FFmpeg options but haven't been tested on real hardware yet.
  Reports welcome.

## License

LiteClip: MIT (see `LICENSE`). FFmpeg: LGPL 2.1+, see `tools/FFMPEG-LICENSE.txt`; LiteClip runs the unmodified
official FFmpeg program and does not include FFmpeg code.

## A message from Gurtspec

This was made with AI. Please don't criticise me for taking coders' jobs or anything like that. I don't support
that either. I just think it's fair for gamers and ordinary people to have a good clipping app that isn't bloated,
without all the unnecessary ads and corporate bullshit.
