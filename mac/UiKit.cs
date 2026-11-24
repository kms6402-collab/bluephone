using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace BluePhone
{
    /// <summary>앱 스타일의 알림 · 확인 · 입력 대화 상자 (모두 비동기)</summary>
    public static class Dialog
    {
        public static Task<bool> Confirm(Window owner, string title, string body, string ok = "확인", string cancel = "취소", bool danger = false)
            => Show(owner, title, body, ok, cancel, danger);

        public static Task Alert(Window owner, string title, string body) => Show(owner, title, body, "확인", null, false);

        public static async Task<string> Prompt(Window owner, string title, string body, string value, string ok = "확인", int maxLength = 40)
        {
            var box = new TextBox { Text = value ?? "", MaxLength = maxLength, Margin = new Thickness(0, 0, 0, 20) };
            box.Classes.Add("field");
            string result = null;
            var w = Make(title, 380);
            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(Body(body, 14));
            root.Children.Add(box);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var c = Btn("취소", "outline"); c.IsCancel = true; c.Click += (s, e) => w.Close();
            var o = Btn(ok, "primary"); o.IsDefault = true; o.Click += (s, e) => { result = box.Text?.Trim() ?? ""; w.Close(); };
            bar.Children.Add(c); bar.Children.Add(o);
            root.Children.Add(bar);
            w.Content = root;
            w.Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            await ShowWindow(w, owner);
            return result;
        }

        static async Task<bool> Show(Window owner, string title, string body, string ok, string cancel, bool danger)
        {
            bool result = false;
            var w = Make(title, 380);
            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(Body(body, 20));
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            if (cancel != null)
            {
                var c = Btn(cancel, "outline"); c.IsCancel = true; c.Click += (s, e) => w.Close();
                bar.Children.Add(c);
            }
            var o = Btn(ok, danger ? "danger" : "primary"); o.IsDefault = true; o.Click += (s, e) => { result = true; w.Close(); };
            bar.Children.Add(o);
            root.Children.Add(bar);
            w.Content = root;
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            if (owner == null) w.Topmost = true;
            await ShowWindow(w, owner);
            return result;
        }

        static TextBlock Body(string text, double bottom)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, bottom), LineHeight = 20 };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("Ink2"));
            return tb;
        }

        public static Window Make(string title, double width)
        {
            var w = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            w.Classes.Add("app");
            Icons.Apply(w);
            return w;
        }

        public static Button Btn(string text, string cls)
        {
            var b = new Button { Content = text, MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Classes.Add(cls);
            return b;
        }

        /// <summary>소유 창이 보이면 그 위에 모달로, 아니면 화면 가운데 일반 창으로 띄우고 닫힐 때까지 기다림</summary>
        public static Task ShowWindow(Window w, Window owner)
        {
            if (owner != null && owner.IsVisible) return w.ShowDialog(owner);
            var tcs = new TaskCompletionSource<bool>();
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Closed += (s, e) => tcs.TrySetResult(true);
            w.Show();
            w.Activate();
            Native.ActivateApp();
            return tcs.Task;
        }
    }

    /// <summary>버튼 아래(위)에 펼치는 메뉴</summary>
    public static class Menus
    {
        public static MenuItem Item(string header, Action click = null, bool bold = false, bool? check = null, string gesture = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (bold) mi.FontWeight = FontWeight.SemiBold;
            if (check.HasValue) { mi.ToggleType = MenuItemToggleType.CheckBox; mi.IsChecked = check.Value; }
            if (!string.IsNullOrEmpty(gesture)) mi.InputGesture = TryGesture(gesture);
            if (click != null) mi.Click += (s, e) => click();
            return mi;
        }

        static KeyGesture TryGesture(string g) { try { return KeyGesture.Parse(g); } catch { return null; } }

        public static Separator Sep() => new Separator();

        public static void Show(Control anchor, IEnumerable<Control> items, PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft)
        {
            var f = new MenuFlyout { Placement = placement };
            foreach (var i in items) f.Items.Add(i);
            f.ShowAt(anchor);
        }

        public static Dot StatusDot(IBrush b, double size = 8) => new Dot(b, size);

        public sealed class Dot : Ellipse
        {
            public Dot(IBrush b, double size) { Width = Height = size; Fill = b; VerticalAlignment = VerticalAlignment.Center; }
        }
    }

    /// <summary>ObservableCollection 을 원하는 목록과 같게 맞춤 (전부 다시 넣지 않아 스크롤 · 선택이 유지됨)</summary>
    public static class ListSync
    {
        public static void Sync<T>(ObservableCollection<T> target, IList<T> desired)
        {
            var want = new HashSet<T>(desired);
            for (int i = target.Count - 1; i >= 0; i--)
                if (!want.Contains(target[i])) target.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                var d = desired[i];
                if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], d)) continue;
                int j = -1;
                for (int k = i + 1; k < target.Count; k++)
                    if (EqualityComparer<T>.Default.Equals(target[k], d)) { j = k; break; }
                if (j >= 0) target.Move(j, i);
                else target.Insert(i, d);
            }
            while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
        }
    }

    public static class Keys
    {
        /// <summary>주 수정키 (macOS: ⌘, 그 밖: Ctrl)</summary>
        public static bool Primary(KeyModifiers m) => Native.IsMac ? m == KeyModifiers.Meta : m == KeyModifiers.Control;
        public static bool PrimaryShift(KeyModifiers m) => Native.IsMac ? m == (KeyModifiers.Meta | KeyModifiers.Shift) : m == (KeyModifiers.Control | KeyModifiers.Shift);
        public static bool HasPrimary(KeyModifiers m) => Native.IsMac ? m.HasFlag(KeyModifiers.Meta) : m.HasFlag(KeyModifiers.Control);
        public static string Gesture(string key) => (Native.IsMac ? "Cmd+" : "Ctrl+") + key;
    }

    public static class ClipKit
    {
        public static async void SetText(Visual v, string text)
        {
            try
            {
                var cb = TopLevel.GetTopLevel(v)?.Clipboard;
                if (cb != null) await cb.SetTextAsync(text ?? "");
            }
            catch { }
        }

        /// <summary>클립보드의 파일 목록 또는 이미지(PNG 로 저장) — 없으면 null</summary>
        public static async Task<IList<string>> FilesAsync(Visual v)
        {
            try
            {
                var cb = TopLevel.GetTopLevel(v)?.Clipboard;
                if (cb == null) return null;
                var formats = await cb.GetFormatsAsync();
                if (formats.Contains(DataFormats.Files))
                {
                    if (await cb.GetDataAsync(DataFormats.Files) is IEnumerable<Avalonia.Platform.Storage.IStorageItem> items)
                    {
                        var list = items.Select(i => i.TryGetLocalPath()).Where(p => p != null).ToList();
                        if (list.Count > 0) return list;
                    }
                }
                if (formats.Contains(DataFormats.Text) || formats.Contains("public.utf8-plain-text")) return null;
                foreach (var f in new[] { "public.png", "PNG", "image/png", "public.tiff", "public.jpeg" })
                {
                    if (!formats.Contains(f)) continue;
                    if (await cb.GetDataAsync(f) is byte[] bytes && bytes.Length > 0)
                    {
                        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BluePhone");
                        Directory.CreateDirectory(dir);
                        var path = System.IO.Path.Combine(dir, $"캡처 {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                        if (f.Contains("png", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(path, bytes);
                        else
                        {
                            using var bmp = new Bitmap(new MemoryStream(bytes));
                            bmp.Save(path);
                        }
                        return new[] { path };
                    }
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>원형 프로필 (색 + 첫 글자 + 사진 + 상태 점)</summary>
    public sealed class Avatar : Panel
    {
        public static readonly StyledProperty<IBrush> FillProperty = AvaloniaProperty.Register<Avatar, IBrush>(nameof(Fill));
        public static readonly StyledProperty<string> InitialProperty = AvaloniaProperty.Register<Avatar, string>(nameof(Initial));
        public static readonly StyledProperty<Bitmap> ImageProperty = AvaloniaProperty.Register<Avatar, Bitmap>(nameof(Image));
        public static readonly StyledProperty<IBrush> StatusProperty = AvaloniaProperty.Register<Avatar, IBrush>(nameof(Status));
        public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Avatar, double>(nameof(Size), 32);

        public IBrush Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
        public string Initial { get => GetValue(InitialProperty); set => SetValue(InitialProperty, value); }
        public Bitmap Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
        public IBrush Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
        public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

        readonly Ellipse _bg = new Ellipse(), _photo = new Ellipse(), _ring = new Ellipse(), _dot = new Ellipse();
        readonly TextBlock _text = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        public Avatar()
        {
            Children.Add(_bg); Children.Add(_text); Children.Add(_photo); Children.Add(_ring); Children.Add(_dot);
            _ring.HorizontalAlignment = _dot.HorizontalAlignment = HorizontalAlignment.Right;
            _ring.VerticalAlignment = _dot.VerticalAlignment = VerticalAlignment.Bottom;
            _ring.Bind(Shape.FillProperty, _ring.GetResourceObservable("Surface"));
            Update();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FillProperty || change.Property == InitialProperty || change.Property == ImageProperty
                || change.Property == StatusProperty || change.Property == SizeProperty) Update();
        }

        void Update()
        {
            double s = Size;
            Width = Height = s;
            _bg.Fill = Fill ?? Brushes.Gray;
            _text.Text = Initial ?? "";
            _text.FontSize = Math.Max(9, s * 0.4);
            _photo.IsVisible = Image != null;
            _photo.Fill = Image != null ? new ImageBrush(Image) { Stretch = Stretch.UniformToFill } : null;
            bool st = Status != null;
            _ring.IsVisible = _dot.IsVisible = st;
            double d = Math.Max(7, Math.Round(s * 0.25));
            _dot.Width = _dot.Height = d;
            _ring.Width = _ring.Height = d + 4;
            _ring.Margin = new Thickness(0, 0, -2, -2);
            _dot.Fill = Status;
        }
    }

    /// <summary>파일 경로로 이미지를 표시 (백그라운드에서 읽고, 큰 사진은 줄여서)</summary>
    public sealed class AsyncImage : Image
    {
        public static readonly StyledProperty<string> PathProperty = AvaloniaProperty.Register<AsyncImage, string>(nameof(Path));
        public static readonly StyledProperty<int> MaxSideProperty = AvaloniaProperty.Register<AsyncImage, int>(nameof(MaxSide), 640);
        public string Path { get => GetValue(PathProperty); set => SetValue(PathProperty, value); }
        public int MaxSide { get => GetValue(MaxSideProperty); set => SetValue(MaxSideProperty, value); }

        public AsyncImage() { Stretch = Stretch.Uniform; StretchDirection = StretchDirection.DownOnly; }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PathProperty) Load(Path);
        }

        async void Load(string path)
        {
            Source = null;
            IsVisible = false;
            if (!Media.Exists(path)) return;
            var bmp = await Media.LoadAsync(path, MaxSide);
            if (path != Path) { bmp?.Dispose(); return; }
            Source = bmp;
            IsVisible = bmp != null;
        }
    }

    /// <summary>로고 · 앱 아이콘 · 메뉴 막대 아이콘 (코드로 그림)</summary>
    public static class Icons
    {
        public const string Handset = "M6.62 10.79c1.44 2.83 3.76 5.14 6.59 6.59l2.2-2.2c.27-.27.67-.36 1.02-.24 1.12.37 2.33.57 3.57.57.55 0 1 .45 1 1V20c0 .55-.45 1-1 1-9.39 0-17-7.61-17-17 0-.55.45-1 1-1h3.5c.55 0 1 .45 1 1 0 1.25.2 2.45.57 3.57.11.35.03.74-.25 1.02l-2.2 2.2z";

        static WindowIcon _app;

        public static WindowIcon App
        {
            get
            {
                if (_app != null) return _app;
                try
                {
                    var s = typeof(Icons).Assembly.GetManifestResourceStream("AppIcon.png");
                    if (s != null) _app = new WindowIcon(s);
                }
                catch { }
                return _app;
            }
        }

        public static void Apply(Window w) { if (App != null) w.Icon = App; }

        /// <summary>파란 둥근 사각형 + 흰 수화기 (제목줄 로고)</summary>
        public static Control Logo(double size = 20)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.25) };
            b.Bind(Border.BackgroundProperty, b.GetResourceObservable("Accent"));
            b.Child = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(Handset), Fill = Brushes.White, Stretch = Stretch.Uniform, Margin = new Thickness(size * 0.22) };
            return b;
        }

        /// <summary>메뉴 막대용 수화기 아이콘 (검은색 · 템플릿 이미지). dot 이면 안 읽은 표시 점</summary>
        public static WindowIcon Tray(bool dot)
        {
            try
            {
                const int s = 44;
                using var rtb = new RenderTargetBitmap(new PixelSize(s, s), new Vector(96, 96));
                using (var ctx = rtb.CreateDrawingContext())
                {
                    var g = Geometry.Parse(Handset);
                    double scale = (s * 0.78) / 24.0;
                    using (ctx.PushTransform(Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(s / 2.0 - (dot ? 3 : 0), s / 2.0)))
                        ctx.DrawGeometry(Brushes.Black, null, g);
                    if (dot)
                    {
                        ctx.DrawEllipse(Brushes.Transparent, new Pen(Brushes.Black, 0), new Point(s - 9, 9), 8, 8);
                        ctx.DrawEllipse(Brushes.Black, null, new Point(s - 9, 9), 6.5, 6.5);
                    }
                }
                var ms = new MemoryStream();
                rtb.Save(ms);
                ms.Position = 0;
                return new WindowIcon(ms);
            }
            catch { return App; }
        }
    }

    public static class Conv
    {
        public static readonly IValueConverter NotEmpty = new FuncValueConverter<object, bool>(v =>
            v is string s ? !string.IsNullOrWhiteSpace(s) : v is int i ? i > 0 : v is bool b ? b : v != null);
        public static readonly IValueConverter Prefix = new FuncValueConverter<string, string>(s => string.IsNullOrEmpty(s) ? "" : " · " + s);
        public static readonly IValueConverter Group = new FuncValueConverter<string, string>(s => "여러 명에게 · " + s);
    }
}
