// LiteClip - windows and controls: main window, clip library, editor, settings, timeline, pop-up, tray icon.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace LiteClip
{
    static class Ui
    {
        public static Brush Hex(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        public static T Find<T>(FrameworkElement root, string name) where T : class { return root.FindName(name) as T; }
        public static string Time(double s) { if (s < 0) s = 0; int m = (int)(s / 60); return m + ":" + (s - m * 60).ToString("00.0", CultureInfo.InvariantCulture); }
        public static string Num(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        public static void DarkTitleBar(Window w)
        {
            w.SourceInitialized += (s, e) =>
            {
                int on = 1; var hwnd = new WindowInteropHelper(w).Handle;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);   // dark title bar (Windows 10 2004+ / 11)
            };
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void BaseText(Control c)
        {
            c.Foreground = Hex("#EDEDED");
            c.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            c.FontSize = 13;
        }

        public static ImageSource AppIcon()
        {
            var icon = Tray.MakeIcon();
            return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        // Puts the clip file on the clipboard, like Ctrl+C in Explorer, so you can paste it into Discord, a chat or a folder.
        public static bool CopyFile(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var files = new System.Collections.Specialized.StringCollection();
                    files.Add(path);
                    Clipboard.SetFileDropList(files);
                    return true;
                }
                catch { Thread.Sleep(80); }      // another app is using the clipboard: try again
            }
            return false;
        }

        // Small dark yes/no dialog (or just one button). Returns true if the first button was clicked.
        public static bool Confirm(Window owner, string title, string text, string yes, bool danger, bool withCancel = true)
        {
            var w = new Window
            {
                Title = "LiteClip", Owner = owner, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Background = Hex("#141414"),
                ShowInTaskbar = owner == null, Topmost = owner == null,      // no LiteClip window behind it: keep it visible
            };
            BaseText(w); DarkTitleBar(w);
            bool result = false;
            var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MaxWidth = 440 };
            stack.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = text, Foreground = Hex("#8C8C8C"), Margin = new Thickness(0, 8, 0, 18), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = yes, Style = (Style)Application.Current.Resources[danger ? "Danger" : "Primary"] };
            var cancel = new Button { Content = L.T("Cancel"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = true };
            ok.Click += (s, e) => { result = true; w.Close(); };
            cancel.Click += (s, e) => w.Close();
            buttons.Children.Add(ok);
            if (withCancel) buttons.Children.Add(cancel); else ok.IsDefault = true;
            stack.Children.Add(buttons);
            w.Content = stack;
            w.ShowDialog();
            return result;
        }

        public static ContextMenu Menu()
        {
            return new ContextMenu { Background = Hex("#141414"), Foreground = Hex("#EDEDED"), BorderBrush = Hex("#2A2A2A") };
        }

        public static MenuItem MenuEntry(ContextMenu menu, string text, Action click)
        {
            var i = new MenuItem { Header = text, Foreground = Hex("#EDEDED") };
            i.Click += (s, e) => click();
            menu.Items.Add(i);
            return i;
        }
    }

    // ------------------------------------------------------------------ main window
    public class MainWindow : Window
    {
        readonly Grid root;
        readonly ContentControl body;
        readonly TextBlock status;
        readonly Ellipse dot;
        readonly StackPanel lenPanel;
        readonly DispatcherTimer timer;
        LibraryView library;
        EditorView editor;

        public MainWindow()
        {
            Title = "LiteClip"; Width = 1180; Height = 760; MinWidth = 860; MinHeight = 540;
            Background = Ui.Hex("#0B0B0B"); WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Icon = Ui.AppIcon();
            Ui.BaseText(this);
            Ui.DarkTitleBar(this);

            root = (Grid)App.LoadXaml("Main.xaml");
            Content = root;
            body = Ui.Find<ContentControl>(root, "Body");
            status = Ui.Find<TextBlock>(root, "Status");
            dot = Ui.Find<Ellipse>(root, "RecDot");
            lenPanel = Ui.Find<StackPanel>(root, "LenPanel");
            RefreshHeader();
            Ui.Find<Button>(root, "SaveNow").Click += (s, e) => App.Instance.SaveClip();
            Ui.Find<Button>(root, "OpenFolder").Click += (s, e) => App.Instance.OpenClipsFolder();
            Ui.Find<Button>(root, "OpenSettings").Click += (s, e) => ShowSettings();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => UpdateStatus();
            timer.Start();
            UpdateStatus();

            PreviewKeyDown += (s, e) => { if (editor != null && body.Content == editor.Root) editor.HandleKey(e); };
            Closed += (s, e) => { timer.Stop(); if (editor != null) editor.Close(); };
            ShowLibrary();
        }

        // One button per clip length from Settings.
        public void RefreshHeader()
        {
            lenPanel.Children.Clear();
            foreach (var len in App.Settings.ClipLengths)
            {
                int sec = len.Seconds;
                var rb = new RadioButton
                {
                    Content = Settings.LengthText(sec), GroupName = "len", Style = (Style)Application.Current.Resources["Seg"],
                    IsChecked = sec == App.Settings.ClipSeconds,
                    ToolTip = len.Hotkey.Length > 0 ? L.T("Shortcut for this length: {0}", len.Hotkey) : null,
                };
                rb.Checked += (s, e) => { if (App.Settings.ClipSeconds != sec) App.Instance.SetClipLength(sec); };
                lenPanel.Children.Add(rb);
            }
        }

        void UpdateStatus()
        {
            var r = App.Recorder;
            if (r.IsPaused) { status.Text = L.T("Paused"); dot.Fill = Ui.Hex("#555555"); return; }
            if (!r.IsRecording) { status.Text = r.LastError != null ? L.T("Not recording: {0}", r.LastError) : L.T("Starting…"); dot.Fill = Ui.Hex("#555555"); return; }
            dot.Fill = Ui.Hex("#FF4D4D");
            status.Text = L.T("Recording · last {0} ready · {1} saves {2}", Settings.LengthText(r.BufferedSeconds), App.Settings.Hotkey, Settings.LengthText(App.Settings.ClipSeconds)) +
                          (r.HasAudio ? "" : " · " + L.T("no sound")) + (r.HasMic ? " · " + (App.Settings.MicMuted ? L.T("mic muted") : L.T("mic on")) : "");
        }

        public void ClipAdded() { if (library != null && body.Content == library.Root) library.Reload(); }

        public void ShowLibrary()
        {
            if (editor != null) { editor.Close(); editor = null; }
            if (library == null) library = new LibraryView();
            library.Reload();
            body.Content = library.Root;
        }

        public void ShowEditor(string clip)
        {
            if (editor != null) editor.Close();
            editor = new EditorView(this, clip);
            body.Content = editor.Root;
        }

        public void ShowSettings()
        {
            if (editor != null) { editor.Close(); editor = null; }
            body.Content = new SettingsView(this).Root;
        }
    }

    // ------------------------------------------------------------------ clip library (grid of thumbnails)
    public class LibraryView
    {
        public readonly ScrollViewer Root;
        readonly WrapPanel grid;

        public LibraryView()
        {
            grid = new WrapPanel { Margin = new Thickness(18, 0, 18, 18) };
            Root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
        }

        public void Reload()
        {
            grid.Children.Clear();
            var dir = App.Settings.ClipsFolder;
            var files = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTime).ToList()
                : new List<FileInfo>();
            if (files.Count == 0)
            {
                grid.Children.Add(new TextBlock
                {
                    Text = L.T("No clips yet.\nPress {0} any time (even in a game) to save the last {1}.", App.Settings.Hotkey, Settings.LengthText(App.Settings.ClipSeconds)),
                    Foreground = Ui.Hex("#8C8C8C"), FontSize = 15, Margin = new Thickness(10, 40, 0, 0), LineHeight = 24,
                });
                return;
            }
            foreach (var f in files) grid.Children.Add(Card(f));
        }

        FrameworkElement Card(FileInfo file)
        {
            var thumb = new Image { Stretch = Stretch.UniformToFill };
            var badge = new TextBlock { Foreground = Ui.Hex("#EDEDED"), FontSize = 11, FontFamily = new FontFamily("Consolas") };
            var badgeBox = new Border
            {
                Background = Ui.Hex("#CC000000"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8), Child = badge,
            };
            var picture = new Grid { Height = 146, Background = Ui.Hex("#000000"), ClipToBounds = true };
            picture.Children.Add(thumb); picture.Children.Add(badgeBox);
            var pictureBox = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = picture };
            pictureBox.Clip = new RectangleGeometry(new Rect(0, 0, 260, 146), 10, 10);

            var name = new TextBlock { Text = file.LastWriteTime.ToString("ddd d MMM · HH:mm:ss"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 0) };
            var size = new TextBlock { Text = (file.Length / 1048576.0).ToString("0.0") + " MB", Foreground = Ui.Hex("#8C8C8C"), FontSize = 12, Margin = new Thickness(2, 2, 0, 0) };
            var stack = new StackPanel(); stack.Children.Add(pictureBox); stack.Children.Add(name); stack.Children.Add(size);
            var card = new Border
            {
                Width = 284, Padding = new Thickness(12), Margin = new Thickness(8), CornerRadius = new CornerRadius(14),
                Background = Ui.Hex("#141414"), Cursor = Cursors.Hand, Child = stack, ToolTip = file.Name,
            };
            card.MouseEnter += (s, e) => card.Background = Ui.Hex("#1E1E1E");
            card.MouseLeave += (s, e) => card.Background = Ui.Hex("#141414");
            card.MouseLeftButtonUp += (s, e) => App.Instance.OpenEditor(file.FullName);

            // Right-click: quick actions
            string path = file.FullName;
            var menu = Ui.Menu();
            Ui.MenuEntry(menu, L.T("Edit"), () => App.Instance.OpenEditor(path));
            Ui.MenuEntry(menu, L.T("Copy"), () => { if (Ui.CopyFile(path)) App.Instance.Notify(L.T("Copied"), L.T("Paste it anywhere with Ctrl+V."), null); });
            Ui.MenuEntry(menu, L.T("Show in folder"), () => Process.Start("explorer.exe", "/select,\"" + path + "\""));
            card.ContextMenu = menu;

            // Thumbnail + length are made in the background (and cached) so the list opens instantly.
            string key = System.IO.Path.GetFileNameWithoutExtension(path) + "_" + file.Length;
            string jpg = System.IO.Path.Combine(Paths.Thumbs, key + ".jpg"), dur = System.IO.Path.Combine(Paths.Thumbs, key + ".txt");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Paths.Thumbs);
                    if (!File.Exists(dur)) File.WriteAllText(dur, Ui.Num(Recorder.Duration(path)));
                    if (!File.Exists(jpg))
                        Recorder.Run(Recorder.FFmpeg, "-hide_banner -loglevel error -y -ss 1 -i \"" + path + "\" -frames:v 1 -vf scale=520:-2 -q:v 4 \"" + jpg + "\"", 30000);
                    double seconds; double.TryParse(File.ReadAllText(dur), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.DecodePixelWidth = 520; bmp.UriSource = new Uri(jpg); bmp.EndInit(); bmp.Freeze();
                    Root.Dispatcher.BeginInvoke(new Action(() => { thumb.Source = bmp; badge.Text = Ui.Time(seconds).Replace(".0", ""); }));
                }
                catch { }
            });
            return card;
        }
    }

    // ------------------------------------------------------------------ editor (preview + trim)
    public class EditorView
    {
        public readonly Grid Root;
        readonly MainWindow owner;
        readonly string clip;
        readonly MediaElement player;
        readonly Timeline timeline;
        readonly Button playPause, export, delete, fit;
        readonly TextBlock time, selection, status;
        readonly CheckBox exact;
        readonly DispatcherTimer ticker;
        bool playing, armedDelete;

        public EditorView(MainWindow owner, string clip)
        {
            this.owner = owner; this.clip = clip;
            Root = (Grid)App.LoadXaml("Editor.xaml");
            player = Ui.Find<MediaElement>(Root, "Player");
            playPause = Ui.Find<Button>(Root, "PlayPause");
            export = Ui.Find<Button>(Root, "Export");
            delete = Ui.Find<Button>(Root, "Delete");
            fit = Ui.Find<Button>(Root, "FitDiscord");
            time = Ui.Find<TextBlock>(Root, "Time");
            selection = Ui.Find<TextBlock>(Root, "Selection");
            status = Ui.Find<TextBlock>(Root, "EditStatus");
            exact = Ui.Find<CheckBox>(Root, "Exact");
            Ui.Find<TextBlock>(Root, "ClipName").Text = System.IO.Path.GetFileNameWithoutExtension(clip);

            timeline = new Timeline();
            Ui.Find<Border>(Root, "TimelineHost").Child = timeline;
            timeline.Seek += t => { player.Position = TimeSpan.FromSeconds(t); timeline.Position = t; ShowTimes(); };
            timeline.RangeChanged += ShowTimes;

            Ui.Find<Button>(Root, "Back").Click += (s, e) => owner.ShowLibrary();
            Ui.Find<Button>(Root, "ShowInFolder").Click += (s, e) => Process.Start("explorer.exe", "/select,\"" + clip + "\"");
            Ui.Find<Button>(Root, "CopyClip").Click += (s, e) =>
                status.Text = Ui.CopyFile(clip) ? L.T("Copied. Paste it into Discord, a chat or a folder with Ctrl+V.") : L.T("Couldn't copy: another app is using the clipboard. Try again.");
            Ui.Find<Button>(Root, "SetStart").Click += (s, e) => SetStart();
            Ui.Find<Button>(Root, "SetEnd").Click += (s, e) => SetEnd();
            Ui.Find<Button>(Root, "ResetRange").Click += (s, e) => { timeline.Start = 0; timeline.End = timeline.Duration; timeline.InvalidateVisual(); ShowTimes(); };
            playPause.Click += (s, e) => TogglePlay();
            export.Click += (s, e) => Export();
            delete.Click += (s, e) => Delete();
            fit.Click += (s, e) => FitForDiscord();

            player.MediaOpened += (s, e) =>
            {
                double d = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
                timeline.Duration = Math.Max(0.1, d); timeline.Start = 0; timeline.End = timeline.Duration;
                timeline.InvalidateVisual(); ShowTimes();
            };
            player.MediaFailed += (s, e) => status.Text = L.T("Couldn't play this clip: {0}", e.ErrorException.Message);
            player.Source = new Uri(clip);
            player.Play(); player.Pause();          // shows the first frame

            ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            ticker.Tick += (s, e) =>
            {
                if (!playing) return;
                double p = player.Position.TotalSeconds;
                if (p >= timeline.End) { Pause(); player.Position = TimeSpan.FromSeconds(timeline.End); p = timeline.End; }
                timeline.Position = p; timeline.InvalidateVisual(); ShowTimes();
            };
            ticker.Start();
            status.Text = L.T("Space: play/pause · ← →: step · I / O: set start / end · drag the white handles to trim.");
        }

        void TogglePlay()
        {
            if (playing) { Pause(); return; }
            if (player.Position.TotalSeconds >= timeline.End - 0.05 || player.Position.TotalSeconds < timeline.Start)
                player.Position = TimeSpan.FromSeconds(timeline.Start);
            player.Play(); playing = true; playPause.Content = "❚❚";
        }

        void Pause() { player.Pause(); playing = false; playPause.Content = "▶"; }

        void SetStart() { timeline.Start = Math.Min(timeline.Position, timeline.End - 0.2); timeline.InvalidateVisual(); ShowTimes(); }
        void SetEnd() { timeline.End = Math.Max(timeline.Position, timeline.Start + 0.2); timeline.InvalidateVisual(); ShowTimes(); }

        public void HandleKey(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
            else if (e.Key == Key.Left || e.Key == Key.Right)
            {
                double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.1 : 1;
                double t = Math.Max(0, Math.Min(timeline.Duration, timeline.Position + (e.Key == Key.Left ? -step : step)));
                player.Position = TimeSpan.FromSeconds(t); timeline.Position = t; timeline.InvalidateVisual(); ShowTimes(); e.Handled = true;
            }
            else if (e.Key == Key.I) { SetStart(); e.Handled = true; }
            else if (e.Key == Key.O) { SetEnd(); e.Handled = true; }
        }

        void ShowTimes()
        {
            time.Text = Ui.Time(timeline.Position) + " / " + Ui.Time(timeline.Duration);
            selection.Text = Ui.Time(timeline.Start) + " → " + Ui.Time(timeline.End) + "  (" + (timeline.End - timeline.Start).ToString("0.0", CultureInfo.InvariantCulture) + "s)";
        }

        void Export()
        {
            double start = timeline.Start, length = timeline.End - timeline.Start;
            bool precise = exact.IsChecked == true;
            string folder = System.IO.Path.GetDirectoryName(clip);
            string baseName = System.IO.Path.GetFileNameWithoutExtension(clip) + " (trimmed)";
            string output = System.IO.Path.Combine(folder, baseName + ".mp4");
            for (int i = 2; File.Exists(output); i++) output = System.IO.Path.Combine(folder, baseName + " " + i + ".mp4");

            string args = "-hide_banner -loglevel error -y -ss " + Ui.Num(start) + " -i \"" + clip + "\" -t " + Ui.Num(length) + " ";
            args += precise   // -map 0 keeps every audio track (game + mic when they're separate)
                ? "-map 0 " + Recorder.VideoEncoderArgs(App.Recorder.Encoder, App.Settings.QualityCq, App.Settings.Fps) + " -c:a aac -b:a 160k "
                : "-map 0 -c copy -avoid_negative_ts make_zero ";
            args += "-movflags +faststart \"" + output + "\"";

            export.IsEnabled = false; status.Text = precise ? L.T("Saving (cutting on the exact frame)…") : L.T("Saving…");
            new Thread(() =>
            {
                string msg; bool ok = false;
                try { Recorder.Run(Recorder.FFmpeg, args, 600000); ok = true; msg = L.T("Saved: {0} (your original clip is untouched)", System.IO.Path.GetFileName(output)); }
                catch (Exception ex) { msg = L.T("Couldn't save: {0}", ex.Message); }
                Root.Dispatcher.BeginInvoke(new Action(() =>
                {
                    export.IsEnabled = true; status.Text = msg;
                    if (ok) Toast.Show(L.T("Trimmed clip saved"), System.IO.Path.GetFileNameWithoutExtension(output), output);
                }));
            }) { IsBackground = true }.Start();
        }

        // Smaller copy of the selected part that fits Discord's upload limit, then copied so you can paste it.
        void FitForDiscord()
        {
            double start = timeline.Start, length = timeline.End - timeline.Start;
            int limit = App.Settings.ShareLimitMB;
            fit.IsEnabled = false;
            status.Text = L.T("Making a copy under {0} MB…", limit);
            new Thread(() =>
            {
                string output = null, error = null;
                try { output = Recorder.FitToSize(App.Settings, clip, start, length, limit); }
                catch (Exception ex) { error = ex.Message; }
                Root.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fit.IsEnabled = true;
                    if (output == null) { status.Text = L.T("Couldn't make it: {0}", error); return; }
                    double mb = new FileInfo(output).Length / 1000000.0;
                    bool copied = Ui.CopyFile(output);
                    status.Text = L.T("Saved {0} ({1} MB).", System.IO.Path.GetFileName(output), mb.ToString("0.0", CultureInfo.InvariantCulture)) + " " +
                                  (copied ? L.T("It's copied: paste it in Discord with Ctrl+V.") : "");
                    if (App.Instance.Window == null || !App.Instance.Window.IsActive)
                        Toast.Show(L.T("Ready for Discord"), L.T("Copied. Paste it with Ctrl+V."), output);
                }));
            }) { IsBackground = true }.Start();
        }

        void Delete()
        {
            if (!armedDelete)
            {
                armedDelete = true; delete.Content = L.T("Click again to delete");
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (s, e) => { t.Stop(); armedDelete = false; delete.Content = L.T("Delete"); };
                t.Start();
                return;
            }
            Close();
            try { Native.Recycle(clip); } catch (Exception ex) { status.Text = L.T("Couldn't delete: {0}", ex.Message); return; }
            owner.ShowLibrary();
        }

        public void Close() { ticker.Stop(); try { player.Stop(); player.Close(); } catch { } }
    }

    // ------------------------------------------------------------------ settings
    public class SettingsView
    {
        public readonly ScrollViewer Root;

        readonly MainWindow owner;

        public SettingsView(MainWindow owner)
        {
            this.owner = owner;
            Root = (ScrollViewer)App.LoadXaml("Settings.xaml");
            var s = App.Settings;
            Func<string, TextBox> box = n => Ui.Find<TextBox>(Root, n);
            Func<string, CheckBox> check = n => Ui.Find<CheckBox>(Root, n);
            Func<string, Slider> slider = n => Ui.Find<Slider>(Root, n);
            Func<string, TextBlock> text = n => Ui.Find<TextBlock>(Root, n);
            Func<string, Button> button = n => Ui.Find<Button>(Root, n);
            var statusText = text("SettingsStatus");

            // ---- clips & shortcuts
            var lengths = box("Lengths"); lengths.Text = Settings.Format(s.ClipLengths);
            var keySave = box("KeySave"); var keyPause = box("KeyPause"); var keyOpen = box("KeyOpen"); var keyMic = box("KeyMic");
            keySave.Text = s.Hotkey; keyPause.Text = s.HotkeyPause; keyOpen.Text = s.HotkeyOpen; keyMic.Text = s.HotkeyMic;
            foreach (var k in new[] { keySave, keyPause, keyOpen, keyMic }) CaptureShortcut(k, text("KeyNote"));

            // ---- video
            var screens = WinForms.Screen.AllScreens;
            var monitorCount = Math.Max(1, screens.Length);
            var monitors = Choices("Monitors", Enumerable.Range(0, monitorCount).Select(i => L.T("Monitor {0}", i + 1) + (screens.Length > i ? "  (" + screens[i].Bounds.Width + "×" + screens[i].Bounds.Height + ")" : "")).ToArray(), null);
            SelectIndex(monitors, s.Monitor);
            var resolutions = Choices("Resolutions", new[] { "Native", "1440p", "1080p", "720p" }, s.Resolution);
            var fpsCustom = box("FpsCustom");
            var fpsChoices = Choices("Fps", new[] { "30", "60", "120", "144" }, s.Fps.ToString());
            fpsCustom.Text = s.Fps.ToString();
            foreach (RadioButton rb in fpsChoices.Children) rb.Checked += (o, e) => fpsCustom.Text = (string)((RadioButton)o).Content;
            var codecs = Choices("Codecs", new[] { "H.264", "HEVC", "AV1" }, s.Codec);
            var encoders = Choices("Encoders", new[] { "Auto", "NVIDIA", "AMD", "Intel", "Windows" }, s.Encoder);
            var quality = slider("Quality"); quality.Value = s.QualityCq;
            Action showQ = () => text("QualityValue").Text = (int)quality.Value + "  (" + (quality.Value <= 19 ? L.T("very high") : quality.Value <= 25 ? L.T("balanced") : quality.Value <= 30 ? L.T("smaller") : L.T("smallest")) + ")";
            quality.ValueChanged += (o, e) => showQ(); showQ();
            check("Cursor").IsChecked = s.ShowCursor;

            // ---- audio
            check("Game").IsChecked = s.RecordAudio;
            check("Mic").IsChecked = s.RecordMic;
            check("MicMuted").IsChecked = s.MicMuted;
            check("Separate").IsChecked = s.SeparateTracks;
            var gameVol = slider("GameVol"); gameVol.Value = s.GameVolume;
            var micVol = slider("MicVol"); micVol.Value = s.MicVolume;
            gameVol.ValueChanged += (o, e) => text("GameVolValue").Text = (int)gameVol.Value + "%"; text("GameVolValue").Text = s.GameVolume + "%";
            micVol.ValueChanged += (o, e) => text("MicVolValue").Text = (int)micVol.Value + "%"; text("MicVolValue").Text = s.MicVolume + "%";
            var micList = Ui.Find<StackPanel>(Root, "MicDevices");
            var devices = AudioLoopback.ListDevices(true);
            devices.Insert(0, new KeyValuePair<string, string>("", L.T("Windows default microphone")));
            foreach (var d in devices)
            {
                var rb = new RadioButton { Content = d.Value, Tag = d.Key, GroupName = "micdev", Style = (Style)Application.Current.Resources["Seg"], HorizontalAlignment = HorizontalAlignment.Left, IsChecked = d.Key == s.MicDevice };
                micList.Children.Add(rb);
            }
            if (!micList.Children.OfType<RadioButton>().Any(r => r.IsChecked == true)) ((RadioButton)micList.Children[0]).IsChecked = true;

            // ---- files
            var folderBox = box("FolderBox"); folderBox.Text = s.ClipsFolder;
            var fileName = box("FileName"); fileName.Text = s.FileName;
            Action preview = () => text("FileNamePreview").Text = L.T("e.g.") + "  " + Recorder.FileNameFor(fileName.Text, "Counter-Strike 2", s.ClipSeconds, DateTime.Now);
            fileName.TextChanged += (o, e) => preview(); preview();
            box("KeepDays").Text = s.KeepDays.ToString();
            box("MaxGB").Text = s.MaxClipsGB.ToString(CultureInfo.InvariantCulture);
            box("ShareLimit").Text = s.ShareLimitMB.ToString();
            button("ChangeFolder").Click += (o, e) => { var p = PickFolder(folderBox.Text, L.T("Where should LiteClip save clips?")); if (p != null) folderBox.Text = p; };

            // ---- look & feel
            check("Toast").IsChecked = s.ShowToast;
            check("Sound").IsChecked = s.PlaySound;
            var accentBox = box("AccentBox"); accentBox.Text = s.Accent;
            var swatches = Ui.Find<WrapPanel>(Root, "Swatches");
            foreach (var hex in new[] { "#F2F2F2", "#FF4D4D", "#FF9F43", "#FFD93D", "#4ADE80", "#38BDF8", "#818CF8", "#F472B6" })
            {
                var sw = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 6, 0), Background = Ui.Hex(hex), Cursor = Cursors.Hand, ToolTip = hex };
                string h = hex; sw.MouseLeftButtonUp += (o, e) => accentBox.Text = h;
                swatches.Children.Add(sw);
            }
            var languages = Ui.Find<WrapPanel>(Root, "Languages");
            foreach (var lang in L.Available())
                languages.Children.Add(new RadioButton { Content = L.DisplayName(lang), Tag = lang, GroupName = "lang", Style = (Style)Application.Current.Resources["Seg"], IsChecked = lang == s.Language });
            if (!languages.Children.OfType<RadioButton>().Any(r => r.IsChecked == true)) ((RadioButton)languages.Children[0]).IsChecked = true;
            button("OpenLang").Click += (o, e) => { Directory.CreateDirectory(Paths.Languages); Process.Start("explorer.exe", "\"" + Paths.Languages + "\""); };

            // ---- advanced
            check("Startup").IsChecked = Native.StartsWithWindows;
            box("AfterSave").Text = s.AfterSaveCommand;
            var bufferBox = box("BufferBox");
            string bufferFolder = s.BufferFolder;
            bufferBox.Text = bufferFolder.Length > 0 ? bufferFolder : L.T("(default)");
            button("ChangeBuffer").Click += (o, e) => { var p = PickFolder(bufferFolder, L.T("Where should LiteClip keep its rolling recording?")); if (p != null) { bufferFolder = p; bufferBox.Text = p; } };
            button("DefaultBuffer").Click += (o, e) => { bufferFolder = ""; bufferBox.Text = L.T("(default)"); };
            var gpuFrames = Choices("GpuFrames", new[] { "1", "2", "3", "4" }, s.GpuFrames.ToString());
            box("CustomArgs").Text = s.CustomEncoderArgs;
            var command = box("Command");
            command.Text = App.Recorder.LastCommand ?? L.T("(not recording right now)");
            button("CopyCommand").Click += (o, e) => { try { Clipboard.SetText(command.Text); statusText.Text = L.T("Command copied."); } catch { } };

            text("PortableText").Text = Paths.Portable
                ? L.T("On. Settings, thumbnails and the recording buffer are kept inside LiteClip's own folder ({0}), so it leaves nothing on this PC and can run from a USB stick.", System.IO.Path.Combine(Paths.AppDir, "data"))
                : L.T("Off. Turn it on to carry LiteClip on a USB stick: settings, thumbnails and the recording buffer move into LiteClip's own folder. You can also turn it on by putting an empty file named portable.txt next to LiteClip.exe.");
            var portable = button("TogglePortable");
            portable.Content = Paths.Portable ? L.T("Turn off portable mode (restarts LiteClip)") : L.T("Turn on portable mode (restarts LiteClip)");
            portable.Click += (o, e) => TogglePortable(statusText);

            bool resetArmed = false;
            button("ResetAll").Click += (o, e) =>
            {
                var b = (Button)o;
                if (!resetArmed) { resetArmed = true; b.Content = L.T("Click again to reset"); return; }
                var fresh = new Settings();
                CopyInto(fresh, App.Settings); App.Settings.Save();
                App.Instance.SettingsChanged(true);
                owner.ShowSettings();
            };
            button("OpenIni").Click += (o, e) => Process.Start("notepad.exe", "\"" + Settings.FilePath + "\"");

            button("Uninstall").Click += (o, e) =>
            {
                if (Ui.Confirm(owner, L.T("Are you sure? It's permanent."),
                    L.T("LiteClip will delete its settings, thumbnails, temporary files, recording buffer, start-with-Windows shortcut and program files. This can't be undone.\n\nYour clips stay in {0}.", s.ClipsFolder),
                    L.T("Remove LiteClip"), true))
                    App.Instance.Uninstall();
            };

            text("About").Text =
                L.T("LiteClip {0} · free and open source (MIT license).", Program.Version) + "\n" +
                L.T("Recording with {0}. Clips never leave your PC: no internet access, no accounts, no tracking.", EncoderName(App.Recorder.Encoder)) + "\n" +
                L.T("Settings file: {0}", Settings.FilePath) + "\n" + L.T("Program folder: {0}", Recorder.AppDir);

            button("Back").Click += (o, e) => owner.ShowLibrary();
            RoutedEventHandler save = (o, e) =>
            {
                // read everything back
                var n = new Settings();
                n.ClipLengths = Settings.Parse(lengths.Text);
                n.ClipSeconds = n.ClipLengths.Any(c => c.Seconds == s.ClipSeconds) ? s.ClipSeconds : n.ClipLengths[Math.Min(1, n.ClipLengths.Count - 1)].Seconds;
                n.Hotkey = keySave.Text; n.HotkeyPause = keyPause.Text; n.HotkeyOpen = keyOpen.Text; n.HotkeyMic = keyMic.Text;
                n.Monitor = Math.Max(0, monitors.Children.OfType<RadioButton>().ToList().FindIndex(r => r.IsChecked == true));
                n.Resolution = Selected(resolutions, "Native");
                int fps; n.Fps = int.TryParse(fpsCustom.Text.Trim(), out fps) ? Math.Max(10, Math.Min(240, fps)) : 60;
                n.Codec = Selected(codecs, "H.264");
                n.Encoder = Selected(encoders, "Auto");
                n.QualityCq = (int)quality.Value;
                n.ShowCursor = check("Cursor").IsChecked == true;
                n.RecordAudio = check("Game").IsChecked == true; n.GameVolume = (int)gameVol.Value;
                n.RecordMic = check("Mic").IsChecked == true; n.MicVolume = (int)micVol.Value;
                n.MicMuted = check("MicMuted").IsChecked == true;
                var dev = micList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                n.MicDevice = dev != null ? (string)dev.Tag : "";
                n.SeparateTracks = check("Separate").IsChecked == true;
                n.ClipsFolder = folderBox.Text;
                n.FileName = fileName.Text.Trim().Length > 0 ? fileName.Text.Trim() : "{app} {date} {time}";
                int days; n.KeepDays = int.TryParse(box("KeepDays").Text, out days) ? Math.Max(0, days) : 0;
                double gb; n.MaxClipsGB = double.TryParse(box("MaxGB").Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out gb) ? Math.Max(0, gb) : 0;
                int share; n.ShareLimitMB = int.TryParse(box("ShareLimit").Text.Trim(), out share) ? Math.Max(1, Math.Min(4000, share)) : 10;
                n.ShowToast = check("Toast").IsChecked == true;
                n.PlaySound = check("Sound").IsChecked == true;
                n.Accent = ValidColor(accentBox.Text) ? accentBox.Text.Trim() : s.Accent;
                var langChoice = languages.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                n.Language = langChoice != null ? (string)langChoice.Tag : "English";
                n.AfterSaveCommand = box("AfterSave").Text.Trim();
                n.BufferFolder = bufferFolder;
                int frames; n.GpuFrames = int.TryParse(Selected(gpuFrames, "2"), out frames) ? frames : 2;
                n.CustomEncoderArgs = box("CustomArgs").Text.Trim();

                bool restart = n.Monitor != s.Monitor || n.Fps != s.Fps || n.Resolution != s.Resolution || n.Codec != s.Codec || n.Encoder != s.Encoder ||
                               n.QualityCq != s.QualityCq || n.ShowCursor != s.ShowCursor || n.RecordAudio != s.RecordAudio || n.GameVolume != s.GameVolume ||
                               n.RecordMic != s.RecordMic || n.MicVolume != s.MicVolume || n.MicDevice != s.MicDevice || n.SeparateTracks != s.SeparateTracks ||
                               n.BufferFolder != s.BufferFolder || n.MaxClipSeconds != s.MaxClipSeconds || n.GpuFrames != s.GpuFrames ||
                               n.CustomEncoderArgs != s.CustomEncoderArgs;
                bool languageChanged = n.Language != s.Language;
                CopyInto(n, s);
                s.Save();
                App.Recorder.SetMicMuted(s.MicMuted);
                try { Native.SetStartWithWindows(check("Startup").IsChecked == true); } catch { }
                string failed = App.Instance.SettingsChanged(restart);
                if (languageChanged)
                {
                    L.Load(s.Language);
                    App.Instance.SettingsChanged(false);          // tray menu in the new language
                    Root.Dispatcher.BeginInvoke(new Action(() => App.Instance.ReopenWindow(true)));
                    return;
                }
                lengths.Text = Settings.Format(s.ClipLengths);
                statusText.Text = L.T("Saved.") + (restart ? " " + L.T("Recording restarted with the new settings.") : "") +
                                  (failed.Length > 0 ? "  " + L.T("These shortcuts are invalid or used by another app: {0}", failed) : "");
                if (restart)
                {
                    // show the new FFmpeg command once recording has restarted
                    var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    later.Tick += (a, b) => { later.Stop(); command.Text = App.Recorder.LastCommand ?? L.T("(not recording right now)"); };
                    later.Start();
                }
            };
            button("SaveSettings").Click += save;
            button("SaveTop").Click += save;
        }

        // Portable mode on/off: moves the settings file, then restarts LiteClip.
        static void TogglePortable(TextBlock statusText)
        {
            try
            {
                string portableSettings = System.IO.Path.Combine(Paths.AppDir, "data", "settings.ini");
                string normalDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteClip");
                if (Paths.Portable)
                {
                    Directory.CreateDirectory(normalDir);
                    if (File.Exists(portableSettings)) File.Copy(portableSettings, System.IO.Path.Combine(normalDir, "settings.ini"), true);
                    File.Delete(Paths.PortableFlag);
                }
                else
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(portableSettings));
                    if (File.Exists(Settings.FilePath)) File.Copy(Settings.FilePath, portableSettings, true);
                    File.WriteAllText(Paths.PortableFlag, "LiteClip portable mode: settings and temporary files are kept in the \"data\" folder next to LiteClip.exe.\r\nDelete this file to turn portable mode off.\r\n");
                }
                App.Instance.Restart();
            }
            catch (Exception ex) { statusText.Text = L.T("Couldn't switch: {0}", ex.Message); }
        }

        // A row of segmented choices; returns its panel.
        WrapPanel Choices(string panelName, string[] options, string selected)
        {
            var panel = Ui.Find<WrapPanel>(Root, panelName);
            foreach (var o in options)
                panel.Children.Add(new RadioButton { Content = o, GroupName = panelName, Style = (Style)Application.Current.Resources["Seg"], IsChecked = selected != null && o == selected });
            if (selected != null && !panel.Children.OfType<RadioButton>().Any(r => r.IsChecked == true) && panel.Children.Count > 0 && panelName != "Fps") ((RadioButton)panel.Children[0]).IsChecked = true;
            return panel;
        }

        static void SelectIndex(WrapPanel panel, int index)
        {
            var items = panel.Children.OfType<RadioButton>().ToList();
            if (items.Count > 0) items[Math.Max(0, Math.Min(items.Count - 1, index))].IsChecked = true;
        }

        static string Selected(WrapPanel panel, string fallback)
        {
            var r = panel.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true);
            return r != null ? (string)r.Content : fallback;
        }

        // Click a box, press keys -> "Ctrl+Alt+S". Backspace/Delete clears it.
        static void CaptureShortcut(TextBox target, TextBlock note)
        {
            target.PreviewKeyDown += (o, e) =>
            {
                e.Handled = true;
                Key k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k == Key.Back || k == Key.Delete) { target.Text = ""; return; }
                if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LeftShift || k == Key.RightShift || k == Key.LWin || k == Key.RWin) return;
                var parts = new List<string>();
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
                bool fKey = k >= Key.F1 && k <= Key.F24;
                if (parts.Count == 0 && !fKey) { note.Text = L.T("Add Ctrl, Alt or Shift (or use an F-key). Backspace clears a box."); return; }
                parts.Add(k.ToString());
                target.Text = string.Join("+", parts);
            };
        }

        static string PickFolder(string start, string title)
        {
            using (var d = new WinForms.FolderBrowserDialog { SelectedPath = start, Description = title })
                return d.ShowDialog() == WinForms.DialogResult.OK ? d.SelectedPath : null;
        }

        static bool ValidColor(string s) { try { ColorConverter.ConvertFromString(s.Trim()); return true; } catch { return false; } }

        // Copies every setting from one object to another (keeps the same Settings instance everywhere).
        static void CopyInto(Settings from, Settings to)
        {
            foreach (var f in typeof(Settings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (f.Name != "FirstRun") f.SetValue(to, f.GetValue(from));
        }

        static string EncoderName(string e)
        {
            string codec = e.StartsWith("hevc") ? "HEVC" : e.StartsWith("av1") ? "AV1" : "H.264";
            string gpu = e.EndsWith("_nvenc") ? L.T("your NVIDIA graphics card (NVENC)") : e.EndsWith("_amf") ? L.T("your AMD graphics card (AMF)") : e.EndsWith("_qsv") ? "Intel Quick Sync" : L.T("Windows' built-in encoder");
            return L.T("{0} on {1}", codec, gpu);
        }
    }

    // ------------------------------------------------------------------ trim timeline
    public class Timeline : FrameworkElement
    {
        public double Duration = 1, Start, End = 1, Position;
        public event Action<double> Seek;
        public event Action RangeChanged;
        int drag;           // 0 none, 1 start handle, 2 end handle, 3 playhead
        const double Pad = 12, Top = 8, H = 30;

        static readonly Brush Track = Ui.Hex("#1A1A1A"), Selected = Ui.Hex("#3A3A3A"), White = Ui.Hex("#F2F2F2"), Dim = Ui.Hex("#8C8C8C");
        static readonly Typeface Face = new Typeface("Consolas");

        public Timeline() { Cursor = Cursors.Hand; }

        double X(double t) { return Pad + (ActualWidth - 2 * Pad) * (Duration > 0 ? t / Duration : 0); }
        double T(double x) { return Math.Max(0, Math.Min(Duration, (x - Pad) / Math.Max(1, ActualWidth - 2 * Pad) * Duration)); }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));     // whole area is clickable
            dc.DrawRoundedRectangle(Track, null, new Rect(Pad, Top, Math.Max(0, ActualWidth - 2 * Pad), H), 8, 8);
            double xs = X(Start), xe = X(End), xp = X(Position);
            dc.DrawRoundedRectangle(Selected, null, new Rect(xs, Top, Math.Max(0, xe - xs), H), 6, 6);
            dc.DrawRoundedRectangle(White, null, new Rect(xs - 3, Top - 3, 6, H + 6), 3, 3);
            dc.DrawRoundedRectangle(White, null, new Rect(xe - 3, Top - 3, 6, H + 6), 3, 3);
            dc.DrawRectangle(White, null, new Rect(xp - 1, Top - 6, 2, H + 12));
            dc.DrawEllipse(White, null, new Point(xp, Top - 6), 4, 4);

            // second markers
            double step = Duration > 40 ? 10 : Duration > 15 ? 5 : 1;
            for (double t = 0; t <= Duration + 0.001; t += step)
            {
                var ft = new FormattedText(Ui.Time(t).Replace(".0", ""), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Dim);
                dc.DrawText(ft, new Point(Math.Min(X(t), ActualWidth - ft.Width) - (t == 0 ? 0 : ft.Width / 2), Top + H + 4));
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            double x = e.GetPosition(this).X;
            if (Math.Abs(x - X(Start)) < 9) drag = 1;
            else if (Math.Abs(x - X(End)) < 9) drag = 2;
            else { drag = 3; Fire(T(x)); }
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            double x = e.GetPosition(this).X;
            Cursor = (Math.Abs(x - X(Start)) < 9 || Math.Abs(x - X(End)) < 9) ? Cursors.SizeWE : Cursors.Hand;
            if (drag == 1) { Start = Math.Min(T(x), End - 0.2); InvalidateVisual(); if (RangeChanged != null) RangeChanged(); Fire(Start); }
            else if (drag == 2) { End = Math.Max(T(x), Start + 0.2); InvalidateVisual(); if (RangeChanged != null) RangeChanged(); Fire(End); }
            else if (drag == 3) Fire(T(x));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { drag = 0; ReleaseMouseCapture(); }

        void Fire(double t) { Position = t; InvalidateVisual(); if (Seek != null) Seek(t); }
    }

    // ------------------------------------------------------------------ small pop-up ("Clip saved")
    public static class Toast
    {
        static Window current;
        public static bool IsOpen { get { return current != null; } }

        public static void Show(string title, string text, string clipPath)
        {
            if (current != null) { try { current.Close(); } catch { } }
            var w = new Window
            {
                Title = "LiteClip toast", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowActivated = false, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight, Opacity = 0,
            };
            Ui.BaseText(w);
            var dot = new Ellipse { Width = 9, Height = 9, Fill = Ui.Hex(clipPath != null ? "#F2F2F2" : "#FF4D4D"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(dot); head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            var stack = new StackPanel();
            stack.Children.Add(head);
            var body = new TextBlock { Text = text, Foreground = Ui.Hex("#8C8C8C"), Margin = new Thickness(18, 3, 0, 0), MaxWidth = 340, TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(body);
            if (clipPath != null)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 10, 0, 0) };
                var edit = new Button { Content = L.T("Edit"), Style = (Style)Application.Current.Resources["Primary"], Padding = new Thickness(12, 5, 12, 5) };
                var copy = new Button { Content = L.T("Copy"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
                var folder = new Button { Content = L.T("Show"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
                edit.Click += (s, e) => { w.Close(); App.Instance.OpenEditor(clipPath); };
                copy.Click += (s, e) => { body.Text = Ui.CopyFile(clipPath) ? L.T("Copied. Paste it with Ctrl+V.") : L.T("Couldn't copy, try again."); };
                folder.Click += (s, e) => { w.Close(); Process.Start("explorer.exe", "/select,\"" + clipPath + "\""); };
                buttons.Children.Add(edit); buttons.Children.Add(copy); buttons.Children.Add(folder);
                stack.Children.Add(buttons);
            }
            w.Content = new Border
            {
                Background = Ui.Hex("#F2141414"), CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 13, 18, 14),
                BorderBrush = Ui.Hex("#2A2A2A"), BorderThickness = new Thickness(1), Child = stack, MinWidth = 260,
            };
            w.Loaded += (s, e) =>
            {
                var area = SystemParameters.WorkArea;
                w.Left = area.Right - w.ActualWidth - 18; w.Top = area.Top + 62;
                w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            };
            var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(clipPath != null ? 5 : 4) };
            life.Tick += (s, e) =>
            {
                if (w.IsMouseOver) return;      // stays while you hover it
                life.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                fade.Completed += (a, b) => w.Close();
                w.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            w.Closed += (s, e) => { life.Stop(); if (current == w) current = null; };
            current = w;
            w.Show();
            life.Start();
        }
    }

    // ------------------------------------------------------------------ tray icon + menu
    public class Tray : IDisposable
    {
        readonly WinForms.NotifyIcon icon;
        readonly WinForms.ToolStripMenuItem save, pause, length, open, folder, mic, quit;

        public Tray()
        {
            var menu = new WinForms.ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, ShowCheckMargin = true };
            menu.Font = new Drawing.Font("Segoe UI", 9.5f);
            save = Item(menu.Items, (s, e) => App.Instance.SaveClip());
            length = new WinForms.ToolStripMenuItem();
            ((WinForms.ToolStripDropDownMenu)length.DropDown).ShowImageMargin = false;
            length.DropDown.Renderer = new DarkMenu();
            menu.Items.Add(length);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            open = Item(menu.Items, (s, e) => App.Instance.ShowWindow());
            folder = Item(menu.Items, (s, e) => App.Instance.OpenClipsFolder());
            mic = Item(menu.Items, (s, e) => App.Instance.SetMicMuted(!App.Settings.MicMuted));
            pause = Item(menu.Items, (s, e) => App.Instance.SetPaused(!App.Recorder.IsPaused));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            quit = Item(menu.Items, (s, e) => App.Instance.Quit());

            icon = new WinForms.NotifyIcon { Icon = MakeIcon(), Text = "LiteClip", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) App.Instance.ShowWindow(); };
            Refresh();
        }

        static WinForms.ToolStripMenuItem Item(WinForms.ToolStripItemCollection items, EventHandler click)
        {
            var i = new WinForms.ToolStripMenuItem(); i.Click += click; items.Add(i); return i;
        }

        public void Refresh()
        {
            int n = App.Settings.ClipSeconds;
            save.Text = L.T("Save last {0}", Settings.LengthText(n)) + "    " + App.Settings.Hotkey;
            length.Text = L.T("Clip length");
            open.Text = L.T("Open LiteClip");
            folder.Text = L.T("Open clips folder");
            quit.Text = L.T("Quit LiteClip");
            length.DropDownItems.Clear();
            foreach (var len in App.Settings.ClipLengths)
            {
                int sec = len.Seconds;
                var i = Item(length.DropDownItems, (s, e) => App.Instance.SetClipLength(sec));
                i.Text = Settings.LengthText(sec) + (len.Hotkey.Length > 0 ? "    " + len.Hotkey : "");
                i.Checked = sec == n;
            }
            mic.Visible = App.Settings.RecordMic;
            mic.Text = (App.Settings.MicMuted ? L.T("Unmute microphone") : L.T("Mute microphone")) + (App.Settings.HotkeyMic.Length > 0 ? "    " + App.Settings.HotkeyMic : "");
            pause.Text = App.Recorder.IsPaused ? L.T("Resume recording") : L.T("Pause recording");
        }

        // Draws the tray icon: a white ring with a red recording dot.
        public static Drawing.Icon MakeIcon()
        {
            var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                using (var ring = new Drawing.Pen(Drawing.Color.FromArgb(240, 240, 240), 3f)) g.DrawEllipse(ring, 3, 3, 26, 26);
                using (var red = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 77, 77))) g.FillEllipse(red, 10, 10, 12, 12);
            }
            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        public void Dispose() { icon.Visible = false; icon.Dispose(); }

        class DarkMenu : WinForms.ToolStripProfessionalRenderer
        {
            public DarkMenu() : base(new Colors()) { RoundedEdges = false; }
            protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e) { e.TextColor = Drawing.Color.FromArgb(237, 237, 237); base.OnRenderItemText(e); }
            protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e) { e.ArrowColor = Drawing.Color.FromArgb(160, 160, 160); base.OnRenderArrow(e); }
            protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                using (var b = new Drawing.SolidBrush(Drawing.Color.FromArgb(242, 242, 242)))
                    e.Graphics.FillEllipse(b, r.X + r.Width / 2 - 3, r.Y + r.Height / 2 - 3, 6, 6);
            }
        }

        class Colors : WinForms.ProfessionalColorTable
        {
            static readonly Drawing.Color Bg = Drawing.Color.FromArgb(20, 20, 20), Hover = Drawing.Color.FromArgb(42, 42, 42), Line = Drawing.Color.FromArgb(48, 48, 48);
            public override Drawing.Color ToolStripDropDownBackground { get { return Bg; } }
            public override Drawing.Color ImageMarginGradientBegin { get { return Bg; } }
            public override Drawing.Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Drawing.Color ImageMarginGradientEnd { get { return Bg; } }
            public override Drawing.Color MenuBorder { get { return Line; } }
            public override Drawing.Color MenuItemBorder { get { return Hover; } }
            public override Drawing.Color MenuItemSelected { get { return Hover; } }
            public override Drawing.Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Drawing.Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Drawing.Color MenuItemPressedGradientBegin { get { return Hover; } }
            public override Drawing.Color MenuItemPressedGradientEnd { get { return Hover; } }
            public override Drawing.Color SeparatorDark { get { return Line; } }
            public override Drawing.Color SeparatorLight { get { return Bg; } }
            public override Drawing.Color CheckBackground { get { return Bg; } }
            public override Drawing.Color CheckSelectedBackground { get { return Hover; } }
            public override Drawing.Color CheckPressedBackground { get { return Hover; } }
        }
    }
}
