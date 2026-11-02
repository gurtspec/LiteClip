# Changelog

## 1.2

- **Much lighter.** LiteClip itself now uses about 4 MB of RAM while it sits in the tray (was ~44 MB), and the
  recorder (FFmpeg) about 60 MB (was ~88 MB). New setting: *Encoder frames in memory*, 1 = lightest.
- **Copy clip**: paste a clip straight into Discord or a chat (editor, pop-up, right-click a clip).
- **Fit for Discord**: one click makes a copy under 10 MB (or your own limit) and copies it.
- **Mute microphone** shortcut, tray item and `--mute-mic` / `--unmute-mic` / `--toggle-mic`. The mic keeps
  running but records silence, so nothing restarts.
- **Languages**: translate LiteClip with a plain text file. Greek included. `lang\template.txt` lists every text.
- **Portable mode**: everything stays in LiteClip's own folder (USB stick friendly).
- **Remove LiteClip**: one button deletes everything LiteClip made on your PC (your clips are kept).
- **Nothing hidden**: Settings shows the exact FFmpeg command recording your screen, and experts can replace
  the encoder options.
- Releases are built publicly by GitHub Actions, with fingerprints and a build attestation.

## 1.1

- Microphone recording (off by default), with its own volume, device choice and optional separate track.
- Your own clip lengths (5 s to 5 min), each with an optional shortcut.
- H.264 / HEVC / AV1, NVIDIA / AMD / Intel / Windows encoders, quality slider, any frame rate, lower resolutions.
- File name templates, auto-cleanup, buffer folder, accent colour, save sound, run-after-save command.
- Command line (`--save 45`, `--pause`, ...) and a status file for status bars.

## 1.0

- First version: replay buffer (15 s / 30 s / 1 min), Alt+X to save, built-in trim editor, tray icon.
