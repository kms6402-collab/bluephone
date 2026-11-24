using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace BluePhone
{
    /// <summary>예약 시간 선택 메뉴 (빠른 선택 + 직접 지정)</summary>
    public static class Schedule
    {
        public static void Pick(Control anchor, Window owner, Action<DateTime> picked)
        {
            var now = DateTime.Now;
            var items = new System.Collections.Generic.List<Control>();
            void Add(string label, DateTime at, string hint)
            {
                var mi = new MenuItem { Header = string.IsNullOrEmpty(hint) ? label : $"{label}    {hint}" };
                mi.Click += (s, e) => picked(at);
                items.Add(mi);
            }
            items.Add(Menus.Item("예약 보내기", bold: true, enabled: false));
            Add("30분 뒤", now.AddMinutes(30), now.AddMinutes(30).ToString("tt h:mm"));
            Add("1시간 뒤", now.AddHours(1), now.AddHours(1).ToString("tt h:mm"));
            var six = now.Date.AddHours(18);
            if (six > now.AddMinutes(5)) Add("오늘 오후 6시", six, "");
            var nextMorning = now.Date.AddDays(1).AddHours(9);
            while (nextMorning.DayOfWeek == DayOfWeek.Saturday || nextMorning.DayOfWeek == DayOfWeek.Sunday) nextMorning = nextMorning.AddDays(1);
            Add(nextMorning.Date == now.Date.AddDays(1) ? "내일 오전 9시" : "다음 근무일 오전 9시", nextMorning, nextMorning.ToString("M/d (ddd)"));
            items.Add(Menus.Sep());
            items.Add(Menus.Item("날짜·시간 직접 지정…", async () => { var at = await Custom(owner); if (at.HasValue) picked(at.Value); }));
            Menus.Show(anchor, items, PlacementMode.TopEdgeAlignedLeft);
        }

        public static async Task<DateTime?> Custom(Window owner, string title = "언제 보낼까요?", string ok = "예약")
        {
            var start = DateTime.Now.AddHours(1);
            start = start.AddMinutes(-(start.Minute % 10)).AddSeconds(-start.Second);
            var date = new TextBox { Text = start.ToString("yyyy-MM-dd"), Width = 130 };
            var time = new TextBox { Text = start.ToString("HH:mm"), Width = 80, Margin = new Thickness(8, 0, 0, 0) };
            date.Classes.Add("field"); time.Classes.Add("field");
            var err = new TextBlock { Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
            err.Bind(TextBlock.ForegroundProperty, err.GetResourceObservable("Danger"));
            DateTime? result = null;

            var w = Dialog.Make("예약 시간", 360);
            var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
            var hint = new TextBlock
            {
                Text = "날짜는 2026-11-20, 시간은 15:30 처럼 입력하세요. 이 Mac이 켜져 있고 프로그램이 실행 중이어야 보내집니다.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14), LineHeight = 19,
            };
            hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("Ink2"));
            root.Children.Add(hint);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(date); row.Children.Add(time);
            root.Children.Add(row);
            root.Children.Add(err);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Spacing = 8 };
            var c = Dialog.Btn("취소", "outline"); c.IsCancel = true;
            c.Click += (s, e) => w.Close();
            var o = Dialog.Btn(ok, "primary"); o.IsDefault = true;
            o.Click += (s, e) =>
            {
                if (DateTime.TryParseExact((date.Text ?? "").Trim() + " " + (time.Text ?? "").Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-M-d H:mm", "yyyy.MM.dd HH:mm", "yyyy.M.d H:mm" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at > DateTime.Now.AddMinutes(1))
                {
                    result = at; w.Close();
                }
                else { err.Text = "형식이 맞지 않거나 이미 지난 시간입니다."; err.IsVisible = true; }
            };
            bar.Children.Add(c); bar.Children.Add(o);
            root.Children.Add(bar);
            w.Content = root;
            w.Opened += (s, e) => { time.Focus(); time.SelectAll(); };
            await Dialog.ShowWindow(w, owner);
            return result;
        }
    }
}
