using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace BluePhone
{
    /// <summary>코드로 화면을 만들 때 쓰는 짧은 도우미</summary>
    public static class Ui
    {
        /// <summary>앱 리소스(테마 색)에 연결 — 테마를 바꾸면 바로 따라 바뀜</summary>
        public static T Res<T>(this T c, AvaloniaProperty prop, string key) where T : Control
        {
            c.Bind(prop, c.GetResourceObservable(key));
            return c;
        }

        public static T Fg<T>(this T c, string key) where T : TextBlock => c.Res(TextBlock.ForegroundProperty, key);

        public static IBrush Brush(string key) =>
            Application.Current != null && Application.Current.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

        public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string fg = null, bool trim = false, bool wrap = false)
        {
            var tb = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            if (weight.HasValue) tb.FontWeight = weight.Value;
            if (fg != null) tb.Fg(fg);
            if (trim) tb.TextTrimming = TextTrimming.CharacterEllipsis;
            if (wrap) tb.TextWrapping = TextWrapping.Wrap;
            return tb;
        }

        public static T Bind<T>(this T c, AvaloniaProperty prop, string path, IValueConverter conv = null) where T : AvaloniaObject
        {
            c.Bind(prop, new Binding(path) { Converter = conv, Mode = BindingMode.OneWay });
            return c;
        }

        public static T Col<T>(this T c, int col) where T : Control { Grid.SetColumn(c, col); return c; }
        public static T Row<T>(this T c, int row) where T : Control { Grid.SetRow(c, row); return c; }
        public static T Dock<T>(this T c, Avalonia.Controls.Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }
        public static T Tip<T>(this T c, string tip) where T : Control { ToolTip.SetTip(c, tip); return c; }
        public static T Cls<T>(this T c, params string[] cls) where T : StyledElement { foreach (var x in cls) c.Classes.Add(x); return c; }

        public static StackPanel H(double spacing = 0, params Control[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
            foreach (var ch in children) p.Children.Add(ch);
            return p;
        }

        public static StackPanel V(double spacing = 0, params Control[] children)
        {
            var p = new StackPanel { Spacing = spacing };
            foreach (var ch in children) p.Children.Add(ch);
            return p;
        }

        public static Border Badge(TextBlock text)
        {
            text.Foreground = Brushes.White;
            text.FontSize = 10.5;
            text.FontWeight = FontWeight.SemiBold;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { CornerRadius = new CornerRadius(8), MinWidth = 16, Height = 16, Padding = new Thickness(5, 0), Child = text, VerticalAlignment = VerticalAlignment.Center };
            return b.Res(Border.BackgroundProperty, "Accent");
        }

        public static Avalonia.Controls.Shapes.Ellipse Dot(double size, string brushKey)
        {
            var e = new Avalonia.Controls.Shapes.Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            return e.Res(Avalonia.Controls.Shapes.Shape.FillProperty, brushKey);
        }

        public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);
        public static readonly IValueConverter HasText = new FuncValueConverter<string, bool>(s => !string.IsNullOrWhiteSpace(s));
        public static readonly IValueConverter Hint = new FuncValueConverter<string, string>(s => string.IsNullOrEmpty(s) ? "" : "  " + s);
    }
}
