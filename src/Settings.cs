// LiteClip - settings, stored as plain text in %APPDATA%\LiteClip\settings.ini
// (or next to LiteClip.exe in portable mode). Every option can be changed in the app or in that file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LiteClip
{
    // Every place LiteClip writes to. In portable mode (an empty file "portable.txt" next to LiteClip.exe)
    // everything stays inside LiteClip's own folder, so it can run from a USB stick and leaves nothing behind.
    public static class Paths
    {
        public static readonly string AppDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public static readonly string PortableFlag = Path.Combine(AppDir, "portable.txt");
        public static readonly bool Portable = File.Exists(PortableFlag);

        static string Local(string specialFolderPath) { return Path.Combine(specialFolderPath, "LiteClip"); }

        public static readonly string Data = Portable ? Path.Combine(AppDir, "data") : Local(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));       // settings.ini
        public static readonly string Cache = Portable ? Path.Combine(AppDir, "data") : Local(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); // thumbnails
        public static readonly string Thumbs = Path.Combine(Cache, "thumbs");
        public static readonly string Temp = Portable ? Path.Combine(AppDir, "data", "temp") : Local(Path.GetTempPath());                                                 // buffer, status file
        public static readonly string DefaultClips = Portable ? Path.Combine(AppDir, "Clips") : Local(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        public static readonly string Languages = Path.Combine(AppDir, "lang");

        // Portable mode stores folders inside LiteClip's folder as relative paths, so a different drive letter still works.
        public static string Store(string path)
        {
            if (!Portable || string.IsNullOrEmpty(path)) return path;
            string root = AppDir.TrimEnd('\\') + "\\";
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
        }

        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(AppDir, path));
        }
    }

    public class ClipLength
    {
        public int Seconds;
        public string Hotkey = "";     // optional shortcut that saves exactly this length
        public override string ToString() { return Hotkey.Length > 0 ? Seconds + "@" + Hotkey : Seconds.ToString(); }
    }

    public class Settings
    {
        // ---- Clips ----
        public List<ClipLength> ClipLengths = Parse("15,30,60");
        public int ClipSeconds = 30;                  // the length the main shortcut / button saves
        public const int MaxLength = 300;             // 5 minutes

        // ---- Shortcuts ----
        public string Hotkey = "Alt+X";               // save a clip (ClipSeconds long)
        public string HotkeyPause = "";               // pause / resume recording
        public string HotkeyOpen = "";                // open LiteClip
        public string HotkeyMic = "";                 // mute / unmute the microphone in clips

        // ---- Video ----
        public int Monitor = 0;                       // 0 = first monitor
        public int Fps = 60;
        public string Resolution = "Native";          // Native, 1440p, 1080p, 720p
        public string Codec = "H.264";                // H.264 (works everywhere), HEVC, AV1 (smallest files)
        public string Encoder = "Auto";               // Auto, NVIDIA, AMD, Intel, Windows
        public int QualityCq = 23;                    // 15 (best, big files) ... 40 (small files)
        public bool ShowCursor = true;

        // ---- Audio ----
        public bool RecordAudio = true;               // game / system sound
        public int GameVolume = 100;                  // percent
        public bool RecordMic = false;                // off by default for privacy
        public int MicVolume = 100;
        public string MicDevice = "";                 // "" = Windows' default microphone
        public bool SeparateTracks = false;           // game and mic as two audio tracks instead of mixed
        public bool MicMuted = false;                 // mic stays connected but records silence (mute shortcut)

        // ---- Files ----
        public string ClipsFolder = Paths.DefaultClips;
        public string FileName = "{app} {date} {time}";   // {app} {date} {time} {length}
        public int KeepDays = 0;                      // 0 = keep forever; otherwise old clips go to the Recycle Bin
        public double MaxClipsGB = 0;                 // 0 = no limit
        public string BufferFolder = "";              // "" = Windows temp folder
        public int ShareLimitMB = 10;                 // "Fit for Discord" target size (10 = Discord free, 50 = Nitro Basic, 500 = Nitro)

        // ---- Feel ----
        public bool ShowToast = true;
        public bool PlaySound = false;
        public string Accent = "#F2F2F2";             // colour of buttons / highlights
        public string Language = "English";           // or the name of a file in the lang folder

        // ---- Advanced ----
        public string AfterSaveCommand = "";          // run after each save; {file} = the clip's path
        public int GpuFrames = 2;                     // frames the NVIDIA encoder keeps ready: 1 (least RAM) .. 4 (safest in very heavy games)
        public string CustomEncoderArgs = "";         // replaces LiteClip's video encoder options when not empty

        public bool FirstRun;

        public static readonly string Dir = Paths.Data;
        public static readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public int MaxClipSeconds { get { return Math.Max(15, ClipLengths.Max(c => c.Seconds)); } }

        // "15,30@Alt+F9,60" -> list of lengths (seconds, optional hotkey), sorted, within 5..300s.
        public static List<ClipLength> Parse(string text)
        {
            var list = new List<ClipLength>();
            foreach (var raw in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.Split('@');
                string num = parts[0].Trim().ToLowerInvariant();
                int mult = 1;
                if (num.EndsWith("m")) { mult = 60; num = num.TrimEnd('m'); } else if (num.EndsWith("s")) num = num.TrimEnd('s');
                double v;
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                int sec = (int)Math.Round(v * mult);
                if (sec < 5 || sec > MaxLength || list.Any(c => c.Seconds == sec)) continue;
                list.Add(new ClipLength { Seconds = sec, Hotkey = parts.Length > 1 ? parts[1].Trim() : "" });
            }
            if (list.Count == 0) list.Add(new ClipLength { Seconds = 30 });
            return list.OrderBy(c => c.Seconds).ToList();
        }

        public static string Format(List<ClipLength> list) { return string.Join(", ", list.Select(c => c.ToString())); }

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) { s.FirstRun = true; s.Save(); return s; }
            var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i > 0 && !line.TrimStart().StartsWith("#")) v[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            Func<string, string, string> str = (k, d) => { string x; return v.TryGetValue(k, out x) ? x : d; };
            Func<string, int, int, int, int> num = (k, d, lo, hi) => { string x; int n; return v.TryGetValue(k, out x) && int.TryParse(x, out n) ? Math.Max(lo, Math.Min(hi, n)) : d; };
            Func<string, bool, bool> flag = (k, d) => { string x; bool b; return v.TryGetValue(k, out x) && bool.TryParse(x, out b) ? b : d; };
            Func<string, string[], string, string> pick = (k, opts, d) => { string x; return v.TryGetValue(k, out x) && opts.Contains(x) ? x : d; };

            s.ClipLengths = Parse(str("ClipLengths", "15,30,60"));
            s.ClipSeconds = num("ClipSeconds", 30, 5, MaxLength);
            if (!s.ClipLengths.Any(c => c.Seconds == s.ClipSeconds)) s.ClipSeconds = s.ClipLengths[Math.Min(1, s.ClipLengths.Count - 1)].Seconds;
            s.Hotkey = str("Hotkey", s.Hotkey);
            s.HotkeyPause = str("HotkeyPause", "");
            s.HotkeyOpen = str("HotkeyOpen", "");
            s.HotkeyMic = str("HotkeyMic", "");
            s.Monitor = num("Monitor", 0, 0, 15);
            s.Fps = num("Fps", 60, 10, 240);
            s.Resolution = pick("Resolution", new[] { "Native", "1440p", "1080p", "720p" }, "Native");
            s.Codec = pick("Codec", new[] { "H.264", "HEVC", "AV1" }, "H.264");
            s.Encoder = pick("Encoder", new[] { "Auto", "NVIDIA", "AMD", "Intel", "Windows" }, "Auto");
            // older versions stored Quality=High/Balanced/Small
            string oldQ = str("Quality", "");
            s.QualityCq = num("QualityCq", oldQ == "High" ? 19 : oldQ == "Small" ? 28 : 23, 15, 40);
            s.ShowCursor = flag("ShowCursor", true);
            s.RecordAudio = flag("RecordAudio", true);
            s.GameVolume = num("GameVolume", 100, 0, 300);
            s.RecordMic = flag("RecordMic", false);
            s.MicVolume = num("MicVolume", 100, 0, 300);
            s.MicDevice = str("MicDevice", "");
            s.SeparateTracks = flag("SeparateTracks", false);
            s.MicMuted = flag("MicMuted", false);
            s.ClipsFolder = Paths.Resolve(str("ClipsFolder", s.ClipsFolder));
            if (string.IsNullOrWhiteSpace(s.ClipsFolder)) s.ClipsFolder = Paths.DefaultClips;
            s.FileName = str("FileName", s.FileName);
            s.KeepDays = num("KeepDays", 0, 0, 3650);
            double gb; s.MaxClipsGB = double.TryParse(str("MaxClipsGB", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out gb) ? Math.Max(0, gb) : 0;
            s.BufferFolder = Paths.Resolve(str("BufferFolder", ""));
            s.ShareLimitMB = num("ShareLimitMB", 10, 1, 4000);
            s.ShowToast = flag("ShowToast", true);
            s.PlaySound = flag("PlaySound", false);
            s.Accent = str("Accent", "#F2F2F2");
            s.Language = str("Language", "English");
            s.AfterSaveCommand = str("AfterSaveCommand", "");
            s.GpuFrames = num("GpuFrames", 2, 1, 4);
            s.CustomEncoderArgs = str("CustomEncoderArgs", "");
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] {
                "# LiteClip settings. Edit here or in the app (Settings). Restart LiteClip after editing this file.",
                "",
                "# Clip lengths in seconds (5-300). Add @Shortcut to give one its own key, e.g. 15, 30@Alt+F9, 2m",
                "ClipLengths=" + Format(ClipLengths),
                "ClipSeconds=" + ClipSeconds,
                "",
                "# Shortcuts, e.g. Alt+X, Ctrl+Shift+F9 (empty = none)",
                "Hotkey=" + Hotkey,
                "HotkeyPause=" + HotkeyPause,
                "HotkeyOpen=" + HotkeyOpen,
                "HotkeyMic=" + HotkeyMic,
                "",
                "# Video. Resolution: Native/1440p/1080p/720p  Codec: H.264/HEVC/AV1  Encoder: Auto/NVIDIA/AMD/Intel/Windows",
                "# QualityCq: 15 (best) - 40 (smallest)",
                "Monitor=" + Monitor,
                "Fps=" + Fps,
                "Resolution=" + Resolution,
                "Codec=" + Codec,
                "Encoder=" + Encoder,
                "QualityCq=" + QualityCq,
                "ShowCursor=" + ShowCursor,
                "",
                "# Audio. Volumes in percent (0-300). MicDevice empty = Windows default microphone",
                "RecordAudio=" + RecordAudio,
                "GameVolume=" + GameVolume,
                "RecordMic=" + RecordMic,
                "MicVolume=" + MicVolume,
                "MicDevice=" + MicDevice,
                "SeparateTracks=" + SeparateTracks,
                "MicMuted=" + MicMuted,
                "",
                "# Files. FileName tokens: {app} {date} {time} {length}. KeepDays / MaxClipsGB: 0 = off (old clips go to the Recycle Bin)",
                "# ShareLimitMB: size for 'Fit for Discord' (10 = Discord free, 50 = Nitro Basic, 500 = Nitro)",
                "ClipsFolder=" + Paths.Store(ClipsFolder),
                "FileName=" + FileName,
                "KeepDays=" + KeepDays,
                "MaxClipsGB=" + MaxClipsGB.ToString(CultureInfo.InvariantCulture),
                "BufferFolder=" + Paths.Store(BufferFolder),
                "ShareLimitMB=" + ShareLimitMB,
                "",
                "# Look and feel. Accent: any colour like #F2F2F2 or #FF4D4D. Language: English or a file name from the lang folder",
                "ShowToast=" + ShowToast,
                "PlaySound=" + PlaySound,
                "Accent=" + Accent,
                "Language=" + Language,
                "",
                "# Advanced. AfterSaveCommand: runs after each save, {file} = clip path. Example: explorer /select,{file}",
                "# GpuFrames: frames the NVIDIA encoder keeps ready, about 11 MB of RAM each at 1440p. 1 = lightest, 4 = safest",
                "# CustomEncoderArgs: replaces LiteClip's video encoder options, e.g. -c:v hevc_nvenc -preset p7 -cq 20",
                "AfterSaveCommand=" + AfterSaveCommand,
                "GpuFrames=" + GpuFrames,
                "CustomEncoderArgs=" + CustomEncoderArgs,
            });
        }

        public static string LengthText(int s)
        {
            if (s >= 60 && s % 60 == 0) return (s / 60) + " min";
            if (s > 60) return (s / 60) + "m " + (s % 60) + "s";
            return s + "s";
        }
    }
}
