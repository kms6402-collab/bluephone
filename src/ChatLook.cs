using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BluePhone
{
    /// <summary>
    /// 대화 창 꾸미기: 테마(색) · 글꼴 · 글자 크기.
    /// 테마는 말풍선뿐 아니라 창 배경 · 머리글 · 입력칸 · 버튼 · 메뉴 · 스크롤바 · 이모지 창까지 모두 바꾼다.
    /// (Theme.xaml 의 공통 색 이름을 대화 창 안에서만 테마 색으로 덮어씀)
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

        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        /// <summary>a 와 b 를 t(0~1) 비율로 섞음</summary>
        static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        static readonly Color White = Colors.White, Black = Colors.Black;

        /// <summary>밝은 테마: 바탕색 하나에서 창 · 입력칸 · 선 · 강조색을 함께 만들어 창 전체 톤을 맞춤</summary>
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

        /// <summary>어두운 테마</summary>
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
        const string Marker = "Ct.Marker";

        static Brush B(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>현재 설정으로 테마 리소스 묶음을 만든다</summary>
        public static ResourceDictionary Build(AppSettings s)
        {
            var t = Find(s.ChatTheme);
            var r = new ResourceDictionary();
            r[Marker] = true;
            // 대화 창 전용
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
            r["Ct.Overlay"] = B(Color.FromArgb(0xEE, t.Bg.R, t.Bg.G, t.Bg.B));
            // 공통 색 이름 덮어쓰기 → 버튼 · 입력칸 · 메뉴 · 스크롤바 · 칩 등 모든 컨트롤이 테마를 따름
            r["Surface"] = B(t.Panel);
            r["Canvas"] = B(t.InputBg);
            r["Ink"] = B(t.Text);
            r["Ink2"] = B(t.Ink2);
            r["Muted"] = B(t.Sub);
            r["Faint"] = B(t.Meta);
            r["Line"] = B(t.Line);
            r["LineStrong"] = B(t.InputLine);
            r["Hover"] = B(t.Hover);
            r["Pressed"] = B(t.Pressed);
            r["Accent"] = B(t.Out);
            r["AccentHover"] = B(Mix(t.Out, Black, 0.10));
            r["AccentPressed"] = B(Mix(t.Out, Black, 0.20));
            r["AccentSoft"] = B(t.AccentSoft);
            r["AccentSoftHover"] = B(t.AccentSoftHover);
            if (t.Dark) r["DangerSoft"] = B(Mix(C("#D93A3F"), t.Panel, 0.75));
            r["Ct.Font"] = string.IsNullOrEmpty(s.ChatFont)
                ? (FontFamily)Application.Current.FindResource("UI")
                : new FontFamily(s.ChatFont + ", Malgun Gothic, Segoe UI");
            r["Ct.Size"] = Math.Max(MinSize, Math.Min(MaxSize, s.ChatFontSize <= 0 ? DefaultSize : s.ChatFontSize));
            return r;
        }

        /// <summary>요소(창 · 메뉴 · 팝업)에 테마 묶음을 붙임 — 이전 테마 묶음은 빼고</summary>
        public static void Attach(FrameworkElement e, ResourceDictionary look)
        {
            if (e == null || look == null) return;
            var md = e.Resources.MergedDictionaries;
            if (md.Contains(look)) return;
            foreach (var old in md.Where(d => d.Contains(Marker)).ToList()) md.Remove(old);
            md.Add(look);
        }

        /// <summary>창에 현재 꾸미기 값을 적용하고 그 묶음을 돌려줌</summary>
        public static ResourceDictionary Apply(FrameworkElement w, AppSettings s)
        {
            var look = Build(s);
            Attach(w, look);
            return look;
        }

        /// <summary>창에 붙어 있는 테마 묶음 (없으면 null)</summary>
        public static ResourceDictionary Of(FrameworkElement e) =>
            e?.Resources.MergedDictionaries.FirstOrDefault(d => d.Contains(Marker));
    }
}
