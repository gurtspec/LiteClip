// LiteClip - captures "what you hear" (game/system audio) using Windows' own WASAPI loopback.
// No extra libraries: just Windows' built-in audio interfaces.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteClip
{
    public class AudioLoopback : IDisposable
    {
        public int SampleRate, Channels, BitsPerSample;
        public bool IsFloat;
        public volatile bool Muted;          // keeps running but writes silence (mic mute shortcut)
        int blockAlign;
        Thread thread;
        volatile bool running;
        Stream output;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        Exception initError;
        IAudioClient client;
        IAudioCaptureClient capture;

        // Raw sample format name FFmpeg needs to read our stream.
        public string FfmpegFormat
        {
            get { if (IsFloat) return "f32le"; return BitsPerSample == 16 ? "s16le" : BitsPerSample == 24 ? "s24le" : "s32le"; }
        }

        readonly bool microphone;
        readonly string deviceId;

        // microphone = false: records "what you hear" (default speakers/headphones, loopback).
        // microphone = true:  records a microphone (deviceId, or Windows' default if empty).
        // Throws if there is no such device.
        public AudioLoopback(bool microphone = false, string deviceId = null)
        {
            this.microphone = microphone;
            this.deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            // Windows audio objects work best on their own (MTA) thread, so everything lives there.
            thread = new Thread(Run) { IsBackground = true, Name = "LiteClip audio" };
            thread.SetApartmentState(ApartmentState.MTA);
            running = true;
            thread.Start();
            ready.WaitOne();
            if (initError != null) throw initError;
        }

        public void Start(Stream target) { output = target; }

        void Run()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice device = null;
                if (deviceId != null) { try { Check(enumerator.GetDevice(deviceId, out device)); } catch { device = null; } }   // chosen device unplugged: use default
                if (device == null)
                    Check(enumerator.GetDefaultAudioEndpoint(microphone ? 1 /* capture */ : 0 /* render */, microphone ? 0 /* console */ : 1 /* multimedia */, out device));
                Guid iid = typeof(IAudioClient).GUID; object o;
                Check(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o));
                client = (IAudioClient)o;

                IntPtr fmt;
                Check(client.GetMixFormat(out fmt));
                ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
                Channels = Marshal.ReadInt16(fmt, 2);
                SampleRate = Marshal.ReadInt32(fmt, 4);
                blockAlign = Marshal.ReadInt16(fmt, 12);
                BitsPerSample = Marshal.ReadInt16(fmt, 14);
                IsFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(fmt, 24) == 3);   // WAVE_FORMAT_EXTENSIBLE sub-format

                Check(client.Initialize(0 /* shared */, microphone ? 0 : 0x00020000 /* loopback */, 10000000 /* 1s buffer */, 0, fmt, IntPtr.Zero));
                Marshal.FreeCoTaskMem(fmt);
                Guid cid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref cid, out o));
                capture = (IAudioCaptureClient)o;
            }
            catch (Exception e) { initError = e; ready.Set(); return; }
            ready.Set();

            while (running && output == null) Thread.Sleep(5);
            if (!running) return;

            byte[] buf = new byte[0];
            var clock = Stopwatch.StartNew();
            long framesWritten = 0;
            try
            {
                Check(client.Start());
                while (running)
                {
                    uint packet;
                    Check(capture.GetNextPacketSize(out packet));
                    if (packet == 0)
                    {
                        // Windows sends nothing while it's silent: fill the gap so audio stays in sync with video.
                        long behind = (long)(clock.Elapsed.TotalSeconds * SampleRate) - framesWritten - SampleRate / 20;
                        if (behind > 0)
                        {
                            int bytes = (int)behind * blockAlign;
                            if (buf.Length < bytes) buf = new byte[bytes];
                            Array.Clear(buf, 0, bytes);
                            output.Write(buf, 0, bytes);
                            framesWritten += behind;
                        }
                        Thread.Sleep(5);
                        continue;
                    }
                    while (packet > 0 && running)
                    {
                        IntPtr data; uint frames, flags; ulong devicePos, qpcPos;
                        Check(capture.GetBuffer(out data, out frames, out flags, out devicePos, out qpcPos));
                        int bytes = (int)frames * blockAlign;
                        if (buf.Length < bytes) buf = new byte[bytes];
                        if ((flags & 2) != 0 || Muted) Array.Clear(buf, 0, bytes);      // AUDCLNT_BUFFERFLAGS_SILENT, or muted
                        else Marshal.Copy(data, buf, 0, bytes);
                        Check(capture.ReleaseBuffer(frames));
                        output.Write(buf, 0, bytes);
                        framesWritten += frames;
                        Check(capture.GetNextPacketSize(out packet));
                    }
                }
            }
            catch (Exception) { /* FFmpeg stopped or the audio device changed: the recorder restarts us */ }
            finally { try { client.Stop(); } catch { } }
        }

        public bool IsRunning { get { return running && thread.IsAlive; } }

        public void Dispose() { running = false; }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        // Lists active microphones (capture = true) or speakers/headphones, as (id, friendly name).
        public static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> ListDevices(bool capture)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDeviceCollection devices;
                Check(enumerator.EnumAudioEndpoints(capture ? 1 : 0, 1 /* active */, out devices));
                uint count; Check(devices.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice d; Check(devices.Item(i, out d));
                    string id; Check(d.GetId(out id));
                    string name = id;
                    IPropertyStore props;
                    if (d.OpenPropertyStore(0 /* read */, out props) >= 0)
                    {
                        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // friendly name
                        PROPVARIANT pv;
                        if (props.GetValue(ref key, out pv) >= 0 && pv.vt == 31 /* VT_LPWSTR */) name = Marshal.PtrToStringUni(pv.p);
                    }
                    result.Add(new System.Collections.Generic.KeyValuePair<string, string>(id, name));
                }
            }
            catch { }
            return result;
        }

        // ---- Windows audio interfaces (from mmdeviceapi.h / audioclient.h / propsys.h) ----

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
