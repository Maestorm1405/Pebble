using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pebble
{
    /// <summary>
    /// Freezes a snapshot of the primary screen, shows a magnifier under the cursor,
    /// and copies the clicked pixel's hex code.
    /// </summary>
    public partial class ColorPickerWindow : Window
    {
        readonly Action<string> _picked;
        BitmapSource _shot;
        byte[] _px;
        int _w, _h, _stride;
        string _hex = "#000000";

        public ColorPickerWindow(Action<string> picked)
        {
            InitializeComponent();
            _picked = picked;
            Capture();
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
            MouseMove += (s, e) => Track(e.GetPosition(Root));
            MouseLeftButtonUp += (s, e) => { string hex = _hex; Done(); _picked(hex); };
            MouseRightButtonUp += (s, e) => Done();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Done(); };
            Loaded += (s, e) => { Activate(); Focus(); Track(Mouse.GetPosition(Root)); };
            Deactivated += (s, e) => Done();
        }

        bool _done;
        void Done()
        {
            if (_done) return;
            _done = true;
            Close();
        }

        void Capture()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;   // physical pixels (Pebble is DPI aware)
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Left, b.Top, 0, 0, bmp.Size);
            IntPtr h = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                _shot = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                _shot.Freeze();
            }
            finally { DeleteObject(h); }
            _w = _shot.PixelWidth; _h = _shot.PixelHeight; _stride = _w * 4;
            _px = new byte[_stride * _h];
            _shot.CopyPixels(_px, _stride, 0);
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        void Track(Point p)
        {
            double scale = _w / Math.Max(1, ActualWidth > 0 ? ActualWidth : Width);
            int x = Math.Clamp((int)(p.X * scale), 0, _w - 1), y = Math.Clamp((int)(p.Y * scale), 0, _h - 1);
            int i = y * _stride + x * 4;
            byte b = _px[i], g = _px[i + 1], r = _px[i + 2];
            _hex = $"#{r:X2}{g:X2}{b:X2}";
            Hex.Text = _hex;
            Swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

            const int n = 15; // 15x15 pixels magnified
            int cx = Math.Clamp(x - n / 2, 0, _w - n), cy = Math.Clamp(y - n / 2, 0, _h - n);
            Zoom.Source = new CroppedBitmap(_shot, new Int32Rect(cx, cy, n, n));

            // keep the loupe next to the cursor, flipping near screen edges
            double lw = Loupe.ActualWidth > 0 ? Loupe.ActualWidth : 140, lh = Loupe.ActualHeight > 0 ? Loupe.ActualHeight : 190;
            double lx = p.X + 22, ly = p.Y + 22;
            if (lx + lw > ActualWidth) lx = p.X - 22 - lw;
            if (ly + lh > ActualHeight) ly = p.Y - 22 - lh;
            System.Windows.Controls.Canvas.SetLeft(Loupe, lx);
            System.Windows.Controls.Canvas.SetTop(Loupe, ly);
        }
    }
}
