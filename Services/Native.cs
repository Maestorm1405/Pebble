using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Pebble
{
    /// <summary>Win32 helpers: media keys, window styles, clipboard sequence, fullscreen detection.</summary>
    static class Native
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Moves the window to the top of the always-on-top band without stealing focus.</summary>
        public static void BringToTopmost(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            const uint NOSIZE = 0x1, NOMOVE = 0x2, NOACTIVATE = 0x10, NOOWNERZORDER = 0x200;
            SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER);
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;

        public static void Key(byte vk)
        {
            keybd_event(vk, 0, 1, UIntPtr.Zero);
            keybd_event(vk, 0, 3, UIntPtr.Zero);
        }

        /// <summary>True when the foreground window covers its whole monitor (games, videos, presentations).</summary>
        public static bool ForegroundIsFullscreen(IntPtr self)
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero || h == self || h == GetShellWindow() || h == GetDesktopWindow()) return false;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            string c = sb.ToString();
            if (c == "WorkerW" || c == "Progman" || c == "Shell_TrayWnd") return false;
            if (!GetWindowRect(h, out RECT r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
            // only matters when the fullscreen window is on the monitor Pebble lives on
            var selfMon = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(self, 2), ref selfMon) && selfMon.rcMonitor.Left != mi.rcMonitor.Left) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
    }

    // ---- Core Audio (master volume of the default output device) ----

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p); int UnregisterControlChangeNotify(IntPtr p);
        int GetChannelCount(out uint c);
        int SetMasterVolumeLevel(float l, ref Guid ctx); int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
        int GetMasterVolumeLevel(out float l); int GetMasterVolumeLevelScalar(out float l);
        int SetChannelVolumeLevel(uint ch, float l, ref Guid ctx); int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float l); int GetChannelVolumeLevelScalar(uint ch, out float l);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { int Activate(ref Guid id, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { int NotImpl1(); int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ep); }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }

    static class Audio
    {
        static IAudioEndpointVolume _endpoint;
        static DateTime _endpointAt;

        static IAudioEndpointVolume Endpoint()
        {
            // refresh every few seconds so switching speakers/headphones is picked up
            if (_endpoint != null && (DateTime.UtcNow - _endpointAt).TotalSeconds < 3) return _endpoint;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out IMMDevice dev));
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out object o));
            _endpoint = (IAudioEndpointVolume)o;
            _endpointAt = DateTime.UtcNow;
            return _endpoint;
        }

        public static float GetVolume() { try { Endpoint().GetMasterVolumeLevelScalar(out float v); return v; } catch { _endpoint = null; return -1f; } }
        public static bool GetMute() { try { Endpoint().GetMute(out bool m); return m; } catch { _endpoint = null; return false; } }
        public static void SetVolume(double v)
        {
            try { Guid g = Guid.Empty; Endpoint().SetMasterVolumeLevelScalar((float)Math.Clamp(v, 0, 1), ref g); } catch { _endpoint = null; }
        }
    }
}
