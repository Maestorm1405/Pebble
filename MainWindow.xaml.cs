using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pebble
{
    public partial class MainWindow : Window
    {
        readonly MediaService _media = new MediaService();
        readonly WeatherService _weather = new WeatherService();
        readonly NotificationService _notes = new NotificationService();
        readonly AppIndex _apps = new AppIndex();
        LauncherWindow _launcher;
        string _hotkeyText = "Alt+Space";
        DateTime _notifyUntil = DateTime.MinValue;
        bool _tidyBusy;
        DateTime _appsRefreshed = DateTime.Now;
        readonly LyricsService _lyrics = new LyricsService();
        readonly GameService _game = new GameService();
        readonly List<IncomingNotification> _held = new List<IncomingNotification>();
        readonly List<IncomingNotification> _history = new List<IncomingNotification>();   // memory only, never written to disk
        readonly Queue<IncomingNotification> _pending = new Queue<IncomingNotification>();
        readonly Dictionary<string, ImageSource> _logoCache = new Dictionary<string, ImageSource>();
        bool _tourPin, _lyricsView, _lyricActive, _inGame, _gameMini;
        int _lyricsVer = -1, _lyricIdx = -2;
        double _mediaPos;
        SettingsWindow _settingsWin;
        readonly Settings _settings = Settings.Load();
        readonly DispatcherTimer _main = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly DispatcherTimer _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        readonly DispatcherTimer _leave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        Forms.NotifyIcon _tray;
        IntPtr _hwnd;

        // island layout
        bool _expanded, _hidden, _dragActive, _internalDrag;
        string _layoutKey = "";
        DateTime _hudUntil = DateTime.MinValue, _lastInside = DateTime.MinValue;
        Border _dragTile; Point _dragStart;
        string _tab = "Media";
        Dictionary<string, (Button Tab, Grid Panel)> _tabs;

        // media
        string _mediaKey; bool _mediaPlaying, _hasMedia; int _artVer = -1;
        ImageBrush _artBrush; Brush _accent = Brushes.White;

        // timer
        double _timerTotal = 1500, _timerRemaining = 1500;
        bool _timerRunning, _timerActive; DateTime _timerEnd; string _timerMode = "Focus";

        // clipboard, system, weather, prompter
        readonly List<string> _clips = new List<string>(); uint _clipSeq;
        double _vol = -1; bool _mute; bool? _power; int _lowWarned = 100; int _tick;
        int _weatherVer = -1; bool _hasWeather;
        bool _promptPlaying; int _promptGen;

        const string DefaultPrompt =
            "Welcome to Pebble's teleprompter.\n\nClick the pencil to paste your own script, then press play. " +
            "The text scrolls right under the top of your screen, close to your webcam, so you stay looking at the camera.\n\n" +
            "Scroll with your mouse wheel to move back or forward. Use the speed slider to set the pace.";

        static readonly IEasingFunction Spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
        static readonly IEasingFunction SoftSpring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.15 };
        static readonly IEasingFunction Smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
        static readonly BrushConverter Conv = new BrushConverter();

        public MainWindow()
        {
            InitializeComponent();

            _tabs = new Dictionary<string, (Button, Grid)>
            {
                ["Media"] = (TabMedia, PanelMedia), ["Timer"] = (TabTimer, PanelTimer), ["Todo"] = (TabTodo, PanelTodo), ["Notif"] = (TabNotif, PanelNotif),
                ["Clip"] = (TabClip, PanelClip), ["Shelf"] = (TabShelf, PanelShelf), ["Prompter"] = (TabPrompter, PanelPrompter),
                ["Tidy"] = (TabTidy, PanelTidy)
            };
            foreach (var kv in _tabs) { var name = kv.Key; kv.Value.Tab.Click += (s, e) => ShowTab(name); }

            PromptText.Text = string.IsNullOrEmpty(_settings.Prompt) ? DefaultPrompt : _settings.Prompt;
            _settings.Shelf = _settings.Shelf.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            _weather.City = _settings.City ?? "";

            WireMedia(); WireTimer(); WireTodo(); WireClipboard(); WireShelf(); WirePrompter(); WireWeather(); WireHover(); WireTidy();
            BtnSearch.Click += (s, e) => { SetExpanded(false); _launcher?.Toggle(); };
            BtnSettings.Click += (s, e) => { SetExpanded(false); OpenSettings(); };
            NotifClear.Click += (s, e) => { _history.Clear(); RenderNotifs(); };
            BtnLyrics.Click += (s, e) =>
            {
                _lyricsView = !_lyricsView;
                LyricsView.Visibility = _lyricsView ? Visibility.Visible : Visibility.Collapsed;
                BtnLyrics.Background = B(_lyricsView ? "#0A84FF" : "#26FFFFFF");
                _lyricIdx = -2;
            };

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW); // keep out of Alt+Tab
                HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
                RegisterHotkeys();
            };
            Loaded += (s, e) =>
            {
                Reposition();
                RenderClips(); RenderShelf(); RenderTodos(); SetTimerPreset("Focus", 25); SetArt(null);
                ShowTab("Media");
                _media.Start(); _weather.Start();
                _notes.Enabled = _settings.NotificationsOn; _notes.Start();
                _apps.Refresh();
                _launcher = new LauncherWindow(_apps, _settings, () => _settings.Save()) { ExtraCommands = PebbleCommands, SourceRect = IslandScreenRect };
                SetTidyToggle(_settings.TidyOn, animate: false);
                ApplySettings();
                _main.Tick += (_, __) => Tick();
                _main.Start();
                CreateTray();
                // welcome tour: only the very first time Pebble opens
                if (!_settings.TourDone)
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                    t.Tick += (_, __) => { t.Stop(); StartTour(); };
                    t.Start();
                }
            };
        }

        // ================= helpers =================

        static SolidColorBrush B(string hex) { var b = (SolidColorBrush)Conv.ConvertFromString(hex); b.Freeze(); return b; }
        static string Glyph(int code) => char.ConvertFromUtf32(code);

        static void Anim(UIElement el, DependencyProperty dp, double to, int ms = 420, IEasingFunction ease = null, int delay = 0)
        {
            var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease ?? Spring };
            if (delay > 0) a.BeginTime = TimeSpan.FromMilliseconds(delay);
            el.BeginAnimation(dp, a);
        }

        static void SetLayer(UIElement el, bool on, int delay)
        {
            el.IsHitTestVisible = on;
            if (on) Anim(el, OpacityProperty, 1, 220, Smooth, delay); else Anim(el, OpacityProperty, 0, 110, Smooth);
        }

        static string FormatTime(double sec)
        {
            if (sec < 0 || double.IsNaN(sec)) sec = 0;
            var t = TimeSpan.FromSeconds(Math.Floor(sec));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>The island's current bounds in screen DIPs (the search pill grows out of it).</summary>
        Rect? IslandScreenRect()
        {
            if (!IsLoaded || Island.ActualWidth <= 0) return null;
            var p = Island.TranslatePoint(new Point(0, 0), this);
            return new Rect(Left + p.X, Top + p.Y, Island.ActualWidth, Island.ActualHeight);
        }

        void Reposition()
        {
            double left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            if (Math.Abs(Left - left) > 0.5 || Top != 0) { Left = left; Top = 0; }
        }

        // ================= island states =================

        void UpdateIsland()
        {
            string mode; double w, h, r;
            if (_expanded) { mode = "expanded"; w = 650; h = 240; r = 30; }
            else if (_notifyUntil > DateTime.Now && !_hidden) { mode = "notify"; w = 420; h = 64; r = 26; }
            else if (_hudUntil > DateTime.Now) { mode = "hud"; w = 360; h = 40; r = 20; }
            else
            {
                mode = "compact"; h = 36; r = 18;
                if (_gameMini) w = 300;
                else
                {
                    w = 240;
                    if (_mediaPlaying) w += 65;
                    if (_lyricActive) w += 260;
                }
                if (_timerActive) w += 80;
            }
            string key = mode + "|" + w;
            if (key == _layoutKey) return;
            _layoutKey = key;

            bool grow = mode == "expanded";
            var ease = grow ? Spring : SoftSpring;
            int ms = grow ? 480 : 360;
            Anim(Island, WidthProperty, w, ms, ease);
            Anim(Island, HeightProperty, h, ms, ease);
            var cr = new CornerRadius(0, 0, r, r);
            Island.CornerRadius = cr; Shadow.CornerRadius = cr;
            SetLayer(Compact, mode == "compact", 140);
            SetLayer(Hud, mode == "hud", 120);
            SetLayer(Notify, mode == "notify", 140);
            SetLayer(Expanded, grow, 130);
            Anim(Shadow, OpacityProperty, grow ? 1 : 0, 300, Smooth);
        }

        void SetExpanded(bool on)
        {
            if (_expanded == on) return;
            _expanded = on;
            if (on) { _lastInside = DateTime.Now; _hudUntil = DateTime.MinValue; }
            UpdateIsland();
        }

        bool IsPinned() => _tourPin || _promptPlaying || (IsActive && Keyboard.FocusedElement is TextBox);

        void ShowHud(string icon, string text, double bar = -1, bool art = false, string color = "#FFFFFF", double seconds = 2.2)
        {
            if (_expanded || _hidden) return;
            bool useArt = art && _artBrush != null;
            HudArt.Visibility = useArt ? Visibility.Visible : Visibility.Collapsed;
            HudIcon.Visibility = useArt ? Visibility.Collapsed : Visibility.Visible;
            HudIcon.Text = icon; HudIcon.Foreground = B(color);
            HudText.Text = text;
            if (bar >= 0)
            {
                HudBar.Visibility = Visibility.Visible;
                HudBarFill.Background = B(color);
                Anim(HudBarFill, WidthProperty, Math.Clamp(bar, 0, 1) * 90, 160, Smooth);
            }
            else HudBar.Visibility = Visibility.Collapsed;
            _hudUntil = DateTime.Now.AddSeconds(seconds);
            UpdateIsland();
        }

        void ShowTab(string name)
        {
            if (_tab == "Prompter" && name != "Prompter") StopPrompter();
            _tab = name;
            foreach (var kv in _tabs)
            {
                kv.Value.Panel.Visibility = kv.Key == name ? Visibility.Visible : Visibility.Collapsed;
                kv.Value.Tab.Tag = kv.Key == name ? "sel" : null;
            }
            if (name == "Tidy") RefreshTidyPanel();
            if (name == "Notif") { NotifBadge.Visibility = Visibility.Collapsed; RenderNotifs(); }
        }

        // ================= hover & drag =================

        void WireHover()
        {
            // while a notification is showing, hovering doesn't expand; a click dismisses it
            // open after the user's "Open delay"; close after their "Close delay" (both in Settings)
            _hover.Tick += (s, e) => { _hover.Stop(); if (Island.IsMouseOver && _notifyUntil <= DateTime.Now) SetExpanded(true); };
            _leave.Tick += (s, e) => { _leave.Stop(); if (!Island.IsMouseOver && !_dragActive && !IsPinned()) SetExpanded(false); };
            Island.MouseEnter += (s, e) =>
            {
                _lastInside = DateTime.Now;
                _leave.Stop();
                if (_expanded || _notifyUntil > DateTime.Now) return;
                if (_settings.OpenDelayMs <= 0) SetExpanded(true);
                else { _hover.Interval = TimeSpan.FromMilliseconds(_settings.OpenDelayMs); _hover.Start(); }
            };
            Island.MouseLeave += (s, e) =>
            {
                _hover.Stop();
                if (!_expanded) return;
                _leave.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.CloseDelayMs));
                _leave.Stop(); _leave.Start();
            };
            Island.MouseLeftButtonUp += (s, e) =>
            {
                if (_notifyUntil > DateTime.Now) { _notifyUntil = DateTime.MinValue; UpdateIsland(); return; }
                if (!_expanded) SetExpanded(true);
            };

            DragEventHandler onDrag = (s, e) =>
            {
                _dragActive = true; _lastInside = DateTime.Now;
                if (e.Data.GetDataPresent(DataFormats.FileDrop) && !_internalDrag)
                {
                    e.Effects = DragDropEffects.Copy;
                    if (!_expanded) SetExpanded(true);
                    if (_tab != "Shelf") ShowTab("Shelf");
                    ShelfOutline.Stroke = B("#0A84FF");
                }
                else e.Effects = DragDropEffects.None;
                e.Handled = true;
            };
            Island.DragEnter += onDrag;
            Island.DragOver += onDrag;
            Island.DragLeave += (s, e) => { _dragActive = false; ShelfOutline.Stroke = B("#48484A"); };
            Island.Drop += (s, e) =>
            {
                _dragActive = false; ShelfOutline.Stroke = B("#48484A");
                _lastInside = DateTime.Now.AddSeconds(1.5);
                if (_internalDrag) return;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files) if (!_settings.Shelf.Contains(f)) _settings.Shelf.Add(f);
                RenderShelf(); _settings.Save(); e.Handled = true;
            };
        }

        void UpdateCollapse()
        {
            if (!_expanded) return;
            var now = DateTime.Now;
            if (Island.IsMouseOver || _dragActive || IsPinned()) { _lastInside = now; return; }
            // fallback for cases the leave timer can't see (drag-and-drop ending outside, pins released, etc.)
            if (!_leave.IsEnabled && (now - _lastInside).TotalMilliseconds > Math.Max(250, _settings.CloseDelayMs)) SetExpanded(false);
        }

        // ================= custom slider tracks =================

        static void RegisterTrack(Grid track, Action<double> onChange)
        {
            double F(MouseEventArgs e) => Math.Clamp(e.GetPosition(track).X / track.Width, 0, 1);
            track.MouseLeftButtonDown += (s, e) => { track.CaptureMouse(); onChange(F(e)); e.Handled = true; };
            track.MouseMove += (s, e) => { if (track.IsMouseCaptured) onChange(F(e)); };
            track.MouseLeftButtonUp += (s, e) => track.ReleaseMouseCapture();
        }

        // ================= media =================

        void WireMedia()
        {
            BtnPlay.Click += (s, e) => _media.TogglePlayPause();
            BtnNext.Click += (s, e) => _media.Next();
            BtnPrev.Click += (s, e) => _media.Previous();
            RegisterTrack(ProgTrack, f => _media.Seek(f));
            RegisterTrack(VolTrack, f => { Audio.SetVolume(f); _vol = f; VolFill.Width = f * VolTrack.Width; });
            MouseWheelEventHandler wheel = (s, e) =>
            {
                double v = Audio.GetVolume(); if (v < 0) return;
                v = Math.Clamp(v + Math.Sign(e.Delta) * 0.04, 0, 1);
                Audio.SetVolume(v); _vol = v; VolFill.Width = v * VolTrack.Width; e.Handled = true;
            };
            VolTrack.MouseWheel += wheel; VolIcon.MouseWheel += wheel;
        }

        static string FormatAppName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string n = id.Split('!')[0].Split('_')[0];
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            n = n.Split('.').Last();
            if (n.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
            if (n.Contains("ZuneMusic")) return "Media Player";
            if (n.Contains("ZuneVideo")) return "Movies & TV";
            switch (n.ToLowerInvariant())
            {
                case "msedge": return "Microsoft Edge";
                case "chrome": return "Google Chrome";
                case "firefox": return "Firefox";
            }
            return n.Length > 0 ? char.ToUpper(n[0]) + n[1..] : "";
        }

        static Brush GetAccent(byte[] bytes)
        {
            try
            {
                var small = new BitmapImage();
                small.BeginInit(); small.CacheOption = BitmapCacheOption.OnLoad; small.StreamSource = new MemoryStream(bytes); small.DecodePixelWidth = 12; small.EndInit();
                var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                int stride = conv.PixelWidth * 4;
                var px = new byte[stride * conv.PixelHeight];
                conv.CopyPixels(px, stride, 0);
                double r = 0, g = 0, b = 0, wsum = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    int pb = px[i], pg = px[i + 1], pr = px[i + 2];
                    int mx = Math.Max(pr, Math.Max(pg, pb)), mn = Math.Min(pr, Math.Min(pg, pb));
                    double wt = (mx - mn) * (mx - mn) + 40;   // favour vivid pixels
                    r += pr * wt; g += pg * wt; b += pb * wt; wsum += wt;
                }
                r /= wsum; g /= wsum; b /= wsum;
                double k = 235 / Math.Max(1, Math.Max(r, Math.Max(g, b)));
                var br = new SolidColorBrush(Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k)));
                br.Freeze();
                return br;
            }
            catch { return Brushes.White; }
        }

        void SetArt(byte[] bytes)
        {
            if (bytes == null)
            {
                _artBrush = null; _accent = Brushes.White;
                ArtImg.Background = null; MiniArt.Background = B("#2C2C2E"); HudArt.Background = null;
                ArtPlaceholder.Visibility = Visibility.Visible;
            }
            else
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(bytes); bmp.DecodePixelWidth = 300; bmp.EndInit(); bmp.Freeze();
                    var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }; brush.Freeze();
                    _artBrush = brush;
                    ArtImg.Background = brush; MiniArt.Background = brush; HudArt.Background = brush;
                    ArtPlaceholder.Visibility = Visibility.Collapsed;
                    _accent = GetAccent(bytes);
                }
                catch (Exception e) { Log.Write("art: " + e.Message); }
            }
            foreach (var bar in new[] { Eq1, Eq2, Eq3, Eq4 }) bar.Fill = _accent;
            ProgFill.Background = _accent;
        }

        void SetEq(bool on)
        {
            int i = 0;
            foreach (var bar in new[] { Eq1, Eq2, Eq3, Eq4 })
            {
                var st = (ScaleTransform)bar.RenderTransform;
                if (on)
                {
                    var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(300 + 90 * ((i * 7) % 4)))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        BeginTime = TimeSpan.FromMilliseconds(70 * i), EasingFunction = Smooth
                    };
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else { st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleY = 0.3; }
                i++;
            }
        }

        void UpdateMedia()
        {
            if (_media.ArtVer != _artVer) { _artVer = _media.ArtVer; SetArt(_media.Art); }
            var m = _media.Current;
            bool has = m.Title.Length > 0;
            bool playing = has && m.Status == "Playing";
            string key = m.Title + "|" + m.Artist;
            if (key != _mediaKey)
            {
                bool first = _mediaKey == null;
                _mediaKey = key;
                MediaTitle.Text = has ? m.Title : "Nothing playing";
                MediaArtist.Text = has ? m.Artist : "Play something in Spotify, YouTube or any media app";
                MediaApp.Text = has ? FormatAppName(m.App) : "";
                if (has) _lyrics.Request(m.Title, m.Artist, m.Dur);
                _lyricIdx = -2;
                if (has && !first && playing && _settings.SongPopups && !_inGame)
                    ShowHud(Glyph(0xE8D6), m.Artist.Length > 0 ? $"{m.Title}  ·  {m.Artist}" : m.Title, art: true, seconds: 2.6);
            }
            if (playing != _mediaPlaying || has != _hasMedia)
            {
                _mediaPlaying = playing; _hasMedia = has;
                BtnPlay.Content = Glyph(playing ? 0xE769 : 0xE768);
                EqBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
                MiniArt.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
                Anim(MiniArt, OpacityProperty, playing ? 1 : 0, 250, Smooth);
                SetEq(playing);
            }
            if (has && m.Dur > 0)
            {
                double pos = m.Pos;
                if (playing && m.Updated.Year > 2000) pos += (DateTime.UtcNow - m.Updated).TotalSeconds;
                pos = Math.Clamp(pos, 0, m.Dur);
                _mediaPos = pos;
                ProgFill.Width = ProgTrack.Width * pos / m.Dur;
                PosText.Text = FormatTime(pos);
                DurText.Text = "-" + FormatTime(m.Dur - pos);
            }
            else { ProgFill.Width = 0; PosText.Text = ""; DurText.Text = ""; }
        }

        // ================= lyrics =================

        void UpdateLyrics()
        {
            var m = _media.Current;
            bool mine = _lyrics.Key == m.Title + "|" + m.Artist;
            var lines = mine ? _lyrics.Lines : null;

            if (_lyrics.Ver != _lyricsVer) { _lyricsVer = _lyrics.Ver; _lyricIdx = -2; }

            int idx = lines != null ? LyricsService.IndexAt(lines, _mediaPos) : -1;
            if (idx != _lyricIdx)
            {
                _lyricIdx = idx;
                if (lines == null)
                {
                    LyricPrev.Text = ""; LyricNext.Text = "";
                    LyricCur.Text = _hasMedia ? (mine ? _lyrics.Status : "") : "Play a song to see its lyrics";
                    MiniLyric.Text = "";
                }
                else
                {
                    string Line(int i) => i >= 0 && i < lines.Count ? lines[i].Text : "";
                    LyricPrev.Text = Line(idx - 1);
                    LyricCur.Text = idx < 0 ? "♪" : (Line(idx).Length > 0 ? Line(idx) : "♪");
                    LyricNext.Text = Line(idx + 1);
                    LyricCur.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(220)));
                    string mini = idx >= 0 ? Line(idx) : "";
                    if (mini != MiniLyric.Text)
                    {
                        MiniLyric.Text = mini;
                        MiniLyric.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
                    }
                }
            }

            bool active = _settings.LyricsInIsland && _mediaPlaying && lines != null && MiniLyric.Text.Length > 0 && !_gameMini;
            if (active != _lyricActive)
            {
                _lyricActive = active;
                MiniLyric.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ================= timer =================

        void WireTimer()
        {
            PresetFocus.Click += (s, e) => SetTimerPreset("Focus", 25);
            PresetShort.Click += (s, e) => SetTimerPreset("Break", 5);
            PresetLong.Click += (s, e) => SetTimerPreset("Break", 15);
            TimerPlus.Click += (s, e) => AdjustTimer(60);
            TimerMinus.Click += (s, e) => AdjustTimer(-60);
            TimerStart.Click += (s, e) =>
            {
                if (_timerRunning) _timerRunning = false;
                else
                {
                    if (_timerRemaining <= 0) _timerRemaining = _timerTotal;
                    _timerEnd = DateTime.Now.AddSeconds(_timerRemaining); _timerRunning = true;
                }
                UpdateTimer();
            };
            TimerReset.Click += (s, e) => { _timerRunning = false; _timerRemaining = _timerTotal; UpdateTimer(); };
            HeaderTimer.MouseLeftButtonUp += (s, e) => { ShowTab("Timer"); e.Handled = true; };
        }

        void AdjustTimer(int delta)
        {
            _timerTotal = Math.Max(60, _timerTotal + delta);
            _timerRemaining = Math.Max(1, _timerRemaining + delta);
            if (_timerRunning) _timerEnd = _timerEnd.AddSeconds(delta);
            UpdateTimer();
        }

        void SetArc(double frac)
        {
            const double r = 70, c = 75;
            if (frac <= 0.0005) { TimerArc.Data = null; return; }
            if (frac >= 0.9995) { TimerArc.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
            double a = frac * 2 * Math.PI;
            var fig = new PathFigure { StartPoint = new Point(c, c - r) };
            fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(); geo.Figures.Add(fig);
            TimerArc.Data = geo;
        }

        void SetTimerPreset(string mode, int min)
        {
            _timerRunning = false; _timerMode = mode;
            _timerTotal = min * 60.0; _timerRemaining = _timerTotal;
            string color = mode == "Focus" ? "#FF9F0A" : "#30D158";
            var brush = B(color);
            TimerArc.Stroke = brush; TimerMode.Foreground = brush; MiniTimer.Foreground = brush; MiniTimerIcon.Foreground = brush;
            HeaderTimerIcon.Foreground = brush; HeaderTimerText.Foreground = brush; TimerStart.Background = brush;
            HeaderTimer.Background = B("#33" + color.TrimStart('#'));
            TimerMode.Text = mode == "Focus" ? "FOCUS" : "BREAK";
            UpdateTimer();
        }

        void UpdateTimer()
        {
            if (_timerRunning)
            {
                double rem = (_timerEnd - DateTime.Now).TotalSeconds;
                if (rem <= 0)
                {
                    _timerRunning = false; _timerRemaining = 0;
                    SystemSounds.Asterisk.Play();
                    bool wasFocus = _timerMode == "Focus";
                    ShowHud(Glyph(0xE916), wasFocus ? "Focus session done. Take a break!" : "Break is over. Back to it!",
                        color: wasFocus ? "#FF9F0A" : "#30D158", seconds: 5);
                    if (wasFocus) SetTimerPreset("Break", 5); else SetTimerPreset("Focus", 25);
                    return;
                }
                _timerRemaining = rem;
            }
            string text = FormatTime(Math.Ceiling(_timerRemaining));
            TimerBig.Text = text; MiniTimer.Text = text; HeaderTimerText.Text = text;
            _timerActive = _timerRunning || (_timerRemaining < _timerTotal && _timerRemaining > 0);
            var vis = _timerActive ? Visibility.Visible : Visibility.Collapsed;
            MiniTimerBox.Visibility = vis; HeaderTimer.Visibility = vis;
            double op = _timerRunning ? 1.0 : 0.55;   // dim while paused
            MiniTimerBox.Opacity = op; HeaderTimer.Opacity = op;
            TimerStart.Content = _timerRunning ? "Pause" : _timerRemaining < _timerTotal ? "Resume" : "Start";
            TimerSub.Text = _timerRunning ? "Running. It stays in the island, so you can keep working." : "Pick a session and get in the zone.";
            SetArc(_timerRemaining / Math.Max(1, _timerTotal));
        }

        // ================= to-do =================

        void WireTodo()
        {
            TodoInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return)
                {
                    string t = TodoInput.Text.Trim();
                    if (t.Length > 0) { _settings.Todos.Add(new TodoItem { Text = t }); TodoInput.Text = ""; RenderTodos(); _settings.Save(); }
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape) { TodoInput.Text = ""; Keyboard.ClearFocus(); e.Handled = true; }
            };
            TodoInput.TextChanged += (s, e) => TodoPlaceholder.Visibility = TodoInput.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            TodoClearDone.Click += (s, e) => { _settings.Todos.RemoveAll(t => t.Done); RenderTodos(); _settings.Save(); };
        }

        void RenderTodos()
        {
            TodoList.Children.Clear();
            foreach (var t in _settings.Todos)
            {
                var item = t;
                var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var box = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                if (item.Done)
                {
                    box.Background = B("#30D158");
                    box.Child = new TextBlock { Text = Glyph(0xE73E), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                else { box.BorderBrush = B("#8E8E93"); box.BorderThickness = new Thickness(1.5); }

                var txt = new TextBlock { Text = item.Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = item.Text };
                if (item.Done) { txt.Foreground = B("#636366"); txt.TextDecorations = TextDecorations.Strikethrough; }
                else txt.Foreground = Brushes.White;
                Grid.SetColumn(txt, 1);

                var del = new Button { Style = (Style)FindResource("IconBtn"), Content = Glyph(0xE711), FontSize = 10, Width = 24, Height = 22, Opacity = 0, ToolTip = "Delete" };
                Grid.SetColumn(del, 2);
                del.Click += (s, e) => { _settings.Todos.Remove(item); RenderTodos(); _settings.Save(); e.Handled = true; };

                g.Children.Add(box); g.Children.Add(txt); g.Children.Add(del);
                row.Child = g;
                row.MouseEnter += (s, e) => { row.Background = B("#14FFFFFF"); del.Opacity = 1; };
                row.MouseLeave += (s, e) => { row.Background = Brushes.Transparent; del.Opacity = 0; };
                row.MouseLeftButtonUp += (s, e) => { item.Done = !item.Done; RenderTodos(); _settings.Save(); };
                TodoList.Children.Add(row);
            }
            int left = _settings.Todos.Count(t => !t.Done);
            TodoCount.Text = _settings.Todos.Count > 0 ? $"{left} left" : "";
            TodoEmpty.Visibility = _settings.Todos.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================= clipboard =================

        void WireClipboard()
        {
            ClipClear.Click += (s, e) => { _clips.Clear(); RenderClips(); };
            Expanded.MouseLeave += (s, e) => ClipHint.Text = "  click to copy again · right-click to remove";
        }

        void RenderClips()
        {
            ClipList.Children.Clear();
            foreach (var c in _clips)
            {
                string full = c;
                string one = string.Join(" ", c.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                if (one.Length > 160) one = one[..160] + "…";
                var btn = new Button
                {
                    Style = (Style)FindResource("ListBtn"),
                    Content = new TextBlock { Text = one, Foreground = Brushes.White, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                    ToolTip = full.Length > 600 ? full[..600] + "…" : full
                };
                btn.Click += (s, e) =>
                {
                    try { Clipboard.SetText(full); ClipHint.Text = "  copied!"; } catch { ClipHint.Text = "  couldn't copy, try again"; }
                };
                btn.MouseRightButtonUp += (s, e) => { _clips.Remove(full); RenderClips(); e.Handled = true; };
                ClipList.Children.Add(btn);
            }
            ClipEmpty.Visibility = _clips.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdateClipboard()
        {
            uint seq = Native.GetClipboardSequenceNumber();
            if (seq == _clipSeq) return;
            string t;
            try
            {
                if (!Clipboard.ContainsText()) { _clipSeq = seq; return; }
                t = Clipboard.GetText();
            }
            catch { return; } // clipboard busy; retry next tick
            _clipSeq = seq;
            if (string.IsNullOrWhiteSpace(t)) return;
            _clips.Remove(t); _clips.Insert(0, t);
            if (_clips.Count > 30) _clips.RemoveRange(30, _clips.Count - 30);
            RenderClips();
        }

        // ================= shelf =================

        static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

        static ImageSource GetFileIcon(string path)
        {
            try
            {
                if (ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(path); bi.DecodePixelWidth = 96; bi.EndInit(); bi.Freeze();
                    return bi;
                }
            }
            catch { }
            try
            {
                if (File.Exists(path))
                {
                    using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch { }
            return null;
        }

        void WireShelf()
        {
            ShelfClear.Click += (s, e) => { _settings.Shelf.Clear(); RenderShelf(); _settings.Save(); };
            ShelfScroll.PreviewMouseWheel += (s, e) => { ShelfScroll.ScrollToHorizontalOffset(ShelfScroll.HorizontalOffset - e.Delta); e.Handled = true; };
        }

        void RenderShelf()
        {
            ShelfList.Children.Clear();
            foreach (var p in _settings.Shelf)
            {
                string path = p;
                var tile = new Border { Width = 88, Padding = new Thickness(6), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, ToolTip = path, Cursor = Cursors.Hand };
                var sp = new StackPanel();
                var src = GetFileIcon(path);
                if (src != null) sp.Children.Add(new Image { Source = src, Width = 44, Height = 44, Stretch = Stretch.Uniform });
                else sp.Children.Add(new TextBlock
                {
                    Text = Glyph(Directory.Exists(path) ? 0xE8B7 : 0xE7C3), FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 34, Height = 44, Foreground = B("#64D2FF"), HorizontalAlignment = HorizontalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(path.TrimEnd('\\')), FontSize = 11, Foreground = B("#E5E5EA"), TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0)
                });
                tile.Child = sp;
                tile.MouseEnter += (s, e) => tile.Background = B("#22FFFFFF");
                tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
                tile.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount >= 2) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } return; }
                    _dragTile = tile; _dragStart = e.GetPosition(tile);
                };
                tile.MouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || _dragTile != tile) return;
                    var pt = e.GetPosition(tile);
                    if (Math.Abs(pt.X - _dragStart.X) + Math.Abs(pt.Y - _dragStart.Y) < 6) return;
                    _dragTile = null;
                    _internalDrag = true;
                    try { DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); }
                    finally { _internalDrag = false; }
                };
                tile.MouseRightButtonUp += (s, e) => { _settings.Shelf.Remove(path); RenderShelf(); _settings.Save(); e.Handled = true; };
                ShelfList.Children.Add(tile);
            }
            ShelfEmpty.Visibility = _settings.Shelf.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================= teleprompter =================

        void WirePrompter()
        {
            PromptPlay.Click += (s, e) =>
            {
                if (PromptEdit.Visibility == Visibility.Visible) SetPromptEditing(false);
                if (_promptPlaying) StopPrompter(); else StartPrompter();
            };
            PromptEditBtn.Click += (s, e) => SetPromptEditing(PromptEdit.Visibility != Visibility.Visible);
            PromptReset.Click += (s, e) => { StopPrompter(); Canvas.SetTop(PromptText, 16); };
            PromptCanvas.MouseWheel += (s, e) =>
            {
                bool was = _promptPlaying; StopPrompter();
                double y = Math.Min(16, Math.Max(-PromptText.ActualHeight, (double)PromptText.GetValue(Canvas.TopProperty) + e.Delta / 3.0));
                Canvas.SetTop(PromptText, y);
                if (was) StartPrompter();
            };
            PromptCanvas.MouseLeftButtonUp += (s, e) => { if (_promptPlaying) StopPrompter(); else StartPrompter(); };
            RegisterTrack(SpeedTrack, f =>
            {
                _settings.Speed = f; SpeedFill.Width = f * SpeedTrack.Width;
                if (_promptPlaying) StartPrompter();
            });
            SpeedFill.Width = _settings.Speed * SpeedTrack.Width;
        }

        void StopPrompter()
        {
            double cur = (double)PromptText.GetValue(Canvas.TopProperty);
            _promptGen++;
            PromptText.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(PromptText, cur);
            _promptPlaying = false; PromptPlay.Content = Glyph(0xE768);
        }

        void StartPrompter()
        {
            PromptText.UpdateLayout();
            double cur = (double)PromptText.GetValue(Canvas.TopProperty);
            double endY = -PromptText.ActualHeight;
            if (cur <= endY + 1) cur = 16;
            double pxPerSec = 8 + _settings.Speed * 72;
            int gen = ++_promptGen;
            var a = new DoubleAnimation(cur, endY, TimeSpan.FromSeconds((cur - endY) / pxPerSec));
            a.Completed += (s, e) => { if (gen == _promptGen && _promptPlaying) StopPrompter(); };
            _promptPlaying = true; PromptPlay.Content = Glyph(0xE769);
            PromptText.BeginAnimation(Canvas.TopProperty, a);
        }

        void SetPromptEditing(bool on)
        {
            if (on)
            {
                StopPrompter();
                PromptEdit.Text = PromptText.Text; PromptEdit.Visibility = Visibility.Visible;
                Activate(); PromptEdit.Focus(); PromptEditBtn.Content = Glyph(0xE73E);
            }
            else
            {
                PromptText.Text = PromptEdit.Text; PromptEdit.Visibility = Visibility.Collapsed;
                PromptEditBtn.Content = Glyph(0xE70F);
                Canvas.SetTop(PromptText, 16);
                _settings.Prompt = PromptText.Text; _settings.Save();
            }
        }

        // ================= weather =================

        static (int Glyph, string Color, string Desc) WeatherLook(int code, int day)
        {
            if (code == 0) return day == 1 ? (0x2600, "#FFD60A", "Clear") : (0x263E, "#FFE8A3", "Clear");
            if (code <= 2) return day == 1 ? (0x26C5, "#FFD60A", "Partly cloudy") : (0x2601, "#C7C7CC", "Partly cloudy");
            if (code == 3) return (0x2601, "#C7C7CC", "Cloudy");
            if (code == 45 || code == 48) return (0x1F32B, "#AEAEB2", "Foggy");
            if (new[] { 71, 73, 75, 77, 85, 86 }.Contains(code)) return (0x2744, "#BFE9FF", "Snow");
            if (code >= 95) return (0x26C8, "#FFD60A", "Thunderstorm");
            if (code >= 51) return (0x1F327, "#64D2FF", "Rain");
            return (0x2601, "#C7C7CC", "Cloudy");
        }

        void WireWeather()
        {
            WeatherBox.MouseEnter += (s, e) => WeatherBox.Background = B("#22FFFFFF");
            WeatherBox.MouseLeave += (s, e) => WeatherBox.Background = Brushes.Transparent;
            WeatherBox.MouseLeftButtonUp += (s, e) =>
            {
                WeatherBox.Visibility = Visibility.Collapsed; CityEditBox.Visibility = Visibility.Visible;
                CityEdit.Text = _weather.City;
                Activate(); CityEdit.Focus(); CityEdit.SelectAll(); e.Handled = true;
            };
            CityEdit.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return)
                {
                    _settings.City = CityEdit.Text.Trim();
                    _weather.SetCity(_settings.City);
                    _settings.Save(); CloseCityEdit(); e.Handled = true;
                }
                else if (e.Key == Key.Escape) { CloseCityEdit(); e.Handled = true; }
            };
            CityEdit.LostKeyboardFocus += (s, e) => CloseCityEdit();
            CityEdit.TextChanged += (s, e) => CityPlaceholder.Visibility = CityEdit.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void CloseCityEdit()
        {
            if (CityEditBox.Visibility != Visibility.Visible) return;
            CityEditBox.Visibility = Visibility.Collapsed; WeatherBox.Visibility = Visibility.Visible;
            Keyboard.ClearFocus();
        }

        void UpdateWeather()
        {
            if (_weather.Ver != _weatherVer)
            {
                _weatherVer = _weather.Ver;
                var w = _weather.Current;
                if (w != null)
                {
                    var look = WeatherLook(w.Code, w.Day);
                    string glyph = Glyph(look.Glyph); var color = B(look.Color);
                    string temp = $"{Math.Round(w.Temp):0}°";
                    WeatherIcon.Text = glyph; WeatherIcon.Foreground = color;
                    WeatherMiniIcon.Text = glyph; WeatherMiniIcon.Foreground = color;
                    WeatherTemp.Text = temp; WeatherMiniTemp.Text = temp;
                    WeatherInfo.Text = $"H {Math.Round(w.Hi):0}°  L {Math.Round(w.Lo):0}°";
                    WeatherBox.ToolTip = $"{look.Desc} · {w.City} · click to change city";
                    _hasWeather = true;
                }
                else
                {
                    _hasWeather = false;
                    WeatherIcon.Text = ""; WeatherTemp.Text = "";
                    string err = _weather.Error;
                    WeatherInfo.Text = err == "NoLocation" ? "Click to set your city" : err.Length > 0 ? err : "Loading weather…";
                }
            }
            WeatherMini.Visibility = _hasWeather && _settings.ShowWeather ? Visibility.Visible : Visibility.Collapsed;
            WeatherMini.Margin = new Thickness(_mediaPlaying ? 12 : 0, 0, 0, 0);
        }

        // ================= volume, battery, bluetooth, clock =================

        static string VolGlyph(double v, bool m) =>
            Glyph(m || v <= 0.001 ? 0xE74F : v < 0.34 ? 0xE993 : v < 0.67 ? 0xE994 : 0xE995);

        void UpdateVolume()
        {
            double v = Audio.GetVolume(); if (v < 0) return;
            bool m = Audio.GetMute();
            if (_vol >= 0 && _settings.VolumePopups && (Math.Abs(v - _vol) > 0.004 || m != _mute))
                ShowHud(VolGlyph(v, m), m ? "Muted" : $"Volume  {Math.Round(v * 100)}%", m ? 0 : v, seconds: 1.6);
            _vol = v; _mute = m;
            VolFill.Width = v * VolTrack.Width;
            VolIcon.Text = VolGlyph(v, m);
        }

        void UpdateSystem()
        {
            var now = DateTime.Now;
            ClockText.Text = now.ToString("ddd") + "  " + now.ToShortTimeString();
            MiniClock.Text = now.ToShortTimeString();

            var ps = Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || ps.BatteryLifePercent > 1)
                BatteryBox.Visibility = Visibility.Collapsed;
            else
            {
                BatteryBox.Visibility = Visibility.Visible;
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
                BatteryText.Text = pct + "%";
                BatteryFill.Width = Math.Max(1.5, 17.6 * pct / 100);
                BatteryFill.Background = charging ? B("#30D158") : pct <= 20 ? B("#FF453A") : Brushes.White;
                ChargeIcon.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
                if (_power.HasValue && charging != _power.Value)
                {
                    if (charging) ShowHud(Glyph(0xE945), $"Charging  {pct}%", pct / 100.0, color: "#30D158", seconds: 2.5);
                    else ShowHud(Glyph(0xE83F), $"On battery  {pct}%", pct / 100.0, seconds: 2.5);
                }
                if (charging) _lowWarned = 100;
                else if (pct <= 10 && _lowWarned > 10) { _lowWarned = 10; ShowHud(Glyph(0xE850), $"Battery critically low  {pct}%", pct / 100.0, color: "#FF453A", seconds: 4); }
                else if (pct <= 20 && _lowWarned > 20) { _lowWarned = 20; ShowHud(Glyph(0xE850), $"Low battery  {pct}%", pct / 100.0, color: "#FF9F0A", seconds: 3); }
                _power = charging;
            }

            while (_media.BtEvents.TryDequeue(out string ev))
            {
                string name = ev[1..];
                if (ev[0] == '+') ShowHud(Glyph(0xE702), name + " connected", color: "#0A84FF", seconds: 2.6);
                else ShowHud(Glyph(0xE702), name + " disconnected", color: "#8E8E93", seconds: 2.6);
            }

            Reposition();
            bool fullscreen = Native.ForegroundIsFullscreen(_hwnd);
            UpdateGame(fullscreen);
            if (!_expanded)
            {
                // "Always": stay visible over fullscreen apps. "Games": only the FPS pill shows in games. "Never": hide.
                string mode = _settings.FullscreenMode ?? "Always";
                bool hide = fullscreen && mode != "Always" && !(mode == "Games" && _gameMini);
                if (hide != _hidden)
                {
                    _hidden = hide;
                    Island.Visibility = Shadow.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
                }
            }
            // Re-assert "always on top": every second over a fullscreen app (they can push in front), every ~5 s otherwise.
            // (UpdateSystem runs on ticks 1, 5, 9…, so the check uses that rhythm.)
            if (!_hidden && (fullscreen || _tick % 20 == 1)) Native.BringToTopmost(_hwnd);
        }

        // ================= game mode =================

        void UpdateGame(bool fullscreen)
        {
            bool was = _inGame;
            if (_settings.GameModeOn) _game.Update(fullscreen);
            _inGame = _settings.GameModeOn && _game.InGame;

            bool mini = _inGame && _settings.GameStats;
            if (mini != _gameMini)
            {
                _gameMini = mini;
                GameMini.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
                CompactLeft.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            }
            if (mini)
            {
                GameFps.Text = _game.Fps < 0 ? "–" : Math.Round(_game.Fps).ToString();
                int p = _game.PingMs;
                GamePing.Text = p < 0 ? "–" : p.ToString();
                PingDot.Fill = B(p < 0 ? "#8E8E93" : p < 60 ? "#30D158" : p < 120 ? "#FFD60A" : "#FF453A");
                GameMini.ToolTip = $"{_game.GameName}\nFPS from Windows' compositor · ping to 1.1.1.1";
            }

            // back from the game: summarise what was held
            if (was && !_inGame && _held.Count > 0)
            {
                int n = _held.Count;
                string apps = string.Join(", ", _held.Select(h => h.App).Where(a => !string.IsNullOrEmpty(a)).Distinct().Take(3));
                _held.Clear();
                ShowHud(Glyph(0xE7E7), $"{n} notification{(n == 1 ? "" : "s")} while you played" + (apps.Length > 0 ? " · " + apps : ""), color: "#0A84FF", seconds: 4.5);
            }
        }

        // ================= main loop =================

        void Tick()
        {
            try
            {
                _tick++;
                UpdateMedia();
                UpdateLyrics();
                UpdateTimer();
                UpdateWeather();
                if (_tick % 2 == 0) UpdateClipboard();
                UpdateVolume();
                if (_tick % 4 == 1) UpdateSystem();
                UpdateNotifications();
                if (_tick % 240 == 8) TidyDownloads(false);   // roughly once a minute
                UpdateCollapse();
                UpdateIsland();
            }
            catch (Exception e) { Log.Write("tick: " + e); }
        }

        // ================= hotkeys =================

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr h, int id);

        const int HotkeySearch = 1, HotkeyColor = 2;

        void RegisterHotkeys()
        {
            if (_hwnd == IntPtr.Zero) return;
            const uint Alt = 0x1, Ctrl = 0x2, NoRepeat = 0x4000, Space = 0x20, KeyC = 0x43;
            UnregisterHotKey(_hwnd, HotkeySearch);
            UnregisterHotKey(_hwnd, HotkeyColor);

            var options = new (string Name, uint Mods)[] { ("Alt+Space", Alt), ("Ctrl+Space", Ctrl), ("Ctrl+Alt+Space", Ctrl | Alt) };
            // the chosen shortcut first; if another app already owns it, fall back to the others
            var ordered = options.OrderBy(o => o.Name == _settings.SearchHotkey ? 0 : 1);
            _hotkeyText = "no shortcut";
            foreach (var o in ordered)
                if (RegisterHotKey(_hwnd, HotkeySearch, o.Mods | NoRepeat, Space)) { _hotkeyText = o.Name; break; }
            if (_hotkeyText != _settings.SearchHotkey) Log.Write($"hotkey: {_settings.SearchHotkey} is taken, using {_hotkeyText}");

            if (_settings.ColorPickerOn && !RegisterHotKey(_hwnd, HotkeyColor, Ctrl | Alt | NoRepeat, KeyC))
                Log.Write("hotkey: Ctrl+Alt+C is taken");
            BtnSearch.ToolTip = $"Search ({_hotkeyText})";
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312) // WM_HOTKEY
            {
                if (w.ToInt32() == HotkeySearch)
                {
                    // pick up newly installed apps / recent files now and then
                    if ((DateTime.Now - _appsRefreshed).TotalMinutes > 5) { _appsRefreshed = DateTime.Now; _apps.Refresh(); }
                    _launcher?.Toggle();
                    handled = true;
                }
                else if (w.ToInt32() == HotkeyColor) { PickColor(); handled = true; }
            }
            return IntPtr.Zero;
        }

        // ================= settings, tour, color picker, restart =================

        internal string NotificationStatus => "Status: " + _notes.Status + ".";

        /// <summary>Re-reads every setting and applies it live.</summary>
        internal void ApplySettings()
        {
            RegisterHotkeys();
            _notes.Enabled = _settings.NotificationsOn;
            WeatherBox.Visibility = _settings.ShowWeather ? Visibility.Visible : Visibility.Collapsed;
            bool f = _settings.TempUnit == "F" || (_settings.TempUnit != "C" && WeatherService.RegionUsesFahrenheit);
            if (f != _weather.Fahrenheit) { _weather.Fahrenheit = f; _weather.SetCity(_weather.City); }
            SetTidyToggle(_settings.TidyOn, animate: false);
            _weatherVer = -1; _lyricIdx = -2; _layoutKey = "";
        }

        internal void SetTidyFromSettings(bool on) { if (on != _settings.TidyOn) SetTidyOn(on); }

        internal void OpenSettings()
        {
            if (_settingsWin != null) { _settingsWin.Activate(); return; }
            _settingsWin = new SettingsWindow(this, _settings);
            _settingsWin.Closed += (s, e) => _settingsWin = null;
            _settingsWin.Show();
            _settingsWin.Activate();
        }

        internal void StartTour()
        {
            var tour = new TourWindow(_hotkeyText,
                step =>
                {
                    // step 2 opens the island so the tabs are visible; the others keep it closed
                    _tourPin = step == 1 || step == 4;
                    SetExpanded(_tourPin);
                    if (step == 1) ShowTab("Media");
                },
                () =>
                {
                    _tourPin = false; SetExpanded(false);
                    _settings.TourDone = true; _settings.Save();
                });
            tour.Show();
            tour.Activate();
        }

        internal void PickColor()
        {
            SetExpanded(false);
            new ColorPickerWindow(hex =>
            {
                try { Clipboard.SetText(hex); } catch { }
                _settings.RecentColors.Remove(hex);
                _settings.RecentColors.Insert(0, hex);
                if (_settings.RecentColors.Count > 12) _settings.RecentColors.RemoveRange(12, _settings.RecentColors.Count - 12);
                _settings.Save();
                ShowHud(Glyph(0xE790), $"{hex} copied", color: hex, seconds: 2.4);
            }).Show();
        }

        internal void RestartPebble()
        {
            _main.Stop();
            _settings.Prompt = PromptText.Text;
            _settings.Save();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            ((App)Application.Current).Restart();
        }

        IEnumerable<LaunchItem> PebbleCommands() => TidyCommands().Concat(new[]
        {
            new LaunchItem { Title = "Pebble settings", Subtitle = "Pebble", Glyph = "", Run = OpenSettings },
            new LaunchItem
            {
                Title = "Notifications", Subtitle = $"Pebble · {_history.Count} recent", Glyph = "", GlyphColor = "#0A84FF",
                Run = () => { SetExpanded(true); ShowTab("Notif"); _lastInside = DateTime.Now.AddSeconds(3); }
            },
            new LaunchItem { Title = "Pick a color", Subtitle = "Pebble · copy any color on screen (Ctrl+Alt+C)", Glyph = "", GlyphColor = "#FF9F0A", Run = PickColor },
            new LaunchItem { Title = "Restart Pebble", Subtitle = "Pebble", Glyph = "", Run = RestartPebble },
            new LaunchItem { Title = "Welcome tour", Subtitle = "Pebble · see the intro again", Glyph = "", Run = StartTour },
        });

        // ================= notifications =================

        void UpdateNotifications()
        {
            // everything that arrives goes into the history right away; then it's either held (gaming) or queued for the pop-up
            bool hold = _inGame && _settings.GameHoldNotifications;
            bool added = false;
            while (_notes.Incoming.TryDequeue(out var inc))
            {
                _history.Insert(0, inc); added = true;
                if (hold) _held.Add(inc); else _pending.Enqueue(inc);
            }
            if (added)
            {
                if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
                if (_expanded && _tab == "Notif") RenderNotifs();
                else NotifBadge.Visibility = Visibility.Visible;
            }

            if (_notifyUntil > DateTime.Now || _pending.Count == 0) return;
            var n = _pending.Dequeue();
            // drop stale ones, and stay quiet while the island is open or a game/video is fullscreen
            if ((DateTime.Now - n.At).TotalSeconds > 30 || _expanded || _hidden) return;

            NotifyApp.Text = n.App;
            NotifyTitle.Text = n.Title;
            NotifyBody.Text = n.Body;
            NotifyBody.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var logo = Logo(n);
            NotifyImg.Source = logo;
            NotifyGlyph.Visibility = logo == null ? Visibility.Visible : Visibility.Collapsed;
            _hudUntil = DateTime.MinValue;
            _notifyUntil = DateTime.Now.AddSeconds(5);
            UpdateIsland();
        }

        ImageSource Logo(IncomingNotification n)
        {
            if (n.Logo == null) return null;
            string key = string.IsNullOrEmpty(n.AppId) ? n.App ?? "" : n.AppId;
            if (_logoCache.TryGetValue(key, out var hit)) return hit;
            ImageSource src = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(n.Logo); bmp.DecodePixelWidth = 72; bmp.EndInit(); bmp.Freeze();
                src = bmp;
            }
            catch { }
            _logoCache[key] = src;
            return src;
        }

        void RenderNotifs()
        {
            NotifList.Children.Clear();
            foreach (var item in _history)
            {
                var n = item;
                var row = new Border
                {
                    CornerRadius = new CornerRadius(10), Background = B("#14FFFFFF"), Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 0, 0, 5),
                    Cursor = string.IsNullOrEmpty(n.AppId) ? Cursors.Arrow : Cursors.Hand,
                    ToolTip = string.IsNullOrEmpty(n.AppId) ? null : $"Open {n.App} · right-click to remove"
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var logo = Logo(n);
                var iconBox = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = B("#2C2C2E"), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
                iconBox.Child = logo != null
                    ? new Image { Source = logo, Stretch = Stretch.UniformToFill }
                    : (UIElement)new TextBlock { Text = Glyph(0xE7E7), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = B("#E5E5EA"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(iconBox);

                var text = new StackPanel();
                var top = new DockPanel();
                var when = new TextBlock { Text = Ago(n.At), FontSize = 11, Foreground = B("#636366") };
                DockPanel.SetDock(when, Dock.Right);
                top.Children.Add(when);
                top.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(n.App) ? "Notification" : n.App, FontSize = 11, Foreground = B("#8E8E93"), TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(top);
                text.Children.Add(new TextBlock { Text = n.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                if (!string.IsNullOrEmpty(n.Body))
                    text.Children.Add(new TextBlock { Text = n.Body, FontSize = 12, Foreground = B("#C7C7CC"), TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 1);
                g.Children.Add(text);
                row.Child = g;

                row.MouseEnter += (s, e) => row.Background = B("#26FFFFFF");
                row.MouseLeave += (s, e) => row.Background = B("#14FFFFFF");
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (string.IsNullOrEmpty(n.AppId)) return;
                    try { Process.Start(new ProcessStartInfo("explorer.exe", "\"shell:AppsFolder\\" + n.AppId + "\"") { UseShellExecute = true }); SetExpanded(false); }
                    catch (Exception ex) { Log.Write("open app: " + ex.Message); }
                };
                row.MouseRightButtonUp += (s, e) => { _history.Remove(n); RenderNotifs(); e.Handled = true; };
                NotifList.Children.Add(row);
            }
            NotifCount.Text = _history.Count > 0 ? _history.Count.ToString() : "";
            NotifEmpty.Visibility = _history.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            NotifEmptyStatus.Text = _notes.Status == "On" ? "New ones will show up here" : _notes.Status;
            NotifBadge.Visibility = Visibility.Collapsed;
        }

        // ================= tidy downloads =================

        async void TidyDownloads(bool manual)
        {
            if (_tidyBusy || (!manual && !_settings.TidyOn)) return;
            _tidyBusy = true;
            try
            {
                var ignore = new HashSet<string>(_settings.TidyIgnore, StringComparer.OrdinalIgnoreCase);
                var moved = await System.Threading.Tasks.Task.Run(() => TidyService.Run(ignore));
                if (moved.Count > 0 || manual) { _settings.LastTidyAt = DateTime.Now; _settings.Save(); }
                if (_tab == "Tidy" && _expanded) RefreshTidyPanel();
                if (moved.Count > 0)
                {
                    _settings.LastTidy = moved; _settings.Save();
                    ShowHud(Glyph(0xE896), $"Tidied {moved.Count} download{(moved.Count == 1 ? "" : "s")}", color: "#64D2FF", seconds: 2.6);
                }
                else if (manual) ShowHud(Glyph(0xE896), "Downloads are already tidy", color: "#64D2FF");
            }
            finally { _tidyBusy = false; }
        }

        async void UndoTidy()
        {
            var moves = _settings.LastTidy;
            if (moves.Count == 0) { ShowHud(Glyph(0xE7A7), "Nothing to undo"); return; }
            int n = await System.Threading.Tasks.Task.Run(() => TidyService.Undo(moves));
            // remember these so auto-tidy doesn't move them straight back
            foreach (var m in moves) if (!_settings.TidyIgnore.Contains(m.From)) _settings.TidyIgnore.Add(m.From);
            _settings.LastTidy = new List<MoveRecord>();
            _settings.Save();
            if (_tab == "Tidy" && _expanded) RefreshTidyPanel();
            ShowHud(Glyph(0xE7A7), $"Put back {n} file{(n == 1 ? "" : "s")}", color: "#64D2FF", seconds: 2.6);
        }

        void WireTidy()
        {
            TidyToggle.MouseLeftButtonUp += (s, e) => { SetTidyOn(!_settings.TidyOn); e.Handled = true; };
            TidyNowBtn.Click += (s, e) => TidyDownloads(true);
            TidyUndoBtn.Click += (s, e) => UndoTidy();
            TidyOpenBtn.Click += (s, e) => OpenPath(TidyService.DownloadsFolder);
        }

        static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { Log.Write("open: " + e.Message); }
        }

        void SetTidyOn(bool on)
        {
            _settings.TidyOn = on; _settings.Save();
            SetTidyToggle(on, animate: true);
            if (on) TidyDownloads(false);
            ShowHud(Glyph(0xE896), on ? "Auto-tidy on" : "Auto-tidy off", color: "#64D2FF", seconds: 1.6);
            if (_tab == "Tidy") RefreshTidyPanel();
        }

        void SetTidyToggle(bool on, bool animate)
        {
            TidyToggle.Background = B(on ? "#30D158" : "#3A3A3C");
            if (animate) TidyKnobShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = Smooth });
            else { TidyKnobShift.BeginAnimation(TranslateTransform.XProperty, null); TidyKnobShift.X = on ? 18 : 0; }
        }

        static readonly (string Folder, int Glyph, string Color)[] TidyLooks =
        {
            ("Installers", 0xE7B8, "#FF9F0A"), ("Documents", 0xE8A5, "#0A84FF"), ("Images", 0xE91B, "#FF375F"), ("Videos", 0xE714, "#BF5AF2"),
            ("Music", 0xE8D6, "#30D158"), ("Archives", 0xE8B7, "#FFD60A"), ("Code", 0xE943, "#64D2FF"),
        };

        async void RefreshTidyPanel()
        {
            var ignore = new HashSet<string>(_settings.TidyIgnore, StringComparer.OrdinalIgnoreCase);
            var (loose, per) = await System.Threading.Tasks.Task.Run(() => TidyService.Stats(ignore));

            string last = _settings.LastTidyAt is DateTime t ? " · last tidied " + Ago(t) : "";
            TidyStatus.Text = (loose > 0 ? $"{loose} file{(loose == 1 ? "" : "s")} to sort" : "All tidy ✓") + last;
            TidyUndoBtn.IsEnabled = _settings.LastTidy.Count > 0;
            TidyUndoBtn.Opacity = TidyUndoBtn.IsEnabled ? 1 : 0.4;

            TidyChips.Children.Clear();
            foreach (var (folder, glyph, color) in TidyLooks)
            {
                per.TryGetValue(folder, out int count);
                string path = System.IO.Path.Combine(TidyService.DownloadsFolder, folder);
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(12), Background = B("#1C1C1E"), Padding = new Thickness(12, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand, ToolTip = Directory.Exists(path) ? "Open " + folder : folder + " (empty)", Opacity = count > 0 ? 1 : 0.5
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = Glyph(glyph), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = B(color), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = folder, FontSize = 12.5, Foreground = Brushes.White, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B("#8E8E93"), VerticalAlignment = VerticalAlignment.Center });
                chip.Child = sp;
                chip.MouseEnter += (s, e) => chip.Background = B("#2C2C2E");
                chip.MouseLeave += (s, e) => chip.Background = B("#1C1C1E");
                chip.MouseLeftButtonUp += (s, e) => { if (Directory.Exists(path)) OpenPath(path); };
                TidyChips.Children.Add(chip);
            }
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
            return t.ToString("MMM d");
        }

        IEnumerable<LaunchItem> TidyCommands() => new[]
        {
            new LaunchItem { Title = "Tidy Downloads now", Subtitle = "Pebble · sort Downloads into folders", Glyph = "", GlyphColor = "#64D2FF", Run = () => TidyDownloads(true) },
            new LaunchItem { Title = "Undo last tidy", Subtitle = _settings.LastTidy.Count > 0 ? $"Pebble · put {_settings.LastTidy.Count} files back" : "Pebble · nothing to undo", Glyph = "", GlyphColor = "#64D2FF", Run = UndoTidy },
            new LaunchItem
            {
                Title = _settings.TidyOn ? "Turn off auto-tidy" : "Turn on auto-tidy", Subtitle = "Pebble · Downloads",
                Glyph = "", GlyphColor = _settings.TidyOn ? "#8E8E93" : "#30D158", Run = () => SetTidyOn(!_settings.TidyOn)
            },
        };

        // ================= tray =================

        async void CreateTray()
        {
            System.Drawing.Icon icon;
            try
            {
                using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Pebble.ico")).Stream;
                icon = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            }
            catch { icon = System.Drawing.SystemIcons.Application; }

            var menu = new Forms.ContextMenuStrip();
            var search = new Forms.ToolStripMenuItem("Search");
            search.Click += (s, e) => _launcher?.Toggle();
            var color = new Forms.ToolStripMenuItem("Pick a color    (Ctrl+Alt+C)");
            color.Click += (s, e) => PickColor();
            var settings = new Forms.ToolStripMenuItem("Settings…");
            settings.Click += (s, e) => OpenSettings();

            var tidyAuto = new Forms.ToolStripMenuItem("Tidy Downloads automatically");
            tidyAuto.Click += (s, e) => SetTidyOn(!_settings.TidyOn);
            var tidyNow = new Forms.ToolStripMenuItem("Tidy Downloads now");
            tidyNow.Click += (s, e) => TidyDownloads(true);
            var tidyUndo = new Forms.ToolStripMenuItem("Undo last tidy");
            tidyUndo.Click += (s, e) => UndoTidy();

            var restart = new Forms.ToolStripMenuItem("Restart Pebble");
            restart.Click += (s, e) => RestartPebble();
            var quit = new Forms.ToolStripMenuItem("Quit Pebble");
            quit.Click += (s, e) => Quit();

            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                search, color, settings, new Forms.ToolStripSeparator(),
                tidyAuto, tidyNow, tidyUndo, new Forms.ToolStripSeparator(),
                restart, quit
            });
            menu.Opening += (s, e) =>
            {
                search.Text = $"Search    ({_hotkeyText})";
                color.Visible = _settings.ColorPickerOn;
                tidyAuto.Checked = _settings.TidyOn;
                tidyUndo.Enabled = _settings.LastTidy.Count > 0;
                tidyUndo.Text = _settings.LastTidy.Count > 0 ? $"Undo last tidy ({_settings.LastTidy.Count} files)" : "Undo last tidy";
            };

            _tray = new Forms.NotifyIcon { Icon = icon, Text = "Pebble", Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) { SetExpanded(true); _lastInside = DateTime.Now.AddSeconds(2); }
            };
        }

        void Quit()
        {
            _main.Stop();
            _settings.Prompt = PromptText.Text;
            _settings.Save();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Application.Current.Shutdown();
        }
    }
}
