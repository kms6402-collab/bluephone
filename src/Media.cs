using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BluePhone
{
    /// <summary>
    /// 첨부 파일 바로 보기 · 미리 보기.
    /// 이미지(png · jpg · bmp · gif …)는 말풍선/쪽지 창에 바로 보이고, GIF 는 움직임까지 재생한다.
    /// 누르면 큰 미리 보기 창 (이미지 · GIF · 텍스트 · 동영상/소리).
    /// </summary>
    public static class Media
    {
        static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic" };
        static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".md", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".sql", ".cs", ".java", ".py", ".js", ".ts",
            ".html", ".htm", ".css", ".c", ".cpp", ".h", ".go", ".kt", ".swift", ".php", ".sh", ".ps1", ".bat", ".cmd", ".properties", ".gradle",
        };
        static readonly HashSet<string> AvExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".wmv", ".avi", ".mov", ".m4v", ".mp3", ".wav", ".wma", ".m4a" };

        public static bool IsImage(string name) => name != null && ImageExt.Contains(Path.GetExtension(name));
        public static bool IsGif(string name) => name != null && Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        public static bool CanPreview(string name) => name != null && (IsImage(name) || TextExt.Contains(Path.GetExtension(name)) || AvExt.Contains(Path.GetExtension(name)));
        public static bool Exists(string path) { try { return !string.IsNullOrEmpty(path) && File.Exists(path); } catch { return false; } }

        // ───────────── 이미지 불러오기 (파일을 잠그지 않고, 화면 크기에 맞춰 줄여서) ─────────────

        static BitmapSource LoadStill(string path, int maxSide)
        {
            var bytes = File.ReadAllBytes(path);
            var probe = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var f0 = probe.Frames[0];
            var b = new BitmapImage();
            b.BeginInit();
            b.StreamSource = new MemoryStream(bytes);
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (maxSide > 0 && Math.Max(f0.PixelWidth, f0.PixelHeight) > maxSide)
            {
                if (f0.PixelWidth >= f0.PixelHeight) b.DecodePixelWidth = maxSide; else b.DecodePixelHeight = maxSide;
            }
            b.EndInit();
            b.Freeze();
            return b;
        }

        public sealed class Anim
        {
            public BitmapSource[] Frames;
            public TimeSpan[] Delays;
        }

        /// <summary>GIF 의 모든 장면을 합성 (위치 · 투명 · 이전 장면 처리 반영)</summary>
        static Anim LoadGif(string path, int maxSide)
        {
            var bytes = File.ReadAllBytes(path);
            var dec = new GifBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count <= 1) return null;
            int W = 0, H = 0;
            try
            {
                var meta = dec.Metadata as BitmapMetadata;
                W = Convert.ToInt32(meta?.GetQuery("/logscrdesc/Width") ?? 0);
                H = Convert.ToInt32(meta?.GetQuery("/logscrdesc/Height") ?? 0);
            }
            catch { }
            if (W <= 0 || H <= 0) { W = dec.Frames[0].PixelWidth; H = dec.Frames[0].PixelHeight; }
            double scale = maxSide > 0 && Math.Max(W, H) > maxSide ? (double)maxSide / Math.Max(W, H) : 1;
            int frames = Math.Min(dec.Frames.Count, 400);
            // 메모리 보호: 큰 GIF 는 장면 수 · 크기를 줄임
            while (W * scale * H * scale * frames * 4 > 160_000_000 && scale > 0.2) scale *= 0.8;

            var canvas = new int[W * H];
            int[] saved = null;
            var outFrames = new List<BitmapSource>();
            var delays = new List<TimeSpan>();
            int prevDisposal = 0, pl = 0, pt = 0, pw = 0, ph = 0;
            for (int i = 0; i < frames; i++)
            {
                var f = dec.Frames[i];
                var meta = f.Metadata as BitmapMetadata;
                int Q(string q, int def) { try { var v = meta?.GetQuery(q); return v == null ? def : Convert.ToInt32(v); } catch { return def; } }
                int left = Q("/imgdesc/Left", 0), top = Q("/imgdesc/Top", 0), delay = Q("/grctlext/Delay", 10), disposal = Q("/grctlext/Disposal", 0);

                // 이전 장면의 처리 방식
                if (prevDisposal == 2) ClearRect(canvas, W, H, pl, pt, pw, ph);
                else if (prevDisposal == 3 && saved != null) Array.Copy(saved, canvas, canvas.Length);
                if (disposal == 3) saved = (int[])canvas.Clone();

                var src = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
                int fw = src.PixelWidth, fh = src.PixelHeight;
                var px = new int[fw * fh];
                src.CopyPixels(px, fw * 4, 0);
                for (int y = 0; y < fh; y++)
                {
                    int cy = top + y; if (cy < 0 || cy >= H) continue;
                    for (int x = 0; x < fw; x++)
                    {
                        int cx = left + x; if (cx < 0 || cx >= W) continue;
                        int p = px[y * fw + x];
                        if (((uint)p >> 24) != 0) canvas[cy * W + cx] = p;
                    }
                }
                var full = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, canvas, W * 4);
                BitmapSource frame = full;
                if (scale < 1)
                {
                    var tb = new TransformedBitmap(full, new ScaleTransform(scale, scale));
                    frame = new CachedBitmap(tb, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
                frame.Freeze();
                outFrames.Add(frame);
                delays.Add(TimeSpan.FromMilliseconds(delay <= 1 ? 100 : delay * 10));
                prevDisposal = disposal; pl = left; pt = top; pw = fw; ph = fh;
            }
            return new Anim { Frames = outFrames.ToArray(), Delays = delays.ToArray() };
        }

        static void ClearRect(int[] c, int W, int H, int l, int t, int w, int h)
        {
            for (int y = Math.Max(0, t); y < Math.Min(H, t + h); y++)
                for (int x = Math.Max(0, l); x < Math.Min(W, l + w); x++) c[y * W + x] = 0;
        }

        /// <summary>이미지 컨트롤에 파일을 표시 (GIF 는 재생). 백그라운드에서 읽어 창이 멈추지 않음</summary>
        public static async void Show(Image img, string path, int maxSide)
        {
            img.BeginAnimation(Image.SourceProperty, null);
            img.Source = null;
            if (!Exists(path)) return;
            img.Tag = path;
            try
            {
                Anim anim = null; BitmapSource still = null;
                bool gif = IsGif(path);
                await Task.Run(() =>
                {
                    if (gif) { try { anim = LoadGif(path, maxSide); } catch { anim = null; } }
                    if (anim == null) still = LoadStill(path, maxSide);
                });
                if (!Equals(img.Tag, path)) return;   // 그 사이 다른 파일로 바뀜
                if (anim != null && anim.Frames.Length > 1)
                {
                    var a = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                    var t = TimeSpan.Zero;
                    for (int i = 0; i < anim.Frames.Length; i++)
                    {
                        a.KeyFrames.Add(new DiscreteObjectKeyFrame(anim.Frames[i], KeyTime.FromTimeSpan(t)));
                        t += anim.Delays[i];
                    }
                    a.Duration = t;
                    img.Source = anim.Frames[0];
                    img.BeginAnimation(Image.SourceProperty, a);
                }
                else img.Source = still;
            }
            catch { img.Source = null; }
        }

        // 첨부 속성: <Image local:Media.Path="{Binding PreviewPath}"/>
        public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
            "Path", typeof(string), typeof(Media), new PropertyMetadata(null, (d, e) =>
            {
                if (d is Image img) Show(img, (string)e.NewValue, 640);
            }));
        public static string GetPath(DependencyObject d) => (string)d.GetValue(PathProperty);
        public static void SetPath(DependencyObject d, string v) => d.SetValue(PathProperty, v);

        // ───────────── 미리 보기 창 ─────────────

        public static void OpenPreview(string path, Window owner)
        {
            if (!Exists(path)) return;
            if (!CanPreview(path)) { OpenExternal(path); return; }
            var w = new Window
            {
                Title = Path.GetFileName(path) + " — 미리 보기", Width = 760, Height = 600, MinWidth = 360, MinHeight = 260,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            Chrome.Apply(w, caption: 46);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            var look = ChatLook.Of(owner);
            if (look != null) ChatLook.Attach(w, look);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 머리글
            var head = new DockPanel { Margin = new Thickness(16, 0, 0, 0) };
            var close = new Button { Style = (Style)Application.Current.FindResource("ChromeClose"), Content = "" };
            close.Click += (s, e) => w.Close();
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            var info = new FileInfo(path);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 });
            var sizeText = new TextBlock { Text = "  " + FileItem.FormatSize(info.Length), Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(sizeText);
            head.Children.Add(title);
            grid.Children.Add(head);

            // 본문
            FrameworkElement body;
            MediaElement media = null;
            var ext = Path.GetExtension(path);
            if (IsImage(path))
            {
                var img = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Show(img, path, 2400);
                img.Loaded += (s, e) =>
                {
                    if (img.Source is BitmapSource bs) sizeText.Text = $"  {bs.PixelWidth}×{bs.PixelHeight} · {FileItem.FormatSize(info.Length)}";
                };
                body = new Border { Child = img, Margin = new Thickness(12, 0, 12, 0) };
            }
            else if (TextExt.Contains(ext))
            {
                string text;
                try
                {
                    using (var fs = File.OpenRead(path))
                    {
                        var buf = new byte[Math.Min(fs.Length, 512 * 1024)];
                        int n = fs.Read(buf, 0, buf.Length);
                        text = DecodeText(buf, n);
                        if (fs.Length > buf.Length) text += "\n\n… (처음 512KB 만 표시)";
                    }
                }
                catch (Exception ex) { text = "파일을 읽지 못했습니다: " + ex.Message; }
                var tb = new TextBox
                {
                    Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas, D2Coding, Malgun Gothic"), FontSize = 13,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Style = (Style)Application.Current.FindResource("Field"), Padding = new Thickness(10), VerticalContentAlignment = VerticalAlignment.Top,
                };
                body = new Border { Child = tb, Margin = new Thickness(12, 0, 12, 0) };
            }
            else
            {
                media = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
                body = new Border { Child = media, Background = Brushes.Black, Margin = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(6) };
            }
            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            // 아래 버튼
            var bar = new DockPanel { Margin = new Thickness(12, 10, 12, 12), LastChildFill = false };
            Button Btn(string text, bool primary = false)
            {
                var b = new Button { Content = text, Height = 32, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
                    Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Outline") };
                DockPanel.SetDock(b, Dock.Right);
                bar.Children.Add(b);
                return b;
            }
            var open = Btn("기본 프로그램으로 열기", true);
            open.Click += (s, e) => OpenExternal(path);
            var folder = Btn("폴더에서 보기");
            folder.Click += (s, e) => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } };
            if (IsImage(path))
            {
                var copy = Btn("이미지 복사");
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetImage(LoadStill(path, 0)); copy.Content = "복사됨"; } catch { }
                };
            }
            if (media != null)
            {
                var play = new Button { Content = "일시 정지", Height = 32, Padding = new Thickness(14, 0, 14, 0), Style = (Style)Application.Current.FindResource("Outline") };
                bool playing = true;
                play.Click += (s, e) => { if (playing) media.Pause(); else media.Play(); playing = !playing; play.Content = playing ? "일시 정지" : "재생"; };
                DockPanel.SetDock(play, Dock.Left);
                bar.Children.Add(play);
                media.MediaEnded += (s, e) => { media.Position = TimeSpan.Zero; media.Pause(); playing = false; play.Content = "재생"; };
                media.MediaFailed += (s, e) => { play.IsEnabled = false; play.Content = "이 PC에서 재생할 수 없는 형식"; };
                w.Loaded += (s, e) => media.Play();
                w.Closed += (s, e) => { try { media.Stop(); media.Close(); } catch { } };
            }
            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);

            var bg = new Border { Child = grid };
            bg.SetResourceReference(Border.BackgroundProperty, "Surface");
            bg.SetResourceReference(TextElement.ForegroundProperty, "Ink");
            w.Content = bg;
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.Show();
            Native.BringToFront(w);
        }

        static string DecodeText(byte[] buf, int n)
        {
            // UTF-8(BOM 포함) 우선, 깨지면 한국어 윈도우 기본(CP949)
            try { return new UTF8Encoding(false, true).GetString(buf, 0, n).TrimStart('﻿'); }
            catch { try { return Encoding.GetEncoding(949).GetString(buf, 0, n); } catch { return Encoding.Default.GetString(buf, 0, n); } }
        }

        public static void OpenExternal(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }
    }
}
