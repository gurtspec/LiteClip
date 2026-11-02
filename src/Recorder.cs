// LiteClip - the replay buffer.
// FFmpeg records the screen (on the GPU) + audio into short 2-second pieces in a temp folder,
// overwriting the oldest ones, so the last few minutes are always on disk.
// Saving a clip just glues the newest pieces together; nothing is re-encoded, so it's instant.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LiteClip
{
    public class Recorder
    {
        public const int SegmentSeconds = 2;

        public static readonly string AppDir = Paths.AppDir;
        public static readonly string FFmpeg = Path.Combine(AppDir, "tools", "ffmpeg.exe");
        public static readonly string FFprobe = Path.Combine(AppDir, "tools", "ffprobe.exe");

        readonly Settings settings;
        readonly object gate = new object();
        Process ffmpeg;
        AudioLoopback audio, mic;                           // game/system sound, microphone
        readonly List<System.IO.Pipes.NamedPipeServerStream> pipes = new List<System.IO.Pipes.NamedPipeServerStream>();   // raw audio goes to FFmpeg through private pipes
        Thread watchdog;
        volatile bool running, paused;
        readonly Queue<string> log = new Queue<string>();
        DateTime startedAt;
        int recordingMaxSeconds;

        public event Action StateChanged;
        public string Encoder { get; private set; }         // FFmpeg encoder in use, e.g. h264_nvenc
        public bool HasAudio { get { return audio != null; } }
        public bool HasMic { get { return mic != null; } }
        public bool IsPaused { get { return paused; } }
        public bool IsRecording { get { lock (gate) return ffmpeg != null && !ffmpeg.HasExited; } }
        public string LastError { get; private set; }
        public string LastCommand { get; private set; }    // the exact FFmpeg command line in use (shown in Settings)

        public static string BufferDirFor(Settings s)
        {
            return string.IsNullOrWhiteSpace(s.BufferFolder) ? Path.Combine(Paths.Temp, "buffer") : Path.Combine(s.BufferFolder, "LiteClip buffer");
        }
        string BufferDir { get { return BufferDirFor(settings); } }

        // Mute the microphone without restarting: it keeps recording silence, so audio stays in sync.
        public void SetMicMuted(bool muted) { var m = mic; if (m != null) m.Muted = muted; }
        public bool MicMuted { get { var m = mic; return m != null && m.Muted; } }

        // Seconds of footage currently available in the buffer.
        public int BufferedSeconds
        {
            get { return IsRecording ? (int)Math.Min(recordingMaxSeconds, (DateTime.Now - startedAt).TotalSeconds) : 0; }
        }

        public Recorder(Settings s) { settings = s; Encoder = PickEncoder(s); }

        public void Start()
        {
            running = true;
            watchdog = new Thread(Watch) { IsBackground = true, Name = "LiteClip watchdog" };
            watchdog.Start();
        }

        public void SetPaused(bool p) { paused = p; if (p) StopFFmpeg(); Changed(); }

        // Apply new settings: restart the capture.
        public void Restart() { Encoder = PickEncoder(settings); StopFFmpeg(); }

        public void Shutdown() { running = false; StopFFmpeg(); }

        void Watch()
        {
            while (running)
            {
                if (!paused && !IsRecording)
                {
                    try { StartFFmpeg(); }
                    catch (Exception e) { LastError = e.Message; Changed(); Thread.Sleep(5000); }
                }
                Thread.Sleep(1000);
            }
        }

        // ---------------------------------------------------------------- encoder choice

        // Graphics card vendor of this PC (for "Auto"). Asked once, then remembered.
        static string vendor;
        static string DetectVendor()
        {
            if (vendor == null) vendor = AskVendor();
            return vendor;
        }

        static string AskVendor()
        {
            try
            {
                var names = new List<string>();
                using (var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (var o in s.Get()) names.Add((o["Name"] ?? "").ToString().ToLowerInvariant());
                if (names.Any(n => n.Contains("nvidia"))) return "NVIDIA";
                if (names.Any(n => n.Contains("amd") || n.Contains("radeon"))) return "AMD";
                if (names.Any(n => n.Contains("intel"))) return "Intel";
            }
            catch { }
            return "Windows";
        }

        // FFmpeg encoder name for the chosen codec + graphics card, e.g. HEVC on NVIDIA -> hevc_nvenc.
        public static string PickEncoder(Settings s) { return PickEncoder(s, s.Codec); }

        public static string PickEncoder(Settings s, string codecName)
        {
            string gpu = s.Encoder == "Auto" ? DetectVendor() : s.Encoder;
            string codec = codecName == "HEVC" ? "hevc" : codecName == "AV1" ? "av1" : "h264";
            switch (gpu)
            {
                case "NVIDIA": return codec + "_nvenc";
                case "AMD": return codec + "_amf";
                case "Intel": return codec + "_qsv";
                default: return codec == "av1" ? "hevc_mf" : codec + "_mf";   // Windows' encoder has no AV1
            }
        }

        // Encoder settings shared by recording and editing.
        public static string VideoEncoderArgs(string encoder, int cq, int fps)
        {
            if (encoder.EndsWith("_nvenc")) return "-c:v " + encoder + " -preset p4 -rc vbr -cq " + cq + " -b:v 0 -g " + fps + " -bf 0";
            if (encoder.EndsWith("_amf")) return "-c:v " + encoder + " -quality balanced -rc cqp -qp_i " + cq + " -qp_p " + cq + " -g " + fps;
            if (encoder.EndsWith("_qsv")) return "-c:v " + encoder + " -global_quality " + cq + " -g " + fps;
            // Windows' built-in encoder: bitrate instead of a quality number
            int mbps = Math.Max(4, 60 - cq * 3 / 2);
            return "-c:v " + encoder + " -hw_encoding 1 -b:v " + mbps + "M -g " + fps;
        }

        // Encoder settings that aim for a bitrate (kbit/s) instead of a quality, for size-limited exports.
        public static string BitrateEncoderArgs(string encoder, int kbps, int fps)
        {
            string rate = " -b:v " + kbps + "k -maxrate " + (kbps * 3 / 2) + "k -bufsize " + (kbps * 2) + "k -g " + (fps * 2);
            if (encoder.EndsWith("_nvenc")) return "-c:v " + encoder + " -preset p5 -rc vbr -multipass fullres" + rate;
            if (encoder.EndsWith("_amf")) return "-c:v " + encoder + " -quality quality -rc vbr_peak" + rate;
            if (encoder.EndsWith("_qsv")) return "-c:v " + encoder + " -preset slower" + rate;
            return "-c:v " + encoder + " -hw_encoding 1" + rate;
        }

        // ---------------------------------------------------------------- recording

        void StartFFmpeg()
        {
            if (!File.Exists(FFmpeg)) throw new FileNotFoundException(L.T("FFmpeg is missing. Run build.ps1 again."), FFmpeg);
            lock (gate)
            {
                string bufferDir = BufferDir;
                Directory.CreateDirectory(bufferDir);
                foreach (var f in Directory.GetFiles(bufferDir, "seg*.*")) try { File.Delete(f); } catch { }
                ClosePipes();

                audio = null; mic = null;
                if (settings.RecordAudio) { try { audio = new AudioLoopback(false); } catch { audio = null; } }                      // no speakers/headphones: no game sound
                if (settings.RecordMic) { try { mic = new AudioLoopback(true, settings.MicDevice); mic.Muted = settings.MicMuted; } catch { mic = null; } }   // no microphone: skip it
                var sources = new List<AudioLoopback>();
                if (audio != null) sources.Add(audio);
                if (mic != null) sources.Add(mic);

                int fps = settings.Fps;
                string custom = settings.CustomEncoderArgs.Trim();
                string encoderInUse = custom.Length > 0 ? custom : Encoder;       // for the checks below
                bool zeroCopy = encoderInUse.Contains("_nvenc") && settings.Resolution == "Native";
                // Fastest path: NVIDIA encodes the captured frames straight on the GPU.
                // Otherwise frames are copied to memory (and scaled there if a lower resolution is chosen).
                string grab = "ddagrab=output_idx=" + settings.Monitor + ":framerate=" + fps + ":draw_mouse=" + (settings.ShowCursor ? 1 : 0);
                string video;
                if (zeroCopy) video = "-f lavfi -i " + grab;
                else
                {
                    string scale = settings.Resolution == "Native" ? "" : ",scale=-2:" + settings.Resolution.TrimEnd('p') + ":flags=bilinear";
                    video = "-f lavfi -i " + grab + ",hwdownload,format=bgra" + scale + ",format=nv12";
                }

                var args = new StringBuilder("-hide_banner -loglevel error -y ");
                args.Append(video).Append(' ');
                // Each sound source gets its own private pipe.
                // (Not stdin: .NET adds text-encoding bytes there, which would scramble raw audio.)
                for (int i = 0; i < sources.Count; i++)
                {
                    string pipeName = "liteclip-audio-" + Process.GetCurrentProcess().Id + "-" + Environment.TickCount + "-" + i;
                    pipes.Add(new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.Out, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 1 << 20));
                    var src = sources[i];
                    args.Append("-f " + src.FfmpegFormat + " -ar " + src.SampleRate + " -ac " + src.Channels +
                                " -thread_queue_size 1024 -i \\\\.\\pipe\\" + pipeName + " ");
                }

                args.Append("-map 0:v ");
                string gv = Vol(settings.GameVolume), mv = Vol(settings.MicVolume);
                if (sources.Count == 1)
                {
                    string v = sources[0] == mic ? mv : gv;
                    args.Append("-map 1:a -af aresample=async=1000,volume=" + v + " -c:a aac -b:a 160k ");
                }
                else if (sources.Count == 2)
                {
                    string chains = "[1:a]aresample=async=1000,volume=" + gv + "[game];[2:a]aresample=async=1000,volume=" + mv + "[mic]";
                    if (settings.SeparateTracks)
                        args.Append("-filter_complex \"" + chains + "\" -map \"[game]\" -map \"[mic]\" -c:a aac -b:a 160k " +
                                    "-metadata:s:a:0 title=Game -metadata:s:a:1 title=Microphone ");
                    else
                        args.Append("-filter_complex \"" + chains + ";[game][mic]amix=inputs=2:duration=longest:normalize=0[aout]\" -map \"[aout]\" -c:a aac -b:a 160k ");
                }
                if (custom.Length > 0) args.Append(custom).Append(' ');   // your own encoder options
                else
                {
                    args.Append(VideoEncoderArgs(Encoder, settings.QualityCq, fps)).Append(' ');
                    // Each frame the NVIDIA encoder keeps ready costs ~11 MB of RAM at 1440p; FFmpeg's default is 4.
                    if (Encoder.EndsWith("_nvenc")) args.Append("-surfaces " + settings.GpuFrames + " -delay 0 ");
                }
                recordingMaxSeconds = settings.MaxClipSeconds;
                int wrap = recordingMaxSeconds / SegmentSeconds + 4;
                // MPEG-TS pieces survive being cut mid-write; AV1 isn't supported in TS, so it uses Matroska pieces.
                bool av1 = encoderInUse.Contains("av1");
                // (Matroska pieces each restart their clock at 0, which is what gluing them back together expects.)
                args.Append("-f segment -segment_time " + SegmentSeconds + " -segment_format " + (av1 ? "matroska" : "mpegts") + " -segment_wrap " + wrap +
                            " -reset_timestamps " + (av1 ? 1 : 0) + " \"" + Path.Combine(bufferDir, av1 ? "seg%03d.mkv" : "seg%03d.ts") + "\"");

                var psi = new ProcessStartInfo(FFmpeg, args.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                };
                LastCommand = "\"" + FFmpeg + "\" " + args;
                ffmpeg = Process.Start(psi);
                Native.KeepWithApp(ffmpeg);                       // FFmpeg closes if LiteClip closes or crashes
                ffmpeg.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) { log.Enqueue(e.Data); while (log.Count > 20) log.Dequeue(); } };
                ffmpeg.BeginErrorReadLine();
                ffmpeg.EnableRaisingEvents = true;
                var a = audio; var m = mic;
                ffmpeg.Exited += (s, e) => { if (a != null) a.Dispose(); if (m != null) m.Dispose(); Changed(); };
                // FFmpeg opens its inputs in order, so connect the pipes in the same order.
                for (int i = 0; i < sources.Count; i++)
                {
                    var wait = pipes[i].BeginWaitForConnection(null, null);
                    if (!wait.AsyncWaitHandle.WaitOne(15000)) throw new IOException(L.T("FFmpeg didn't connect to the audio stream.") + " " + RecentLog());
                    pipes[i].EndWaitForConnection(wait);
                    sources[i].Start(pipes[i]);
                }
                startedAt = DateTime.Now;
                LastError = null;
            }
            Changed();
        }

        static string Vol(int percent) { return (percent / 100.0).ToString("0.##", CultureInfo.InvariantCulture); }

        void StopFFmpeg()
        {
            lock (gate)
            {
                if (audio != null) { audio.Dispose(); audio = null; }
                if (mic != null) { mic.Dispose(); mic = null; }
                if (ffmpeg != null && !ffmpeg.HasExited) { try { ffmpeg.Kill(); ffmpeg.WaitForExit(3000); } catch { } }
                ffmpeg = null;
                ClosePipes();
            }
            Changed();
        }

        void ClosePipes()
        {
            foreach (var p in pipes) { try { p.Dispose(); } catch { } }
            pipes.Clear();
        }

        public string RecentLog() { lock (log) return string.Join("\n", log.ToArray()); }

        // ---------------------------------------------------------------- saving clips

        // Saves the last `seconds` of footage as an .mp4 in the clips folder. Returns the file path.
        public string SaveClip(int seconds)
        {
            if (!IsRecording) throw new InvalidOperationException(paused ? L.T("Recording is paused.") : L.T("The recorder isn't running yet."));
            string app = ForegroundApp();             // what you were playing, for {app} in the file name
            var pieces = new DirectoryInfo(BufferDir).GetFiles("seg*.*")
                .Where(f => f.Length > 0).OrderBy(f => f.LastWriteTimeUtc).ToList();
            int need = seconds / SegmentSeconds + 2;
            if (pieces.Count > need) pieces = pieces.Skip(pieces.Count - need).ToList();
            if (pieces.Count == 0) throw new InvalidOperationException(L.T("Nothing recorded yet. Try again in a few seconds."));

            string work = Paths.Temp;
            Directory.CreateDirectory(work);
            string list = Path.Combine(work, "list.txt");
            string joined = Path.Combine(work, "joined" + pieces[0].Extension);
            File.WriteAllLines(list, pieces.Select(p => "file '" + p.FullName.Replace("'", "'\\''") + "'"));

            Directory.CreateDirectory(settings.ClipsFolder);
            string output = UniquePath(settings.ClipsFolder, FileNameFor(settings.FileName, app, seconds, DateTime.Now));

            Run(FFmpeg, "-hide_banner -loglevel error -y -f concat -safe 0 -i \"" + list + "\" -map 0 -c copy \"" + joined + "\"");
            Run(FFmpeg, "-hide_banner -loglevel error -y -sseof -" + seconds + " -i \"" + joined + "\" -map 0 -c copy -movflags +faststart \"" + output + "\"");
            try { File.Delete(joined); } catch { }
            if (!File.Exists(output)) throw new IOException(L.T("FFmpeg couldn't write the clip."));

            AfterSave(output);
            return output;
        }

        // ---------------------------------------------------------------- "Fit for Discord"

        // Makes a copy of (part of) a clip that's smaller than limitMB, as H.264 (plays everywhere).
        // Picks the bitrate from the length, lowers resolution / frame rate when needed, and retries if it came out too big.
        public static string FitToSize(Settings s, string clip, double start, double length, int limitMB)
        {
            int height = 1080, audioTracks = 0; double fps = 60; string type = "";
            foreach (var raw in Run(FFprobe, "-v error -show_entries stream=codec_type,height,avg_frame_rate -of default=nw=1 \"" + clip + "\"", 20000).Split('\n'))
            {
                var line = raw.Trim(); int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1);
                if (key == "codec_type") { type = value; if (value == "audio") audioTracks++; }
                else if (key == "height" && type == "video") int.TryParse(value, out height);
                else if (key == "avg_frame_rate" && type == "video")
                {
                    var f = value.Split('/'); double a, b;
                    if (f.Length == 2 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                        double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b > 0 && a > 0) fps = a / b;
                }
            }

            string folder = Path.GetDirectoryName(clip);
            string output = UniquePath(folder, Path.GetFileNameWithoutExtension(clip) + " (Discord).mp4");
            int audioK = audioTracks > 0 ? 96 : 0;
            double bits = limitMB * 1000.0 * 1000.0 * 8 * 0.94;          // a little room for the file's own overhead
            int sourceFps = (int)Math.Round(fps);
            string encoder = PickEncoder(s, "H.264");

            for (int attempt = 0; attempt < 4; attempt++)
            {
                int videoK = (int)(bits / Math.Max(0.5, length) / 1000) - audioK;
                if (videoK < 200)
                    throw new InvalidOperationException(L.T("That's too long to fit in {0} MB. Trim it to about {1} seconds or less.", limitMB, (int)(bits / 1000 / (200 + audioK))));
                int h = Math.Min(height, videoK >= 6000 ? 1080 : videoK >= 2800 ? 720 : videoK >= 1500 ? 540 : videoK >= 900 ? 480 : 360);
                int outFps = videoK < 2000 && sourceFps > 31 ? 30 : sourceFps;

                string args = "-hide_banner -loglevel error -y -ss " + start.ToString("0.###", CultureInfo.InvariantCulture) + " -i \"" + clip + "\" -t " +
                              length.ToString("0.###", CultureInfo.InvariantCulture) + " ";
                // Discord only plays the first audio track, so game + mic are mixed into one.
                if (audioTracks >= 2) args += "-filter_complex \"[0:a:0][0:a:1]amix=inputs=2:normalize=0[a]\" -map 0:v:0 -map \"[a]\" ";
                else args += "-map 0:v:0 " + (audioTracks == 1 ? "-map 0:a:0 " : "");
                args += "-vf \"scale=-2:" + h + ":flags=bicubic" + (outFps != sourceFps ? ",fps=" + outFps : "") + ",format=nv12\" ";
                args += BitrateEncoderArgs(encoder, videoK, outFps) + " ";
                if (audioTracks > 0) args += "-c:a aac -b:a " + audioK + "k ";
                args += "-movflags +faststart \"" + output + "\"";
                Run(FFmpeg, args, 600000);

                long size = new FileInfo(output).Length, limit = limitMB * 1000L * 1000L;
                if (size <= limit) return output;
                bits *= 0.92 * limit / size;                               // came out too big: aim lower and try again
            }
            try { File.Delete(output); } catch { }
            throw new IOException(L.T("Couldn't get it under {0} MB. Trim it a bit shorter and try again.", limitMB));
        }

        // Builds a file name from the template: {app} {date} {time} {length}
        public static string FileNameFor(string template, string app, int seconds, DateTime when)
        {
            string name = (string.IsNullOrWhiteSpace(template) ? "{app} {date} {time}" : template)
                .Replace("{app}", app)
                .Replace("{date}", when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Replace("{time}", when.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
                .Replace("{length}", seconds + "s");
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
            name = name.Trim();
            return (name.Length == 0 ? "Clip " + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) : name) + ".mp4";
        }

        static string UniquePath(string folder, string file)
        {
            string path = Path.Combine(folder, file);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + " (" + i + ").mp4");
            return path;
        }

        // Name of the app in front (e.g. "Counter-Strike 2"), or "Desktop".
        static string ForegroundApp()
        {
            try
            {
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                var p = Process.GetProcessById((int)pid);
                string exe = p.ProcessName.ToLowerInvariant();
                if (exe == "explorer" || exe == "liteclip" || exe == "zebar" || exe == "glazewm") return "Desktop";
                string desc = null;
                try { desc = p.MainModule.FileVersionInfo.FileDescription; } catch { }
                return string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc.Trim();
            }
            catch { return "Clip"; }
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        // Optional extras after each save: your own command, then tidy up old clips.
        void AfterSave(string clip)
        {
            if (!string.IsNullOrWhiteSpace(settings.AfterSaveCommand))
            {
                try
                {
                    string cmd = settings.AfterSaveCommand.Replace("{file}", "\"" + clip + "\"");
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { UseShellExecute = false, CreateNoWindow = true });
                }
                catch { }
            }
            CleanUp();
        }

        // Moves old clips to the Recycle Bin when "keep for N days" or "max size" is set.
        public void CleanUp()
        {
            try
            {
                if (!Directory.Exists(settings.ClipsFolder) || (settings.KeepDays <= 0 && settings.MaxClipsGB <= 0)) return;
                var clips = new DirectoryInfo(settings.ClipsFolder).GetFiles("*.mp4").OrderBy(f => f.LastWriteTime).ToList();
                if (settings.KeepDays > 0)
                    foreach (var f in clips.Where(f => f.LastWriteTime < DateTime.Now.AddDays(-settings.KeepDays)).ToList())
                    { try { Native.Recycle(f.FullName); clips.Remove(f); } catch { } }
                if (settings.MaxClipsGB > 0)
                {
                    long limit = (long)(settings.MaxClipsGB * 1024 * 1024 * 1024), total = clips.Sum(f => f.Length);
                    foreach (var f in clips.ToList())
                    {
                        if (total <= limit || clips.Count <= 1) break;
                        try { Native.Recycle(f.FullName); total -= f.Length; clips.Remove(f); } catch { }
                    }
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- helpers

        // Runs a short FFmpeg/FFprobe job and returns its text output. Throws if it fails.
        public static string Run(string exe, string args, int timeoutMs = 120000)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                Native.KeepWithApp(p);
                var err = p.StandardError.ReadToEndAsync();
                string outText = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException(L.T("FFmpeg took too long.")); }
                if (p.ExitCode != 0) throw new IOException(err.Result.Trim().Length > 0 ? err.Result.Trim() : "FFmpeg failed (" + p.ExitCode + ")");
                return outText;
            }
        }

        public static double Duration(string file)
        {
            try
            {
                var s = Run(FFprobe, "-v error -show_entries format=duration -of default=nw=1:nk=1 \"" + file + "\"", 20000);
                double d; return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
            }
            catch { return 0; }
        }

        void Changed() { var h = StateChanged; if (h != null) h(); }
    }
}
