using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;

namespace BluePhone
{
    /// <summary>한 동료와의 1:1 대화를 말풍선으로 보여 주는 창</summary>
    public sealed class ChatWindow : Window
    {
        readonly Core _core;
        public Peer Peer { get; }
        readonly ObservableCollection<ChatMessage> _rows = new ObservableCollection<ChatMessage>();
        readonly Attachments _files;
        DateTime? _at;
        bool _sending;

        Avatar _hAvatar;
        TextBlock _hName, _hStatus, _empty, _hint, _sendText, _scheduleText;
        StackPanel _hSecure;
        ListBox _list;
        Button _moreBtn, _sendBtn, _lookBtn, _pinBtn, _emojiBtn, _formatBtn, _scheduleBtn;
        Border _scheduleBar, _overlay;
        TextBox _input;
        ToggleButton _urgent;

        const int PageSize = 300;
        long _since;
        int _shown = PageSize;

        public ChatWindow(Core core, Peer peer)
        {
            _core = core;
            Peer = peer;
            _files = new Attachments(this);
            Width = 420; Height = 600; MinWidth = 340; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Cls("app");
            Icons.Apply(this);
            Content = Build();

            _since = SinceFor(PageSize);
            Rebuild();
            core.History.CollectionChanged += History_Changed;
            peer.PropertyChanged += Peer_PropertyChanged;
            Closed += (s, e) => { peer.PropertyChanged -= Peer_PropertyChanged; core.History.CollectionChanged -= History_Changed; };
            RefreshHeader();
            RefreshMore();
            UpdateSendState();

            _input.TextChanged += (s, e) => UpdateSendState();
            _files.Changed += UpdateSendState;
            Activated += (s, e) =>
            {
                _core.LastChat = this;
                foreach (var m in _core.History.Where(x => x.Chat && !x.Outgoing && !x.Read && x.PeerId == Peer.Id).ToList()) _core.MarkRead(m);
            };
            Opened += (s, e) => { ScrollToEnd(); _input.Focus(); };
            AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Escape && string.IsNullOrEmpty(_input.Text)) { Close(); e.Handled = true; }
                else if (Keys.Primary(e.KeyModifiers) && (e.Key == Key.OemPlus || e.Key == Key.Add)) { Zoom(+1); e.Handled = true; }
                else if (Keys.Primary(e.KeyModifiers) && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { Zoom(-1); e.Handled = true; }
                else if (Keys.Primary(e.KeyModifiers) && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { _core.SetChatLook(size: ChatLook.DefaultSize); e.Handled = true; }
                else if (Keys.Primary(e.KeyModifiers) && e.Key == Key.W) { Close(); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
            AddHandler(PointerWheelChangedEvent, (s, e) =>
            {
                if (!Keys.HasPrimary(e.KeyModifiers)) return;
                Zoom(e.Delta.Y > 0 ? +1 : -1);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
        }

        Control Build()
        {
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            root.Res(Panel.BackgroundProperty, "Ct.Panel");

            // 머리글
            var hg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 58 };
            _hAvatar = new Avatar { Size = 34, Margin = new Thickness(16, 0, 10, 0) };
            hg.Children.Add(_hAvatar);
            _hName = Ui.Text("", 14, FontWeight.SemiBold, "Ct.Text", trim: true);
            _hStatus = Ui.Text("", 12, fg: "Ct.Sub");
            var lockT = Ui.Text("🔒", 10);
            _hSecure = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), IsVisible = false, Children = { lockT, WithLeft(Ui.Text("암호화", 12, fg: "Ct.Sub"), 3) } };
            ToolTip.SetTip(_hSecure, "이 동료와의 쪽지·파일은 암호화되어 전송됩니다 (ECDH P-256 + AES-256)");
            hg.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _hName, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0), Children = { _hStatus, _hSecure } } } }.Col(1));
            _lookBtn = HeadBtn("🎨", "대화 창 꾸미기 — 테마 · 글꼴 · 글자 크기");
            _lookBtn.Click += (s, e) => Look_Click();
            _pinBtn = HeadBtn("📌", "항상 위에");
            _pinBtn.Opacity = 0.5;
            _pinBtn.Click += (s, e) => { Topmost = !Topmost; _pinBtn.Opacity = Topmost ? 1 : 0.5; ToolTip.SetTip(_pinBtn, Topmost ? "항상 위에 (켜짐)" : "항상 위에"); };
            hg.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Spacing = 2, Children = { _lookBtn, _pinBtn } }.Col(2));
            var head = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = hg };
            head.Res(Border.BorderBrushProperty, "Ct.Line").Res(Border.BackgroundProperty, "Ct.Panel");
            root.Children.Add(head);

            // 말풍선 목록
            _list = new ListBox { ItemsSource = _rows, Padding = new Thickness(0, 10, 0, 10), SelectionMode = SelectionMode.Single }.Row(1);
            _list.Res(ListBox.BackgroundProperty, "Ct.Bg");
            _list.Cls("bubbles");
            _list.ItemTemplate = new FuncDataTemplate<ChatMessage>((m, _) => m == null ? new Panel() : Bubble(m));
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                    new Avalonia.Styling.Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    new Avalonia.Styling.Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent),
                    new Avalonia.Styling.Setter(ListBoxItem.FocusableProperty, false),
                },
            });
            foreach (var st in new[] { ":pointerover", ":selected", ":pressed", ":selected:pointerover", ":selected:focus" })
            {
                var sel = st;
                _list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>().Class(sel).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                {
                    Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
                });
            }
            root.Children.Add(_list);
            _moreBtn = new Button { Content = "이전 대화 더 보기", Height = 28, Padding = new Thickness(12, 0), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false }.Cls("outline").Row(1);
            _moreBtn.Click += (s, e) => More_Click();
            root.Children.Add(_moreBtn);
            _empty = Ui.Text("아직 주고받은 대화가 없어요", 12, fg: "Ct.Sub").Row(1);
            _empty.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(_empty);

            // 입력
            var comp = new StackPanel();
            var fileList = _files.BuildList("Ct.InputLine");
            fileList.Margin = new Thickness(0, 0, 0, 6);
            comp.Children.Add(fileList);
            _scheduleText = Ui.Text("", 12.5, fg: "Accent");
            var clearSch = new Button { Content = "✕", FontSize = 9, Width = 22, Height = 22 }.Cls("icon").Dock(Dock.Right);
            clearSch.Click += (s, e) => ClearSchedule();
            _scheduleBar = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 4, 5), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = new DockPanel { Children = { clearSch, _scheduleText } } };
            _scheduleBar.Res(Border.BackgroundProperty, "AccentSoft");
            comp.Children.Add(_scheduleBar);
            _input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40, MaxHeight = 160, Padding = new Thickness(10, 9), VerticalContentAlignment = VerticalAlignment.Top, Watermark = "메시지 입력" }.Cls("field");
            _input.Res(TextBox.BackgroundProperty, "Ct.InputBg").Res(TextBox.ForegroundProperty, "Ct.Text").Res(TextBox.BorderBrushProperty, "Ct.InputLine")
                  .Res(TextBox.FontFamilyProperty, "Ct.Font").Res(TextBox.FontSizeProperty, "Ct.Size");
            _input.AddHandler(KeyDownEvent, Input_KeyDown, RoutingStrategies.Tunnel);
            _files.EnablePaste(_input);
            comp.Children.Add(_input);

            var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            _emojiBtn = ToolBtn("😊", "이모지 · 이모티콘"); _emojiBtn.Click += (s, e) => Emoji.ShowPicker(_emojiBtn, _input);
            _formatBtn = ToolBtn("Aa", $"서식 (마크다운) — 굵게 {Native.Mod}B · 기울임 {Native.Mod}I · 목록 · 인용 · 링크"); _formatBtn.FontSize = 13; _formatBtn.Click += (s, e) => Markdown.ShowFormatMenu(_formatBtn, _input, this);
            var codeBtn = ToolBtn("</>", $"코드 블록 넣기 ({(Native.IsMac ? "⌘⇧C" : "Ctrl+Shift+C")})"); codeBtn.FontSize = 11; codeBtn.Click += async (s, e) => await Markdown.InsertCode(_input, this);
            var attachBtn = ToolBtn("📎", "파일 · 폴더 첨부"); attachBtn.Click += async (s, e) => await _files.Pick();
            var capBtn = ToolBtn("✂", _core.Settings.CaptureHotkey && Native.IsMac ? "화면 캡처 (⌃⌥S)" : "화면 캡처"); capBtn.Click += (s, e) => _core.StartCapture(this);
            _scheduleBtn = ToolBtn("⏰", "예약 보내기"); _scheduleBtn.Click += (s, e) => Schedule_Click();
            _urgent = new ToggleButton { Content = Ui.Text("긴급", 12.5), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Cls("chip");
            foreach (var c in new Control[] { _emojiBtn, _formatBtn, codeBtn, attachBtn, capBtn, _scheduleBtn, _urgent }) bar.Children.Add(c.Dock(Dock.Left));
            _sendText = new TextBlock { Text = "보내기" };
            _sendBtn = new Button { Content = _sendText, Height = 32, Padding = new Thickness(16, 0) }.Cls("primary").Dock(Dock.Right);
            _sendBtn.Click += (s, e) => Send();
            bar.Children.Add(_sendBtn);
            _hint = Ui.Text("", 11.5, fg: "Ct.Meta", trim: true);
            _hint.HorizontalAlignment = HorizontalAlignment.Right; _hint.Margin = new Thickness(0, 0, 10, 0);
            bar.Children.Add(_hint);
            comp.Children.Add(bar);
            var composer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 10, 12, 12), Child = comp }.Row(2);
            composer.Res(Border.BorderBrushProperty, "Ct.Line").Res(Border.BackgroundProperty, "Ct.Panel");
            root.Children.Add(composer);

            _overlay = Attachments.DropOverlay();
            Grid.SetRowSpan(_overlay, 3);
            root.Children.Add(_overlay);
            _files.EnableDrop(root, _overlay);
            return root;
        }

        static Control WithLeft(Control c, double l) { c.Margin = new Thickness(l, 0, 0, 0); return c; }

        static Button HeadBtn(string glyph, string tip)
        {
            var b = new Button { Content = glyph, FontSize = 14 }.Cls("icon").Tip(tip);
            return b;
        }

        static Button ToolBtn(string glyph, string tip)
        {
            var b = new Button { Content = glyph, FontSize = 15 }.Cls("icon").Tip(tip);
            b.Res(Button.ForegroundProperty, "Ct.Sub");
            return b;
        }

        // ───────────────────────── 말풍선 ─────────────────────────

        Control Bubble(ChatMessage m)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(14, 3, 14, 3), Background = Brushes.Transparent };
            if (!m.Outgoing)
            {
                g.Children.Add(new Avatar { Size = 30, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) }
                    .Bind(Avatar.FillProperty, "AvatarBrush").Bind(Avatar.InitialProperty, "Initial").Bind(Avatar.ImageProperty, "AvatarImage"));
            }
            var align = m.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var col = new StackPanel { HorizontalAlignment = align }.Col(1);

            var tags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 3), HorizontalAlignment = align, Spacing = 4 };
            if (m.Urgent) tags.Children.Add(Tag("긴급", "DangerSoft", "Danger"));
            if (m.IsNote)
            {
                var t = Tag("쪽지", "Hover", "Muted");
                ToolTip.SetTip(t, "쪽지 창으로 주고받은 쪽지");
                tags.Children.Add(t);
            }
            if (m.Outgoing && m.To.Count > 1) tags.Children.Add(Ui.Text("여러 명에게 · " + m.Who, 11, fg: "Faint"));
            tags.IsVisible = tags.Children.Count > 0;
            col.Children.Add(tags);

            var body = new StackPanel();
            var rt = new RichText { Text = m.Text };
            rt.Res(TemplatedControl.FontSizeProperty, "Ct.Size").Res(TemplatedControl.FontFamilyProperty, "Ct.Font");
            body.Children.Add(rt);
            foreach (var f in m.Files)
                body.Children.Add(new FileView(f, m.Outgoing, this, ff => Download(m, ff)));
            var bubble = new Border
            {
                CornerRadius = m.Outgoing ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4),
                Padding = new Thickness(12, 8), MaxWidth = m.HasMarkup ? 340 : 300, HorizontalAlignment = align, Child = body,
            };
            bubble.Res(Border.BackgroundProperty, m.Outgoing ? "Ct.Out" : "Ct.In");
            rt.Res(TemplatedControl.ForegroundProperty, m.Outgoing ? "Ct.OutText" : "Ct.InText");
            if (m.Outgoing)
            {
                // 내가 보낸 말풍선 안의 링크 · 코드 블록은 글자색과 같게
                bubble.Resources["LinkFg"] = Ui.Brush("Ct.OutText");
                bubble.Bind(Border.OpacityProperty, new Avalonia.Data.Binding("IsPending") { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, double>(p => p ? 0.6 : 1) });
            }
            bubble.ContextMenu = BubbleMenu(m);
            col.Children.Add(bubble);

            var time = Ui.Text("", 11, fg: "Ct.Meta").Bind(TextBlock.TextProperty, "TimeText");
            var meta = Ui.Text("", 11).Bind(TextBlock.TextProperty, "Meta", Conv.Prefix).Bind(IsVisibleProperty, "Meta", Ui.HasText)
                .Bind(TextBlock.ForegroundProperty, "IsProblem", new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(b => Ui.Brush(b ? "Danger" : "Ct.Meta")));
            col.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 0), HorizontalAlignment = align, Children = { time, meta } });
            g.Children.Add(col);
            return g;
        }

        static Border Tag(string text, string bg, string fg)
        {
            var b = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), Child = Ui.Text(text, 11, FontWeight.SemiBold, fg) };
            return b.Res(Border.BackgroundProperty, bg);
        }

        ContextMenu BubbleMenu(ChatMessage m)
        {
            var cm = new ContextMenu();
            cm.Opening += (s, e) =>
            {
                cm.ItemsSource = new Control[]
                {
                    Menus.Item("복사", () =>
                    {
                        var part = RichText.SelectionIn(_list);
                        ClipKit.SetText(this, !string.IsNullOrEmpty(part) ? part : m.Text ?? "");
                    }, gesture: Keys.Gesture("C")),
                    Menus.Item("다시 알림 (30분 후)", () => _core.RemindLater(m, DateTime.Now.AddMinutes(30))),
                    Menus.Item("예약 취소", () => { if (m.Scheduled) _core.CancelScheduled(m); }, enabled: m.Scheduled),
                    Menus.Sep(),
                    Menus.Item("삭제", async () =>
                    {
                        if (await Dialog.Confirm(this, "쪽지 삭제", "이 쪽지를 내 기록에서 삭제할까요?", "삭제", danger: true))
                            _core.DeleteHistory(new[] { m });
                    }),
                };
            };
            return cm;
        }

        async Task Download(ChatMessage m, FileItem f)
        {
            var dir = Store.DownloadDir(_core.Settings);
            string dest = Store.UniquePath(dir, f.Name);
            if (f.State == "실패" || _shiftDown)
            {
                if (_shiftDown)
                {
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = f.Name, Title = "저장할 위치" });
                    var p = file?.TryGetLocalPath();
                    if (p == null) return;
                    dest = p;
                }
            }
            await _core.DownloadFile(m, f, dest);
        }

        bool _shiftDown;
        protected override void OnKeyDown(KeyEventArgs e) { _shiftDown = e.KeyModifiers.HasFlag(KeyModifiers.Shift); base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { _shiftDown = e.KeyModifiers.HasFlag(KeyModifiers.Shift); base.OnKeyUp(e); }

        // ───────────────────────── 목록 ─────────────────────────

        bool Visible(ChatMessage m) => m.Chat && m.Ts >= _since && m.Involves(Peer.Id);

        void Rebuild()
        {
            var list = _core.History.Where(Visible).OrderBy(m => m.Ts).ToList();
            ListSync.Sync(_rows, list);
            _empty.IsVisible = _rows.Count == 0;
        }

        void History_Changed(object sender, NotifyCollectionChangedEventArgs e)
        {
            bool added = e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && e.NewItems.Cast<ChatMessage>().Any(Visible);
            Rebuild();
            if (added) ScrollToEnd();
        }

        void ScrollToEnd()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_rows.Count > 0) _list.ScrollIntoView(_rows.Count - 1);
                Dispatcher.UIThread.Post(() => { if (_rows.Count > 0) _list.ScrollIntoView(_rows.Count - 1); }, DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        }

        long SinceFor(int n) => _core.History.Where(m => m.Chat && m.Involves(Peer.Id)).Select(m => m.Ts).OrderByDescending(t => t).Skip(n - 1).FirstOrDefault();

        void RefreshMore()
        {
            bool more = _since > 0 && _core.History.Any(m => m.Chat && m.Ts < _since && m.Involves(Peer.Id));
            _moreBtn.IsVisible = more;
        }

        void More_Click()
        {
            var first = _rows.FirstOrDefault();
            _shown += PageSize;
            _since = SinceFor(_shown);
            Rebuild();
            RefreshMore();
            if (first != null) Dispatcher.UIThread.Post(() => _list.ScrollIntoView(first), DispatcherPriority.Background);
        }

        void Peer_PropertyChanged(object sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(RefreshHeader);

        void RefreshHeader()
        {
            Title = Peer.DisplayName + " — 대화";
            _hName.Text = Peer.DisplayName;
            _hAvatar.Initial = Peer.Initial;
            _hAvatar.Fill = Peer.AvatarBrush;
            _hAvatar.Image = Peer.AvatarImage;
            _hAvatar.Status = Peer.StatusBrush;
            _hStatus.Text = string.Join(" · ", new[] { Peer.StatusText, Peer.Dept }.Where(x => !string.IsNullOrWhiteSpace(x)));
            _hSecure.IsVisible = Peer.Secure;
            UpdateSendState();
        }

        void UpdateSendState()
        {
            bool has = (_input.Text ?? "").Trim().Length > 0 || _files.Items.Count > 0;
            _sendBtn.IsEnabled = has && !_sending && _files.Zipping == 0;
            _hint.Text = _core.Settings.EnterToSend ? "Enter 보내기" : $"{(Native.IsMac ? "⌘" : "Ctrl+")}Enter 보내기";
            ToolTip.SetTip(_hint, _core.Settings.EnterToSend ? $"Shift+Enter 줄바꿈 · 설정에서 {(Native.IsMac ? "⌘" : "Ctrl+")}Enter 로 바꿀 수 있습니다" : null);
            if (!Peer.IsOnline) _hint.Text = "오프라인 · 접속하면 전달";
        }

        // ───────────────────────── 입력 · 보내기 ─────────────────────────

        void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (Markdown.HandleKey(_input, e, this)) { e.Handled = true; return; }
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            var mods = e.KeyModifiers;
            bool send = _core.Settings.EnterToSend ? mods == KeyModifiers.None : Keys.Primary(mods);
            if (send)
            {
                e.Handled = true;
                // 한글 입력 중이면 조합이 끝난 글자가 먼저 들어오도록 한 박자 늦게 보냄
                Dispatcher.UIThread.Post(() => { if (_sendBtn.IsEnabled) Send(); }, DispatcherPriority.Background);
            }
            else if (_core.Settings.EnterToSend && mods == KeyModifiers.Shift)
            {
                e.Handled = true;
                Markdown.InsertAtCaret(_input, "\n");
            }
        }

        async void Send()
        {
            if (_sending) return;
            UpdateSendState();
            if (!_sendBtn.IsEnabled) return;
            _sending = true;
            var text = (_input.Text ?? "").TrimEnd();
            var files = _files.Items.Select(f => f.Path).ToList();
            var at = _at;
            _input.Text = "";
            _files.Items.Clear();
            ClearSchedule();
            bool urgent = _urgent.IsChecked == true;
            _urgent.IsChecked = false;
            try { await _core.Send(new[] { Peer }, text, files, urgent, false, at: at, chat: true); }
            finally { _sending = false; UpdateSendState(); _input.Focus(); }
            ScrollToEnd();
        }

        public void Attach(string path) => _files.Add(new[] { path });

        void Schedule_Click()
        {
            Schedule.Pick(_scheduleBtn, this, at =>
            {
                _at = at;
                _scheduleBar.IsVisible = true;
                _scheduleText.Text = "예약 · " + at.ToString("M월 d일 (ddd) tt h:mm") + " 에 보냅니다";
                _sendText.Text = "예약";
            });
        }

        void ClearSchedule()
        {
            _at = null;
            _scheduleBar.IsVisible = false;
            _sendText.Text = "보내기";
        }

        // ───────────────────────── 꾸미기 ─────────────────────────

        public void ApplyLook() { }

        void Zoom(int step)
        {
            var cur = _core.Settings.ChatFontSize <= 0 ? ChatLook.DefaultSize : _core.Settings.ChatFontSize;
            _core.SetChatLook(size: Math.Max(ChatLook.MinSize, Math.Min(ChatLook.MaxSize, Math.Round(cur) + step)));
        }

        void Look_Click()
        {
            var s = _core.Settings;
            var theme = new MenuItem { Header = "테마" };
            foreach (var t in ChatLook.Themes)
            {
                var tt = t;
                var sw = new Panel { Width = 16, Height = 16 };
                sw.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Fill = new SolidColorBrush(t.Bg), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), StrokeThickness = 1 });
                sw.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(t.Out) });
                bool on = (s.ChatTheme ?? "light") == t.Id;
                var mi = new MenuItem { Header = on ? t.Name + "  ✓" : t.Name, Icon = sw, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal };
                mi.Click += (o, ev) => _core.SetChatLook(theme: tt.Id);
                theme.Items.Add(mi);
            }
            var font = new MenuItem { Header = "글꼴" };
            foreach (var f in ChatLook.Fonts)
            {
                var ff = f;
                var mi = Menus.Item(f.Name, () => _core.SetChatLook(font: ff.Family ?? ""), check: (s.ChatFont ?? "") == (f.Family ?? ""));
                if (f.Family != null) mi.FontFamily = new FontFamily(f.Family);
                font.Items.Add(mi);
            }
            var size = new MenuItem { Header = "글자 크기" };
            double cur = s.ChatFontSize <= 0 ? ChatLook.DefaultSize : s.ChatFontSize;
            foreach (var z in ChatLook.Sizes)
            {
                var zz = z;
                size.Items.Add(Menus.Item(z.Name, () => _core.SetChatLook(size: zz.Size), check: Math.Abs(cur - z.Size) < 0.01, gesture: z.Size == ChatLook.DefaultSize ? Keys.Gesture("D0") : null));
            }
            size.Items.Add(Menus.Sep());
            size.Items.Add(Menus.Item(Native.IsMac ? "⌘ + / − , ⌘ + 스크롤로도 조절" : "Ctrl + 마우스 휠, Ctrl + / − 로도 조절", enabled: false));
            Menus.Show(_lookBtn, new Control[]
            {
                theme, font, size, Menus.Sep(),
                Menus.Item("기본값으로", () => _core.SetChatLook(theme: "light", font: "", size: ChatLook.DefaultSize)),
            }, PlacementMode.BottomEdgeAlignedRight);
        }
    }
}
