using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BluePhone
{
    /// <summary>새 쪽지 · 전체 공지 쓰기</summary>
    public sealed class ComposeWindow : Window
    {
        readonly Core _core;
        readonly ObservableCollection<Peer> _to = new ObservableCollection<Peer>();
        readonly Attachments _files;
        readonly bool _broadcast;
        string _quote;
        bool _sending, _confirmedClose;
        DateTime? _at;

        TextBlock _heading, _broadcastInfo, _quoteText, _scheduleText, _hint, _sendText;
        Button _addBtn, _sendBtn, _emojiBtn, _formatBtn, _scheduleBtn;
        Border _quoteBar, _scheduleBar;
        TextBox _body;
        ToggleButton _urgent;

        public ComposeWindow(Core core, IList<Peer> to, bool broadcast, string quote)
        {
            _core = core;
            _broadcast = broadcast;
            _files = new Attachments(this);
            Title = "새 쪽지";
            Width = 460; Height = 440; MinWidth = 360; MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Cls("app");
            Icons.Apply(this);
            foreach (var p in to ?? new Peer[0]) _to.Add(p);
            Content = Build();

            if (broadcast)
            {
                _heading.Text = "전체 공지";
                Title = "전체 공지";
                _addBtn.IsVisible = false;
                _broadcastInfo.IsVisible = true;
                _broadcastInfo.Text = $"접속 중인 동료 {_to.Count}명 전체";
            }
            else if (_to.Count == 1)
            {
                _heading.Text = _to[0].DisplayName + "님에게";
                Title = _heading.Text;
            }

            if (!string.IsNullOrWhiteSpace(quote))
            {
                _quote = quote.Trim();
                _quoteBar.IsVisible = true;
                _quoteText.Text = _quote.Replace("\r", "").Replace("\n", "  ");
            }

            UpdateSendState();
            _to.CollectionChanged += (s, e) => UpdateSendState();
            _body.TextChanged += (s, e) => UpdateSendState();
            _files.Changed += UpdateSendState;
            Opened += (s, e) => { _body.Focus(); _body.CaretIndex = (_body.Text ?? "").Length; };
            AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Escape && string.IsNullOrEmpty(_body.Text) && _files.Items.Count == 0) { Close(); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
        }

        Control Build()
        {
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
            root.Res(Panel.BackgroundProperty, "Surface");
            _heading = Ui.Text("새 쪽지", 14, FontWeight.SemiBold);
            _heading.Margin = new Thickness(18, 12, 0, 6);
            root.Children.Add(_heading);

            // 받는 사람
            var chips = new ItemsControl { ItemsSource = _to, Focusable = false };
            chips.ItemsPanel = new FuncTemplate<Panel>(() => new WrapPanel());
            chips.ItemTemplate = new FuncDataTemplate<Peer>((p, _) =>
            {
                if (p == null) return new Panel();
                var x = new Button { Content = "✕", FontSize = 8, Width = 20, Height = 20, Margin = new Thickness(2, 0, 0, 0), MinHeight = 0 }.Cls("icon").Tip("빼기");
                x.Click += (s, e) => _to.Remove(p);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Children =
                {
                    new Avatar { Size = 18, Fill = p.AvatarBrush, Initial = p.Initial, Image = p.AvatarImage },
                    WithMargin(Ui.Text(p.DisplayName, 13), 6, 2),
                    WithMargin(new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = p.StatusBrush, VerticalAlignment = VerticalAlignment.Center }, 3, 2),
                    x,
                } };
                var b = new Border { CornerRadius = new CornerRadius(12), Height = 26, Padding = new Thickness(4, 0, 2, 0), Margin = new Thickness(0, 0, 6, 4), Child = row };
                b.Res(Border.BackgroundProperty, "Canvas");
                ToolTip.SetTip(b, p.Address);
                return b;
            });
            _broadcastInfo = Ui.Text("", 13, fg: "Ink2");
            _broadcastInfo.IsVisible = false; _broadcastInfo.Margin = new Thickness(0, 5, 8, 4);
            _addBtn = new Button { Height = 26, Padding = new Thickness(8, 0), Margin = new Thickness(0, 0, 0, 4), Content = Ui.H(5, Ui.Text("+", 13, FontWeight.SemiBold, "Accent"), Ui.Text("추가", 13, FontWeight.SemiBold, "Accent")) }.Cls("ghost");
            _addBtn.Click += (s, e) => AddTo_Click();
            var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Children = { chips, _broadcastInfo, _addBtn } };
            var label = Ui.Text("받는 사람", 12, fg: "Muted").Dock(Dock.Left);
            label.VerticalAlignment = VerticalAlignment.Top; label.Margin = new Thickness(0, 6, 12, 0);
            var toBar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 4, 12, 8), Child = new DockPanel { Children = { label, wrap } } }.Row(1);
            toBar.Res(Border.BorderBrushProperty, "Line");
            root.Children.Add(toBar);

            // 인용
            _quoteText = Ui.Text("", 12, fg: "Muted", trim: true);
            _quoteText.MaxHeight = 36; _quoteText.TextWrapping = TextWrapping.Wrap;
            var dropQuote = new Button { Content = "✕", FontSize = 9, Width = 22, Height = 22 }.Cls("icon").Tip("인용 빼기").Dock(Dock.Right);
            dropQuote.Click += (s, e) => { _quote = null; _quoteBar.IsVisible = false; };
            _quoteBar = new Border { IsVisible = false, Margin = new Thickness(18, 12, 18, 0), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 2), Child = new DockPanel { Children = { dropQuote, _quoteText } } }.Row(2);
            _quoteBar.Res(Border.BorderBrushProperty, "LineStrong");
            root.Children.Add(_quoteBar);

            // 본문
            _body = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Watermark = "내용을 입력하세요", Margin = new Thickness(16, 12, 14, 8), VerticalContentAlignment = VerticalAlignment.Top, FontSize = 13.5 }.Cls("bare").Row(3);
            _body.AddHandler(KeyDownEvent, Body_KeyDown, RoutingStrategies.Tunnel);
            _files.EnablePaste(_body);
            root.Children.Add(_body);

            // 첨부 · 예약
            var fl = _files.BuildList();
            fl.Margin = new Thickness(16, 0, 16, 6);
            _scheduleText = Ui.Text("", 12.5, fg: "Accent");
            var clearSch = new Button { Content = "✕", FontSize = 9, Width = 22, Height = 22 }.Cls("icon").Dock(Dock.Right);
            clearSch.Click += (s, e) => ClearSchedule();
            _scheduleBar = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 4, 5), Margin = new Thickness(16, 0, 16, 6), IsVisible = false, Child = new DockPanel { Children = { clearSch, _scheduleText } } };
            _scheduleBar.Res(Border.BackgroundProperty, "AccentSoft");
            root.Children.Add(new StackPanel { Children = { fl, _scheduleBar } }.Row(4));

            // 아래
            var bar = new DockPanel();
            var attach = Tool("📎", "파일 첨부 — 모든 형식 · 폴더는 자동 압축 (끌어다 놓기, ⌘V 도 가능)"); attach.Click += async (s, e) => await _files.Pick();
            var cap = Tool("✂", _core.Settings.CaptureHotkey && Native.IsMac ? "화면 캡처해서 첨부 (⌃⌥S)" : "화면 캡처해서 첨부"); cap.Click += (s, e) => _core.StartCapture(this);
            _emojiBtn = Tool("😊", "이모지 · 이모티콘"); _emojiBtn.Click += (s, e) => Emoji.ShowPicker(_emojiBtn, _body);
            _formatBtn = Tool("Aa", $"서식 (마크다운) — 굵게 {Native.Mod}B · 기울임 {Native.Mod}I · 목록 · 인용 · 링크"); _formatBtn.FontSize = 13; _formatBtn.Click += (s, e) => Markdown.ShowFormatMenu(_formatBtn, _body, this);
            var code = Tool("</>", "코드 블록 넣기"); code.FontSize = 11; code.Click += async (s, e) => await Markdown.InsertCode(_body, this);
            _scheduleBtn = Tool("⏰", "예약 보내기"); _scheduleBtn.Click += (s, e) => Schedule_Click();
            _urgent = new ToggleButton { Content = Ui.H(5, Ui.Text("❗", 11), Ui.Text("긴급", 12.5)), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Cls("chip")
                .Tip("받는 사람이 방해 금지 상태여도 알림이 뜨고, Dock 아이콘이 튕깁니다");
            foreach (var c in new Control[] { attach, cap, _emojiBtn, _formatBtn, code, _scheduleBtn, _urgent }) bar.Children.Add(c.Dock(Dock.Left));
            _sendText = new TextBlock { Text = "보내기" };
            _sendBtn = new Button { Content = _sendText, Height = 34, Padding = new Thickness(18, 0), MinWidth = 92 }.Cls("primary").Dock(Dock.Right);
            _sendBtn.Click += (s, e) => Send();
            bar.Children.Add(_sendBtn);
            _hint = Ui.Text("", 11.5, fg: "Faint", trim: true);
            _hint.HorizontalAlignment = HorizontalAlignment.Right; _hint.Margin = new Thickness(0, 0, 12, 0);
            bar.Children.Add(_hint);
            var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 10, 12, 12), Child = bar }.Row(5);
            footer.Res(Border.BorderBrushProperty, "Line");
            root.Children.Add(footer);

            var overlay = Attachments.DropOverlay("AccentSoft");
            Grid.SetRowSpan(overlay, 6);
            root.Children.Add(overlay);
            _files.EnableDrop(root, overlay);
            return root;
        }

        static Control WithMargin(Control c, double l, double r) { c.Margin = new Thickness(l, 0, r, 0); return c; }

        static Button Tool(string glyph, string tip) => new Button { Content = glyph, FontSize = 15 }.Cls("icon").Tip(tip);

        public void Attach(string path) => _files.Add(new[] { path });

        void Schedule_Click()
        {
            Schedule.Pick(_scheduleBtn, this, at =>
            {
                _at = at;
                _scheduleBar.IsVisible = true;
                _scheduleText.Text = "예약 · " + at.ToString("M월 d일 (ddd) tt h:mm") + " 에 보냅니다";
                if (!_sending) _sendText.Text = "예약";
            });
        }

        void ClearSchedule()
        {
            _at = null;
            _scheduleBar.IsVisible = false;
            if (!_sending) _sendText.Text = "보내기";
        }

        void UpdateSendState()
        {
            bool hasContent = (_body.Text ?? "").Trim().Length > 0 || _files.Items.Count > 0;
            _sendBtn.IsEnabled = !_sending && hasContent && _to.Count > 0 && _files.Zipping == 0;
            var key = Native.IsMac ? "⌘Enter" : "Ctrl+Enter";
            if (_to.Count == 0) _hint.Text = "받는 사람을 추가하세요";
            else if (!hasContent) _hint.Text = key;
            else _hint.Text = _to.Count > 1 ? $"{_to.Count}명 · {key}" : key;
        }

        void AddTo_Click()
        {
            var candidates = _core.Peers.Where(p => !_to.Contains(p) && !_core.IsBlocked(p.Id))
                .OrderBy(p => p.SortKey).ThenBy(p => p.Group).ThenBy(p => p.DisplayName).ToList();
            var items = new List<Control>();
            if (candidates.Count == 0) items.Add(Menus.Item("추가할 동료가 없습니다", enabled: false));
            var online = candidates.Where(p => p.IsOnline).ToList();
            bool any = false;
            if (online.Count > 1)
            {
                items.Add(Menus.Item($"접속 중인 동료 모두 ({online.Count})", () => { foreach (var p in online) if (!_to.Contains(p)) _to.Add(p); }, bold: true));
                any = true;
            }
            foreach (var g in _core.Settings.Groups)
            {
                var members = _core.GroupMembers(g).Where(p => !_to.Contains(p)).ToList();
                if (members.Count == 0) continue;
                items.Add(Menus.Item($"그룹 · {g.Name} ({members.Count})", () => { foreach (var p in members) if (!_to.Contains(p)) _to.Add(p); }, bold: true));
                any = true;
            }
            if (any) items.Add(Menus.Sep());
            foreach (var p in candidates.Take(60))
            {
                var peer = p;
                var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = p.DisplayName } } };
                if (!string.IsNullOrWhiteSpace(p.Dept)) head.Children.Add(Ui.Text("  " + p.Dept, 13, fg: "Faint"));
                var mi = new MenuItem { Header = head, Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = p.StatusBrush } };
                mi.Click += (o, ev) => { if (!_to.Contains(peer)) _to.Add(peer); };
                items.Add(mi);
            }
            var f = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var i in items) f.Items.Add(i);
            f.ShowAt(_addBtn);
        }

        void Body_KeyDown(object sender, KeyEventArgs e)
        {
            if (Markdown.HandleKey(_body, e, this)) { e.Handled = true; return; }
            if ((e.Key == Key.Enter || e.Key == Key.Return) && Keys.Primary(e.KeyModifiers))
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(() => { if (_sendBtn.IsEnabled) Send(); }, DispatcherPriority.Background);
            }
        }

        async void Send()
        {
            if (_sending) return;
            _sending = true;
            UpdateSendState();
            _sendText.Text = "보내는 중…";

            var text = (_body.Text ?? "").TrimEnd();
            if (_quote != null)
            {
                var lines = _quote.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(3)
                                  .Select(l => "> " + (l.Length > 80 ? l.Substring(0, 80) + "…" : l));
                text = string.Join("\n", lines) + "\n\n" + text;
            }

            var targets = _to.ToList();
            var m = await _core.Send(targets, text, _files.Items.Select(f => f.Path).ToList(), _urgent.IsChecked == true, _broadcast, at: _at);

            if (m.Failed)
            {
                _sending = false;
                _sendText.Text = "다시 보내기";
                UpdateSendState();
                _core.DeleteHistory(new[] { m });
                await Dialog.Alert(this, "전달하지 못했습니다", "상대 PC에 연결할 수 없습니다. 상대가 프로그램을 종료했거나 방화벽이 막고 있을 수 있습니다.");
                return;
            }
            _confirmedClose = true;
            Close();
            if (m.PendingTo.Count > 0 && !m.Scheduled)
                _core.Notify("대기열에 넣었습니다", m.PendingTo.Count == targets.Count && targets.Count == 1
                    ? $"{targets[0].DisplayName}님이 지금 오프라인이라 대기열에 넣었습니다. 접속하면 자동으로 전달됩니다."
                    : $"{m.PendingTo.Count}명은 오프라인이라 접속하면 자동으로 전달됩니다.");
            if (m.FailedTo.Count > 0)
                _ = Dialog.Alert(null, "일부에게 전달하지 못했습니다", string.Join(", ", m.FailedTo) + "\n\n접속이 끊겼거나 방화벽이 막고 있을 수 있습니다.");
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_confirmedClose && !_sending && e.CloseReason == WindowCloseReason.WindowClosing
                && ((_body.Text ?? "").Trim().Length > 0 || _files.Items.Count > 0))
            {
                e.Cancel = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (await Dialog.Confirm(this, "작성 중인 쪽지를 닫을까요?", "입력한 내용은 저장되지 않습니다.", "닫기"))
                    { _confirmedClose = true; Close(); }
                });
            }
            base.OnClosing(e);
        }
    }
}
