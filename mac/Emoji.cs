using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace BluePhone
{
    /// <summary>이모지 · 이모티콘 선택 창 (macOS 는 컬러 이모지 글꼴로 바로 그림)</summary>
    public static class Emoji
    {
        static List<string> _all;
        static HashSet<string> _set;

        public static readonly string[] Emoticons =
        {
            "^^", "^_^", "^^;", "ㅎㅎ", "ㅋㅋㅋ", "ㅠㅠ", "ㅜㅜ", "-_-;", "(_ _)", "(__)",
            "(^▽^)", "(＾▽＾)/", "(｡•̀ᴗ-)✧", "٩(ˊᗜˋ*)و", "(つ﹏⊂)", "(ง •̀_•́)ง", "(｀・ω・´)", "(✿◠‿◠)",
            "¯\\_(ツ)_/¯", "(╯°□°)╯︵ ┻━┻", "♡", "♥", "☆", "★", "♪", "→", "✔",
        };

        static void Load()
        {
            if (_all != null) return;
            _all = new List<string>();
            _set = new HashSet<string>();
            try
            {
                using var s = typeof(Emoji).Assembly.GetManifestResourceStream("emoji/list.txt");
                if (s == null) return;
                using var r = new StreamReader(s, Encoding.UTF8);
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 2) continue;
                    _all.Add(parts[0]);
                    _set.Add(parts[0]);
                    _set.Add(parts[0].Replace("️", ""));
                }
            }
            catch { }
        }

        /// <summary>이모지만 1~3개 (공백 제외)인지 — 크게 보여 줌</summary>
        public static bool IsJumbo(string text)
        {
            Load();
            var t = (text ?? "").Replace(" ", "").Replace("\n", "").Replace("\r", "");
            if (t.Length == 0 || t.Length > 24) return false;
            int count = 0, i = 0;
            while (i < t.Length)
            {
                int hit = 0;
                for (int l = Math.Min(11, t.Length - i); l >= 1; l--)
                    if (_set.Contains(t.Substring(i, l))) { hit = l; break; }
                if (hit == 0) return false;
                i += hit;
                if (i < t.Length && t[i] == '️') i++;
                if (++count > 3) return false;
            }
            return count > 0;
        }

        public static void ShowPicker(Control anchor, TextBox target)
        {
            Load();
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6), Spacing = 6 };
            var body = new ContentControl { Height = 214 };
            ToggleButton Tab(string text, bool on)
            {
                var t = new ToggleButton { Content = new TextBlock { Text = text, FontSize = 12 }, IsChecked = on };
                t.Classes.Add("filter");
                tabs.Children.Add(t);
                return t;
            }
            var t1 = Tab("이모지", true);
            var t2 = Tab("이모티콘", false);

            ScrollViewer Grid(IEnumerable<Control> cells)
            {
                var wrap = new WrapPanel();
                foreach (var c in cells) wrap.Children.Add(c);
                return new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            }
            Button Cell(string text, double w, double h, double size)
            {
                var b = new Button { Content = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center }, Width = w, Height = h, Padding = new Thickness(0), Focusable = false };
                b.Classes.Add("ghost");
                if (text.Length > 3) ToolTip.SetTip(b, text);
                b.Click += (s, e) => Markdown.InsertAtCaret(target, text);
                return b;
            }
            var emojiGrid = Grid(_all.Select(e => (Control)Cell(e, 37, 37, 20)));
            var emoticonGrid = Grid(Emoticons.Select(t => (Control)Cell(t, t.Length > 6 ? 147 : 72, 32, 12.5)));
            body.Content = emojiGrid;
            t1.Click += (s, e) => { t1.IsChecked = true; t2.IsChecked = false; body.Content = emojiGrid; };
            t2.Click += (s, e) => { t2.IsChecked = true; t1.IsChecked = false; body.Content = emoticonGrid; };

            var stack = new StackPanel { Width = 300 };
            stack.Children.Add(tabs);
            stack.Children.Add(body);
            var note = new TextBlock { Text = "여러 개를 연달아 고를 수 있어요", FontSize = 10.5, Margin = new Thickness(4, 6, 0, 0) };
            note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("Faint"));
            stack.Children.Add(note);
            var f = new Flyout { Content = stack, Placement = PlacementMode.TopEdgeAlignedLeft };
            f.Closed += (s, e) => target.Focus();
            f.ShowAt(anchor);
        }
    }
}
