using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BluePhone
{
    /// <summary>한 동료와의 1:1 대화, 또는 여러 동료와의 그룹 대화를 말풍선으로 보여 주는 창</summary>
    public partial class ChatWindow : Window
    {
        readonly Core _core;
        /// <summary>1:1 대화 상대 (그룹 대화면 null)</summary>
        public Peer Peer { get; }
        /// <summary>그룹 대화방 (1:1 이면 null)</summary>
        public ChatRoom Room { get; }
        /// <summary>대화 열쇠: 1:1 은 동료 ID, 그룹은 room:방ID</summary>
        public string ChatKey { get; }
        ObservableCollection<ChatMessage> _rows = new ObservableCollection<ChatMessage>();
        readonly ObservableCollection<ComposeWindow.Attachment> _files = new ObservableCollection<ComposeWindow.Attachment>();
        readonly List<Peer> _watched = new List<Peer>();
        DateTime? _at;
        bool _sending;
        int _zipping;

        public ChatWindow(Core core, Peer peer) : this(core, peer, null) { }
        public ChatWindow(Core core, ChatRoom room) : this(core, null, room) { }

        ChatWindow(Core core, Peer peer, ChatRoom room)
        {
            _core = core;
            Peer = peer;
            Room = room;
            ChatKey = room != null ? Typists.RoomKey(room.Id) : peer.Id;
            _look = ChatLook.Apply(this, core.Settings);
            InitializeComponent();
            Chrome.Apply(this, caption: 58);
            Chrome.NoMaximize(this);

            // 처음에는 최근 대화만 (오래 쌓인 기록이 많아도 창이 바로 열리도록) — '이전 대화 더 보기'로 늘림
            Rebuild();
            FileList.ItemsSource = _files;

            // 기록 전체를 다시 거르지 않고, 새로 온 메시지만 끝에 붙임
            core.History.CollectionChanged += History_Changed;
            core.TypingChanged += Core_TypingChanged;
            core.RoomsChanged += Core_RoomsChanged;
            Closed += (s, e) =>
            {
                core.History.CollectionChanged -= History_Changed;
                core.TypingChanged -= Core_TypingChanged;
                core.RoomsChanged -= Core_RoomsChanged;
                foreach (var w in _watched) w.PropertyChanged -= Peer_PropertyChanged;
                _core.NotifyTyping(ChatKey, false);
            };
            WatchPeers();
            RefreshHeader();
            RefreshTyping();
            // 이 창 안의 오른쪽 클릭 메뉴도 테마 색을 따르도록
            AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((s, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null;
                     d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                    if (d is FrameworkElement fe && fe.ContextMenu != null) { ChatLook.Attach(fe.ContextMenu, _look); break; }
            }), true);
            UpdateSendState();

            Input.TextChanged += (s, e) =>
            {
                UpdateSendState();
                _core.NotifyTyping(ChatKey, Input.Text.Length > 0);
            };
            _files.CollectionChanged += (s, e) => UpdateSendState();
            Activated += (s, e) =>
            {
                _core.LastChat = this;
                _core.MarkChatRead(ChatKey);
            };
            Loaded += (s, e) => { ScrollToEnd(); Input.Focus(); };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && Input.Text.Length == 0) Close();
                else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == System.Windows.Input.Key.OemPlus || e.Key == System.Windows.Input.Key.Add)) { Zoom(+1); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == System.Windows.Input.Key.OemMinus || e.Key == System.Windows.Input.Key.Subtract)) { Zoom(-1); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == System.Windows.Input.Key.D0 || e.Key == System.Windows.Input.Key.NumPad0)) { _core.SetChatLook(size: ChatLook.DefaultSize); e.Handled = true; }
            };
            // Ctrl + 마우스 휠로 글자 크기
            PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                Zoom(e.Delta > 0 ? +1 : -1);
                e.Handled = true;
            };

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible; };
            DragLeave += (s, e) => DropOverlay.Visibility = Visibility.Collapsed;
            PreviewDragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
            PreviewDrop += (s, e) =>
            {
                DropOverlay.Visibility = Visibility.Collapsed;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { AddFiles(paths); e.Handled = true; }
            };
            CaptureBtn.ToolTip = core.Settings.CaptureHotkey ? "화면 캡처 (Ctrl+Alt+S)" : "화면 캡처";
            InitMention();
        }

        /// <summary>머리글에 보일 동료(1:1 상대 또는 방 구성원)의 접속 상태 변화를 지켜봄</summary>
        void WatchPeers()
        {
            foreach (var w in _watched) w.PropertyChanged -= Peer_PropertyChanged;
            _watched.Clear();
            if (Peer != null) _watched.Add(Peer);
            else foreach (var m in Room.Others) { var p = _core.Peers.FirstOrDefault(x => x.Id == m.Id); if (p != null) _watched.Add(p); }
            foreach (var w in _watched) w.PropertyChanged += Peer_PropertyChanged;
        }

        void Peer_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshHeader));
        }

        void Core_RoomsChanged()
        {
            if (Room == null) return;
            if (Room.Left) { Close(); return; }
            WatchPeers();
            RefreshHeader();
        }

        void Core_TypingChanged(string key) { if (key == ChatKey) RefreshTyping(); }

        void RefreshTyping()
        {
            var t = _core.TypingText(ChatKey);
            TypingText.Text = t ?? "";
            TypingBar.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void RefreshHeader()
        {
            if (Room != null)
            {
                Title = Room.Title + " — 그룹 대화";
                HName.Text = Room.Title;
                HInitial.Text = "\uE716";
                HInitial.FontFamily = (FontFamily)FindResource("Glyph");
                HInitial.FontSize = 15;
                HAvatar.Fill = Palette.Avatar("room:" + Room.Id);
                HPhoto.ImageSource = null;
                HDot.Visibility = HDotRing.Visibility = Visibility.Collapsed;
                int online = _watched.Count(p => p.IsOnline);
                HStatus.Text = $"{Room.Count}명 · 접속 {online}명";
                HSecure.Visibility = _watched.Count > 0 && _watched.All(p => p.Secure) ? Visibility.Visible : Visibility.Collapsed;
                UpdateSendState();
                return;
            }
            Title = Peer.DisplayName + " — 대화";
            HName.Text = Peer.DisplayName;
            HInitial.Text = Peer.Initial;
            HAvatar.Fill = Peer.AvatarBrush;
            HPhoto.ImageSource = Peer.AvatarImage;
            HDot.Fill = Peer.StatusBrush;
            HStatus.Text = string.Join(" · ", new[] { Peer.StatusText, Peer.Dept }.Where(x => !string.IsNullOrWhiteSpace(x)));
            HSecure.Visibility = Peer.Secure ? Visibility.Visible : Visibility.Collapsed;
            UpdateSendState();
        }

        void RefreshEmpty() => Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (List.Items.Count > 0) List.ScrollIntoView(List.Items[List.Items.Count - 1]);
            }));
        }

        void UpdateSendState()
        {
            bool has = Input.Text.Trim().Length > 0 || _files.Count > 0;
            SendBtn.IsEnabled = has && !_sending && _zipping == 0;
            Hint.Text = _core.Settings.EnterToSend ? "Enter 보내기" : "Ctrl+Enter 보내기";
            Hint.ToolTip = _core.Settings.EnterToSend ? "Shift+Enter 줄바꿈 · 설정에서 Ctrl+Enter 로 바꿀 수 있습니다" : null;
            if (Peer != null && !Peer.IsOnline) Hint.Text = "오프라인 · 접속하면 전달";
        }

        // ───────────── input ─────────────

        void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (MentionKey(e)) { e.Handled = true; return; }
            if (Markdown.HandleKey(Input, e, this)) { e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                bool send = _core.Settings.EnterToSend ? Keyboard.Modifiers == ModifierKeys.None : Keyboard.Modifiers == ModifierKeys.Control;
                if (send) { e.Handled = true; if (SendBtn.IsEnabled) Send_Click(null, null); }
                else if (_core.Settings.EnterToSend && Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    e.Handled = true;
                    var i = Input.CaretIndex;
                    Input.Text = Input.Text.Insert(i, Environment.NewLine);
                    Input.CaretIndex = i + Environment.NewLine.Length;
                }
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var pasted = ComposeWindow.FromClipboard();
                if (pasted != null) { AddFiles(pasted); e.Handled = true; }
            }
        }

        async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_sending) return;
            _sending = true;
            UpdateSendState();
            var text = Input.Text.TrimEnd();
            var files = _files.Select(f => f.Path).ToList();
            var at = _at;
            Input.Clear();
            _files.Clear();
            ClearSchedule_Click(null, null);
            bool urgent = Urgent.IsChecked == true;
            Urgent.IsChecked = false;
            _core.NotifyTyping(ChatKey, false);
            try
            {
                if (Room != null) await _core.SendRoom(Room, text, files, urgent, at);
                else await _core.Send(new[] { Peer }, text, files, urgent, false, at: at, chat: true);
            }
            finally { _sending = false; UpdateSendState(); Input.Focus(); }
            ScrollToEnd();
        }

        public void Attach(string path) => AddFiles(new[] { path });

        async void AddFiles(System.Collections.Generic.IEnumerable<string> paths)
        {
            foreach (var p in paths.ToList())
            {
                if (_files.Any(f => string.Equals(f.Source ?? f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                if (Directory.Exists(p))
                {
                    var item = new ComposeWindow.Attachment { Path = p, Source = p, SizeText = "압축 중…" };
                    _files.Add(item);
                    _zipping++; UpdateSendState();
                    try
                    {
                        var zip = await System.Threading.Tasks.Task.Run(() => ComposeWindow.ZipFolder(p));
                        var i = _files.IndexOf(item);
                        if (i >= 0) _files[i] = new ComposeWindow.Attachment { Path = zip, Source = p, SizeText = FileItem.FormatSize(new FileInfo(zip).Length), Kind = "폴더 · ZIP" };
                    }
                    catch (Exception ex) { _files.Remove(item); Dialog.Alert(this, "폴더를 압축하지 못했습니다", ex.Message); }
                    finally { _zipping--; UpdateSendState(); }
                    continue;
                }
                if (!File.Exists(p)) continue;
                try { _files.Add(new ComposeWindow.Attachment { Path = p, SizeText = FileItem.FormatSize(new FileInfo(p).Length) }); } catch { }
            }
        }

        void Attach_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "첨부할 파일 선택", Filter = "모든 파일 (*.*)|*.*", DereferenceLinks = false };
            if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
        }

        void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is ComposeWindow.Attachment a) _files.Remove(a);
        }

        void Capture_Click(object sender, RoutedEventArgs e) => _core.StartCapture(this);

        void Emoji_Click(object sender, RoutedEventArgs e) => Emoji.ShowPicker(EmojiBtn, Input);
        void Format_Click(object sender, RoutedEventArgs e) => Markdown.ShowFormatMenu(FormatBtn, Input, this, _look);
        void Code_Click(object sender, RoutedEventArgs e) => Markdown.InsertCode(Input, this);

        // 바로 보기 이미지 · 미리 보기
        void Thumb_Click(object sender, MouseButtonEventArgs e)
        {
            { e.Handled = true; if ((sender as FrameworkElement)?.Tag is FileItem f) Media.OpenPreview(f.LocalPath, this); }
        }
        void ThumbPath_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string path) Media.OpenPreview(path, this);
        }
        void Preview_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FileItem f) Media.OpenPreview(f.LocalPath, this);
        }

        // ───────────── 꾸미기 ─────────────

        ResourceDictionary _look;

        public void ApplyLook() => _look = ChatLook.Apply(this, _core.Settings);

        // ───────────── 최근 대화만 먼저 ─────────────

        const int FirstPage = 120;
        const int PageSize = 300;
        int _shown = FirstPage;

        /// <summary>이 대화의 메시지 중 최근 _shown 개를 한 번에 채움 (기록은 한 번만 훑음)</summary>
        void Rebuild()
        {
            var all = _core.History.Where(m => Core.InChat(m, ChatKey)).OrderBy(m => m.Ts).ToList();
            var show = all.Count > _shown ? all.GetRange(all.Count - _shown, _shown) : all;
            foreach (var m in show)
                foreach (var f in m.Files)
                {
                    if (m.Outgoing) f.State = "보낸 파일";
                    else if (string.IsNullOrEmpty(f.State) && !string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath)) f.State = "완료";
                }
            _rows = new ObservableCollection<ChatMessage>(show);
            List.ItemsSource = _rows;
            MoreBtn.Visibility = all.Count > show.Count ? Visibility.Visible : Visibility.Collapsed;
            RefreshEmpty();
        }

        void History_Changed(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    bool added = false;
                    foreach (ChatMessage m in e.NewItems)
                    {
                        if (!Core.InChat(m, ChatKey) || _rows.Contains(m)) continue;
                        if (m.Outgoing) foreach (var f in m.Files) f.State = "보낸 파일";
                        int i = _rows.Count;
                        while (i > 0 && _rows[i - 1].Ts > m.Ts) i--;
                        _rows.Insert(i, m);
                        _shown++;
                        added = true;
                    }
                    if (added) { RefreshEmpty(); ScrollToEnd(); }
                    break;
                case NotifyCollectionChangedAction.Remove:
                    foreach (ChatMessage m in e.OldItems) _rows.Remove(m);
                    RefreshEmpty();
                    break;
                default:
                    Rebuild();
                    break;
            }
        }

        void More_Click(object sender, RoutedEventArgs e)
        {
            var first = _rows.FirstOrDefault();
            _shown += PageSize;
            Rebuild();
            if (first != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => List.ScrollIntoView(first)));
        }

        // ───────────── 그룹 대화 ─────────────

        /// <summary>동료를 여러 명 골라 체크하는 메뉴 (메뉴가 닫히지 않아 연달아 고를 수 있음) + 마지막 줄의 실행 버튼</summary>
        MenuItem PickPeers(string header, ICollection<string> exclude, Func<int, string> actionText, Action<List<Peer>> act)
        {
            var root = new MenuItem { Header = header };
            root.Items.Add(new MenuItem { Header = "(불러오는 중)" });
            root.SubmenuOpened += (s, e) =>
            {
                if (e.OriginalSource != root) return;
                root.Items.Clear();
                var chosen = new List<Peer>();
                var peers = _core.Peers.Where(x => !_core.IsBlocked(x.Id) && !exclude.Contains(x.Id))
                    .OrderByDescending(x => x.IsOnline).ThenBy(x => x.DisplayName).ToList();
                var go = new MenuItem { Header = actionText(0), IsEnabled = false, FontWeight = FontWeights.SemiBold };
                go.Click += (o, ev) => { if (chosen.Count > 0) act(chosen.ToList()); };
                if (peers.Count == 0) root.Items.Add(new MenuItem { Header = "더 초대할 동료가 없습니다", IsEnabled = false });
                foreach (var p in peers)
                {
                    var pp = p;
                    var mi = new MenuItem
                    {
                        Header = (p.IsOnline ? "" : "(오프라인) ") + (string.IsNullOrEmpty(p.Dept) ? p.DisplayName : $"{p.DisplayName}  · {p.Dept}"),
                        IsCheckable = true, StaysOpenOnClick = true,
                    };
                    mi.Click += (o, ev) =>
                    {
                        if (mi.IsChecked) { if (!chosen.Contains(pp)) chosen.Add(pp); } else chosen.Remove(pp);
                        go.Header = actionText(chosen.Count);
                        go.IsEnabled = chosen.Count > 0;
                    };
                    root.Items.Add(mi);
                }
                if (peers.Count > 0) { root.Items.Add(new Separator()); root.Items.Add(go); }
            };
            return root;
        }

        void People_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = PeopleBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            ChatLook.Attach(m, _look);
            if (Room == null)
            {
                m.Items.Add(PickPeers("그룹 대화 만들기", new[] { Peer.Id }, n => n == 0 ? "함께할 동료를 고르세요" : $"{n + 1}명과 그룹 대화 시작",
                    list => { list.Insert(0, Peer); _core.OpenRoom(_core.CreateRoom(list)); }));
                m.Items.Add(new MenuItem { Header = $"{Peer.DisplayName}님과 1:1 대화 중", IsEnabled = false });
                m.IsOpen = true;
                return;
            }
            var members = new MenuItem { Header = $"대화 상대 {Room.Count}명" };
            foreach (var mem in Room.Members.OrderByDescending(x => x.Id == _core.Settings.Id))
            {
                var p = _core.Peers.FirstOrDefault(x => x.Id == mem.Id);
                string label = mem.Id == _core.Settings.Id ? "나 (" + _core.Settings.Name + ")" : (p?.DisplayName ?? mem.Name) + (p != null && p.IsOnline ? "" : "  · 오프라인");
                var mi = new MenuItem { Header = label };
                if (p != null && mem.Id != _core.Settings.Id) mi.Click += (o, ev) => _core.OpenChat(p);
                else mi.IsEnabled = false;
                if (p != null) mi.ToolTip = "누르면 1:1 대화";
                members.Items.Add(mi);
            }
            m.Items.Add(members);
            m.Items.Add(PickPeers("동료 초대", Room.Members.Select(x => x.Id).ToList(), n => n == 0 ? "초대할 동료를 고르세요" : $"{n}명 초대하기",
                async list => await _core.InviteToRoom(Room, list)));
            var ren = new MenuItem { Header = "방 이름 바꾸기…" };
            ren.Click += async (o, ev) =>
            {
                var v = Dialog.Prompt(this, "방 이름", "모든 구성원에게 보이는 이름입니다. 비워 두면 구성원 이름으로 표시합니다.", Room.Name ?? "", "저장");
                if (v != null) await _core.RenameRoom(Room, v);
            };
            m.Items.Add(ren);
            m.Items.Add(new Separator());
            var leave = new MenuItem { Header = "방 나가기" };
            leave.Click += async (o, ev) =>
            {
                if (Dialog.Confirm(this, "방 나가기", "이 대화방에서 나갈까요? 다른 구성원에게 나갔다고 알리고, 내 PC의 대화 내용도 지웁니다.", "나가기", danger: true))
                    await _core.LeaveRoom(Room);
            };
            m.Items.Add(leave);
            m.IsOpen = true;
        }

        void Zoom(int step)
        {
            var cur = _core.Settings.ChatFontSize <= 0 ? ChatLook.DefaultSize : _core.Settings.ChatFontSize;
            _core.SetChatLook(size: Math.Max(ChatLook.MinSize, Math.Min(ChatLook.MaxSize, Math.Round(cur) + step)));
        }

        void Look_Click(object sender, RoutedEventArgs e)
        {
            var s = _core.Settings;
            var m = new ContextMenu { PlacementTarget = LookBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            ChatLook.Attach(m, _look);

            var theme = new MenuItem { Header = "테마" };
            foreach (var t in ChatLook.Themes)
            {
                var tt = t;
                var sw = new Grid { Width = 16, Height = 16 };
                sw.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(t.Bg), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), StrokeThickness = 1 });
                sw.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(t.Out) });
                bool on = (s.ChatTheme ?? "light") == t.Id;
                var mi = new MenuItem { Header = on ? t.Name + "  ✓" : t.Name, Icon = sw, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal };
                mi.Click += (o, ev) => _core.SetChatLook(theme: tt.Id);
                theme.Items.Add(mi);
            }
            m.Items.Add(theme);

            var font = new MenuItem { Header = "글꼴" };
            foreach (var f in ChatLook.Fonts)
            {
                var ff = f;
                var mi = new MenuItem { Header = f.Name, IsChecked = (s.ChatFont ?? "") == (f.Family ?? "") };
                if (f.Family != null) mi.FontFamily = new FontFamily(f.Family);
                mi.Click += (o, ev) => _core.SetChatLook(font: ff.Family ?? "");
                font.Items.Add(mi);
            }
            m.Items.Add(font);

            var size = new MenuItem { Header = "글자 크기" };
            double cur = s.ChatFontSize <= 0 ? ChatLook.DefaultSize : s.ChatFontSize;
            foreach (var z in ChatLook.Sizes)
            {
                var zz = z;
                var mi = new MenuItem { Header = z.Name, IsChecked = Math.Abs(cur - z.Size) < 0.01, InputGestureText = z.Size == ChatLook.DefaultSize ? "Ctrl+0" : "" };
                mi.Click += (o, ev) => _core.SetChatLook(size: zz.Size);
                size.Items.Add(mi);
            }
            size.Items.Add(new Separator());
            size.Items.Add(new MenuItem { Header = "Ctrl + 마우스 휠, Ctrl + / − 로도 조절", IsEnabled = false });
            m.Items.Add(size);

            m.Items.Add(new Separator());
            var reset = new MenuItem { Header = "기본값으로" };
            reset.Click += (o, ev) => _core.SetChatLook(theme: "light", font: "", size: ChatLook.DefaultSize);
            m.Items.Add(reset);
            m.IsOpen = true;
        }

        void Schedule_Click(object sender, RoutedEventArgs e)
        {
            Schedule.Pick(ScheduleBtn, this, at =>
            {
                _at = at;
                ScheduleBar.Visibility = Visibility.Visible;
                ScheduleText.Text = "예약 · " + at.ToString("M월 d일 (ddd) tt h:mm") + " 에 보냅니다";
                SendText.Text = "예약";
            });
        }

        void ClearSchedule_Click(object sender, RoutedEventArgs e)
        {
            _at = null;
            ScheduleBar.Visibility = Visibility.Collapsed;
            SendText.Text = "보내기";
        }

        // ───────────── bubbles ─────────────

        static ChatMessage MsgOf(object sender)
        {
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is ChatMessage m)
                return m;
            return (sender as FrameworkElement)?.DataContext as ChatMessage;
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            var m = MsgOf(sender);
            // 말풍선에서 일부를 골라 두었으면 그 부분만, 아니면 메시지 전체
            var part = SelectableTextBlock.SelectionIn(this);
            if (!string.IsNullOrEmpty(part)) { try { Clipboard.SetText(part); } catch { } return; }
            if (m != null) try { Clipboard.SetText(m.Text ?? ""); } catch { }
        }

        void Remind_Click(object sender, RoutedEventArgs e)
        {
            var m = MsgOf(sender);
            if (m != null) _core.RemindLater(m, DateTime.Now.AddMinutes(30));
        }

        void CancelSchedule_Click(object sender, RoutedEventArgs e)
        {
            var m = MsgOf(sender);
            if (m != null && m.Scheduled) _core.CancelScheduled(m);
        }

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            var m = MsgOf(sender);
            if (m != null && Dialog.Confirm(this, "쪽지 삭제", "이 쪽지를 내 기록에서 삭제할까요?", "삭제", danger: true))
                _core.DeleteHistory(new[] { m });
        }

        async void FileAction_Click(object sender, RoutedEventArgs e)
        {
            if (!(((Button)sender).Tag is FileItem f)) return;
            var m = _core.History.FirstOrDefault(x => x.Files.Contains(f));
            if (m == null) return;
            if (f.State == "완료")
            {
                try { Process.Start(new ProcessStartInfo(f.LocalPath) { UseShellExecute = true }); } catch { _core.OpenDownloads(f.LocalPath); }
                return;
            }
            var dir = Store.DownloadDir(_core.Settings);
            string dest = Store.UniquePath(dir, f.Name);
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.Name, InitialDirectory = dir };
                if (dlg.ShowDialog(this) != true) return;
                dest = dlg.FileName;
            }
            try { await _core.DownloadFile(m, f, dest); }
            catch (Exception ex) { Dialog.Alert(this, "파일을 받지 못했습니다", ex.Message); }
        }

        void FileFolder_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is FileItem f) _core.OpenDownloads(f.LocalPath);
        }

        // ───────────── chrome ─────────────

        void Pin_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            PinBtn.Foreground = Topmost ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink2");
            PinBtn.Content = Topmost ? "" : "";
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
