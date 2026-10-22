using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Pebble
{
    /// <summary>
    /// Detects when a game is in the foreground and measures FPS and ping.
    /// FPS comes from the Windows compositor (DWM): accurate for windowed and most borderless games,
    /// capped at the monitor's refresh rate, and unavailable for exclusive-fullscreen games.
    /// Ping is round-trip time to 1.1.1.1 (your connection's latency, not a specific game server).
    /// </summary>
    sealed class GameService
    {
        public bool InGame { get; private set; }
        public string GameName { get; private set; } = "";
        public double Fps { get; private set; } = -1;
        public int PingMs { get; private set; } = -1;

        ulong _lastFrames; long _lastStamp;
        bool _pinging; DateTime _lastPing = DateTime.MinValue;

        static readonly string[] KnownGames =
        {
            "robloxplayerbeta", "minecraft.windows", "fortniteclient-win64-shipping", "valorant-win64-shipping", "cs2", "csgo",
            "gta5", "gtav", "r5apex", "rocketleague", "overwatch", "league of legends", "dota2", "destiny2", "eldenring",
            "fallguys_client_game", "among us", "genshinimpact", "starrail", "cod", "bf2042", "rainbowsix", "rustclient",
            "terraria", "stardew valley", "hollow knight", "brawlhalla", "paladins", "warframe.x64", "pubg", "tslgame",
            "deadbydaylight-win64-shipping", "marvel-win64-shipping", "helldivers2", "palworld-win64-shipping", "eurotrucks2"
        };
        static readonly string[] Launchers = { "steam", "steamwebhelper", "epicgameslauncher", "riotclientservices", "riotclientux", "battle.net", "explorer", "pebble" };
        static readonly string[] GamePaths = { @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\xboxgames\", @"\roblox\versions\", @"\gog galaxy\games\" };

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct DWM_TIMING_INFO
        {
            public uint cbSize;
            public ulong rateRefresh, qpcRefreshPeriod, rateCompose, qpcVBlank, cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose, cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame, cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete, cFramePending, qpcFramePending,
                cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed,
                cRefreshNextPresented, cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
        }
        [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);

        /// <summary>Call about once a second from the UI thread.</summary>
        public void Update(bool foregroundFullscreen)
        {
            var (name, path, title) = Foreground();
            bool game = false;
            if (name != null && !Launchers.Contains(name))
            {
                string p = (path ?? "").ToLowerInvariant();
                game = KnownGames.Contains(name)
                    || (name == "javaw" && title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase))
                    || GamePaths.Any(g => p.Contains(g))
                    || (foregroundFullscreen && !IsMediaApp(name));
            }

            if (game && !InGame) { _lastFrames = 0; Fps = -1; }
            InGame = game;
            if (!game) { GameName = ""; return; }
            GameName = Pretty(name, path);

            // FPS: frames composed by DWM per second
            var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (_lastFrames != 0 && info.cFrame >= _lastFrames)
                {
                    double secs = (now - _lastStamp) / (double)Stopwatch.Frequency;
                    double fps = (info.cFrame - _lastFrames) / Math.Max(0.2, secs);
                    // when DWM isn't composing (exclusive fullscreen) the count stalls; show "-" rather than a wrong number
                    Fps = fps < 5 ? -1 : (Fps < 0 ? fps : Fps * 0.4 + fps * 0.6);
                }
                _lastFrames = info.cFrame; _lastStamp = now;
            }

            if (!_pinging && (DateTime.Now - _lastPing).TotalSeconds >= 2)
            {
                _pinging = true; _lastPing = DateTime.Now;
                Task.Run(async () =>
                {
                    try
                    {
                        using var ping = new Ping();
                        var r = await ping.SendPingAsync("1.1.1.1", 1000);
                        PingMs = r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1;
                    }
                    catch { PingMs = -1; }
                    finally { _pinging = false; }
                });
            }
        }

        static bool IsMediaApp(string name) =>
            new[] { "chrome", "msedge", "firefox", "opera", "brave", "vlc", "spotify", "netflix", "powerpnt", "mpc-hc64", "potplayermini64", "applicationframehost" }.Contains(name);

        static string Pretty(string name, string path)
        {
            if (name == "robloxplayerbeta") return "Roblox";
            if (name == "javaw" || name == "minecraft.windows") return "Minecraft";
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(v.ProductName)) return v.ProductName.Trim();
                if (!string.IsNullOrWhiteSpace(v.FileDescription)) return v.FileDescription.Trim();
            }
            catch { }
            return name;
        }

        static (string name, string path, string title) Foreground()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return (null, null, "");
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == Environment.ProcessId) return (null, null, "");
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                IntPtr ph = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (ph == IntPtr.Zero) return (null, null, t.ToString());
                try
                {
                    var sb = new StringBuilder(1024); int size = sb.Capacity;
                    if (!QueryFullProcessImageName(ph, 0, sb, ref size)) return (null, null, t.ToString());
                    string path = sb.ToString();
                    return (Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), path, t.ToString());
                }
                finally { CloseHandle(ph); }
            }
            catch { return (null, null, ""); }
        }
    }
}
