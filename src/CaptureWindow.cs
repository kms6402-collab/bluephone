using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace BluePhone
{
    /// <summary>
    /// 화면 영역 캡처. 전체 가상 화면을 먼저 찍어 둔 뒤 그 위에서 드래그로 영역을 고른다.
    /// 창을 물리 픽셀 좌표로 덮고 이미지를 늘려 그리므로, 고른 영역은 비율로 원본 픽셀에 대응한다.
    /// </summary>
    public sealed class CaptureWindow : Window
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        static bool _active;
        readonly Drawing.Bitmap _shot;
        readonly int _vx, _vy, _vw, _vh;
        readonly Action<string> _done;
        readonly Canvas _canvas = new Canvas();
        readonly System.Windows.Shapes.Path _shade;
        readonly Rectangle _frame;
        readonly Border _label;
        readonly TextBlock _labelText;
        Point? _start;
        Rect _sel;
        bool _finished;

        public static void Capture(Action<string> done)
        {
            if (_active) return;
            _active = true;
            // 열려 있는 우리 창이 찍히지 않도록 잠깐 기다린 뒤 촬영
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try { new CaptureWindow(done).Show(); }
                catch { _active = false; done(null); }
            };
            timer.Start();
        }

        CaptureWindow(Action<string> done)
        {
            _done = done;
            _vx = GetSystemMetrics(76); _vy = GetSystemMetrics(77); _vw = GetSystemMetrics(78); _vh = GetSystemMetrics(79);
            _shot = new Drawing.Bitmap(_vw, _vh, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(_shot)) g.CopyFromScreen(_vx, _vy, 0, 0, new Drawing.Size(_vw, _vh));

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;
            Background = Brushes.Black;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            Title = "화면 캡처";

            var h = _shot.GetHbitmap();
            BitmapSource src;
            try { src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            finally { DeleteObject(h); }
            src.Freeze();

            var grid = new Grid();
            grid.Children.Add(new Image { Source = src, Stretch = Stretch.Fill });
            _shade = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) };
            _frame = new Rectangle { Stroke = (Brush)Application.Current.FindResource("Accent"), StrokeThickness = 2, Visibility = Visibility.Collapsed };
            _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = (FontFamily)Application.Current.FindResource("UI") };
            _label = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 21, 24, 29)), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Child = _labelText, Visibility = Visibility.Collapsed };
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 21, 24, 29)), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 8, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0),
                Child = new TextBlock { Text = "드래그해서 보낼 영역을 고르세요 · Esc 취소", Foreground = Brushes.White, FontSize = 13, FontFamily = (FontFamily)Application.Current.FindResource("UI") },
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_label);
            grid.Children.Add(_shade);
            grid.Children.Add(_canvas);
            grid.Children.Add(hint);
            Content = grid;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                // 모니터별 DPI 변경 시 WPF 가 창 크기를 바꾸지 않도록 WM_DPICHANGED 를 막고 다시 덮는다
                HwndSource.FromHwnd(hwnd).AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x02E0) { handled = true; Cover(hw); }
                    return IntPtr.Zero;
                });
                Cover(hwnd);
            };
            Loaded += (s, e) => { UpdateShade(); Activate(); Focus(); };
            SizeChanged += (s, e) => UpdateShade();
            MouseLeftButtonDown += (s, e) => { _start = e.GetPosition(this); CaptureMouse(); };
            MouseMove += (s, e) =>
            {
                if (_start == null) return;
                var p = e.GetPosition(this);
                _sel = new Rect(_start.Value, p);
                UpdateShade();
            };
            MouseLeftButtonUp += (s, e) =>
            {
                ReleaseMouseCapture();
                if (_start == null) return;
                _start = null;
                if (_sel.Width < 4 || _sel.Height < 4) return;
                Finish(Save());
            };
            MouseRightButtonUp += (s, e) => Finish(null);
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Finish(null); };
            Deactivated += (s, e) => { if (!_finished && IsLoaded) Activate(); };
        }

        void Cover(IntPtr hwnd) => SetWindowPos(hwnd, new IntPtr(-1), _vx, _vy, _vw, _vh, 0x0040);

        void UpdateShade()
        {
            var all = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            if (_sel.Width > 0 && _sel.Height > 0)
            {
                _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(_sel));
                _frame.Visibility = _label.Visibility = Visibility.Visible;
                Canvas.SetLeft(_frame, _sel.X - 1); Canvas.SetTop(_frame, _sel.Y - 1);
                _frame.Width = _sel.Width + 2; _frame.Height = _sel.Height + 2;
                var (pw, ph) = ((int)Math.Round(_sel.Width * Sx), (int)Math.Round(_sel.Height * Sy));
                _labelText.Text = $"{pw} × {ph}";
                Canvas.SetLeft(_label, _sel.X);
                Canvas.SetTop(_label, _sel.Y > 30 ? _sel.Y - 28 : _sel.Bottom + 6);
            }
            else _shade.Data = all;
        }

        double Sx => ActualWidth > 0 ? _vw / ActualWidth : 1;
        double Sy => ActualHeight > 0 ? _vh / ActualHeight : 1;

        string Save()
        {
            var r = new Drawing.Rectangle((int)Math.Round(_sel.X * Sx), (int)Math.Round(_sel.Y * Sy), (int)Math.Round(_sel.Width * Sx), (int)Math.Round(_sel.Height * Sy));
            r.Intersect(new Drawing.Rectangle(0, 0, _vw, _vh));
            if (r.Width < 2 || r.Height < 2) return null;
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BluePhone");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"캡처 {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            using (var crop = _shot.Clone(r, _shot.PixelFormat)) crop.Save(path, Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        void Finish(string path)
        {
            if (_finished) return;
            _finished = true;
            _active = false;
            Close();
            _shot.Dispose();
            _done(path);
        }
    }

    /// <summary>전역 단축키 (메시지 전용 창으로 받음)</summary>
    public sealed class HotKey : IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        public const uint Alt = 1, Ctrl = 2, Shift = 4, NoRepeat = 0x4000;
        readonly HwndSource _src;
        readonly int _id = 0xB10E;
        public bool Registered { get; }

        public HotKey(uint mods, Key key, Action action)
        {
            _src = new HwndSource(new HwndSourceParameters("BluePhoneHotKey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            _src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x0312 && wp.ToInt32() == _id) { handled = true; action(); }
                return IntPtr.Zero;
            });
            Registered = RegisterHotKey(_src.Handle, _id, mods | NoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key));
        }

        public void Dispose()
        {
            try { UnregisterHotKey(_src.Handle, _id); } catch { }
            _src.Dispose();
        }
    }
}
