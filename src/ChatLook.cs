using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BluePhone
{
    /// <summary>대화 창 꾸미기: 테마(색) · 글꼴 · 글자 크기</summary>
    public static class ChatLook
    {
        public sealed class Theme
        {
            public string Id, Name;
            public string Bg, Panel, Text, Sub, Line, In, InText, Out, OutText, InputBg, InputLine, Meta, FileBg;
        }

        static Theme Light(string id, string name, string bg, string inBubble, string outBubble, string meta = "#A3A9B4") => new Theme
        {
            Id = id, Name = name, Bg = bg, Panel = "#FFFFFF", Text = "#15181D", Sub = "#6B7280", Line = "#E6E8EC",
            In = inBubble, InText = "#15181D", Out = outBubble, OutText = "#FFFFFF", InputBg = "#FFFFFF", InputLine = "#D5D9E0",
            Meta = meta, FileBg = "#14000000",
        };

        static Theme Dark(string id, string name, string bg, string panel, string inBubble, string outBubble, string line, string input) => new Theme
        {
            Id = id, Name = name, Bg = bg, Panel = panel, Text = "#E8EAED", Sub = "#9AA0A6", Line = line,
            In = inBubble, InText = "#E8EAED", Out = outBubble, OutText = "#FFFFFF", InputBg = input, InputLine = line,
            Meta = "#7D838C", FileBg = "#22FFFFFF",
        };

        public static readonly Theme[] Themes =
        {
            Light("light", "기본", "#FFFFFF", "#F1F3F6", "#1D5BF0"),
            new Theme
            {
                Id = "dark", Name = "어둡게", Bg = "#16181C", Panel = "#1E2126", Text = "#E8EAED", Sub = "#9AA0A6", Line = "#2E3238",
                In = "#2A2E35", InText = "#E8EAED", Out = "#3A6CF4", OutText = "#FFFFFF", InputBg = "#262A30", InputLine = "#3A3F47",
                Meta = "#7D838C", FileBg = "#22FFFFFF",
            },
            Light("sky", "하늘", "#E8F1FB", "#FFFFFF", "#2F7FD8", "#7F8EA3"),
            Light("mint", "민트", "#E7F5EF", "#FFFFFF", "#1E9A73", "#7C9A8E"),
            Light("lavender", "라벤더", "#F0EDFA", "#FFFFFF", "#7352D6", "#8D86A6"),
            Light("paper", "종이", "#F5F0E6", "#FFFFFF", "#6B5643", "#9C907E"),
            Light("blossom", "벚꽃", "#FCEEF2", "#FFFFFF", "#D9487A", "#A88C96"),
            Light("sunset", "노을", "#FDF0E6", "#FFFFFF", "#E2672E", "#A8907E"),
            new Theme
            {
                Id = "lemon", Name = "레몬", Bg = "#FFF8D9", Panel = "#FFFFFF", Text = "#15181D", Sub = "#6B7280", Line = "#EDE6C8",
                In = "#FFFFFF", InText = "#15181D", Out = "#FFD43B", OutText = "#2B2410", InputBg = "#FFFFFF", InputLine = "#E3DBB8",
                Meta = "#A39A76", FileBg = "#14000000",
            },
            Light("ocean", "바다", "#E3F4F7", "#FFFFFF", "#0E8AA6", "#7A979E"),
            Light("mono", "모노", "#F4F4F5", "#FFFFFF", "#27272A", "#9A9AA2"),
            Dark("navy", "밤하늘", "#0F1626", "#151D30", "#222C44", "#4C7DF0", "#26304A", "#1B2439"),
            Dark("forest", "숲", "#121A16", "#18221D", "#22302A", "#2E9B6F", "#2A3830", "#1E2A24"),
            Dark("black", "블랙", "#000000", "#0B0B0C", "#1C1C1F", "#3A6CF4", "#222225", "#141416"),
            Dark("grape", "포도", "#1A1424", "#211A2E", "#2E2540", "#8E63E8", "#33294A", "#261E36"),
        };

        public static Theme Find(string id) => Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];

        /// <summary>글꼴 후보 (이 PC에 설치된 것만 보여 줌). Family 가 null 이면 프로그램 기본 글꼴</summary>
        static readonly (string Name, string Family)[] FontCandidates =
        {
            ("기본", null),
            ("맑은 고딕", "Malgun Gothic"),
            ("나눔고딕", "NanumGothic"),
            ("나눔바른고딕", "NanumBarunGothic"),
            ("나눔스퀘어", "NanumSquare"),
            ("Pretendard", "Pretendard"),
            ("Noto Sans KR", "Noto Sans KR"),
            ("굴림", "Gulim"),
            ("돋움", "Dotum"),
            ("바탕", "Batang"),
            ("궁서", "Gungsuh"),
            ("Segoe UI", "Segoe UI"),
        };

        static List<(string Name, string Family)> _fonts;

        public static IReadOnlyList<(string Name, string Family)> Fonts
        {
            get
            {
                if (_fonts != null) return _fonts;
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    installed.Add(f.Source);
                    foreach (var n in f.FamilyNames.Values) installed.Add(n);
                }
                _fonts = FontCandidates.Where(c => c.Family == null || installed.Contains(c.Family)).ToList();
                return _fonts;
            }
        }

        public static readonly (string Name, double Size)[] Sizes =
        {
            ("작게", 12), ("보통", 13.5), ("크게", 15), ("더 크게", 17), ("아주 크게", 20),
        };

        public const double MinSize = 11, MaxSize = 24, DefaultSize = 13.5;

        static Brush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>창 리소스에 현재 꾸미기 값을 넣는다 (XAML 은 DynamicResource 로 참조)</summary>
        public static void Apply(FrameworkElement w, AppSettings s)
        {
            var t = Find(s.ChatTheme);
            var r = w.Resources;
            r["Ct.Bg"] = B(t.Bg);
            r["Ct.Panel"] = B(t.Panel);
            r["Ct.Text"] = B(t.Text);
            r["Ct.Sub"] = B(t.Sub);
            r["Ct.Line"] = B(t.Line);
            r["Ct.In"] = B(t.In);
            r["Ct.InText"] = B(t.InText);
            r["Ct.Out"] = B(t.Out);
            r["Ct.OutText"] = B(t.OutText);
            r["Ct.InputBg"] = B(t.InputBg);
            r["Ct.InputLine"] = B(t.InputLine);
            r["Ct.Meta"] = B(t.Meta);
            r["Ct.FileBg"] = B(t.FileBg);
            r["Ct.Font"] = string.IsNullOrEmpty(s.ChatFont)
                ? (FontFamily)Application.Current.FindResource("UI")
                : new FontFamily(s.ChatFont + ", Malgun Gothic, Segoe UI");
            r["Ct.Size"] = Math.Max(MinSize, Math.Min(MaxSize, s.ChatFontSize <= 0 ? DefaultSize : s.ChatFontSize));
        }
    }
}
