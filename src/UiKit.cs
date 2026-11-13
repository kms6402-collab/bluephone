using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace BluePhone
{
    public static class Chrome
    {
        /// <summary>시스템 그림자 · 스냅 · 리사이즈를 유지하는 커스텀 타이틀바</summary>
        public static void Apply(Window w, double caption = 40, bool resizable = true)
        {
            w.Style = (Style)Application.Current.FindResource("AppWindow");
            if (!resizable) w.ResizeMode = ResizeMode.CanMinimize;
            WindowChrome.SetWindowChrome(w, new WindowChrome
            {
                CaptionHeight = caption,
                ResizeBorderThickness = new Thickness(resizable ? 6 : 0),
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            w.SourceInitialized += (s, e) => Native.RoundCorners(w);
            w.StateChanged += (s, e) =>
                w.BorderThickness = w.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);
            if (w.Icon == null)
                w.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                    typeof(Chrome).Assembly.GetManifestResourceStream("app.ico"));
        }
    }

    /// <summary>앱 스타일에 맞춘 간단한 알림/확인 대화상자</summary>
    public static class Dialog
    {
        public static bool Confirm(Window owner, string title, string body, string ok = "확인", string cancel = "취소", bool danger = false)
            => Show(owner, title, body, ok, cancel, danger);

        public static void Alert(Window owner, string title, string body) => Show(owner, title, body, "확인", null, false);

        /// <summary>한 줄 입력. 취소하면 null</summary>
        public static string Prompt(Window owner, string title, string body, string value, string ok = "확인")
        {
            var box = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Text = value, Margin = new Thickness(0, 0, 0, 20), MaxLength = 40 };
            string result = null;
            var w = new Window
            {
                Width = 380, SizeToContent = SizeToContent.Height, ShowInTaskbar = false, Title = title,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            Chrome.Apply(w, caption: 0, resizable: false);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock
            {
                Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
                Foreground = (Brush)Application.Current.FindResource("Ink2"), LineHeight = 20,
            });
            root.Children.Add(box);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var c = new Button { Content = "취소", Style = (Style)Application.Current.FindResource("Outline"), MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            c.Click += (s, e) => w.Close();
            var o = new Button { Content = ok, Style = (Style)Application.Current.FindResource("Primary"), MinWidth = 76, IsDefault = true };
            o.Click += (s, e) => { result = box.Text.Trim(); w.Close(); };
            bar.Children.Add(c); bar.Children.Add(o);
            root.Children.Add(bar);
            w.Content = root;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            w.ShowDialog();
            return result;
        }

        static bool Show(Window owner, string title, string body, string ok, string cancel, bool danger)
        {
            var w = new Window
            {
                Width = 380, SizeToContent = SizeToContent.Height, ShowInTaskbar = owner == null,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Title = title, Topmost = owner == null,
            };
            Chrome.Apply(w, caption: 0, resizable: false);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            bool result = false;

            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(new TextBlock
            {
                Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 20),
                Foreground = (Brush)Application.Current.FindResource("Ink2"), LineHeight = 20,
            });
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (cancel != null)
            {
                var c = new Button { Content = cancel, Style = (Style)Application.Current.FindResource("Outline"), MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
                c.Click += (s, e) => w.Close();
                bar.Children.Add(c);
            }
            var o = new Button { Content = ok, Style = (Style)Application.Current.FindResource("Primary"), MinWidth = 76, IsDefault = true };
            if (danger) o.Resources["Accent"] = o.Resources["AccentHover"] = o.Resources["AccentPressed"] = Application.Current.FindResource("Danger");
            o.Click += (s, e) => { result = true; w.Close(); };
            bar.Children.Add(o);
            root.Children.Add(bar);
            w.Content = root;
            w.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.ShowDialog();
            return result;
        }
    }

    public static class TrayMenu
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        public static ContextMenu Build(Core core)
        {
            var m = new ContextMenu();
            void Add(string text, Action a, bool check = false, bool bold = false)
            {
                var mi = new MenuItem { Header = text, IsChecked = check };
                if (bold) mi.FontWeight = FontWeights.SemiBold;
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
            }
            Add("파란전화기 열기", core.ShowMain, bold: true);
            Add("새 쪽지", () => core.Compose(new Peer[0]));
            if (core.Unread > 0) Add($"모두 읽음 처리 ({core.Unread})", core.MarkAllRead);
            Add("화면 캡처해서 보내기", () => core.StartCapture());
            Add("받은 파일 폴더 열기", () => core.OpenDownloads());
            m.Items.Add(new Separator());
            foreach (var s in new[] { Status.Online, Status.Busy, Status.Away, Status.Dnd })
            {
                var st = s;
                Add(Status.Label(st), () => core.SetStatus(st), check: core.MyStatus == st);
            }
            m.Items.Add(new Separator());
            Add("설정", () => core.ShowSettings(null));
            if (Updater.Ready != null) Add($"업데이트 {Updater.Ready.VersionText} 적용 후 다시 시작", core.ApplyUpdateAndRestart, bold: true);
            Add("종료", core.Quit);
            return m;
        }

        public static void OpenAtCursor(ContextMenu m)
        {
            m.Placement = PlacementMode.MousePoint;
            m.Opened += (s, e) =>
            {
                if (PresentationSource.FromVisual(m) is HwndSource src) SetForegroundWindow(src.Handle);
                m.Focus();
            };
            m.IsOpen = true;
        }
    }

    public sealed class BoolToVis : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            bool b = v is bool x ? x : v is int i ? i > 0 : v is string s ? !string.IsNullOrEmpty(s) : v != null;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
