// LiteClip - a small, open-source replay-buffer clipper for Windows.
// Keeps the last few minutes of your screen + sound; press a shortcut to save a clip, then trim it.
// No internet access, no accounts, no telemetry.
//
// Command line (works while LiteClip is running, e.g. from a script, Stream Deck or status bar):
//   LiteClip.exe                 open the window
//   LiteClip.exe --save          save a clip (your chosen length)
//   LiteClip.exe --save 45       save the last 45 seconds
//   LiteClip.exe --pause | --resume | --toggle-pause
//   LiteClip.exe --mute-mic | --unmute-mic | --toggle-mic
//   LiteClip.exe --background    start without opening the window
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace LiteClip
{
    public static class Program
    {
        public const string Version = "1.2";
        const string PipeName = "LiteClip.Commands";
        static Mutex single;

        [STAThread]
        public static void Main(string[] args)
        {
            bool isNew;
            single = new Mutex(true, "LiteClip.SingleInstance", out isNew);
            string command = string.Join(" ", args).Trim();
            if (!isNew)
            {
                // Already running: pass the command (or "show") to that copy, then quit.
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        pipe.Connect(3000);
                        var bytes = System.Text.Encoding.UTF8.GetBytes((command.Length > 0 ? command : "--show") + "\n");
                        pipe.Write(bytes, 0, bytes.Length);
                    }
                }
                catch { }
                return;
            }
            bool background = args.Any(a => a == "--background" || a == "--save" || a == "--pause" || a.EndsWith("-mic"));
            var app = new App(background);
            app.Run();
            GC.KeepAlive(single);
        }

        // Lets a new copy start right away (used when restarting LiteClip).
        public static void ReleaseSingleInstance() { try { single.ReleaseMutex(); single.Dispose(); } catch { } }

        // Listens for commands from other launches of LiteClip.exe (only your own user account can send them).
        public static void ListenForCommands(Action<string> handle)
        {
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe)) { var line = reader.ReadLine(); if (line != null) handle(line.Trim()); }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            }) { IsBackground = true, Name = "LiteClip commands" }.Start();
        }
    }

    public class App : Application
    {
        public static Settings Settings;
        public static Recorder Recorder;
        public static App Instance;
        readonly bool background;
        Hotkeys hotkeys;
        Tray tray;
        MainWindow window;
        int idleSeconds;
        System.Windows.Threading.DispatcherTimer tick;
        public MainWindow Window { get { return window; } }

        public App(bool startInBackground) { background = startInBackground; Instance = this; ShutdownMode = ShutdownMode.OnExplicitShutdown; }

        // Loads a window section from the .xaml files built into LiteClip.exe, translated.
        public static object LoadXaml(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiteClip.ui." + name))
            {
                var o = XamlReader.Load(s);
                var d = o as DependencyObject;
                if (d != null) L.Apply(d);
                return o;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml("Theme.xaml"));
            DispatcherUnhandledException += (s, ex) => { ex.Handled = true; Toast.Show(L.T("Something went wrong"), ex.Exception.Message, null); };

            Settings = Settings.Load();
            L.Load(Settings.Language);
            ApplyAccent();
            Recorder = new Recorder(Settings);
            Recorder.Start();
            Recorder.CleanUp();

            hotkeys = new Hotkeys();
            ApplyHotkeys();
            tray = new Tray();

            Program.ListenForCommands(cmd => Dispatcher.BeginInvoke(new Action(() => HandleCommand(cmd))));

            // Every second: refresh the status file, and give memory back to Windows while no window is open.
            tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (s, ev) => { WriteStatus(); TrimWhenIdle(); };
            tick.Start();

            // First start: start with Windows (not in portable mode, which shouldn't leave anything on the PC).
            if (Settings.FirstRun && !Paths.Portable) { try { Native.SetStartWithWindows(true); } catch { } }
            string startArgs = string.Join(" ", e.Args);
            if (startArgs.Contains("--save") || startArgs.Contains("--pause") || startArgs.Contains("-mic")) HandleCommand(startArgs);
            if (!background) ShowWindow();
            else if (!startArgs.Contains("--save")) Notify(L.T("LiteClip is running"), L.T("Press {0} to save the last {1}.", Settings.Hotkey, Settings.LengthText(Settings.ClipSeconds)), null);
        }

        void HandleCommand(string cmd)
        {
            var parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] == "--show") { ShowWindow(); return; }
            switch (parts[0])
            {
                case "--save":
                    int n;
                    if (parts.Length > 1 && int.TryParse(parts[1].TrimEnd('s'), out n)) SaveClip(Math.Max(5, Math.Min(Settings.MaxClipSeconds, n)));
                    else SaveClip();
                    break;
                case "--pause": SetPaused(true); break;
                case "--resume": SetPaused(false); break;
                case "--toggle-pause": SetPaused(!Recorder.IsPaused); break;
                case "--mute-mic": SetMicMuted(true); break;
                case "--unmute-mic": SetMicMuted(false); break;
                case "--toggle-mic": SetMicMuted(!Settings.MicMuted); break;
                case "--background": break;
                default: ShowWindow(); break;
            }
        }

        // Registers all shortcuts. Returns the ones Windows refused (taken by another app or invalid).
        public string ApplyHotkeys()
        {
            hotkeys.Clear();
            var failed = new List<string>();
            if (!hotkeys.Add(Settings.Hotkey, () => SaveClip())) failed.Add(Settings.Hotkey);
            if (!hotkeys.Add(Settings.HotkeyPause, () => SetPaused(!Recorder.IsPaused))) failed.Add(Settings.HotkeyPause);
            if (!hotkeys.Add(Settings.HotkeyOpen, ShowWindow)) failed.Add(Settings.HotkeyOpen);
            if (!hotkeys.Add(Settings.HotkeyMic, () => SetMicMuted(!Settings.MicMuted))) failed.Add(Settings.HotkeyMic);
            foreach (var len in Settings.ClipLengths)
            {
                int sec = len.Seconds;
                if (!hotkeys.Add(len.Hotkey, () => SaveClip(sec))) failed.Add(len.Hotkey);
            }
            return string.Join(", ", failed);
        }

        // Accent colour for buttons/highlights, with readable text on top.
        public void ApplyAccent()
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(Settings.Accent); } catch { c = Color.FromRgb(0xF2, 0xF2, 0xF2); }
            double light = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            Resources["Accent"] = new SolidColorBrush(c);
            Resources["AccentText"] = new SolidColorBrush(light > 0.6 ? Color.FromRgb(0x0B, 0x0B, 0x0B) : Colors.White);
        }

        public void SetPaused(bool p)
        {
            Recorder.SetPaused(p); tray.Refresh();
            Notify(p ? L.T("Recording paused") : L.T("Recording resumed"),
                   p ? L.T("Nothing is being recorded.") : L.T("Saving the last {0} is ready again in a moment.", Settings.LengthText(Settings.ClipSeconds)), null);
        }

        // Mic mute: the mic keeps running but records silence, so nothing restarts and audio stays in sync.
        public void SetMicMuted(bool muted)
        {
            if (!Settings.RecordMic)
            {
                Notify(L.T("Microphone recording is off"), L.T("Turn it on in Settings → Audio first."), null);
                return;
            }
            Settings.MicMuted = muted; Settings.Save();
            Recorder.SetMicMuted(muted);
            tray.Refresh(); WriteStatus();
            Notify(muted ? L.T("Microphone muted") : L.T("Microphone on"),
                   muted ? L.T("Your voice won't be in clips until you unmute.") : L.T("Your voice is in clips again."), null);
        }

        public static readonly string StatusFile = Path.Combine(Paths.Temp, "status.txt");
        string lastClip = "";
        int clipCount;

        void WriteStatus()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatusFile));
                File.WriteAllText(StatusFile,
                    "recording=" + (Recorder.IsRecording ? 1 : 0) + "\n" +
                    "paused=" + (Recorder.IsPaused ? 1 : 0) + "\n" +
                    "buffered=" + Recorder.BufferedSeconds + "\n" +
                    "clip=" + Settings.ClipSeconds + "\n" +
                    "hotkey=" + Settings.Hotkey + "\n" +
                    "mic=" + (Recorder.HasMic ? 1 : 0) + "\n" +
                    "micmuted=" + (Recorder.HasMic && Settings.MicMuted ? 1 : 0) + "\n" +
                    "saving=" + (saving ? 1 : 0) + "\n" +
                    "saved=" + clipCount + "\n" +
                    "last=" + lastClip + "\n");
            }
            catch { }
        }

        // While LiteClip sits in the tray, its window parts aren't needed: give that memory back to Windows.
        void TrimWhenIdle()
        {
            bool busy = (window != null && window.IsVisible) || Toast.IsOpen || saving;
            if (busy) { idleSeconds = 0; return; }
            idleSeconds++;
            if (idleSeconds == 3 || idleSeconds % 300 == 0) Native.TrimMemory();
        }

        public void ShowWindow()
        {
            if (window == null || !window.IsLoaded)
            {
                window = new MainWindow();
                var w = window;
                w.Closed += (s, e) => { if (window == w) window = null; };   // let the closed window's memory go
            }
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        // Closes and reopens the window (after the language changed).
        public void ReopenWindow(bool showSettings)
        {
            if (window != null) { var old = window; window = null; old.Close(); }
            ShowWindow();
            if (showSettings) window.ShowSettings();
        }

        public void SetClipLength(int seconds)
        {
            Settings.ClipSeconds = seconds; Settings.Save();
            tray.Refresh();
            if (window != null) window.RefreshHeader();
        }

        // Called after Settings were changed in the app.
        public string SettingsChanged(bool restartRecording)
        {
            ApplyAccent();
            string failed = ApplyHotkeys();
            tray.Refresh();
            if (window != null) window.RefreshHeader();
            if (restartRecording) Recorder.Restart();
            Recorder.CleanUp();
            return failed;
        }

        public void Notify(string title, string text, string clip) { if (Settings.ShowToast) Toast.Show(title, text, clip); }

        bool saving;
        public void SaveClip() { SaveClip(Settings.ClipSeconds); }

        public void SaveClip(int seconds)
        {
            if (saving) return;
            saving = true;
            new Thread(() =>
            {
                try
                {
                    string path = Recorder.SaveClip(seconds);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lastClip = path; clipCount++; WriteStatus();
                        if (Settings.PlaySound) SaveSound.Play();
                        Notify(L.T("Clip saved"), Settings.LengthText(seconds) + "  ·  " + Path.GetFileNameWithoutExtension(path), path);
                        if (window != null) window.ClipAdded();
                    }));
                }
                catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => Toast.Show(L.T("Couldn't save clip"), ex.Message, null))); }
                finally { saving = false; }
            }) { IsBackground = true }.Start();
        }

        public void OpenEditor(string clip) { ShowWindow(); window.ShowEditor(clip); }

        public void OpenClipsFolder()
        {
            Directory.CreateDirectory(Settings.ClipsFolder);
            Process.Start("explorer.exe", "\"" + Settings.ClipsFolder + "\"");
        }

        void StopEverything() { tick.Stop(); tray.Dispose(); hotkeys.Dispose(); Recorder.Shutdown(); }

        public void Quit() { StopEverything(); Shutdown(); }

        // Starts a fresh copy of LiteClip (e.g. after switching portable mode on or off).
        public void Restart()
        {
            StopEverything();
            Program.ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = false, WorkingDirectory = Paths.AppDir });
            Shutdown();
        }

        // ---------------------------------------------------------------- remove LiteClip from this PC
        // Deletes everything LiteClip made (settings, thumbnails, temp files, buffer, Startup shortcut) and its program files.
        // Only deletes files and folders LiteClip knows are its own, never "the whole folder".
        // Your clips are kept. Windows doesn't let a running program delete itself, so LiteClip.exe is left for you to delete.
        public void Uninstall()
        {
            var settingsCopy = Settings;
            StopEverything();
            if (window != null) { try { window.Close(); } catch { } }

            if (Native.StartupIsThisCopy()) { try { Native.SetStartWithWindows(false); } catch { } }
            var folders = new List<string> { Paths.Temp, Recorder.BufferDirFor(settingsCopy) };
            if (Paths.Portable) folders.Add(Paths.Data);
            else { folders.Add(Paths.Data); folders.Add(Paths.Cache); }
            foreach (var f in folders) { try { if (Directory.Exists(f)) Directory.Delete(f, true); } catch { } }

            string app = Paths.AppDir;
            foreach (var f in new[] { @"tools\ffmpeg.exe", @"tools\ffprobe.exe", @"tools\FFMPEG-LICENSE.txt", "LiteClip.ico", "portable.txt" })
                try { File.Delete(Path.Combine(app, f)); } catch { }
            // files from the release zip, only if they're LiteClip's (they mention it)
            foreach (var f in new[] { "IS IT SAFE.txt", "FINGERPRINTS.txt", "LICENSE" })
            {
                string p = Path.Combine(app, f);
                try { if (File.Exists(p) && File.ReadAllText(p).Contains("LiteClip")) File.Delete(p); } catch { }
            }
            try { if (Directory.Exists(Paths.Languages)) foreach (var f in Directory.GetFiles(Paths.Languages, "*.txt")) File.Delete(f); } catch { }
            // The release zip's "source" folder (only if it really is LiteClip's source)
            string source = Path.Combine(app, "source");
            if (File.Exists(Path.Combine(source, "src", "Program.cs")) && File.Exists(Path.Combine(source, "build.ps1"))) { try { Directory.Delete(source, true); } catch { } }
            foreach (var d in new[] { Path.Combine(app, "tools"), Paths.Languages }) { try { Directory.Delete(d); } catch { } }   // only if empty

            Ui.Confirm(null, L.T("LiteClip is removed"),
                       L.T("LiteClip removed its settings, temporary files and program files.\n\nYour clips are still in:\n{0}\n\nWindows doesn't let a running app delete itself, so one step is left: delete LiteClip.exe from the folder that opens next.", settingsCopy.ClipsFolder),
                       L.T("Open the folder"), false, false);
            try { Process.Start("explorer.exe", "\"" + app + "\""); } catch { }   // nothing selected, so a stray Enter can't start it again
            Shutdown();
        }
    }

    // A short, soft two-tone "blip", generated in code (no sound files needed).
    static class SaveSound
    {
        static byte[] wav;

        public static void Play()
        {
            try
            {
                if (wav == null) wav = Make();
                new SoundPlayer(new MemoryStream(wav)).Play();
            }
            catch { }
        }

        static byte[] Make()
        {
            const int rate = 44100; int n = rate * 22 / 100;
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, f = t < 0.09 ? 880 : 1320;
                double env = Math.Min(1, i / 300.0) * Math.Exp(-t * 14);
                w.Write((short)(Math.Sin(2 * Math.PI * f * t) * env * 9000));
            }
            return ms.ToArray();
        }
    }
}
