using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BluePhone
{
    /// <summary>예약 시간 선택 메뉴 (빠른 선택 + 직접 지정)</summary>
    public static class Schedule
    {
        public static void Pick(FrameworkElement anchor, Window owner, Action<DateTime> picked)
        {
            var now = DateTime.Now;
            var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
            void Add(string label, DateTime at, string hint)
            {
                var mi = new MenuItem { Header = label, InputGestureText = hint };
                mi.Click += (s, e) => picked(at);
                m.Items.Add(mi);
            }
            m.Items.Add(new MenuItem { Header = "예약 보내기", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            Add("30분 뒤", now.AddMinutes(30), now.AddMinutes(30).ToString("tt h:mm"));
            Add("1시간 뒤", now.AddHours(1), now.AddHours(1).ToString("tt h:mm"));
            var six = now.Date.AddHours(18);
            if (six > now.AddMinutes(5)) Add("오늘 오후 6시", six, "");
            var nextMorning = now.Date.AddDays(1).AddHours(9);
            while (nextMorning.DayOfWeek == DayOfWeek.Saturday || nextMorning.DayOfWeek == DayOfWeek.Sunday) nextMorning = nextMorning.AddDays(1);
            Add(nextMorning.Date == now.Date.AddDays(1) ? "내일 오전 9시" : "다음 근무일 오전 9시", nextMorning, nextMorning.ToString("M/d (ddd)"));
            m.Items.Add(new Separator());
            var custom = new MenuItem { Header = "날짜·시간 직접 지정…" };
            custom.Click += (s, e) => { var at = Custom(owner); if (at.HasValue) picked(at.Value); };
            m.Items.Add(custom);
            m.IsOpen = true;
        }

        static DateTime? Custom(Window owner)
        {
            var start = DateTime.Now.AddHours(1);
            start = start.AddMinutes(-(start.Minute % 10)).AddSeconds(-start.Second);
            var date = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Text = start.ToString("yyyy-MM-dd"), Width = 130 };
            var time = new TextBox { Style = (Style)Application.Current.FindResource("Field"), Text = start.ToString("HH:mm"), Width = 80, Margin = new Thickness(8, 0, 0, 0) };
            var err = new TextBlock { Foreground = (Brush)Application.Current.FindResource("Danger"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            DateTime? result = null;

            var w = new Window { Width = 360, SizeToContent = SizeToContent.Height, ShowInTaskbar = false, Title = "예약 시간", WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Chrome.Apply(w, caption: 0, resizable: false);
            if (owner != null && owner.IsVisible) w.Owner = owner;
            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            root.Children.Add(new TextBlock { Text = "언제 보낼까요?", FontSize = 15, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock { Text = "날짜는 2026-11-20, 시간은 15:30 처럼 입력하세요. 이 PC가 켜져 있고 프로그램이 실행 중이어야 보내집니다.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14), Foreground = (Brush)Application.Current.FindResource("Ink2"), LineHeight = 19 });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(date); row.Children.Add(time);
            root.Children.Add(row);
            root.Children.Add(err);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var c = new Button { Content = "취소", Style = (Style)Application.Current.FindResource("Outline"), MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            c.Click += (s, e) => w.Close();
            var o = new Button { Content = "예약", Style = (Style)Application.Current.FindResource("Primary"), MinWidth = 76, IsDefault = true };
            o.Click += (s, e) =>
            {
                if (DateTime.TryParseExact(date.Text.Trim() + " " + time.Text.Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-M-d H:mm", "yyyy.MM.dd HH:mm", "yyyy.M.d H:mm" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at > DateTime.Now.AddMinutes(1))
                {
                    result = at; w.Close();
                }
                else { err.Text = "형식이 맞지 않거나 이미 지난 시간입니다."; err.Visibility = Visibility.Visible; }
            };
            bar.Children.Add(c); bar.Children.Add(o);
            root.Children.Add(bar);
            w.Content = root;
            w.Loaded += (s, e) => { time.Focus(); time.SelectAll(); };
            w.ShowDialog();
            return result;
        }
    }
}
