using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pebble
{
    /// <summary>First-launch walkthrough: a card with an arrow pointing at the island.</summary>
    public partial class TourWindow : Window
    {
        readonly Action<int> _onStep;
        readonly Action _onDone;
        int _step;
        bool _finished;

        // icon, title, body, card top (DIPs below the top of the screen)
        readonly (int Glyph, string Title, string Body, double Top)[] _steps;

        internal TourWindow(string searchKey, Action<int> onStep, Action onDone)
        {
            InitializeComponent();
            _onStep = onStep; _onDone = onDone;
            _steps = new[]
            {
                (0xE76E, "Hi, I'm Pebble 👋", "I live at the top of your screen. Hover over me any time to open me up.", 46.0),
                (0xE8A9, "Everything in one place", "Music, timer, to-do, clipboard, file shelf, teleprompter and Downloads each have a tab. Drag files onto me to keep them on the shelf.", 250.0),
                (0xE721, "Search anything", $"Press {searchKey} (or the magnifier in the island) to find apps, settings and files, do quick math, or search the web.", 46.0),
                (0xE7FC, "Built for gaming", "When you play, I show your FPS and ping and hold notifications until you're done.", 46.0),
                (0xE713, "Make it yours", "Click the gear in the island, or right-click my icon in the taskbar tray, to open Settings and switch anything on or off.", 250.0),
            };
            NextBtn.Click += (s, e) => Go(_step + 1);
            SkipBtn.Click += (s, e) => Finish();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Finish();
                else if (e.Key == Key.Right || e.Key == Key.Return) Go(_step + 1);
                else if (e.Key == Key.Left) Go(Math.Max(0, _step - 1));
            };
            Loaded += (s, e) => Go(0);
        }

        void Go(int i)
        {
            if (i >= _steps.Length) { Finish(); return; }
            _step = i;
            var st = _steps[i];
            StepIcon.Text = char.ConvertFromUtf32(st.Glyph);
            StepTitle.Text = st.Title;
            StepBody.Text = st.Body;
            NextBtn.Content = i == _steps.Length - 1 ? "Get started" : "Next";
            SkipBtn.Visibility = i == _steps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;

            Dots.Children.Clear();
            for (int k = 0; k < _steps.Length; k++)
                Dots.Children.Add(new Border
                {
                    Width = k == i ? 18 : 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(0, 0, 5, 0),
                    Background = new SolidColorBrush(k == i ? Colors.White : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                });

            _onStep(i);
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            var slide = new DoubleAnimation { To = st.Top, Duration = TimeSpan.FromMilliseconds(380), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } };
            if (i == 0) Top = st.Top - 20;
            BeginAnimation(TopProperty, slide);
            Card.Opacity = 0;
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        }

        void Finish()
        {
            if (_finished) return;
            _finished = true;
            _onDone();
            Close();
        }
    }
}
