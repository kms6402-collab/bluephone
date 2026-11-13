using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BluePhone
{
    /// <summary>
    /// 컬러 이모지. WPF(.NET Framework)는 컬러 글꼴을 그리지 못하므로 Twemoji(CC-BY 4.0) 이미지를
    /// 실행 파일에 넣어 두고, 말풍선 안의 이모지 문자를 이미지로 바꿔 그린다.
    /// </summary>
    public static class Emoji
    {
        public sealed class Item { public string Text; public string File; }

        static List<Item> _all;
        static Dictionary<string, Item> _map;
        static int _maxLen;
        static readonly Dictionary<string, BitmapImage> _cache = new Dictionary<string, BitmapImage>();

        public static IReadOnlyList<Item> All { get { Load(); return _all; } }

        /// <summary>한국식 텍스트 이모티콘</summary>
        public static readonly string[] Emoticons =
        {
            "^^", "^_^", "^^;", "ㅎㅎ", "ㅋㅋㅋ", "ㅠㅠ", "ㅜㅜ", "-_-;", "(_ _)", "(__)",
            "(^▽^)", "(＾▽＾)/", "(｡•̀ᴗ-)✧", "٩(ˊᗜˋ*)و", "(つ﹏⊂)", "(ง •̀_•́)ง", "(｀・ω・´)", "(✿◠‿◠)",
            "¯\\_(ツ)_/¯", "(╯°□°)╯︵ ┻━┻", "♡", "♥", "☆", "★", "♪", "→", "✔",
        };

        static void Load()
        {
            if (_all != null) return;
            _all = new List<Item>();
            _map = new Dictionary<string, Item>();
            using (var s = typeof(Emoji).Assembly.GetManifestResourceStream("emoji/list.txt"))
            {
                if (s == null) return;
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var parts = line.Split('\t');
                        if (parts.Length != 2) continue;
                        var it = new Item { Text = parts[0], File = parts[1] };
                        _all.Add(it);
                        _map[it.Text] = it;
                        var bare = it.Text.Replace("️", "");
                        if (!_map.ContainsKey(bare)) _map[bare] = it;
                    }
                }
            }
            _maxLen = _map.Keys.Select(k => k.Length).DefaultIfEmpty(1).Max();
        }

        public static ImageSource Image(Item it)
        {
            if (_cache.TryGetValue(it.File, out var b)) return b;
            var s = typeof(Emoji).Assembly.GetManifestResourceStream("emoji/" + it.File + ".png");
            if (s == null) return null;
            b = new BitmapImage();
            b.BeginInit();
            b.StreamSource = s;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            _cache[it.File] = b;
            return b;
        }

        /// <summary>문자열을 일반 텍스트와 이모지 조각으로 나눔</summary>
        public static List<(string text, Item emoji)> Split(string text)
        {
            Load();
            var parts = new List<(string, Item)>();
            if (string.IsNullOrEmpty(text)) return parts;
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                Item hit = null; int len = 0;
                for (int l = Math.Min(_maxLen, text.Length - i); l >= 1; l--)
                    if (_map.TryGetValue(text.Substring(i, l), out hit)) { len = l; break; }
                if (hit != null)
                {
                    if (sb.Length > 0) { parts.Add((sb.ToString(), null)); sb.Clear(); }
                    parts.Add((null, hit));
                    i += len;
                    if (i < text.Length && text[i] == '️') i++;
                }
                else sb.Append(text[i++]);
            }
            if (sb.Length > 0) parts.Add((sb.ToString(), null));
            return parts;
        }

        // ───────────── TextBlock 첨부 속성: local:Emoji.Text="{Binding Text}" ─────────────

        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Emoji), new PropertyMetadata(null, (d, e) => Render(d as TextBlock)));
        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);

        static void Render(TextBlock tb)
        {
            if (tb == null) return;
            tb.Inlines.Clear();
            var parts = Split(GetText(tb));
            // 이모지만 1~3개 보내면 크게 (텍스트 없이)
            int emojiCount = parts.Count(p => p.emoji != null);
            bool jumbo = emojiCount > 0 && emojiCount <= 3 && parts.All(p => p.emoji != null || string.IsNullOrWhiteSpace(p.text));
            foreach (var (text, emoji) in parts)
            {
                if (emoji == null) { tb.Inlines.Add(new Run(text)); continue; }
                var img = new Image { Source = Image(emoji), ToolTip = null, SnapsToDevicePixels = true };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                if (jumbo) { img.Width = img.Height = 40; img.Margin = new Thickness(1, 2, 1, 2); }
                else
                {
                    // 글자 크기에 맞춤 (테마에서 글자 크기를 바꾸면 함께 바뀜)
                    img.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("FontSize") { Source = tb, Converter = Scale.Instance });
                    img.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("FontSize") { Source = tb, Converter = Scale.Instance });
                    img.Margin = new Thickness(1, 0, 1, 0);
                }
                tb.Inlines.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
            }
        }

        sealed class Scale : System.Windows.Data.IValueConverter
        {
            public static readonly Scale Instance = new Scale();
            public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is double d ? Math.Round(d * 1.3) : 18.0;
            public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
        }

        // ───────────── 선택 창 ─────────────

        /// <summary>이모지·이모티콘 선택 창. 고른 것을 입력칸 커서 위치에 넣는다 (여러 개 연달아 고를 수 있음)</summary>
        public static void ShowPicker(FrameworkElement anchor, TextBox target)
        {
            Load();
            var surface = (Brush)Application.Current.FindResource("Surface");
            var line = (Brush)Application.Current.FindResource("Line");
            var popup = new Popup
            {
                PlacementTarget = anchor, Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade, VerticalOffset = -6,
            };
            var root = new Border
            {
                Background = surface, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8), Width = 316, Margin = new Thickness(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.16 },
            };
            // 팝업은 창의 글꼴을 물려받지 않으므로 직접 지정 (지정하지 않으면 한글이 네모로 보임)
            TextElement.SetFontFamily(root, (FontFamily)Application.Current.FindResource("UI"));
            TextElement.SetForeground(root, (Brush)Application.Current.FindResource("Ink"));
            var stack = new StackPanel();
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
            var body = new ContentControl { Height = 214 };
            ToggleButton Tab(string text, bool on)
            {
                var t = new ToggleButton { Style = (Style)Application.Current.FindResource("FilterChip"), Content = new TextBlock { Text = text, FontSize = 12 }, IsChecked = on, Margin = new Thickness(0, 0, 6, 0) };
                tabs.Children.Add(t);
                return t;
            }
            var t1 = Tab("이모지", true);
            var t2 = Tab("이모티콘", false);

            void Insert(string s)
            {
                int at = target.CaretIndex;
                if (target.SelectionLength > 0) { at = target.SelectionStart; target.Text = target.Text.Remove(at, target.SelectionLength); }
                target.Text = target.Text.Insert(at, s);
                target.CaretIndex = at + s.Length;
            }

            ScrollViewer Grid(IEnumerable<UIElement> cells)
            {
                var wrap = new WrapPanel();
                foreach (var c in cells) wrap.Children.Add(c);
                return new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            }
            Button Cell(object content, string insert, string tip, double w, double h)
            {
                var b = new Button { Style = (Style)Application.Current.FindResource("Ghost"), Content = content, Width = w, Height = h, Padding = new Thickness(0), ToolTip = tip, Focusable = false };
                b.Click += (s, e) => Insert(insert);
                return b;
            }
            var emojiGrid = Grid(_all.Select(it =>
            {
                var img = new Image { Source = Image(it), Width = 24, Height = 24 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return (UIElement)Cell(img, it.Text, null, 37, 37);
            }));
            var emoticonGrid = Grid(Emoticons.Select(t =>
                (UIElement)Cell(new TextBlock { Text = t, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis }, t, t, t.Length > 6 ? 147 : 72, 32)));
            body.Content = emojiGrid;
            t1.Click += (s, e) => { t1.IsChecked = true; t2.IsChecked = false; body.Content = emojiGrid; };
            t2.Click += (s, e) => { t2.IsChecked = true; t1.IsChecked = false; body.Content = emoticonGrid; };

            stack.Children.Add(tabs);
            stack.Children.Add(body);
            stack.Children.Add(new TextBlock
            {
                Text = "여러 개를 연달아 고를 수 있어요 · Twemoji (CC-BY 4.0)", FontSize = 10.5, Margin = new Thickness(4, 6, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("Faint"),
            });
            root.Child = stack;
            popup.Child = root;
            popup.Closed += (s, e) => target.Focus();
            popup.IsOpen = true;
        }
    }
}
