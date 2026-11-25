using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace BluePhone
{
    /// <summary>받은 쪽지 · 대화 알림 · 다시 알림 · 프로그램 안내 창 (화면 오른쪽 아래에 쌓임)</summary>
    public sealed class MessageWindow : Window
    {
        readonly Core _core;
        public ChatMessage Message { get; }
        public int Slot { get; set; }
        public bool UserMoved { get; set; }
        bool _reminder, _chatAlert, _replying, _sending, _md, _opened;
        string _lastSaved;
        Action _notice;

        Border _urgentLine, _tag1, _veil, _replyPane, _autoBar;
        TextBlock _fromName, _tag1Text, _subLine, _veilText, _replyText, _footnote;
        Avatar _avatar;
        Control _bodyHost;
        StackPanel _fileList;
        TextBox _replyBox;
        Button _chatBtn, _remindBtn, _replyBtn;
        ScaleTransform _autoScale;

        public bool IsChatAlert
        {
            get => _chatAlert;
            set
            {
                _chatAlert = value;
                if (!value) return;
                if (!Message.Urgent) SetTag(Message.Room != null ? "그룹 대화" : "1:1 대화", "Accent", "AccentSoft");
                _replyText.Text = "대화 열기";
                _chatBtn.IsVisible = false;
                if (_privacy < 2) Title = (Message.FromName ?? "") + (Message.Room != null ? "님 · " + (_core.RoomOf(Message.Room)?.Title ?? "그룹 대화") : "님의 대화");
            }
        }

        public bool IsReminder
        {
            get => _reminder;
            set { _reminder = value; if (value && !Message.Urgent) SetTag("다시 알림", "Accent", "AccentSoft"); }
        }

        public MessageWindow(Core core, ChatMessage m)
        {
            _core = core;
            Message = m;
            Width = 384; MinWidth = 320; MinHeight = 160;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            this.Cls("app");
            Icons.Apply(this);
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = -1;
            SystemDecorations = SystemDecorations.BorderOnly;
            Content = Build();

            _md = true;
            RefreshHeader();
            core.PeerRenamed += OnPeerRenamed;
            Closed += (s, e) => core.PeerRenamed -= OnPeerRenamed;

            if (m.Outgoing)
            {
                _subLine.Text = "보낸 쪽지 · " + m.FullTime;
                _footnote.Text = m.Meta;
                _replyText.Text = "다시 쓰기";
            }
            else
            {
                _subLine.Text = string.Join(" · ", new[] { m.PeerDept, m.FullTime }.Where(x => !string.IsNullOrWhiteSpace(x)));
                ToolTip.SetTip(_subLine, m.PeerIp);
                if (m.To.Count > 1 && !m.Broadcast && m.Room == null) _footnote.Text = $"나 외 {m.To.Count - 1}명에게 보낸 쪽지";
            }

            if (m.Urgent) { _urgentLine.IsVisible = true; SetTag("긴급", "Danger", "DangerSoft"); }
            else if (m.Broadcast) SetTag("전체 공지", "Accent", "AccentSoft");
            else if (m.Auto) SetTag("자동응답", "Muted", "Hover");

            if (m.Broadcast || (m.Outgoing && m.ToIds.Count != 1 && m.Room == null) || (m.Room == null ? _core.PeerOf(m) == null : _core.RoomOf(m.Room) == null)) _chatBtn.IsVisible = false;

            foreach (var f in m.Files) _fileList.Children.Add(new FileView(f, m.Outgoing, this, ff => Download(ff), "Canvas"));
            _fileList.IsVisible = m.Files.Count > 0;
            if (m.Files.Count > 0 && string.IsNullOrWhiteSpace(m.Text)) _bodyHost.IsVisible = false;

            Activated += (s, e) => MarkRead();
            AddHandler(PointerPressedEvent, (s, e) =>
            {
                if (_veiled && !InButton(e.Source)) Reveal();
                MarkRead();
                StopAutoClose();
            }, RoutingStrategies.Tunnel);
            Opened += (s, e) => { _opened = true; };
            PropertyChanged += (s, e) =>
            {
                if (e.Property == BoundsProperty && _opened) _core.LayoutPopups();
            };
            PositionChanged += (s, e) => { if (!_core.InLayout && _opened && _dragging) UserMoved = true; };
            AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (_replying && !string.IsNullOrEmpty(_replyBox.Text)) { _replyBox.Text = ""; return; }
                    Close();
                }
                else if (e.Key == Key.R && Keys.Primary(e.KeyModifiers) && !_replying) { e.Handled = true; Reply_Click(); }
                else if (e.Key == Key.W && Keys.Primary(e.KeyModifiers)) { e.Handled = true; Close(); }
            }, RoutingStrategies.Tunnel);
        }

        bool _dragging;

        Control Build()
        {
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
            root.Res(Panel.BackgroundProperty, "Surface");
            _urgentLine = new Border { Height = 3, IsVisible = false };
            _urgentLine.Res(Border.BackgroundProperty, "Danger");
            root.Children.Add(_urgentLine);

            // 머리글 (끌어서 옮기기)
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 64, Background = Brushes.Transparent }.Row(1);
            _avatar = new Avatar { Size = 38, Margin = new Thickness(18, 0, 12, 0), Fill = Message.AvatarBrush };
            head.Children.Add(_avatar);
            _fromName = Ui.Text("", 14, FontWeight.SemiBold, trim: true);
            _fromName.MaxWidth = 190;
            _tag1Text = Ui.Text("", 11, FontWeight.SemiBold);
            _tag1 = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Child = _tag1Text };
            _subLine = Ui.Text("", 12, fg: "Muted", trim: true);
            _subLine.Margin = new Thickness(0, 2, 0, 0);
            head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { _fromName, _tag1 } }, _subLine } }.Col(1));
            var close = new Button { Content = "✕", FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) }.Cls("icon").Tip("닫기 (Esc)").Col(2);
            close.Click += (s, e) => { MarkRead(); Close(); };
            head.Children.Add(close);
            head.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || InButton(e.Source)) return;
                _dragging = true;
                try { BeginMoveDrag(e); } catch { }
                _dragging = false;
                if (!_core.InLayout) UserMoved = true;
            };
            root.Children.Add(head);

            // 본문
            var m = Message;
            Control content = Markdown.HasMarkup(m.Text) ? Markdown.Render(m.Text) : new RichText { Text = m.Text, FontSize = 14 };
            if (content is Panel) content.SetValue(TemplatedControl.FontSizeProperty, 14.0);
            var sv = new ScrollViewer { Content = new Border { Child = content, Margin = new Thickness(0, 0, 6, 0) }, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _bodyHost = new Border { Child = sv, Margin = new Thickness(18, 2, 12, 16), MinHeight = 30 };
            var veilBtn = new Button { Content = "내용 보기", Height = 28, Padding = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center }.Cls("outline").Dock(Dock.Right);
            veilBtn.Click += (s, e) => { Reveal(); MarkRead(); StopAutoClose(); };
            _veilText = Ui.Text("내용이 가려져 있습니다", 13, fg: "Ink2");
            _veilText.Margin = new Thickness(8, 0, 0, 0);
            _veil = new Border { Margin = new Thickness(18, 2, 18, 16), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12), Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
                Child = new DockPanel { Children = { veilBtn, new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Text("🔒", 13), _veilText } } } } };
            _veil.Res(Border.BackgroundProperty, "Canvas");
            ToolTip.SetTip(_veil, "누르면 내용을 표시합니다");
            root.Children.Add(new Panel { Children = { _bodyHost, _veil } }.Row(2));

            // 첨부
            _fileList = new StackPanel { Margin = new Thickness(18, 0, 18, 8) }.Row(3);
            root.Children.Add(_fileList);

            // 빠른 답장
            _replyBox = new TextBox { Watermark = $"답장 입력 · {(Native.IsMac ? "⌘" : "Ctrl+")}Enter 로 보내기", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 120, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(9, 7) }.Cls("field");
            _replyBox.AddHandler(KeyDownEvent, (s, e) =>
            {
                if ((e.Key == Key.Enter || e.Key == Key.Return) && Keys.Primary(e.KeyModifiers))
                {
                    e.Handled = true;
                    Dispatcher.UIThread.Post(SendReply, DispatcherPriority.Background);
                }
            }, RoutingStrategies.Tunnel);
            _replyPane = new Border { IsVisible = false, Margin = new Thickness(18, 0, 18, 10), Child = _replyBox }.Row(4);
            root.Children.Add(_replyPane);

            // 아래
            var bar = new DockPanel();
            var copy = new Button { Content = "⧉", FontSize = 15 }.Cls("icon").Tip("내용 복사").Dock(Dock.Left);
            copy.Click += (s, e) => Copy_Click();
            _chatBtn = new Button { Content = "💬", FontSize = 14 }.Cls("icon").Tip("대화 보기 — 이 동료와 주고받은 메시지를 말풍선으로").Dock(Dock.Left);
            _chatBtn.Click += (s, e) => Chat_Click();
            _remindBtn = new Button { Content = "⏰", FontSize = 14 }.Cls("icon").Tip("다시 알림").Dock(Dock.Left);
            _remindBtn.Click += (s, e) => Remind_Click();
            bar.Children.Add(copy); bar.Children.Add(_chatBtn); bar.Children.Add(_remindBtn);
            _replyText = new TextBlock { Text = "답장" };
            _replyBtn = new Button { Content = _replyText, Height = 34, Padding = new Thickness(18, 0), MinWidth = 84 }.Cls("primary").Dock(Dock.Right);
            _replyBtn.Click += (s, e) => Reply_Click();
            var closeBtn = new Button { Content = "닫기", Height = 34, Padding = new Thickness(16, 0), Margin = new Thickness(0, 0, 8, 0) }.Cls("outline").Dock(Dock.Right);
            closeBtn.Click += (s, e) => { MarkRead(); Close(); };
            bar.Children.Add(_replyBtn); bar.Children.Add(closeBtn);
            _footnote = Ui.Text("", 12, fg: "Muted", trim: true);
            _footnote.Margin = new Thickness(8, 0);
            _footnote.PointerReleased += (s, e) => { if (_lastSaved != null) _core.OpenDownloads(_lastSaved); };
            bar.Children.Add(_footnote);
            var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 10, 12, 12), Child = bar };
            footer.Res(Border.BorderBrushProperty, "Line").Res(Border.BackgroundProperty, "Surface");
            _autoScale = new ScaleTransform(1, 1);
            _autoBar = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.55, IsVisible = false, RenderTransform = _autoScale, RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative), ZIndex = 1 };
            _autoBar.Res(Border.BackgroundProperty, "Accent");
            root.Children.Add(new Panel { Children = { footer, _autoBar } }.Row(5));

            var frame = new Border { Child = root, BorderThickness = new Thickness(Native.IsMac ? 0 : 1) };
            frame.Res(Border.BorderBrushProperty, "LineStrong");
            return frame;
        }

        static bool InButton(object source)
        {
            for (var v = source as StyledElement; v != null; v = v.Parent)
                if (v is Button || v is TextBox) return true;
            return false;
        }

        void RefreshHeader()
        {
            var m = Message;
            if (_privacy >= 2) { ApplyAnonymousHeader(); return; }
            if (m.Outgoing)
            {
                var names = m.ToNames;
                _fromName.Text = names.Count > 1 ? $"{names[0]} 외 {names.Count - 1}명" : (names.FirstOrDefault() ?? m.FromName ?? "");
                Title = "보낸 쪽지";
            }
            else
            {
                _fromName.Text = m.FromName;
                Title = (m.FromName ?? "") + "님의 쪽지";
            }
            _avatar.Initial = string.IsNullOrEmpty(_fromName.Text) ? "?" : _fromName.Text.Substring(0, 1);
            _avatar.Image = m.AvatarImage;
        }

        void OnPeerRenamed(Peer p)
        {
            if (p != null && Message.Involves(p.Id)) RefreshHeader();
        }

        void SetTag(string text, string fg, string bg)
        {
            _tag1.IsVisible = true;
            _tag1Text.Text = text;
            _tag1Text.Fg(fg);
            _tag1.Res(Border.BackgroundProperty, bg);
        }

        void MarkRead()
        {
            if (_veiled) return;
            if (!Message.Outgoing && !Message.Read) _core.MarkRead(Message, fromPopup: true);
        }

        // ───────────── 자동 닫기 ─────────────

        DispatcherTimer _autoTimer;
        DateTime _autoEnd;
        int _autoSecs;

        public void StartAutoClose(int seconds)
        {
            _autoSecs = seconds;
            _autoEnd = DateTime.Now.AddSeconds(seconds);
            _autoBar.IsVisible = true;
            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _autoTimer.Tick += (s, e) =>
            {
                if (Busy)
                {
                    _autoEnd = DateTime.Now.AddSeconds(_autoSecs);
                    _autoScale.ScaleX = 1;
                    return;
                }
                var left = (_autoEnd - DateTime.Now).TotalSeconds;
                _autoScale.ScaleX = Math.Max(0, left / _autoSecs);
                if (left <= 0) { StopAutoClose(); Close(); }
            };
            _autoTimer.Start();
            Closed += (s, e) => StopAutoClose();
        }

        bool Busy => IsPointerOver || _replying || _sending || _replyBox.IsKeyboardFocusWithin;

        void StopAutoClose()
        {
            _autoTimer?.Stop();
            _autoTimer = null;
            _autoBar.IsVisible = false;
        }

        public void CloseIfIdle()
        {
            if ((_replying && (_replyBox.Text ?? "").Trim().Length > 0) || _sending) { StopAutoClose(); return; }
            Close();
        }

        // ───────────── 개인정보 보호 ─────────────

        int _privacy;
        bool _veiled;

        public int Privacy
        {
            get => _privacy;
            set
            {
                _privacy = Message.Outgoing ? 0 : Math.Max(0, Math.Min(2, value));
                if (_privacy == 0) return;
                _veiled = true;
                _bodyHost.IsVisible = false;
                _fileList.IsVisible = false;
                _veil.IsVisible = true;
                var kind = Message.Chat ? "메시지" : "쪽지";
                int n = Message.Files.Count;
                _veilText.Text = n > 0 ? $"새 {kind}가 도착했습니다 · 첨부 {n}개" : $"새 {kind}가 도착했습니다";
                if (_privacy >= 2) ApplyAnonymousHeader();
            }
        }

        void ApplyAnonymousHeader()
        {
            var kind = Message.Chat ? "새 메시지" : "새 쪽지";
            _fromName.Text = kind;
            Title = kind;
            _avatar.Initial = "";
            _avatar.Fill = Ui.Brush("Faint");
            _avatar.Image = null;
            _subLine.Text = Message.FullTime;
            ToolTip.SetTip(_subLine, null);
            _footnote.Text = "";
        }

        void Reveal()
        {
            if (!_veiled) return;
            _veiled = false;
            int was = _privacy;
            _privacy = 0;
            _veil.IsVisible = false;
            var m = Message;
            _fileList.IsVisible = m.Files.Count > 0;
            _bodyHost.IsVisible = !(m.Files.Count > 0 && string.IsNullOrWhiteSpace(m.Text));
            if (was >= 2)
            {
                _avatar.Fill = m.AvatarBrush;
                _subLine.Text = string.Join(" · ", new[] { m.PeerDept, m.FullTime }.Where(x => !string.IsNullOrWhiteSpace(x)));
                ToolTip.SetTip(_subLine, m.PeerIp);
                if (m.To.Count > 1 && !m.Broadcast && m.Room == null) _footnote.Text = $"나 외 {m.To.Count - 1}명에게 보낸 쪽지";
                RefreshHeader();
                if (_chatAlert) Title = (m.FromName ?? "") + (m.Room != null ? "님 · " + (_core.RoomOf(m.Room)?.Title ?? "그룹 대화") : "님의 대화");
            }
        }

        // ───────────── 버튼 ─────────────

        void Chat_Click()
        {
            MarkRead();
            if (_core.OpenChatFor(Message) == null) return;
            Close();
        }

        void Remind_Click()
        {
            var now = DateTime.Now;
            var items = new System.Collections.Generic.List<Control> { Menus.Item("다시 알림", bold: true, enabled: false) };
            void Add(string label, DateTime at)
            {
                var hint = at.Date == now.Date ? at.ToString("tt h:mm") : at.ToString("M/d tt h:mm");
                items.Add(Menus.Item($"{label}    {hint}", () =>
                {
                    _core.RemindLater(Message, at);
                    MarkRead();
                    _core.Notify("다시 알려 드릴게요", at.ToString("M월 d일 tt h:mm") + " 에 이 쪽지를 다시 띄웁니다.");
                    Close();
                }));
            }
            Add("10분 뒤", now.AddMinutes(10));
            Add("30분 뒤", now.AddMinutes(30));
            Add("1시간 뒤", now.AddHours(1));
            Add("내일 오전 9시", now.Date.AddDays(1).AddHours(9));
            StopAutoClose();
            Menus.Show(_remindBtn, items, PlacementMode.TopEdgeAlignedLeft);
        }

        void Copy_Click()
        {
            Reveal();
            var part = RichText.SelectionIn(this);
            ClipKit.SetText(this, string.IsNullOrEmpty(part) ? Message.Text ?? "" : part);
            _footnote.Text = string.IsNullOrEmpty(part) ? "복사했습니다" : "선택한 부분을 복사했습니다";
        }

        public void SetNotice(string button, Action action)
        {
            _notice = action;
            _replyText.Text = button;
            _chatBtn.IsVisible = _remindBtn.IsVisible = false;
            if (action == null) _replyBtn.IsVisible = false;
        }

        void Reply_Click()
        {
            if (_notice != null) { _notice(); return; }
            Reveal();
            if (_chatAlert) { Chat_Click(); return; }
            MarkRead();
            if (Message.Outgoing)
            {
                var targets = Message.ToIds.Select(id => _core.Peers.FirstOrDefault(p => p.Id == id)).Where(p => p != null).ToArray();
                _core.Compose(targets);
                return;
            }
            if (!_replying)
            {
                _replying = true;
                _replyPane.IsVisible = true;
                _replyText.Text = "보내기";
                Native.ActivateApp();
                Activate();
                Dispatcher.UIThread.Post(() => _replyBox.Focus(), DispatcherPriority.Background);
                return;
            }
            SendReply();
        }

        async void SendReply()
        {
            var text = (_replyBox.Text ?? "").Trim();
            if (text.Length == 0 || _sending) return;
            var peer = _core.PeerOf(Message);
            var room = Message.Room == null ? null : _core.RoomOf(Message.Room);
            if (peer == null && room == null) { _footnote.Text = "보낸 사람을 찾을 수 없습니다"; return; }
            _sending = true;
            _replyBtn.IsEnabled = false;
            _replyText.Text = "보내는 중…";
            // 그룹 대화 알림의 답장은 그 대화방으로
            var m = room != null ? await _core.SendRoom(room, text, null, false) : await _core.Send(new[] { peer }, text, null, false, false);
            if (m.Failed)
            {
                _sending = false;
                _replyBtn.IsEnabled = true;
                _replyText.Text = "다시 보내기";
                _footnote.Text = "전달하지 못했습니다";
                _footnote.Fg("Danger");
                return;
            }
            _footnote.Text = "답장을 보냈습니다";
            _replyText.Text = "보냄";
            await Task.Delay(500);
            Close();
        }

        async Task Download(FileItem f)
        {
            MarkRead();
            var dest = Store.UniquePath(Store.DownloadDir(_core.Settings), f.Name);
            try
            {
                await _core.DownloadFile(Message, f, dest);
                _lastSaved = dest;
                _footnote.Text = Native.IsMac ? "저장했습니다 · Finder 에서 보기" : "저장했습니다 · 폴더 열기";
                ToolTip.SetTip(_footnote, dest);
                _footnote.Cursor = new Cursor(StandardCursorType.Hand);
                _footnote.Fg("Accent");
            }
            catch (Exception ex)
            {
                _footnote.Text = ex.Message;
                _footnote.Fg("Danger");
            }
        }
    }
}
