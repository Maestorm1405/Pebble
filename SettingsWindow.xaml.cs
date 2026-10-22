using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Pebble
{
    /// <summary>One place to switch every Pebble feature on or off. Changes apply immediately.</summary>
    public partial class SettingsWindow : Window
    {
        readonly MainWindow _host;
        readonly Settings _s;
        WrapPanel _colors;

        static readonly Brush Gray = Frozen("#8E8E93");
        static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        internal SettingsWindow(MainWindow host, Settings settings)
        {
            InitializeComponent();
            _host = host; _s = settings;
            Header.MouseLeftButtonDown += (s, e) => DragMove();
            CloseBtn.Click += (s, e) => Close();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Build();
        }

        void Changed() { _s.Save(); _host.ApplySettings(); }

        async void Build()
        {
            Section("General");
            var auto = Toggle("Start with Windows", "Open Pebble automatically when you sign in.", await Autostart.IsEnabledAsync(), null);
            auto.Click += async (s, e) => auto.IsChecked = await Autostart.SetAsync(auto.IsChecked == true);
            Choice("Search shortcut", "Opens the search pill from anywhere.", new[] { "Alt+Space", "Ctrl+Space", "Ctrl+Alt+Space" },
                _s.SearchHotkey, v => { _s.SearchHotkey = v; Changed(); });
            ActionRow("Welcome tour", "See the quick intro again.", "Show tour", () => { Close(); _host.StartTour(); });

            Section("Island");
            Choice("Show in fullscreen apps", "Always: stays on top of everything. Games only: just the FPS pill in games. Never: hides for fullscreen videos and games.",
                new[] { "Always", "Games only", "Never" },
                _s.FullscreenMode == "Games" ? "Games only" : _s.FullscreenMode == "Never" ? "Never" : "Always",
                v => { _s.FullscreenMode = v == "Games only" ? "Games" : v; Changed(); });
            var openOpts = new (string Label, int Ms)[] { ("Instant", 0), ("Fast", 130), ("Normal", 300), ("Slow", 600), ("Very slow", 1000) };
            Choice("Open delay", "How long you hover over the island before it opens.", openOpts.Select(o => o.Label).ToArray(),
                Nearest(openOpts, _s.OpenDelayMs), v => { _s.OpenDelayMs = openOpts.First(o => o.Label == v).Ms; Changed(); });
            var closeOpts = new (string Label, int Ms)[] { ("Fast", 250), ("Normal", 550), ("Slow", 1000), ("Very slow", 2000) };
            Choice("Close delay", "How long the island stays open after your mouse leaves it.", closeOpts.Select(o => o.Label).ToArray(),
                Nearest(closeOpts, _s.CloseDelayMs), v => { _s.CloseDelayMs = closeOpts.First(o => o.Label == v).Ms; Changed(); });
            Toggle("Weather", "Temperature in the island and the open view.", _s.ShowWeather, v => { _s.ShowWeather = v; Changed(); });
            Choice("Temperature unit", null, new[] { "Auto", "°F", "°C" },
                _s.TempUnit == "F" ? "°F" : _s.TempUnit == "C" ? "°C" : "Auto",
                v => { _s.TempUnit = v == "°F" ? "F" : v == "°C" ? "C" : "Auto"; Changed(); });
            Toggle("Song pop-ups", "Show the new song when a track changes.", _s.SongPopups, v => { _s.SongPopups = v; Changed(); });
            Toggle("Volume pop-ups", "Show a volume bar when you change the volume.", _s.VolumePopups, v => { _s.VolumePopups = v; Changed(); });
            Toggle("Live lyrics in the island", "Show the line being sung while music plays (when lyrics are available).", _s.LyricsInIsland, v => { _s.LyricsInIsland = v; Changed(); });
            Toggle("Notifications in the island", "Discord, WhatsApp, email and other alerts slide into the island.  " + _host.NotificationStatus,
                _s.NotificationsOn, v => { _s.NotificationsOn = v; Changed(); });

            Section("Game mode");
            Toggle("Game mode", "Pebble notices when you're playing a game.", _s.GameModeOn, v => { _s.GameModeOn = v; Changed(); });
            Toggle("Show FPS and ping", "FPS is measured by Windows' compositor (capped at your refresh rate, not available in exclusive fullscreen). Ping is your connection's latency.",
                _s.GameStats, v => { _s.GameStats = v; Changed(); });
            Toggle("Hold notifications while playing", "You'll get a summary when you stop.", _s.GameHoldNotifications, v => { _s.GameHoldNotifications = v; Changed(); });

            Section("Downloads");
            Toggle("Auto-tidy", "Sort new downloads into Installers, Documents, Images and more.", _s.TidyOn, v => _host.SetTidyFromSettings(v));

            Section("Color picker");
            Toggle("Color picker shortcut", "Press Ctrl+Alt+C, then click anywhere to copy that color's hex code.", _s.ColorPickerOn, v => { _s.ColorPickerOn = v; Changed(); });
            ActionRow("Pick a color", "Recent colors are below. Click one to copy it.", "Pick now", () => { Close(); _host.PickColor(); });
            _colors = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
            Body.Children.Add(_colors);
            RenderColors();

            Section("About");
            ActionRow("Pebble 1.0.2", "Your settings, to-dos and logs are stored in your user folder.", "Open data folder",
                () => Process.Start(new ProcessStartInfo(Settings.Folder) { UseShellExecute = true }));
            ActionRow("Restart Pebble", "Useful if something looks stuck.", "Restart", () => _host.RestartPebble());
        }

        void RenderColors()
        {
            _colors.Children.Clear();
            if (_s.RecentColors.Count == 0)
            {
                _colors.Children.Add(new TextBlock { Text = "No colors yet.", Foreground = Gray, FontSize = 12 });
                return;
            }
            foreach (var hex in _s.RecentColors)
            {
                string h = hex;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(9), Background = Frozen("#2C2C2E"), Padding = new Thickness(6, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand, ToolTip = "Copy " + h
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                Brush fill; try { fill = Frozen(h); } catch { fill = Brushes.Gray; }
                sp.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = fill, BorderBrush = Frozen("#40FFFFFF"), BorderThickness = new Thickness(1) });
                sp.Children.Add(new TextBlock { Text = h, Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") });
                chip.Child = sp;
                chip.MouseLeftButtonUp += (s, e) => { try { Clipboard.SetText(h); chip.ToolTip = "Copied!"; } catch { } };
                _colors.Children.Add(chip);
            }
        }

        static string Nearest((string Label, int Ms)[] opts, int ms) =>
            opts.OrderBy(o => Math.Abs(o.Ms - ms)).First().Label;

        // ---------- row builders ----------

        void Section(string title)
        {
            Body.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Gray,
                Margin = new Thickness(2, Body.Children.Count == 0 ? 4 : 22, 0, 8)
            });
        }

        Grid Row(string title, string desc, UIElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = Brushes.White });
            if (!string.IsNullOrEmpty(desc))
                text.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(text);
            Grid.SetColumn((FrameworkElement)control, 1);
            g.Children.Add(control);
            var card = new Border { Background = Frozen("#1C1C1E"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 0, 6), Child = g };
            Body.Children.Add(card);
            return g;
        }

        ToggleButton Toggle(string title, string desc, bool value, Action<bool> onChange)
        {
            var t = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = value };
            if (onChange != null) t.Click += (s, e) => onChange(t.IsChecked == true);
            Row(title, desc, t);
            return t;
        }

        void Choice(string title, string desc, string[] options, string current, Action<string> onChange)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string group = "g" + Guid.NewGuid().ToString("N");
            foreach (var o in options)
            {
                string opt = o;
                var rb = new RadioButton { Style = (Style)FindResource("Seg"), Content = o, GroupName = group, IsChecked = o == current };
                rb.Checked += (s, e) => onChange(opt);
                sp.Children.Add(rb);
            }
            Row(title, desc, sp);
        }

        void ActionRow(string title, string desc, string button, Action onClick)
        {
            var b = new Button { Style = (Style)FindResource("Btn"), Content = button, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (s, e) => onClick();
            Row(title, desc, b);
        }
    }
}
