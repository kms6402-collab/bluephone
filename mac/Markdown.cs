using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BluePhone
{
    /// <summary>
    /// 마크다운 표시 (대화 말풍선 · 쪽지 창) — Windows 판과 같은 문법.
    /// # 제목, **굵게**, *기울임*, ~~취소선~~, `코드`, ```코드 블록```, - 목록, 1. 번호 목록, > 인용, [링크](주소), 주소 자동 링크, --- 구분선
    /// </summary>
    public static class Markdown
    {
        static readonly Regex Markup = new Regex(
            @"(^|\n)\s*(#{1,3}\s|```|>\s|[-*+]\s|\d+[.)]\s|---\s*($|\n))|\*\*[^*\n]+\*\*|(^|[^\w*])\*[^\s*][^*\n]*\*(?!\w)|~~[^~\n]+~~|`[^`\n]+`|\[[^\]\n]+\]\([^)\s]+\)",
            RegexOptions.Compiled);

        public static bool HasMarkup(string text) => !string.IsNullOrEmpty(text) && Markup.IsMatch(text.Replace("\r\n", "\n"));

        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasMarkup(text)) return text;
            var s = Regex.Replace(text, @"```[^\n]*\n?", "");
            s = Regex.Replace(s, @"(?m)^\s*(#{1,3}|>|[-*+])\s+", "");
            s = Regex.Replace(s, @"\*\*([^*]+)\*\*|~~([^~]+)~~|`([^`]+)`", "$1$2$3");
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return s;
        }

        static readonly IBrush CodeBg = new SolidColorBrush(Color.FromArgb(0x24, 0x80, 0x80, 0x80));
        static readonly IBrush QuoteBar = new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80));
        public static readonly FontFamily Mono = new FontFamily("Menlo, SF Mono, Consolas, D2Coding, monospace");

        /// <summary>마크다운 → 화면 요소</summary>
        public static Control Render(string text)
        {
            var root = new StackPanel();
            if (string.IsNullOrEmpty(text)) return root;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var para = new List<string>();

            void FlushPara()
            {
                if (para.Count == 0) return;
                root.Children.Add(Paragraph(string.Join("\n", para)));
                para.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var t = line.TrimStart();
                if (t.StartsWith("```"))
                {
                    FlushPara();
                    var lang = t.Substring(3).Trim();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Add(lines[i++]);
                    root.Children.Add(CodeBlock(string.Join("\n", code), lang));
                    if (i < lines.Length)
                    {
                        var rest = lines[i].TrimStart().TrimStart('`').Trim();
                        if (rest.Length > 0) para.Add(rest);
                    }
                    continue;
                }
                var h = Regex.Match(t, @"^(#{1,3})\s+(.*)$");
                if (h.Success)
                {
                    FlushPara();
                    var tb = Paragraph(h.Groups[2].Value);
                    tb.FontWeight = FontWeight.SemiBold;
                    tb.FontSize = h.Groups[1].Length == 1 ? 18 : h.Groups[1].Length == 2 ? 16 : 14.5;
                    tb.Margin = new Thickness(0, root.Children.Count > 0 ? 6 : 0, 0, 2);
                    root.Children.Add(tb);
                    continue;
                }
                if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    FlushPara();
                    root.Children.Add(new Border { Height = 1, Background = QuoteBar, Margin = new Thickness(0, 6) });
                    continue;
                }
                if (t.StartsWith(">"))
                {
                    FlushPara();
                    var q = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">")) q.Add(lines[i++].TrimStart().Substring(1).TrimStart());
                    i--;
                    var inner = Paragraph(string.Join("\n", q));
                    inner.Opacity = 0.85;
                    root.Children.Add(new Border { BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 1, 0, 1), Margin = new Thickness(0, 2), Child = inner });
                    continue;
                }
                var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
                if (li.Success)
                {
                    FlushPara();
                    int indent = li.Groups[1].Value.Replace("\t", "  ").Length / 2;
                    var marker = li.Groups[2].Value;
                    var g = new Grid { Margin = new Thickness(indent * 14, 1, 0, 1), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    var bullet = new TextBlock { Text = char.IsDigit(marker[0]) ? marker.TrimEnd(')').TrimEnd('.') + "." : "•", Margin = new Thickness(0, 0, 6, 0), MinWidth = 10 };
                    var body = Paragraph(li.Groups[3].Value);
                    Grid.SetColumn(body, 1);
                    g.Children.Add(bullet); g.Children.Add(body);
                    root.Children.Add(g);
                    continue;
                }
                if (t.Length == 0) { FlushPara(); continue; }
                para.Add(line);
            }
            FlushPara();
            for (int k = 1; k < root.Children.Count; k++)
                if (root.Children[k] is TextBlock tb && root.Children[k - 1] is TextBlock && tb.Margin.Top == 0) tb.Margin = new Thickness(0, 6, 0, 0);
            return root;
        }

        static SelectableTextBlock Paragraph(string text)
        {
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            tb.Inlines = new InlineCollection();
            AddInlines(tb.Inlines, text);
            return tb;
        }

        static readonly Regex InlineRx = new Regex(
            @"(?<code>`[^`\n]+`)|(?<bold>\*\*(?=\S)[^*\n]+?(?<=\S)\*\*)|(?<strike>~~(?=\S)[^~\n]+?(?<=\S)~~)|(?<!\w)(?<ital>\*(?=[^\s*])[^*\n]+?(?<=[^\s*])\*)(?!\w)|(?<link>\[[^\]\n]+\]\([^)\s]+\))|(?<url>(?:https?://|www\.)[^\s<>()]+[^\s<>().,;:!?'""])",
            RegexOptions.Compiled);

        static void AddInlines(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                if (m.Groups["code"].Success)
                    target.Add(new Run(m.Value.Substring(1, m.Value.Length - 2)) { FontFamily = Mono, Background = CodeBg });
                else if (m.Groups["bold"].Success)
                {
                    var s = new Span { FontWeight = FontWeight.Bold };
                    AddInlines(s.Inlines, m.Value.Substring(2, m.Value.Length - 4));
                    target.Add(s);
                }
                else if (m.Groups["strike"].Success)
                {
                    var s = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(s.Inlines, m.Value.Substring(2, m.Value.Length - 4));
                    target.Add(s);
                }
                else if (m.Groups["ital"].Success)
                {
                    var s = new Span { FontStyle = FontStyle.Italic };
                    AddInlines(s.Inlines, m.Value.Substring(1, m.Value.Length - 2));
                    target.Add(s);
                }
                else if (m.Groups["link"].Success)
                {
                    var lm = Regex.Match(m.Value, @"^\[([^\]]+)\]\(([^)]+)\)$");
                    target.Add(Link(lm.Groups[1].Value, lm.Groups[2].Value));
                }
                else if (m.Groups["url"].Success) target.Add(Link(m.Value, m.Value));
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }

        static readonly Regex UrlRx = new Regex(@"(?:https?://|www\.)[^\s<>()""']+[^\s<>().,;:!?'""\]]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>일반 글자 안의 주소를 눌러 열 수 있는 링크로</summary>
        public static void AddLinked(InlineCollection target, string text)
        {
            text ??= "";
            int pos = 0;
            foreach (Match m in UrlRx.Matches(text))
            {
                if (m.Index > pos) AddMentions(target, text.Substring(pos, m.Index - pos));
                target.Add(Link(m.Value, m.Value));
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) AddMentions(target, text.Substring(pos));
        }

        /// <summary>'@이름'(그룹 대화에서 부른 사람)은 굵게</summary>
        static void AddMentions(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in Mention.Rx.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                target.Add(new Run(m.Value) { FontWeight = FontWeight.Bold });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }

        static DateTime _lastOpen;
        static string _lastUrl;

        public static void OpenUrl(string url)
        {
            if (url == _lastUrl && (DateTime.Now - _lastOpen).TotalMilliseconds < 800) return;
            _lastUrl = url; _lastOpen = DateTime.Now;
            var log = Environment.GetEnvironmentVariable("BLUEPHONE_OPENLOG");
            if (!string.IsNullOrEmpty(log)) { try { System.IO.File.AppendAllText(log, url + Environment.NewLine); } catch { } return; }
            Native.Open(url);
        }

        /// <summary>링크: 밑줄 + 'LinkFg' 색 (내가 보낸 말풍선에서는 글자색과 같게)</summary>
        static Inline Link(string text, string url)
        {
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            if (!Regex.IsMatch(url, @"^(https?|mailto):", RegexOptions.IgnoreCase)) return new Run(text);
            var tb = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand), TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(tb, url + " — 누르면 브라우저에서 엽니다");
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("LinkFg"));
            tb.PointerReleased += (s, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; OpenUrl(url); } };
            return new InlineUIContainer(tb) { BaselineAlignment = BaselineAlignment.TextBottom };
        }

        static Control CodeBlock(string code, string lang)
        {
            var box = new TextBox
            {
                Text = code, IsReadOnly = true, FontFamily = Mono, FontSize = 12.5, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, MaxHeight = 360,
            };
            box.Classes.Add("code");
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            var copy = new Button { Content = "복사", FontSize = 11, Padding = new Thickness(8, 1), Background = CodeBg, Focusable = false };
            copy.Classes.Add("ghost");
            copy.Click += (s, e) => { ClipKit.SetText(copy, code); copy.Content = "복사됨"; };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(copy, Dock.Right);
            head.Children.Add(copy);
            head.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(lang) ? "코드" : lang, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(box);
            return new Border { Background = CodeBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 4), Child = stack, MinWidth = 180 };
        }

        // ───────────── 입력 도우미 ─────────────

        static void Replace(TextBox box, int start, int len, string text, int selStart, int selLen)
        {
            var t = box.Text ?? "";
            box.Text = t.Substring(0, start) + text + t.Substring(start + len);
            box.SelectionStart = selStart;
            box.SelectionEnd = selStart + selLen;
            box.CaretIndex = selStart + selLen;
            box.Focus();
        }

        static (int start, int len) Sel(TextBox box)
        {
            int a = Math.Min(box.SelectionStart, box.SelectionEnd), b = Math.Max(box.SelectionStart, box.SelectionEnd);
            if (a == b) a = b = Math.Min(box.CaretIndex, (box.Text ?? "").Length);
            return (a, b - a);
        }

        public static void Wrap(TextBox box, string left, string right = null, string placeholder = "")
        {
            right ??= left;
            var (start, len) = Sel(box);
            var sel = len > 0 ? (box.Text ?? "").Substring(start, len) : placeholder;
            Replace(box, start, len, left + sel + right, start + left.Length, sel.Length);
        }

        public static void Prefix(TextBox box, string prefix)
        {
            var text = box.Text ?? "";
            var (selStart, len) = Sel(box);
            int start = selStart > 0 ? text.LastIndexOf('\n', selStart - 1) + 1 : 0;
            int end = selStart + len;
            var block = text.Substring(start, end - start);
            int n = 1;
            var nb = string.Join("\n", block.Replace("\r\n", "\n").Split('\n').Select(l => (prefix == "1." ? (n++) + ". " : prefix) + l));
            Replace(box, start, end - start, nb, start + nb.Length, 0);
        }

        public static void InsertAtCaret(TextBox box, string s)
        {
            var (start, len) = Sel(box);
            Replace(box, start, len, s, start + s.Length, 0);
        }

        public static bool InOpenFence(TextBox box)
        {
            var t = box.Text ?? "";
            var before = t.Substring(0, Math.Min(box.CaretIndex, t.Length));
            return Regex.Matches(before, @"(^|\n)\s*```").Count % 2 == 1;
        }

        /// <summary>서식 단축키 (⌘B · ⌘I · ⌘E · ⌘⇧X · ⌘⇧C). 처리했으면 true</summary>
        public static bool HandleKey(TextBox box, KeyEventArgs e, Window owner)
        {
            var m = e.KeyModifiers;
            if (Keys.Primary(m) && e.Key == Key.B) { Wrap(box, "**"); return true; }
            if (Keys.Primary(m) && e.Key == Key.I) { Wrap(box, "*"); return true; }
            if (Keys.PrimaryShift(m) && e.Key == Key.X) { Wrap(box, "~~"); return true; }
            if (Keys.Primary(m) && e.Key == Key.E) { Wrap(box, "`"); return true; }
            if (Keys.PrimaryShift(m) && e.Key == Key.C) { _ = InsertCode(box, owner); return true; }
            if (e.Key == Key.Enter && m == KeyModifiers.None && InOpenFence(box)) { InsertAtCaret(box, "\n"); return true; }
            return false;
        }

        public static void ShowFormatMenu(Control anchor, TextBox box, Window owner)
        {
            string M(string k) => (Native.IsMac ? "⌘" : "Ctrl+") + k;
            string MS(string k) => (Native.IsMac ? "⌘⇧" : "Ctrl+Shift+") + k;
            Control Add(string header, string key, Action a) { var mi = new MenuItem { Header = string.IsNullOrEmpty(key) ? header : header + "    " + key }; mi.Click += (s, e) => a(); return mi; }
            Menus.Show(anchor, new Control[]
            {
                Add("코드 블록 넣기…", MS("C"), () => _ = InsertCode(box, owner)),
                Add("인라인 코드  `코드`", M("E"), () => Wrap(box, "`")),
                Menus.Sep(),
                Add("굵게  **글자**", M("B"), () => Wrap(box, "**")),
                Add("기울임  *글자*", M("I"), () => Wrap(box, "*")),
                Add("취소선  ~~글자~~", MS("X"), () => Wrap(box, "~~")),
                Add("제목  # 제목", null, () => Prefix(box, "## ")),
                Menus.Sep(),
                Add("글머리 목록  - 항목", null, () => Prefix(box, "- ")),
                Add("번호 목록  1. 항목", null, () => Prefix(box, "1.")),
                Add("인용  > 글", null, () => Prefix(box, "> ")),
                Add("링크  [글자](주소)", null, () => Wrap(box, "[", "](https://)", "링크 글자")),
                Add("구분선  ---", null, () => InsertAtCaret(box, "\n---\n")),
                Menus.Sep(),
                Menus.Item("받는 사람 화면에 서식이 적용되어 보입니다", enabled: false),
            }, PlacementMode.TopEdgeAlignedLeft);
        }

        static readonly string[] Langs = { "", "C#", "Java", "Python", "JavaScript", "TypeScript", "SQL", "HTML", "CSS", "JSON", "XML", "YAML", "C", "C++", "Go", "Kotlin", "Swift", "PHP", "Shell", "PowerShell", "로그", "텍스트" };

        /// <summary>코드 넣기 창 — 코드를 붙여 넣고 언어를 고르면 ``` 블록으로 넣음</summary>
        public static async Task InsertCode(TextBox box, Window owner)
        {
            var w = new Window { Title = "코드 넣기", Width = 560, Height = 440, MinWidth = 380, MinHeight = 280, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.Classes.Add("app");
            Icons.Apply(w);
            var grid = new Grid { Margin = new Thickness(18, 14, 18, 16), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
            grid.Children.Add(new TextBlock { Text = "코드 넣기", FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) });
            var langRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Spacing = 10 };
            langRow.Children.Add(new TextBlock { Text = "언어", VerticalAlignment = VerticalAlignment.Center });
            var lang = new AutoCompleteBox { Width = 160, ItemsSource = Langs, FilterMode = AutoCompleteFilterMode.StartsWith, MinimumPrefixLength = 0 };
            langRow.Children.Add(lang);
            Grid.SetRow(langRow, 1);
            grid.Children.Add(langRow);
            var code = new TextBox { AcceptsReturn = true, AcceptsTab = true, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Top };
            code.Classes.Add("field");
            ScrollViewer.SetHorizontalScrollBarVisibility(code, ScrollBarVisibility.Auto);
            var (ss, sl) = Sel(box);
            if (sl > 0) code.Text = (box.Text ?? "").Substring(ss, sl);
            Grid.SetRow(code, 2);
            grid.Children.Add(code);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            buttons.Children.Add(new TextBlock { Text = (Native.IsMac ? "⌘" : "Ctrl+") + "Enter 넣기", Opacity = 0.6, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var cancel = Dialog.Btn("취소", "outline"); cancel.IsCancel = true; cancel.Click += (s, e) => w.Close();
            var ok = Dialog.Btn("넣기", "primary");
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);
            var bg = new Border { Child = grid };
            bg.Bind(Border.BackgroundProperty, bg.GetResourceObservable("Surface"));
            w.Content = bg;

            void Commit()
            {
                var body = (code.Text ?? "").Replace("\r\n", "\n").TrimEnd('\n');
                if (body.Trim().Length == 0) { w.Close(); return; }
                var fence = body.Contains("```") ? "````" : "```";
                var block = fence + (lang.Text ?? "").Trim() + "\n" + body + "\n" + fence;
                var text = box.Text ?? "";
                var before = text.Substring(0, ss);
                var after = text.Substring(ss + sl);
                if (before.Length > 0 && !before.EndsWith("\n")) block = "\n" + block;
                if (after.Length > 0 && !after.StartsWith("\n")) block += "\n";
                Replace(box, ss, sl, block, ss + block.Length, 0);
                w.Close();
            }
            ok.Click += (s, e) => Commit();
            code.AddHandler(InputElement.KeyDownEvent, (s, e) => { if (e.Key == Key.Enter && Keys.Primary(e.KeyModifiers)) { e.Handled = true; Commit(); } }, RoutingStrategies.Tunnel);
            w.Opened += (s, e) => code.Focus();
            await Dialog.ShowWindow(w, owner);
            box.Focus();
        }
    }

    /// <summary>
    /// 메시지 본문 표시: 마크다운이 있으면 서식으로, 없으면 주소만 링크로 바꾼 선택 가능한 글.
    /// 이모지만 1~3개면 크게 보여 줌.
    /// </summary>
    public sealed class RichText : ContentControl
    {
        public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<RichText, string>(nameof(Text));
        public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TextProperty) Build();
        }

        void Build()
        {
            var text = Text ?? "";
            IsVisible = text.Length > 0;
            if (text.Length == 0) { Content = null; return; }
            if (Markdown.HasMarkup(text)) { Content = Markdown.Render(text); return; }
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            if (Emoji.IsJumbo(text)) { tb.Text = text.Trim(); tb.FontSize = 40; tb.LineHeight = 48; }
            else
            {
                tb.Inlines = new InlineCollection();
                Markdown.AddLinked(tb.Inlines, text);
            }
            Content = tb;
        }

        /// <summary>이 컨트롤 안에서 골라 둔 글자 (없으면 null)</summary>
        public static string SelectionIn(Control root)
        {
            if (root == null) return null;
            foreach (var tb in root.GetLogicalDescendantsOfType<SelectableTextBlock>())
            {
                var s = tb.SelectedText;
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }
    }

    static class LogicalExt
    {
        public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control c) where T : class
        {
            foreach (var child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(c))
                if (child is T t) yield return t;
        }
    }
}
