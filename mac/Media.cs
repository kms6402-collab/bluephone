using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace BluePhone
{
    /// <summary>첨부 파일 바로 보기 · 미리 보기 창 (이미지 · 텍스트). 동영상 · 소리는 기본 프로그램으로 엶</summary>
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
        public static bool CanPreview(string name) => name != null && (IsImage(name) || TextExt.Contains(Path.GetExtension(name)) || AvExt.Contains(Path.GetExtension(name)));
        public static bool Exists(string path) { try { return !string.IsNullOrEmpty(path) && File.Exists(path); } catch { return false; } }

        /// <summary>이미지를 백그라운드에서 읽어 긴 변이 maxSide 를 넘지 않게 줄임</summary>
        public static Task<Bitmap> LoadAsync(string path, int maxSide) => Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(path);
                var full = new Bitmap(fs);
                var sz = full.PixelSize;
                if (maxSide <= 0 || Math.Max(sz.Width, sz.Height) <= maxSide) return full;
                double k = (double)maxSide / Math.Max(sz.Width, sz.Height);
                var small = full.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(sz.Width * k)), Math.Max(1, (int)(sz.Height * k))), BitmapInterpolationMode.HighQuality);
                full.Dispose();
                return small;
            }
            catch { return null; }
        });

        public static void OpenExternal(string path) => Native.Open(path);

        public static void OpenPreview(string path, Window owner)
        {
            if (!Exists(path)) return;
            var ext = Path.GetExtension(path);
            if (!IsImage(path) && !TextExt.Contains(ext)) { OpenExternal(path); return; }

            var info = new FileInfo(path);
            var w = new Window { Title = info.Name + " — 미리 보기", Width = 760, Height = 600, MinWidth = 360, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.Classes.Add("app");
            Icons.Apply(w);

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 10), Spacing = 8 };
            head.Children.Add(new TextBlock { Text = info.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460, VerticalAlignment = VerticalAlignment.Center });
            var sizeText = new TextBlock { Text = FileItem.FormatSize(info.Length), Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(sizeText);
            grid.Children.Add(head);

            Control body;
            if (IsImage(path))
            {
                var img = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
                _ = Task.Run(async () =>
                {
                    var bmp = await LoadAsync(path, 2400);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        img.Source = bmp;
                        if (bmp != null) sizeText.Text = $"{bmp.PixelSize.Width}×{bmp.PixelSize.Height} · {FileItem.FormatSize(info.Length)}";
                    });
                });
                body = new Border { Child = img, Margin = new Thickness(12, 0) };
            }
            else
            {
                string text;
                try
                {
                    using var fs = File.OpenRead(path);
                    var buf = new byte[Math.Min(fs.Length, 512 * 1024)];
                    int n = fs.Read(buf, 0, buf.Length);
                    text = DecodeText(buf, n);
                    if (fs.Length > buf.Length) text += "\n\n… (처음 512KB 만 표시)";
                }
                catch (Exception ex) { text = "파일을 읽지 못했습니다: " + ex.Message; }
                var tb = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 13, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
                tb.Classes.Add("field");
                ScrollViewer.SetHorizontalScrollBarVisibility(tb, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                body = new Border { Child = tb, Margin = new Thickness(12, 0) };
            }
            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 12), Spacing = 8 };
            var folder = Dialog.Btn(Native.IsMac ? "Finder 에서 보기" : "폴더에서 보기", "outline");
            folder.Click += (s, e) => Native.Reveal(path);
            var open = Dialog.Btn("기본 프로그램으로 열기", "primary");
            open.Click += (s, e) => OpenExternal(path);
            bar.Children.Add(folder); bar.Children.Add(open);
            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);

            var bg = new Border { Child = grid };
            bg.Bind(Border.BackgroundProperty, bg.GetResourceObservable("Surface"));
            w.Content = bg;
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            if (owner != null && owner.IsVisible) w.Show(owner); else w.Show();
            w.Activate();
        }

        static string DecodeText(byte[] buf, int n)
        {
            try { return new UTF8Encoding(false, true).GetString(buf, 0, n).TrimStart('﻿'); }
            catch
            {
                try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(949).GetString(buf, 0, n); }
                catch { return Encoding.Latin1.GetString(buf, 0, n); }
            }
        }
    }
}
