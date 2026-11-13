using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BluePhone
{
    /// <summary>
    /// 마크다운 표시 (대화 말풍선 · 쪽지 창).
    /// 지원: # 제목, **굵게**, *기울임*, ~~취소선~~, `코드`, ```코드 블록```, - 목록, 1. 번호 목록, > 인용, [링크](주소), 주소 자동 링크, --- 구분선.
    /// 상대가 예전 버전이면 기호가 그대로 보일 뿐 내용은 전달된다.
    /// </summary>
    public static class Markdown
    {
        static readonly Regex Markup = new Regex(
            @"(^|\n)\s*(#{1,3}\s|```|>\s|[-*+]\s|\d+[.)]\s|---\s*($|\n))|\*\*[^*\n]+\*\*|(^|[^\w*])\*[^\s*][^*\n]*\*(?!\w)|~~[^~\n]+~~|`[^`\n]+`|\[[^\]\n]+\]\([^)\s]+\)",
            RegexOptions.Compiled);

        /// <summary>마크다운 서식이 들어 있는지 (없으면 기존처럼 평문으로 표시)</summary>
        public static bool HasMarkup(string text) => !string.IsNullOrEmpty(text) && Markup.IsMatch(text.Replace("\r\n", "\n"));

        /// <summary>목록 미리보기용: 서식 기호를 걷어 낸 한 줄</summary>
        public static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text) || !HasMarkup(text)) return text;
            var s = Regex.Replace(text, @"```[^\n]*\n?", "");
            s = Regex.Replace(s, @"(?m)^\s*(#{1,3}|>|[-*+])\s+", "");
            s = Regex.Replace(s, @"\*\*([^*]+)\*\*|~~([^~]+)~~|`([^`]+)`", "$1$2$3");
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return s;
        }

        // ───────────── 첨부 속성: <ContentControl local:Markdown.Text="{Binding Text}"/> ─────────────

        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Markdown), new PropertyMetadata(null, (d, e) =>
            {
                if (d is ContentControl cc) cc.Content = Render((string)e.NewValue);
            }));
        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string v) => d.SetValue(TextProperty, v);

        static readonly Brush CodeBg = Frozen(Color.FromArgb(0x24, 0x80, 0x80, 0x80));
        static readonly Brush QuoteBar = Frozen(Color.FromArgb(0x70, 0x80, 0x80, 0x80));
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static readonly FontFamily Mono = new FontFamily("Consolas, D2Coding, Malgun Gothic");

        /// <summary>마크다운 → 화면 요소</summary>
        public static UIElement Render(string text)
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

                // 코드 블록
                if (t.StartsWith("```"))
                {
                    FlushPara();
                    var lang = t.Substring(3).Trim();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Add(lines[i++]);
                    root.Children.Add(CodeBlock(string.Join("\n", code), lang));
                    // 닫는 ``` 뒤에 글이 이어 붙어 있으면 버리지 않고 다음 문단으로
                    if (i < lines.Length)
                    {
                        var rest = lines[i].TrimStart().TrimStart('`').Trim();
                        if (rest.Length > 0) para.Add(rest);
                    }
                    continue;
                }
                // 제목
                var h = Regex.Match(t, @"^(#{1,3})\s+(.*)$");
                if (h.Success)
                {
                    FlushPara();
                    var tb = Paragraph(h.Groups[2].Value);
                    tb.FontWeight = FontWeights.SemiBold;
                    tb.FontSize = h.Groups[1].Length == 1 ? 18 : h.Groups[1].Length == 2 ? 16 : 14.5;
                    tb.Margin = new Thickness(0, root.Children.Count > 0 ? 6 : 0, 0, 2);
                    root.Children.Add(tb);
                    continue;
                }
                // 구분선
                if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    FlushPara();
                    root.Children.Add(new Border { Height = 1, Background = QuoteBar, Margin = new Thickness(0, 6, 0, 6) });
                    continue;
                }
                // 인용
                if (t.StartsWith(">"))
                {
                    FlushPara();
                    var q = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">")) q.Add(lines[i++].TrimStart().Substring(1).TrimStart());
                    i--;
                    var inner = Paragraph(string.Join("\n", q));
                    inner.Opacity = 0.85;
                    root.Children.Add(new Border
                    {
                        BorderBrush = QuoteBar, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 1, 0, 1),
                        Margin = new Thickness(0, 2, 0, 2), Child = inner,
                    });
                    continue;
                }
                // 목록
                var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
                if (li.Success)
                {
                    FlushPara();
                    int indent = li.Groups[1].Value.Replace("\t", "  ").Length / 2;
                    var marker = li.Groups[2].Value;
                    var g = new Grid { Margin = new Thickness(indent * 14, 1, 0, 1) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
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
            // 문단 사이 간격
            for (int k = 1; k < root.Children.Count; k++)
                if (root.Children[k] is TextBlock tb && root.Children[k - 1] is TextBlock && tb.Margin.Top == 0) tb.Margin = new Thickness(0, 6, 0, 0);
            return root;
        }

        static TextBlock Paragraph(string text)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AddInlines(tb.Inlines, text);
            return tb;
        }

        static readonly Regex InlineRx = new Regex(
            @"(?<code>`[^`\n]+`)|(?<bold>\*\*(?=\S)[^*\n]+?(?<=\S)\*\*)|(?<strike>~~(?=\S)[^~\n]+?(?<=\S)~~)|(?<!\w)(?<ital>\*(?=[^\s*])[^*\n]+?(?<=[^\s*])\*)(?!\w)|(?<link>\[[^\]\n]+\]\([^)\s]+\))|(?<url>https?://[^\s<>()]+[^\s<>().,;:!?'""])",
            RegexOptions.Compiled);

        static void AddInlines(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos) AddText(target, text.Substring(pos, m.Index - pos));
                if (m.Groups["code"].Success)
                {
                    var r = new Run(m.Value.Substring(1, m.Value.Length - 2)) { FontFamily = Mono, Background = CodeBg };
                    target.Add(r);
                }
                else if (m.Groups["bold"].Success)
                {
                    var s = new Span { FontWeight = FontWeights.Bold };
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
                    var s = new Span { FontStyle = FontStyles.Italic };
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
            if (pos < text.Length) AddText(target, text.Substring(pos));
        }

        /// <summary>일반 글자 (컬러 이모지는 이미지로)</summary>
        static void AddText(InlineCollection target, string text)
        {
            foreach (var (t, e) in Emoji.Split(text))
            {
                if (e == null) { target.Add(new Run(t)); continue; }
                var img = new Image { Source = Emoji.Image(e), Width = 18, Height = 18, Margin = new Thickness(1, 0, 1, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                target.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });
            }
        }

        static Inline Link(string text, string url)
        {
            if (!Regex.IsMatch(url, @"^(https?|mailto):", RegexOptions.IgnoreCase)) return new Run(text);   // 파일 실행 등 위험한 주소는 링크로 만들지 않음
            var h = new Hyperlink(new Run(text)) { ToolTip = url, Foreground = null };
            h.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            h.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            return h;
        }

        static UIElement CodeBlock(string code, string lang)
        {
            var box = new TextBox
            {
                Text = code, IsReadOnly = true, FontFamily = Mono, FontSize = 12.5, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 360, IsReadOnlyCaretVisible = false,
            };
            box.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding("(TextElement.Foreground)") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Border), 1) });
            var copy = new Button { Content = "복사", FontSize = 11, Padding = new Thickness(8, 1, 8, 1), Cursor = Cursors.Hand, Focusable = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            copy.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = box });
            copy.Template = CopyTemplate();
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(code); copy.Content = "복사됨"; } catch { }
            };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(copy, Dock.Right);
            head.Children.Add(copy);
            head.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(lang) ? "코드" : lang, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(box);
            var border = new Border { Background = CodeBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 4, 0, 4), Child = stack, MinWidth = 180 };
            return border;
        }

        static ControlTemplate _copyTpl;
        static ControlTemplate CopyTemplate()
        {
            if (_copyTpl != null) return _copyTpl;
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, CodeBg);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            bd.AppendChild(cp);
            _copyTpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            _copyTpl.Seal();
            return _copyTpl;
        }

        // ───────────── 입력 도우미 (대화 창 · 쪽지 쓰기 창 공용) ─────────────

        /// <summary>선택한 글자를 기호로 감쌈 (선택이 없으면 기호 사이에 커서)</summary>
        public static void Wrap(TextBox box, string left, string right = null, string placeholder = "")
        {
            right = right ?? left;
            int start = box.SelectionStart, len = box.SelectionLength;
            var sel = len > 0 ? box.SelectedText : placeholder;
            box.SelectedText = left + sel + right;
            box.Select(start + left.Length, sel.Length);
            box.Focus();
        }

        /// <summary>선택한 각 줄 앞에 기호 (목록 · 인용)</summary>
        public static void Prefix(TextBox box, string prefix)
        {
            int start = box.GetCharacterIndexFromLineIndex(box.GetLineIndexFromCharacterIndex(box.SelectionStart));
            int end = box.SelectionStart + box.SelectionLength;
            var block = box.Text.Substring(start, end - start);
            var lines = block.Replace("\r\n", "\n").Split('\n');
            int n = 1;
            var nb = string.Join(Environment.NewLine, lines.Select(l => (prefix == "1." ? (n++) + ". " : prefix) + l));
            box.Select(start, end - start);
            box.SelectedText = nb;
            box.CaretIndex = start + nb.Length;
            box.Focus();
        }

        /// <summary>커서가 닫히지 않은 ``` 코드 블록 안에 있는지 — 이때 Enter 는 보내기 대신 줄바꿈</summary>
        public static bool InOpenFence(TextBox box)
        {
            var before = box.Text.Substring(0, Math.Min(box.CaretIndex, box.Text.Length));
            return Regex.Matches(before, @"(^|\n)\s*```").Count % 2 == 1;
        }

        /// <summary>서식 단축키 처리. 처리했으면 true</summary>
        public static bool HandleKey(TextBox box, KeyEventArgs e, Window owner)
        {
            var mod = Keyboard.Modifiers;
            if (mod == ModifierKeys.Control && e.Key == Key.B) { Wrap(box, "**"); return true; }
            if (mod == ModifierKeys.Control && e.Key == Key.I) { Wrap(box, "*"); return true; }
            if (mod == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.X) { Wrap(box, "~~"); return true; }
            if (mod == ModifierKeys.Control && e.Key == Key.E) { Wrap(box, "`"); return true; }
            if (mod == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C) { InsertCode(box, owner); return true; }
            if (e.Key == Key.Enter && mod == ModifierKeys.None && InOpenFence(box))
            {
                var i = box.CaretIndex;
                box.Text = box.Text.Insert(i, Environment.NewLine);
                box.CaretIndex = i + Environment.NewLine.Length;
                return true;
            }
            return false;
        }

        /// <summary>서식 메뉴 (버튼 아래에 펼침)</summary>
        public static void ShowFormatMenu(FrameworkElement anchor, TextBox box, Window owner, ResourceDictionary look = null)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
            if (look != null) ChatLook.Attach(m, look);
            void Add(string header, string key, Action a)
            {
                var mi = new MenuItem { Header = header, InputGestureText = key ?? "" };
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
            }
            Add("코드 블록 넣기…", "Ctrl+Shift+C", () => InsertCode(box, owner));
            Add("인라인 코드  `코드`", "Ctrl+E", () => Wrap(box, "`"));
            m.Items.Add(new Separator());
            Add("굵게  **글자**", "Ctrl+B", () => Wrap(box, "**"));
            Add("기울임  *글자*", "Ctrl+I", () => Wrap(box, "*"));
            Add("취소선  ~~글자~~", "Ctrl+Shift+X", () => Wrap(box, "~~"));
            Add("제목  # 제목", null, () => Prefix(box, "## "));
            m.Items.Add(new Separator());
            Add("글머리 목록  - 항목", null, () => Prefix(box, "- "));
            Add("번호 목록  1. 항목", null, () => Prefix(box, "1."));
            Add("인용  > 글", null, () => Prefix(box, "> "));
            Add("링크  [글자](주소)", null, () => Wrap(box, "[", "](https://)", "링크 글자"));
            Add("구분선  ---", null, () => { box.SelectedText = Environment.NewLine + "---" + Environment.NewLine; box.CaretIndex = box.SelectionStart + box.SelectionLength; box.Focus(); });
            m.Items.Add(new Separator());
            m.Items.Add(new MenuItem { Header = "받는 사람 화면에 서식이 적용되어 보입니다", IsEnabled = false });
            m.IsOpen = true;
        }

        static readonly string[] Langs = { "", "C#", "Java", "Python", "JavaScript", "TypeScript", "SQL", "HTML", "CSS", "JSON", "XML", "YAML", "C", "C++", "Go", "Kotlin", "Swift", "PHP", "Shell", "PowerShell", "로그", "텍스트" };

        /// <summary>코드 넣기 창 — 코드를 붙여 넣고 언어를 고르면 ``` 블록으로 넣음</summary>
        public static void InsertCode(TextBox box, Window owner)
        {
            var w = new Window
            {
                Title = "코드 넣기", Width = 560, Height = 440, MinWidth = 380, MinHeight = 280, ShowInTaskbar = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            Chrome.Apply(w, caption: 46);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            var look = ChatLook.Of(owner);
            if (look != null) ChatLook.Attach(w, look);

            var grid = new Grid { Margin = new Thickness(18, 0, 18, 16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock { Text = "코드 넣기", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(title);

            var langRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            langRow.Children.Add(new TextBlock { Text = "언어", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var lang = new ComboBox { Width = 160, IsEditable = true, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var l in Langs) lang.Items.Add(l);
            lang.Text = "";
            langRow.Children.Add(lang);
            Grid.SetRow(langRow, 1);
            grid.Children.Add(langRow);

            var code = new TextBox
            {
                AcceptsReturn = true, AcceptsTab = true, FontFamily = Mono, FontSize = 13, TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8), Style = (Style)Application.Current.FindResource("Field"), VerticalContentAlignment = VerticalAlignment.Top,
            };
            if (box.SelectionLength > 0) code.Text = box.SelectedText;
            Grid.SetRow(code, 2);
            grid.Children.Add(code);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = "취소", Style = (Style)Application.Current.FindResource("Outline"), Height = 32, Padding = new Thickness(16, 0, 16, 0), IsCancel = true };
            var ok = new Button { Content = "넣기", Style = (Style)Application.Current.FindResource("Primary"), Height = 32, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(new TextBlock { Text = "Ctrl+Enter 넣기", Opacity = 0.6, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);

            var bg = new Border { Child = grid };
            bg.SetResourceReference(Border.BackgroundProperty, "Surface");
            bg.SetResourceReference(TextElement.ForegroundProperty, "Ink");
            w.Content = bg;

            bool done = false;
            void Commit()
            {
                var body = (code.Text ?? "").Replace("\r\n", "\n").TrimEnd('\n');
                if (body.Trim().Length == 0) { w.Close(); return; }
                var nl = Environment.NewLine;
                var fence = body.Contains("```") ? "````" : "```";
                var langText = (lang.Text ?? "").Trim();
                var block = fence + langText + nl + body.Replace("\n", nl) + nl + fence;
                // 앞뒤 줄바꿈 보정
                int at = box.SelectionLength > 0 ? box.SelectionStart : box.CaretIndex;
                var before = box.Text.Substring(0, at);
                var after = box.Text.Substring(at + box.SelectionLength);
                if (before.Length > 0 && !before.EndsWith("\n")) block = nl + block;
                if (after.Length > 0 && !after.StartsWith("\r") && !after.StartsWith("\n")) block += nl;
                box.Text = before + block + after;
                box.CaretIndex = (before + block).Length;
                done = true;
                w.Close();
            }
            ok.Click += (s, e) => Commit();
            code.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Commit(); } };
            w.Loaded += (s, e) => code.Focus();
            w.ShowDialog();
            box.Focus();
            _ = done;
        }
    }
}
