using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pebble
{
    sealed class LaunchItem
    {
        public string Title, Subtitle, Glyph, GlyphColor = "#E5E5EA", CountKey;
        public string IconSource;   // shell parsing name for a real icon
        public double Score;
        public Action Run;
    }

    public partial class LauncherWindow : Window
    {
        readonly AppIndex _index;
        readonly Settings _settings;
        readonly Action _onCountsChanged;
        List<LaunchItem> _items = new List<LaunchItem>();
        int _sel;

        /// <summary>Extra actions supplied by the island (e.g. Tidy Downloads).</summary>
        internal Func<IEnumerable<LaunchItem>> ExtraCommands;

        /// <summary>Where the island currently is on screen (DIPs), so the search pill can slide out of it.</summary>
        internal Func<Rect?> SourceRect;

        // geometry (DIPs, screen coordinates)
        const double PillW = 660, PillH = 58, Gap = 10, TopMargin = 50, RegionH = 760;
        Rect _from, _to;
        bool _open;
        int _gen;

        static readonly IEasingFunction GrowEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 };
        static readonly IEasingFunction SmoothOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        static readonly IEasingFunction SmoothIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

        static readonly (string Name, string Uri, string Glyph)[] SettingsPages =
        {
            ("Display", "ms-settings:display", ""), ("Sound", "ms-settings:sound", ""),
            ("Bluetooth & devices", "ms-settings:bluetooth", ""), ("Wi-Fi", "ms-settings:network-wifi", ""),
            ("Network & internet", "ms-settings:network", ""), ("Background / wallpaper", "ms-settings:personalization-background", ""),
            ("Themes", "ms-settings:themes", ""), ("Dark mode / colors", "ms-settings:colors", ""),
            ("Installed apps", "ms-settings:appsfeatures", ""), ("Default apps", "ms-settings:defaultapps", ""),
            ("Startup apps", "ms-settings:startupapps", ""), ("Notifications", "ms-settings:notifications", ""),
            ("Focus / Do not disturb", "ms-settings:quiethours", ""), ("Power & battery", "ms-settings:powersleep", ""),
            ("Storage", "ms-settings:storagesense", ""), ("Windows Update", "ms-settings:windowsupdate", ""),
            ("Privacy & security", "ms-settings:privacy", ""), ("Mouse", "ms-settings:mousetouchpad", ""),
            ("Keyboard", "ms-settings:keyboard", ""), ("Date & time", "ms-settings:dateandtime", ""),
            ("Accounts", "ms-settings:yourinfo", ""), ("Taskbar", "ms-settings:taskbar", ""),
            ("Night light", "ms-settings:nightlight", ""), ("Gaming / Game Mode", "ms-settings:gaming-gamemode", ""),
            ("Location", "ms-settings:privacy-location", ""), ("Printers & scanners", "ms-settings:printers", ""),
        };

        internal LauncherWindow(AppIndex index, Settings settings, Action onCountsChanged)
        {
            InitializeComponent();
            _index = index; _settings = settings; _onCountsChanged = onCountsChanged;
            Query.TextChanged += (s, e) =>
            {
                Placeholder.Visibility = Query.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                Refresh();
            };
            PreviewKeyDown += OnKey;
            Deactivated += (s, e) => CloseLauncher(animated: true);
        }

        // ================= open / close animation =================

        public void Toggle()
        {
            if (_open) CloseLauncher(animated: true); else OpenLauncher();
        }

        void OpenLauncher()
        {
            _open = true;
            int gen = ++_gen;
            double sw = SystemParameters.PrimaryScreenWidth;

            // window = transparent strip across the top of the screen
            Left = 0; Width = sw; Top = 0; Height = RegionH;

            _from = SourceRect?.Invoke() ?? new Rect(sw / 2 - 120, 0, 240, 36);
            _to = new Rect((sw - PillW) / 2, TopMargin, PillW, PillH);

            // start: pill sitting exactly on top of the island
            Place(Pill, _from); Place(PillShadow, _from);
            Pill.Opacity = 0; PillShadow.Opacity = 0; PillContent.Opacity = 0;
            ResultsPanel.BeginAnimation(OpacityProperty, null); ResultsPanel.Opacity = 0; ResultsPanel.Visibility = Visibility.Collapsed;

            Query.Text = "";
            Refresh();

            Show();
            Activate();
            Query.Focus();

            // slide down and out of the island
            MoveTo(Pill, _to, 440, GrowEase);
            MoveTo(PillShadow, _to, 440, GrowEase);
            Fade(Pill, 1, 60, SmoothOut);
            Fade(PillShadow, 1, 320, SmoothOut, 120);
            Fade(PillContent, 1, 220, SmoothOut, 150);
            LayoutResults(appearing: true, delay: 170);
        }

        void CloseLauncher(bool animated)
        {
            if (!_open) return;
            _open = false;
            int gen = ++_gen;
            if (!animated) { Hide(); return; }

            Fade(ResultsPanel, 0, 110, SmoothIn);
            Fade(PillContent, 0, 90, SmoothIn);
            Fade(PillShadow, 0, 120, SmoothIn);
            MoveTo(Pill, _from, 230, SmoothIn);
            MoveTo(PillShadow, _from, 230, SmoothIn);
            var a = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(130), EasingFunction = SmoothIn, BeginTime = TimeSpan.FromMilliseconds(120) };
            a.Completed += (s, e) => { if (gen == _gen) Hide(); };
            Pill.BeginAnimation(OpacityProperty, a);
        }

        Rect Local(Rect screen) => new Rect(screen.X - Left, screen.Y - Top, screen.Width, screen.Height);

        void Place(FrameworkElement el, Rect screen)
        {
            var r = Local(screen);
            foreach (var dp in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty }) el.BeginAnimation(dp, null);
            Canvas.SetLeft(el, r.X); Canvas.SetTop(el, r.Y); el.Width = r.Width; el.Height = r.Height;
        }

        void MoveTo(FrameworkElement el, Rect screen, int ms, IEasingFunction ease)
        {
            var r = Local(screen);
            Animate(el, Canvas.LeftProperty, r.X, ms, ease);
            Animate(el, Canvas.TopProperty, r.Y, ms, ease);
            Animate(el, WidthProperty, r.Width, ms, ease);
            Animate(el, HeightProperty, r.Height, ms, ease);
        }

        static void Animate(UIElement el, DependencyProperty dp, double to, int ms, IEasingFunction ease, int delay = 0)
        {
            var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
            if (delay > 0) a.BeginTime = TimeSpan.FromMilliseconds(delay);
            el.BeginAnimation(dp, a);
        }

        static DoubleAnimation Fade(UIElement el, double to, int ms, IEasingFunction ease, int delay = 0)
        {
            var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
            if (delay > 0) a.BeginTime = TimeSpan.FromMilliseconds(delay);
            el.BeginAnimation(OpacityProperty, a);
            return a;
        }

        /// <summary>Sizes the results card to its rows; it hangs just below the pill and grows downward.</summary>
        void LayoutResults(bool appearing = false, int delay = 0)
        {
            if (_items.Count == 0)
            {
                if (ResultsPanel.Visibility == Visibility.Visible) Fade(ResultsPanel, 0, 120, SmoothIn);
                return;
            }
            var pill = Local(_to);
            Results.Measure(new Size(PillW - 2, double.PositiveInfinity));
            double h = Results.DesiredSize.Height + 2;   // DesiredSize already includes the list's margin; +2 for the border
            double top = pill.Y + pill.Height + Gap;
            Canvas.SetLeft(ResultsPanel, pill.X);

            bool wasHidden = ResultsPanel.Visibility != Visibility.Visible || ResultsPanel.Opacity < 0.05 || appearing;
            ResultsPanel.Visibility = Visibility.Visible;
            if (wasHidden)
            {
                ResultsPanel.BeginAnimation(HeightProperty, null); ResultsPanel.BeginAnimation(Canvas.TopProperty, null);
                ResultsPanel.Height = h; Canvas.SetTop(ResultsPanel, top);
                ResultsShift.BeginAnimation(TranslateTransform.YProperty, null);
                ResultsShift.Y = -18;
                Animate(ResultsPanel, OpacityProperty, 1, 240, SmoothOut, delay);
                var slide = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(320), EasingFunction = GrowEase };
                if (delay > 0) slide.BeginTime = TimeSpan.FromMilliseconds(delay);
                ResultsShift.BeginAnimation(TranslateTransform.YProperty, slide);
            }
            else
            {
                Animate(ResultsPanel, HeightProperty, h, 170, SmoothOut);
                Animate(ResultsPanel, Canvas.TopProperty, top, 170, SmoothOut);
                Animate(ResultsPanel, OpacityProperty, 1, 120, SmoothOut);
            }
        }

        // ================= keyboard =================

        void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: CloseLauncher(animated: true); e.Handled = true; break;
                case Key.Down: Select(_sel + 1); e.Handled = true; break;
                case Key.Up: Select(_sel - 1); e.Handled = true; break;
                case Key.Tab: Select(_sel + (Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1)); e.Handled = true; break;
                case Key.Return: RunSelected(); e.Handled = true; break;
            }
        }

        void RunSelected()
        {
            if (_sel < 0 || _sel >= _items.Count) return;
            var item = _items[_sel];
            CloseLauncher(animated: true);
            try
            {
                item.Run();
                if (item.CountKey != null)
                {
                    _settings.LaunchCounts.TryGetValue(item.CountKey, out int c);
                    _settings.LaunchCounts[item.CountKey] = c + 1;
                    _onCountsChanged();
                }
            }
            catch (Exception ex) { Log.Write("launch: " + ex.Message); }
        }

        // ================= search =================

        static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

        int Count(string key) => key != null && _settings.LaunchCounts.TryGetValue(key, out int c) ? c : 0;

        void Refresh()
        {
            string q = Query.Text.Trim();
            var items = new List<LaunchItem>();

            // empty query: just the pill. Frequently used apps still rank higher once you type.
            if (q.Length > 0)
            {
                if (Calc.LooksLikeMath(q) && Calc.TryEval(q, out double v))
                {
                    string result = Calc.Format(v);
                    items.Add(new LaunchItem
                    {
                        Title = "= " + result, Subtitle = "Calculator · Enter to copy", Glyph = "", GlyphColor = "#FF9F0A", Score = 1000,
                        Run = () => { try { Clipboard.SetText(result); } catch { } }
                    });
                }
                foreach (var a in _index.Apps)
                {
                    double s = AppIndex.Score(a.Name, q);
                    if (s > 0) items.Add(AppItem(a, s + Math.Min(30, Count("app:" + a.Name) * 3)));
                }
                foreach (var (name, uri, glyph) in SettingsPages)
                {
                    double s = AppIndex.Score(name, q);
                    if (s > 0) items.Add(new LaunchItem { Title = name, Subtitle = "Settings", Glyph = glyph, Score = s - 5 + Count("set:" + uri) * 3, CountKey = "set:" + uri, Run = () => Open(uri) });
                }
                foreach (var cmd in Commands().Concat(ExtraCommands?.Invoke() ?? Enumerable.Empty<LaunchItem>()))
                {
                    double s = AppIndex.Score(cmd.Title, q);
                    if (s > 0) { cmd.Score = s - 8; items.Add(cmd); }
                }
                foreach (var r in _index.Recent)
                {
                    double s = AppIndex.Score(r.Name, q);
                    if (s > 0)
                    {
                        var entry = r;
                        items.Add(new LaunchItem { Title = r.Name, Subtitle = "Recent file", IconSource = r.ParsingName, Score = s - 20, Run = () => AppIndex.Launch(entry) });
                    }
                }
                items = items.OrderByDescending(i => i.Score).Take(6).ToList();
                items.Add(new LaunchItem
                {
                    Title = $"Search the web for “{q}”", Subtitle = "Google", Glyph = "", GlyphColor = "#64D2FF", Score = -1,
                    Run = () => Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q))
                });
            }

            _items = items;
            Render();
            Select(0);
            if (_open) LayoutResults();
        }

        LaunchItem AppItem(AppEntry a, double score) => new LaunchItem
        {
            Title = a.Name, Subtitle = "App", IconSource = a.ParsingName, Score = score, CountKey = "app:" + a.Name,
            Run = () => AppIndex.Launch(a)
        };

        static IEnumerable<LaunchItem> Commands() => new[]
        {
            new LaunchItem { Title = "Lock PC", Subtitle = "System", Glyph = "", Run = () => LockWorkStation() },
            new LaunchItem { Title = "Sleep", Subtitle = "System", Glyph = "", Run = () => SetSuspendState(false, false, false) },
            new LaunchItem { Title = "Restart", Subtitle = "System · restarts your PC now", Glyph = "", GlyphColor = "#FF9F0A", Run = () => Process.Start("shutdown", "/r /t 0") },
            new LaunchItem { Title = "Shut down", Subtitle = "System · turns your PC off now", Glyph = "", GlyphColor = "#FF453A", Run = () => Process.Start("shutdown", "/s /t 0") },
            new LaunchItem { Title = "Downloads folder", Subtitle = "Folder", Glyph = "", Run = () => Open(TidyService.DownloadsFolder) },
            new LaunchItem { Title = "Task Manager", Subtitle = "System", Glyph = "", Run = () => Open("taskmgr") },
        };

        static readonly Brush Gray = Frozen(Color.FromRgb(0x8E, 0x8E, 0x93));
        static readonly Brush SelBg = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        static readonly Brush ChipBg = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        void Render()
        {
            Results.Children.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                int index = i;
                var row = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 14, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                UIElement icon = null;
                if (it.IconSource != null)
                {
                    var src = ShellIcons.Get(it.IconSource, 64);
                    if (src != null) icon = new Image { Source = src, Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
                }
                icon ??= new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = ChipBg, HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = it.Glyph ?? "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
                        Foreground = (Brush)new BrushConverter().ConvertFromString(it.GlyphColor),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                g.Children.Add(icon);

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = it.Title, FontSize = 15, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = it.Subtitle, FontSize = 11.5, Foreground = Gray });
                Grid.SetColumn(text, 1);
                g.Children.Add(text);

                var enter = new Border
                {
                    CornerRadius = new CornerRadius(7), Background = ChipBg, Padding = new Thickness(7, 2, 7, 3), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden,
                    Child = new TextBlock { Text = "↵", FontSize = 12, Foreground = Gray }
                };
                Grid.SetColumn(enter, 2);
                g.Children.Add(enter);

                row.Child = g;
                row.MouseEnter += (s, e) => Select(index);
                row.MouseLeftButtonUp += (s, e) => { Select(index); RunSelected(); };
                Results.Children.Add(row);
            }
        }

        void Select(int i)
        {
            if (_items.Count == 0) return;
            _sel = (i + _items.Count) % _items.Count;
            for (int k = 0; k < Results.Children.Count; k++)
            {
                var row = (Border)Results.Children[k];
                bool on = k == _sel;
                row.Background = on ? SelBg : Brushes.Transparent;
                ((Grid)row.Child).Children[2].Visibility = on ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
}
