// LiteClip - small Windows helpers: global hotkey, child-process cleanup, recycle bin, start with Windows.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace LiteClip
{
    public static class Native
    {
        // ---- Child processes close together with LiteClip (even if it crashes) ----
        static IntPtr job = IntPtr.Zero;

        public static void KeepWithApp(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int size = Marshal.SizeOf(info);
                    IntPtr ptr = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
                    Marshal.FreeHGlobal(ptr);
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        // ---- Recycle bin (deleted clips can be restored) ----
        public static void Recycle(string path)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        // ---- Start with Windows (a normal shortcut in your Startup folder) ----
        static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LiteClip.lnk"); } }

        public static bool StartsWithWindows { get { return File.Exists(StartupLink); } }

        public static void SetStartWithWindows(bool on)
        {
            if (!on) { if (File.Exists(StartupLink)) File.Delete(StartupLink); return; }
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(StartupLink);
            link.TargetPath = Process.GetCurrentProcess().MainModule.FileName;
            link.Arguments = "--background";
            link.WorkingDirectory = Recorder.AppDir;
            link.Save();
        }

        // True if the Startup shortcut starts this copy of LiteClip (not another copy somewhere else).
        public static bool StartupIsThisCopy()
        {
            try
            {
                if (!File.Exists(StartupLink)) return false;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic link = shell.CreateShortcut(StartupLink);
                string target = link.TargetPath;
                return string.Equals(Path.GetFullPath(target), Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---- Memory ----
        // While no LiteClip window is open, hand memory LiteClip isn't using back to Windows.
        // (It only comes back if it's needed, e.g. when you open the window again.)
        public static void TrimMemory()
        {
            try
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }

    // ---- Global hotkeys (work while you're in a game) ----
    public class Hotkeys : IDisposable
    {
        readonly HwndSource window;
        readonly System.Collections.Generic.Dictionary<int, Action> actions = new System.Collections.Generic.Dictionary<int, Action>();
        int nextId = 0xC100;

        public Hotkeys()
        {
            var p = new HwndSourceParameters("LiteClip hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
            window = new HwndSource(p);
            window.AddHook(WndProc);
        }

        // Text like "Alt+X", "Ctrl+Shift+F9". Returns false if it's not a valid key or another app already uses it.
        public bool Add(string text, Action onPress)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;      // empty = no shortcut, that's fine
            uint mods = 0x4000; // MOD_NOREPEAT
            Key key = Key.None;
            foreach (var raw in text.Split('+'))
            {
                var part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= 2;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= 1;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= 4;
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) mods |= 8;
                else Enum.TryParse(part, true, out key);
            }
            if (key == Key.None) return false;
            int id = nextId++;
            if (!RegisterHotKey(window.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
            actions[id] = onPress;
            return true;
        }

        public void Clear()
        {
            foreach (var id in actions.Keys) UnregisterHotKey(window.Handle, id);
            actions.Clear();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            Action a;
            if (msg == 0x0312 && actions.TryGetValue(wParam.ToInt32(), out a)) { a(); handled = true; }
            return IntPtr.Zero;
        }

        public void Dispose() { Clear(); window.Dispose(); }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
