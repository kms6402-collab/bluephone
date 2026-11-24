using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace BluePhone
{
    /// <summary>
    /// 꾸미기: 테마(색) · 글꼴 · 글자 크기 — Windows 판과 같은 15가지 테마.
    /// 앱 리소스의 공통 색 이름(Surface · Ink · Accent …)을 덮어써 모든 창 · 메뉴 · 입력칸이 함께 바뀜
    /// </summary>
    public static class ChatLook
    {
        public sealed class Theme
        {
            public string Id, Name;
            public bool Dark;
            public Color Bg, Panel, Text, Ink2, Sub, Meta, Line, InputBg, InputLine, Hover, Pressed,
                         In, InText, Out, OutText, AccentSoft, AccentSoftHover, FileBg;
        }

        static Color C(string hex) => Color.Parse(hex);

        static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        static readonly Color White = Colors.White, Black = Colors.Black;

        static Theme Light(string id, string name, string bg, string inBubble, string accent, string outText = "#FFFFFF")
        {
            var b = C(bg); var a = C(accent);
            var panel = Mix(b, White, 0.45);
            return new Theme
            {
                Id = id, Name = name, Bg = b, Panel = panel,
                Text = C("#15181D"), Ink2 = Mix(C("#3D4450"), a, 0.12), Sub = Mix(C("#6B7280"), a, 0.18), Meta = Mix(C("#A3A9B4"), a, 0.15),
                Line = Mix(b, Black, 0.07), InputBg = Mix(b, White, 0.75), InputLine = Mix(b, Black, 0.16),
                Hover = Mix(panel, Black, 0.05), Pressed = Mix(panel, Black, 0.09),
                In = C(inBubble), InText = C("#15181D"), Out = a, OutText = C(outText),
                AccentSoft = Mix(a, panel, 0.86), AccentSoftHover = Mix(a, panel, 0.78), FileBg = C("#14000000"),
            };
        }

        static Theme Dark(string id, string name, string bg, string panel, string inBubble, string accent, string line, string input)
        {
            var p = C(panel); var a = C(accent); var i = C(input);
            return new Theme
            {
                Id = id, Name = name, Dark = true, Bg = C(bg), Panel = p,
                Text = C("#E8EAED"), Ink2 = C("#C9CDD3"), Sub = C("#9AA0A6"), Meta = C("#7D838C"),
                Line = C(line), InputBg = i, InputLine = Mix(i, White, 0.13),
                Hover = Mix(p, White, 0.07), Pressed = Mix(p, White, 0.11),
                In = C(inBubble), InText = C("#E8EAED"), Out = a, OutText = White,
                AccentSoft = Mix(a, p, 0.72), AccentSoftHover = Mix(a, p, 0.62), FileBg = C("#22FFFFFF"),
            };
        }

        public static readonly Theme[] Themes =
        {
            new Theme
            {
                Id = "light", Name = "기본", Bg = White, Panel = White, Text = C("#15181D"), Ink2 = C("#4A5160"), Sub = C("#6B7280"), Meta = C("#A3A9B4"),
                Line = C("#E6E8EC"), InputBg = White, InputLine = C("#D5D9E0"), Hover = C("#EEF0F3"), Pressed = C("#E4E7EC"),
                In = C("#F1F3F6"), InText = C("#15181D"), Out = C("#1D5BF0"), OutText = White,
                AccentSoft = C("#EAF0FE"), AccentSoftHover = C("#DCE6FD"), FileBg = C("#14000000"),
            },
            Light("sky", "하늘", "#E3EEFA", "#FFFFFF", "#2F7FD8"),
            Light("mint", "민트", "#E2F3EB", "#FFFFFF", "#1E9A73"),
            Light("lavender", "라벤더", "#ECE8F9", "#FFFFFF", "#7352D6"),
            Light("paper", "종이", "#F3EDE1", "#FFFDF8", "#6B5643"),
            Light("blossom", "벚꽃", "#FBE9EF", "#FFFFFF", "#D9487A"),
            Light("sunset", "노을", "#FCEDE2", "#FFFFFF", "#E2672E"),
            Light("lemon", "레몬", "#FFF6D2", "#FFFFFF", "#E0A800", "#2B2410"),
            Light("ocean", "바다", "#DFF2F6", "#FFFFFF", "#0E8AA6"),
            Light("mono", "모노", "#F1F1F2", "#FFFFFF", "#27272A"),
            Dark("dark", "어둡게", "#16181C", "#1E2126", "#2A2E35", "#3A6CF4", "#2E3238", "#262A30"),
            Dark("navy", "밤하늘", "#0F1626", "#151D30", "#222C44", "#4C7DF0", "#26304A", "#1B2439"),
            Dark("forest", "숲", "#121A16", "#18221D", "#22302A", "#2E9B6F", "#2A3830", "#1E2A24"),
            Dark("black", "블랙", "#000000", "#0B0B0C", "#1C1C1F", "#3A6CF4", "#222225", "#141416"),
            Dark("grape", "포도", "#1A1424", "#211A2E", "#2E2540", "#8E63E8", "#33294A", "#261E36"),
        };

        public static Theme Find(string id) => Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];
        public static Theme Current { get; private set; } = Themes[0];

        /// <summary>프로그램 기본 글꼴 (한글은 Apple SD Gothic Neo)</summary>
        public const string UiFont = "Apple SD Gothic Neo, Malgun Gothic, Segoe UI, Noto Sans CJK KR, sans-serif";

        static readonly (string Name, string Family)[] FontCandidates =
        {
            ("기본", null),
            ("Apple SD 산돌고딕 Neo", "Apple SD Gothic Neo"),
            ("나눔고딕", "NanumGothic"),
            ("나눔바른고딕", "NanumBarunGothic"),
            ("나눔스퀘어", "NanumSquare"),
            ("나눔명조", "NanumMyeongjo"),
            ("Pretendard", "Pretendard"),
            ("Noto Sans KR", "Noto Sans KR"),
            ("Noto Sans CJK KR", "Noto Sans CJK KR"),
            ("애플명조", "AppleMyungjo"),
            ("맑은 고딕", "Malgun Gothic"),
            ("Helvetica Neue", "Helvetica Neue"),
        };

        static List<(string Name, string Family)> _fonts;

        public static IReadOnlyList<(string Name, string Family)> Fonts
        {
            get
            {
                if (_fonts != null) return _fonts;
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var f in FontManager.Current.SystemFonts) installed.Add(f.Name); } catch { }
                _fonts = FontCandidates.Where(c => c.Family == null || installed.Contains(c.Family)).ToList();
                return _fonts;
            }
        }

        public static readonly (string Name, double Size)[] Sizes =
        {
            ("작게", 12), ("보통", 13.5), ("크게", 15), ("더 크게", 17), ("아주 크게", 20),
        };

        public const double MinSize = 11, MaxSize = 24, DefaultSize = 13.5;

        static IBrush B(Color c) => new ImmutableSolidColorBrush(c);

        public static Dictionary<string, object> Build(AppSettings s)
        {
            var t = Find(s.ChatTheme);
            var r = new Dictionary<string, object>
            {
                ["Ct.Bg"] = B(t.Bg),
                ["Ct.Panel"] = B(t.Panel),
                ["Ct.Text"] = B(t.Text),
                ["Ct.Sub"] = B(t.Sub),
                ["Ct.Line"] = B(t.Line),
                ["Ct.In"] = B(t.In),
                ["Ct.InText"] = B(t.InText),
                ["Ct.Out"] = B(t.Out),
                ["Ct.OutText"] = B(t.OutText),
                ["Ct.InputBg"] = B(t.InputBg),
                ["Ct.InputLine"] = B(t.InputLine),
                ["Ct.Meta"] = B(t.Meta),
                ["Ct.FileBg"] = B(t.FileBg),
                ["Ct.Overlay"] = B(Color.FromArgb(0xEE, t.Bg.R, t.Bg.G, t.Bg.B)),
                ["Surface"] = B(t.Panel),
                ["Canvas"] = B(t.Dark ? t.Bg : Mix(t.Panel, t.Bg == White ? C("#F5F6F8") : t.Bg, 0.6)),
                ["Ink"] = B(t.Text),
                ["Ink2"] = B(t.Ink2),
                ["Muted"] = B(t.Sub),
                ["Faint"] = B(t.Meta),
                ["Line"] = B(t.Line),
                ["LineStrong"] = B(t.InputLine),
                ["Hover"] = B(t.Hover),
                ["Pressed"] = B(t.Pressed),
                ["Accent"] = B(t.Out),
                ["AccentFg"] = B(t.OutText),
                ["AccentHover"] = B(Mix(t.Out, Black, 0.10)),
                ["AccentPressed"] = B(Mix(t.Out, Black, 0.20)),
                ["AccentSoft"] = B(t.AccentSoft),
                ["AccentSoftHover"] = B(t.AccentSoftHover),
                ["DangerSoft"] = B(t.Dark ? Mix(C("#D93A3F"), t.Panel, 0.75) : C("#FDECEC")),
                ["InputBg"] = B(t.InputBg),
                ["LinkFg"] = B(t.Dark ? C("#7FA6FF") : t.Out),
                ["Ct.Font"] = new FontFamily(string.IsNullOrEmpty(s.ChatFont) ? UiFont : s.ChatFont + ", " + UiFont),
                ["Ct.Size"] = Math.Max(MinSize, Math.Min(MaxSize, s.ChatFontSize <= 0 ? DefaultSize : s.ChatFontSize)),
            };
            return r;
        }

        /// <summary>프로그램 전체에 테마 적용 (열려 있는 창도 바로 바뀜)</summary>
        public static void ApplyGlobal(AppSettings s)
        {
            var app = Application.Current;
            if (app == null) return;
            Current = Find(s.ChatTheme);
            foreach (var kv in Build(s)) app.Resources[kv.Key] = kv.Value;
            app.RequestedThemeVariant = Current.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
