using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace BluePhone
{
    /// <summary>동료 목록의 묶음 제목 줄</summary>
    public sealed class HeaderRow
    {
        public string Title { get; set; }
        public int Count { get; set; }
        public string CountText => Count.ToString();
        public string Key => "h:" + Title;
    }

    /// <summary>동료 목록의 한 줄 (같은 동료가 여러 그룹에 보일 수 있어 묶음 이름과 함께 둠)</summary>
    public sealed class PeerRow
    {
        public string Section { get; set; }
        public Peer Peer { get; set; }
    }

    public sealed class MainWindow : Window
    {
        readonly Core _core;
        bool _hideOffline, _quitting, _favOnly;
        PeerGroup _groupFilter;

        readonly ObservableCollection<object> _peerRows = new ObservableCollection<object>();
        readonly ObservableCollection<ChatMessage> _msgRows = new ObservableCollection<ChatMessage>();
        readonly Dictionary<string, object> _rowCache = new Dictionary<string, object>();

        TextBox _search;
        Button _clearSearch, _moreBtn, _statusBtn;
        TextBlock _versionLabel, _updateBadgeText, _myName, _myDept, _myStatus, _peerCount, _unreadText, _chatUnreadText, _composeText;
        Border _updateBadge, _unreadBadge, _chatBadge;
        Avalonia.Controls.Shapes.Ellipse _moreDot, _myDot;
        Avatar _myAvatar;
        RadioButton _tabPeers, _tabHistory, _tabChats;
        Grid _peersPane, _historyPane, _chatsPane;
        WrapPanel _groupBar;
        ListBox _peerList, _historyList, _chatList;
        StackPanel _peersEmpty, _historyEmpty, _chatsEmpty;
        TextBlock _peersEmptyTitle, _peersEmptyBody, _chatsEmptyTitle, _chatsEmptyBody;

        public MainWindow(Core core)
        {
            _core = core;
            Title = AppInfo.Name;
            MinWidth = 300; MinHeight = 460;
            this.Cls("app");
            Icons.Apply(this);
            var s = core.Settings;
            Width = Math.Max(MinWidth, s.WinWidth);
            Height = Math.Max(MinHeight, s.WinHeight);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Opened += (o, e) => PlaceOnScreen();

            Content = Build();

            core.StatusChanged += RefreshMe;
            core.UnreadChanged += RefreshCounts;
            core.Peers.CollectionChanged += (o, e) =>
            {
                if (e.NewItems != null) foreach (Peer p in e.NewItems) p.PropertyChanged += Peer_PropertyChanged;
                QueuePeers();
            };
            foreach (var p in core.Peers) p.PropertyChanged += Peer_PropertyChanged;
            core.History.CollectionChanged += (o, e) => { QueueHistory(); RefreshCounts(); };
            core.GroupsChanged += BuildGroupBar;
            core.BlockedChanged += () => { QueuePeers(); RefreshCounts(); QueueConversations(); };
            core.PeerRenamed += _ => QueueConversations();
            AvatarStore.Changed += _ => Dispatcher.UIThread.Post(QueueConversations);
            core.RoomsChanged += QueueConversations;
            core.TypingChanged += _ => QueueConversations();

            BuildGroupBar();
            RefreshMe();
            RebuildPeers();
            RebuildHistory();
            RefreshCounts();
            _versionLabel.Text = AppInfo.VersionText;

            AddHandler(KeyDownEvent, (o, e) =>
            {
                if (Keys.Primary(e.KeyModifiers) && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
                else if (Keys.Primary(e.KeyModifiers) && e.Key == Key.N) { Compose_Click(); e.Handled = true; }
                else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; e.Handled = true; }
            }, RoutingStrategies.Tunnel);
        }

        void PlaceOnScreen()
        {
            var s = _core.Settings;
            var sc = Screens.Primary ?? Screens.All.FirstOrDefault();
            double k = Native.IsMac ? 1 : (sc?.Scaling ?? 1);
            bool inside = s.WinLeft > -1e5 && Screens.All.Any(x =>
            {
                var b = x.Bounds;
                double px = (s.WinLeft + 40) * k, py = (s.WinTop + 20) * k;
                return px >= b.X && px < b.Right && py >= b.Y && py < b.Bottom;
            });
            if (inside) Position = new PixelPoint((int)(s.WinLeft * k), (int)(s.WinTop * k));
            else if (sc != null)
            {
                var wa = sc.WorkingArea;
                Position = new PixelPoint((int)(wa.Right - (Width + 40) * k), (int)(wa.Y + (wa.Height - Height * k) / 2));
            }
        }

        // ───────────────────────── 화면 구성 ─────────────────────────

        Control Build()
        {
            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
            root.Res(Panel.BackgroundProperty, "Surface");

            // 머리글 (로고 · 이름 · 버전 · 업데이트 알림 · 메뉴)
            var head = new DockPanel { Margin = new Thickness(14, 8, 8, 2), Height = 34 };
            _moreBtn = new Button { Content = "⋯", FontSize = 18 }.Cls("icon").Tip("메뉴").Dock(Dock.Right);
            _moreDot = Ui.Dot(7, "Danger");
            _moreDot.HorizontalAlignment = HorizontalAlignment.Right; _moreDot.VerticalAlignment = VerticalAlignment.Top;
            _moreDot.IsVisible = false; _moreDot.IsHitTestVisible = false; _moreDot.Margin = new Thickness(0, 4, 4, 0);
            var moreWrap = new Panel { Children = { _moreBtn, _moreDot } }.Dock(Dock.Right);
            _moreBtn.Click += (o, e) => More_Click();
            head.Children.Add(moreWrap);
            _versionLabel = Ui.Text("", 11.5, fg: "Faint");
            _versionLabel.Margin = new Thickness(7, 1, 0, 0);
            _updateBadgeText = Ui.Text("업데이트", 11, FontWeight.SemiBold);
            _updateBadgeText.Foreground = Brushes.White;
            _updateBadge = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 9, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand) };
            _updateBadge.Child = Ui.H(5, new TextBlock { Text = "⬇", Foreground = Brushes.White, FontSize = 10, VerticalAlignment = VerticalAlignment.Center }, _updateBadgeText);
            _updateBadge.Res(Border.BackgroundProperty, "Accent");
            _updateBadge.PointerReleased += (o, e) => _core.OpenUpdate();
            head.Children.Add(Ui.H(0, Icons.Logo(20), WithMargin(Ui.Text(AppInfo.Name, 13, FontWeight.SemiBold), 9), _versionLabel, _updateBadge));
            root.Children.Add(head);

            // 내 프로필
            var me = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 6, 12, 12) }.Row(1);
            _myAvatar = new Avatar { Size = 40 };
            _myName = Ui.Text("", 15, FontWeight.SemiBold, trim: true);
            _myDept = Ui.Text("", 12, fg: "Muted", trim: true);
            _myDept.Margin = new Thickness(0, 1, 0, 0);
            me.Children.Add(_myAvatar);
            me.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _myName, _myDept } }.Col(1));
            _myDot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            _myStatus = Ui.Text("", 12.5);
            _myStatus.Margin = new Thickness(7, 0, 6, 0);
            _statusBtn = new Button { Content = Ui.H(0, _myDot, _myStatus, Ui.Text("▾", 10, fg: "Muted")), Padding = new Thickness(10, 0, 8, 0), Height = 30, VerticalAlignment = VerticalAlignment.Center }.Cls("outline").Tip("상태 변경").Col(2);
            _statusBtn.Click += (o, e) => Status_Click();
            me.Children.Add(_statusBtn);
            root.Children.Add(me);

            // 검색
            var searchGrid = new Grid { Margin = new Thickness(14, 0, 14, 10) }.Row(2);
            _search = new TextBox { Watermark = "이름, 부서, IP 검색", Padding = new Thickness(30, 6, 28, 6), Height = 34, BorderThickness = new Thickness(0) }.Cls("field");
            _search.Res(TextBox.BackgroundProperty, "Canvas");
            _search.TextChanged += (o, e) => Search_Changed();
            searchGrid.Children.Add(_search);
            searchGrid.Children.Add(new TextBlock { Text = "⌕", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 2), IsHitTestVisible = false }.Fg("Muted"));
            _clearSearch = new Button { Content = "✕", FontSize = 10, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0), IsVisible = false }.Cls("icon").Tip("지우기");
            _clearSearch.Click += (o, e) => { _search.Text = ""; _search.Focus(); };
            searchGrid.Children.Add(_clearSearch);
            root.Children.Add(searchGrid);

            // 탭
            _peerCount = Ui.Text("", 13, fg: "Faint"); _peerCount.Margin = new Thickness(5, 0, 0, 0);
            _unreadText = new TextBlock(); _unreadBadge = Ui.Badge(_unreadText); _unreadBadge.Margin = new Thickness(6, 0, 0, 0); _unreadBadge.IsVisible = false;
            _chatUnreadText = new TextBlock(); _chatBadge = Ui.Badge(_chatUnreadText); _chatBadge.Margin = new Thickness(6, 0, 0, 0); _chatBadge.IsVisible = false;
            _tabPeers = new RadioButton { GroupName = "maintab", IsChecked = true, Content = Ui.H(0, Ui.Text("동료", 13, FontWeight.SemiBold), _peerCount) }.Cls("tab");
            _tabHistory = new RadioButton { GroupName = "maintab", Content = Ui.H(0, Ui.Text("쪽지함", 13, FontWeight.SemiBold), _unreadBadge) }.Cls("tab");
            _tabChats = new RadioButton { GroupName = "maintab", Content = Ui.H(0, Ui.Text("대화함", 13, FontWeight.SemiBold), _chatBadge) }.Cls("tab");
            foreach (var t in new[] { _tabPeers, _tabHistory, _tabChats }) t.IsCheckedChanged += (o, e) => Tab_Changed();
            var tabs = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 0, 0, 0), Child = new StackPanel { Orientation = Orientation.Horizontal, Height = 34, Children = { _tabPeers, _tabHistory, _tabChats } } }.Row(3);
            tabs.Res(Border.BorderBrushProperty, "Line");
            root.Children.Add(tabs);

            // 동료
            _peersPane = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }.Row(4);
            _groupBar = new WrapPanel { Margin = new Thickness(14, 8, 10, 0) };
            _peersPane.Children.Add(_groupBar);
            _peerList = new ListBox { ItemsSource = _peerRows, SelectionMode = SelectionMode.Multiple, Padding = new Thickness(0, 0, 0, 8) }.Cls("flat").Row(1);
            _peerList.DataTemplates.Add(new FuncDataTemplate<HeaderRow>((r, _) => HeaderTemplate()));
            _peerList.DataTemplates.Add(new FuncDataTemplate<PeerRow>((r, _) => PeerTemplate()));
            _peerList.ContainerPrepared += (o, e) => e.Container.Classes.Set("header", e.Container.DataContext is HeaderRow);
            _peerList.SelectionChanged += (o, e) =>
            {
                foreach (var x in e.AddedItems.OfType<HeaderRow>().ToList()) _peerList.SelectedItems.Remove(x);
                int n = SelectedPeers().Length;
                _composeText.Text = n > 1 ? $"{n}명에게 쪽지" : "쪽지 보내기";
            };
            _peerList.DoubleTapped += (o, e) => { if (RowOf(e.Source) is PeerRow r) _core.OpenChat(r.Peer); };
            _peerList.KeyDown += (o, e) => { if (e.Key == Key.Enter && SelectedPeers().Length > 0) { _core.Compose(SelectedPeers()); e.Handled = true; } };
            _peerList.ContextMenu = new ContextMenu();
            _peerList.ContextMenu.Opening += (o, e) => FillPeerMenu(_peerList.ContextMenu);
            _peersPane.Children.Add(_peerList);
            _peersEmptyTitle = Ui.Text("같은 네트워크의 동료를 찾는 중", 13, FontWeight.SemiBold);
            _peersEmptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _peersEmptyBody = Ui.Text("상대 PC에서도 파란전화기가 실행 중이어야 합니다. 다른 대역이나 포트가 다른 PC는 IP 주소로 직접 추가하세요.", 12, fg: "Muted", wrap: true);
            _peersEmptyBody.TextAlignment = TextAlignment.Center; _peersEmptyBody.Margin = new Thickness(0, 6, 0, 14); _peersEmptyBody.LineHeight = 18;
            var rescan = Dialog.Btn("다시 찾기", "outline"); rescan.Click += (o, e) => _core.Rescan();
            var addPeer = Dialog.Btn("IP 주소로 추가", "outline"); addPeer.Click += (o, e) => AddByAddress();
            _peersEmpty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(32, 0, 32, 40), IsVisible = false,
                Children = { _peersEmptyTitle, _peersEmptyBody, Ui.H(8, rescan, addPeer).Also(x => x.HorizontalAlignment = HorizontalAlignment.Center) } }.Row(1);
            _peersPane.Children.Add(_peersEmpty);
            root.Children.Add(_peersPane);

            // 쪽지함
            _historyPane = new Grid { IsVisible = false }.Row(4);
            _historyList = new ListBox { ItemsSource = _msgRows, SelectionMode = SelectionMode.Multiple, Padding = new Thickness(0, 6, 0, 8) }.Cls("flat");
            _historyList.ItemTemplate = new FuncDataTemplate<ChatMessage>((m, _) => MsgTemplate());
            _historyList.SelectionChanged += (o, e) =>
            {
                if (!IsActive) return;
                foreach (var m in e.AddedItems.OfType<ChatMessage>())
                    if (!m.Outgoing && !m.Read) _core.MarkRead(m);
            };
            _historyList.DoubleTapped += (o, e) => { if (RowOf(e.Source) is ChatMessage m) OpenMsg(m); };
            _historyList.KeyDown += (o, e) =>
            {
                if (e.Key == Key.Enter) { HmOpen(); e.Handled = true; }
                else if (e.Key == Key.Delete || (e.Key == Key.Back && Keys.HasPrimary(e.KeyModifiers))) { HmDelete(); e.Handled = true; }
            };
            _historyList.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    Menus.Item("열기", HmOpen, bold: true),
                    Menus.Item("답장", HmReply),
                    Menus.Item("대화 보기", HmChat),
                    Menus.Item("예약 취소", HmCancel),
                    Menus.Item("내용 복사", HmCopy),
                    Menus.Sep(),
                    Menus.Item("삭제", HmDelete, gesture: Native.IsMac ? "Cmd+Back" : "Delete"),
                },
            };
            _historyPane.Children.Add(_historyList);
            _historyEmpty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 0, 20, 40), IsVisible = false };
            var he = Ui.Text("주고받은 쪽지가 여기에 쌓여요 · 1:1 대화는 대화함에서 봅니다", 12, fg: "Muted", wrap: true);
            he.TextAlignment = TextAlignment.Center;
            _historyEmpty.Children.Add(he);
            _historyPane.Children.Add(_historyEmpty);
            root.Children.Add(_historyPane);

            // 대화함
            _chatsPane = new Grid { IsVisible = false }.Row(4);
            _chatList = new ListBox { SelectionMode = SelectionMode.Multiple, Padding = new Thickness(0, 6, 0, 8) }.Cls("flat");
            _chatList.ItemTemplate = new FuncDataTemplate<Conversation>((c, _) => ConvTemplate());
            _chatList.DoubleTapped += (o, e) => { if (RowOf(e.Source) is Conversation c) OpenConversation(c); };
            _chatList.KeyDown += (o, e) =>
            {
                if (e.Key == Key.Enter) { CmOpen(); e.Handled = true; }
                else if (e.Key == Key.Delete || (e.Key == Key.Back && Keys.HasPrimary(e.KeyModifiers))) { CmDelete(); e.Handled = true; }
            };
            _chatList.ContextMenu = new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    Menus.Item("대화 열기", CmOpen, bold: true),
                    Menus.Item("읽음으로 표시", CmRead),
                    Menus.Sep(),
                    Menus.Item("대화 내용 삭제", CmDelete),
                },
            };
            _chatsPane.Children.Add(_chatList);
            _chatsEmptyTitle = Ui.Text("아직 대화가 없어요", 13, FontWeight.SemiBold); _chatsEmptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _chatsEmptyBody = Ui.Text("동료 목록에서 동료를 더블클릭하면 대화를 시작합니다. 여러 명을 골라 오른쪽 클릭 › 그룹 대화 시작으로 그룹 대화를 만들 수 있습니다.", 12, fg: "Muted", wrap: true);
            _chatsEmptyBody.TextAlignment = TextAlignment.Center; _chatsEmptyBody.Margin = new Thickness(0, 6, 0, 0);
            _chatsEmpty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(32, 0, 32, 40), IsVisible = false, Children = { _chatsEmptyTitle, _chatsEmptyBody } };
            _chatsPane.Children.Add(_chatsEmpty);
            root.Children.Add(_chatsPane);

            // 아래 버튼
            var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _composeText = Ui.Text("쪽지 보내기", 13, FontWeight.SemiBold);
            var compose = new Button { Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.H(8, new TextBlock { Text = "✉", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, _composeText) }.Cls("primary");
            compose.Click += (o, e) => Compose_Click();
            var broadcast = new Button { Content = "전체 공지", Height = 36, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 0) }.Cls("outline").Tip("접속 중인 모든 동료에게 보냅니다").Col(1);
            broadcast.Click += (o, e) => Broadcast_Click();
            actions.Children.Add(compose); actions.Children.Add(broadcast);
            var bottom = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 10, 12, 12), Child = actions }.Row(5);
            bottom.Res(Border.BorderBrushProperty, "Line");
            root.Children.Add(bottom);

            // 파일을 끌어다 놓으면 새 쪽지에 첨부
            DragDrop.SetAllowDrop(root, true);
            root.AddHandler(DragDrop.DropEvent, (o, e) =>
            {
                var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
                if (files == null || files.Count == 0) return;
                var w = new ComposeWindow(_core, SelectedPeers(), false, null);
                w.Show();
                foreach (var f in files) w.Attach(f);
            });
            return root;
        }

        static Control WithMargin(Control c, double left) { c.Margin = new Thickness(left, 0, 0, 0); return c; }

        static object RowOf(object source)
        {
            for (var v = source as StyledElement; v != null; v = v.Parent)
                if (v is ListBoxItem li) return li.DataContext;
            return null;
        }

        // ───────────────────────── 항목 모양 ─────────────────────────

        Control HeaderTemplate()
        {
            var title = Ui.Text("", 11.5, FontWeight.SemiBold, "Muted").Bind(TextBlock.TextProperty, "Title");
            var count = Ui.Text("", 11.5, fg: "Faint").Bind(TextBlock.TextProperty, "CountText");
            count.Margin = new Thickness(6, 0, 0, 0);
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 0, 4), Children = { title, count } };
        }

        Control PeerTemplate()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,Auto"), Height = 48, Background = Brushes.Transparent };
            g.Bind(OpacityProperty, "Peer.Opacity");
            g.Bind(ToolTip.TipProperty, "Peer.Tooltip");
            ToolTip.SetShowDelay(g, 900);
            var av = new Avatar { Size = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                .Bind(Avatar.FillProperty, "Peer.AvatarBrush").Bind(Avatar.InitialProperty, "Peer.Initial")
                .Bind(Avatar.ImageProperty, "Peer.AvatarImage").Bind(Avatar.StatusProperty, "Peer.StatusBrush");
            g.Children.Add(av);
            var name = Ui.Text("", 13, FontWeight.SemiBold, trim: true).Bind(TextBlock.TextProperty, "Peer.DisplayName");
            var real = Ui.Text("", 12, fg: "Faint", trim: true).Bind(TextBlock.TextProperty, "Peer.RealNameHint", Ui.Hint);
            var line1b = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, real } };
            var sub = Ui.Text("", 12, fg: "Muted", trim: true).Bind(TextBlock.TextProperty, "Peer.SubText");
            sub.Margin = new Thickness(0, 1, 0, 0);
            g.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Children = { line1b, sub } }.Col(1));
            var dot = Ui.Dot(7, "Accent").Bind(IsVisibleProperty, "Peer.HasUnread").Col(2);
            dot.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(dot);
            return g;
        }

        static readonly IValueConverter ProblemBrush = new FuncValueConverter<bool, IBrush>(b => Ui.Brush(b ? "Danger" : "Faint"));

        Control MsgTemplate()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*"), MinHeight = 58, Background = Brushes.Transparent };
            var av = new Avatar { Size = 32, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 11, 0, 0) }
                .Bind(Avatar.FillProperty, "AvatarBrush").Bind(Avatar.InitialProperty, "Initial").Bind(Avatar.ImageProperty, "AvatarImage");
            g.Children.Add(av);
            var who = Ui.Text("", 13, FontWeight.SemiBold, trim: true).Bind(TextBlock.TextProperty, "Who");
            var chatTag = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text("대화", 10.5, FontWeight.SemiBold, "Accent") }.Res(Border.BackgroundProperty, "AccentSoft").Bind(IsVisibleProperty, "Chat");
            var time = Ui.Text("", 11.5, fg: "Faint").Bind(TextBlock.TextProperty, "TimeText");
            time.HorizontalAlignment = HorizontalAlignment.Right;
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            top.Children.Add(who); top.Children.Add(chatTag.Col(1)); top.Children.Add(time.Col(3));
            who.MaxWidth = 200;
            var meLabel = Ui.Text("나:", 13, fg: "Faint").Bind(IsVisibleProperty, "Outgoing");
            meLabel.Margin = new Thickness(0, 0, 4, 0);
            var preview = Ui.Text("", 13, fg: "Ink2", trim: true).Bind(TextBlock.TextProperty, "Preview");
            var unread = Ui.Dot(7, "Accent").Bind(IsVisibleProperty, "IsUnread");
            unread.Margin = new Thickness(8, 0, 2, 0);
            var line2 = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            line2.Children.Add(unread.Dock(Dock.Right));
            line2.Children.Add(meLabel.Dock(Dock.Left));
            line2.Children.Add(preview);
            var meta = Ui.Text("", 11.5).Bind(TextBlock.TextProperty, "Meta").Bind(IsVisibleProperty, "Meta", Ui.HasText).Bind(TextBlock.ForegroundProperty, "IsProblem", ProblemBrush);
            meta.Margin = new Thickness(0, 3, 0, 0);
            meta.TextWrapping = TextWrapping.Wrap;
            g.Children.Add(new StackPanel { Margin = new Thickness(0, 10, 12, 10), Children = { top, line2, meta } }.Col(1));
            return g;
        }

        Control ConvTemplate()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*"), MinHeight = 60, Background = Brushes.Transparent };
            g.Children.Add(new Avatar { Size = 38, VerticalAlignment = VerticalAlignment.Center }
                .Bind(Avatar.FillProperty, "AvatarBrush").Bind(Avatar.InitialProperty, "Initial").Bind(Avatar.ImageProperty, "AvatarImage").Bind(Avatar.StatusProperty, "StatusBrush"));
            var name = Ui.Text("", 13, FontWeight.SemiBold, trim: true).Bind(TextBlock.TextProperty, "Name");
            var cnt = Ui.Text("", 13, fg: "Faint").Bind(TextBlock.TextProperty, "MemberText").Bind(IsVisibleProperty, "IsRoom").Dock(Dock.Right);
            cnt.Margin = new Thickness(5, 0, 0, 0);
            var nameRow = new DockPanel { Margin = new Thickness(0, 0, 64, 0), Children = { cnt, name } };
            var time = Ui.Text("", 11.5, fg: "Faint").Bind(TextBlock.TextProperty, "TimeText");
            time.HorizontalAlignment = HorizontalAlignment.Right;
            var badgeText = new TextBlock().Bind(TextBlock.TextProperty, "UnreadText");
            var badge = Ui.Badge(badgeText).Bind(IsVisibleProperty, "HasUnread").Dock(Dock.Right);
            badge.Margin = new Thickness(8, 0, 0, 0);
            badge.MinWidth = 18; badge.Height = 18;
            var preview = Ui.Text("", 13, fg: "Ink2", trim: true).Bind(TextBlock.TextProperty, "Preview")
                .Bind(TextBlock.ForegroundProperty, "IsTyping", new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(t => Ui.Brush(t ? "Accent" : "Ink2")));
            var line2 = new DockPanel { Margin = new Thickness(0, 3, 0, 0), Children = { badge, preview } };
            g.Children.Add(new StackPanel { Margin = new Thickness(0, 10, 14, 10), VerticalAlignment = VerticalAlignment.Center, Children = { new Grid { Children = { nameRow, time } }, line2 } }.Col(1));
            return g;
        }

        // ───────────────────────── 동료 목록 ─────────────────────────

        static readonly HashSet<string> PeerProps = new HashSet<string>
        {
            nameof(Peer.Status_), nameof(Peer.DisplayName), nameof(Peer.Group), nameof(Peer.Favorite), nameof(Peer.Dept), nameof(Peer.Ip), nameof(Peer.Host),
        };

        void Peer_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Peer.Status_)) RefreshCounts();
            if (PeerProps.Contains(e.PropertyName)) QueuePeers();
        }

        bool _peersQueued, _historyQueued, _convQueued;

        void QueuePeers()
        {
            if (_peersQueued) return;
            _peersQueued = true;
            Dispatcher.UIThread.Post(() => { _peersQueued = false; RebuildPeers(); RefreshCounts(); }, DispatcherPriority.Background);
        }

        void QueueHistory()
        {
            if (_historyQueued) return;
            _historyQueued = true;
            Dispatcher.UIThread.Post(() => { _historyQueued = false; RebuildHistory(); RefreshCounts(); }, DispatcherPriority.Background);
        }

        T Cached<T>(string key, Func<T> make) where T : class
        {
            if (_rowCache.TryGetValue(key, out var o) && o is T t) return t;
            t = make();
            _rowCache[key] = t;
            return t;
        }

        /// <summary>
        /// 전체 보기: 즐겨찾기 → 내가 만든 그룹 → 부서별 → 부서 미지정 으로 묶음 (그룹에 넣은 동료는 그 그룹 아래에).
        /// 그룹(또는 즐겨찾기)을 고르면 그 이름 하나로만 묶어 구성원만 보여 줌
        /// </summary>
        void RebuildPeers()
        {
            var visible = _core.Peers.Where(PeerFilter).ToList();
            string fixedTitle = _groupFilter != null ? _groupFilter.Name : _favOnly ? "즐겨찾기" : null;
            var sections = new List<(string title, Peer p)>();
            foreach (var p in visible)
            {
                if (fixedTitle != null) { sections.Add((fixedTitle, p)); continue; }
                var names = new List<string>();
                if (p.Favorite) names.Add("즐겨찾기");
                foreach (var g in _core.Settings.Groups) if (g.Members.Contains(p.Id) && !names.Contains(g.Name)) names.Add(g.Name);
                if (names.Count == 0) names.Add(p.Group);
                foreach (var n in names) sections.Add((n, p));
            }
            int Rank(string name)
            {
                if (fixedTitle != null) return 0;
                if (name == "즐겨찾기") return 0;
                int i = _core.Settings.Groups.FindIndex(g => g.Name == name);
                if (i >= 0) return 1 + i;
                return name == "부서 미지정" ? 100000 : 10000;
            }
            var rows = new List<object>();
            foreach (var grp in sections.GroupBy(x => x.title).OrderBy(x => Rank(x.Key)).ThenBy(x => x.Key, StringComparer.CurrentCulture))
            {
                var h = Cached("h:" + grp.Key, () => new HeaderRow { Title = grp.Key });
                if (h.Count != grp.Count())
                {
                    // 개수가 바뀌면 새 제목 줄로 (제목 줄은 화면이 다시 그려져야 숫자가 바뀜)
                    h = new HeaderRow { Title = grp.Key, Count = grp.Count() };
                    _rowCache["h:" + grp.Key] = h;
                }
                rows.Add(h);
                foreach (var (title, p) in grp.OrderBy(x => x.p.SortKey).ThenBy(x => x.p.DisplayName, StringComparer.CurrentCulture))
                    rows.Add(Cached("p:" + title + "\n" + p.Id, () => new PeerRow { Section = title, Peer = p }));
            }
            ListSync.Sync(_peerRows, rows);
        }

        bool PeerFilter(Peer p)
        {
            if (_core.IsBlocked(p.Id)) return false;
            if (_hideOffline && !p.IsOnline) return false;
            if (_favOnly && !p.Favorite) return false;
            if (_groupFilter != null && !_groupFilter.Members.Contains(p.Id)) return false;
            var q = _search?.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return Has(p.DisplayName, q) || Has(p.Dept, q) || Has(p.Ip, q) || Has(p.Host, q);
        }

        bool MsgFilter(ChatMessage m)
        {
            if (m.Chat) return false;
            var q = _search?.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return Has(m.Who, q) || Has(m.Text, q) || Has(m.PeerDept, q) || m.Files.Any(f => Has(f.Name, q));
        }

        static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;

        void RebuildHistory() => ListSync.Sync(_msgRows, _core.History.Where(MsgFilter).ToList());

        // ───────────────────────── 그룹 ─────────────────────────

        void BuildGroupBar()
        {
            _groupBar.Children.Clear();
            if (_groupFilter != null && !_core.Settings.Groups.Contains(_groupFilter)) _groupFilter = null;
            ToggleButton Chip(string text, bool on, Action click, PeerGroup g = null)
            {
                var b = new ToggleButton { Content = Ui.Text(text, 12), IsChecked = on, Margin = new Thickness(0, 0, 6, 6) }.Cls("filter");
                b.Click += (s, e) => click();
                if (g != null)
                {
                    var cm = new ContextMenu();
                    cm.Opening += (s, e) => FillGroupMenu(cm, g);
                    cm.Closed += (s, e) => _core.SaveGroups();
                    b.ContextMenu = cm;
                    ToolTip.SetTip(b, $"{_core.GroupMembers(g).Count}명 · 오른쪽 클릭(또는 Control+클릭)으로 구성원 추가 · 편집");
                }
                _groupBar.Children.Add(b);
                return b;
            }
            void Pick(PeerGroup g, bool favOnly) { _groupFilter = g; _favOnly = favOnly; BuildGroupBar(); RebuildPeers(); RefreshCounts(); }
            Chip("전체", _groupFilter == null && !_favOnly, () => Pick(null, false));
            if (_core.Peers.Any(x => x.Favorite)) Chip("★ 즐겨찾기", _favOnly, () => Pick(null, true));
            else if (_favOnly) _favOnly = false;
            foreach (var g in _core.Settings.Groups)
            {
                var gg = g;
                Chip(g.Name, _groupFilter == g, () => Pick(gg, false), g);
            }
            var add = new Button { Content = "+", FontSize = 14, Width = 26, Height = 26, Margin = new Thickness(0, 0, 0, 6) }.Cls("icon").Tip("새 그룹 만들기");
            add.Click += async (s, e) => await NewGroup(SelectedPeers());
            _groupBar.Children.Add(add);
            RebuildPeers();
        }

        void FillGroupMenu(ContextMenu cm, PeerGroup g)
        {
            var send = Menus.Item("그룹에 쪽지 보내기", () => { var list = _core.GroupMembers(g); if (list.Count > 0) _core.Compose(list); }, bold: true);
            var room = Menus.Item("그룹 대화 시작", () =>
            {
                var list = _core.GroupMembers(g).Where(x => !_core.IsBlocked(x.Id)).ToList();
                if (list.Count == 0) { _ = Dialog.Alert(this, "그룹 대화", "그룹에 동료가 없습니다."); return; }
                _core.OpenRoom(_core.CreateRoom(list, g.Name));
            });
            var members = new MenuItem { Header = "구성원 추가 · 빼기" };
            var peers = _core.Peers.Where(x => !_core.IsBlocked(x.Id)).OrderByDescending(x => g.Members.Contains(x.Id)).ThenBy(x => x.DisplayName).ToList();
            if (peers.Count == 0) members.Items.Add(Menus.Item("동료가 없습니다", enabled: false));
            foreach (var p in peers)
            {
                var pp = p;
                var mi = new MenuItem
                {
                    Header = string.IsNullOrEmpty(p.Dept) ? p.DisplayName : $"{p.DisplayName}  · {p.Dept}",
                    ToggleType = MenuItemToggleType.CheckBox, IsChecked = g.Members.Contains(p.Id), StaysOpenOnClick = true,
                };
                mi.Click += (o, ev) =>
                {
                    if (g.Members.Contains(pp.Id)) g.Members.Remove(pp.Id); else g.Members.Add(pp.Id);
                    mi.IsChecked = g.Members.Contains(pp.Id);
                    Store.SaveSettings(_core.Settings);
                    RebuildPeers();
                    RefreshCounts();
                };
                members.Items.Add(mi);
            }
            var ren = Menus.Item("이름 바꾸기…", async () =>
            {
                var v = await Dialog.Prompt(this, "그룹 이름 바꾸기", "새 이름을 입력하세요.", g.Name, "저장");
                if (!string.IsNullOrWhiteSpace(v)) { g.Name = v.Trim(); _core.SaveGroups(); }
            });
            var del = Menus.Item("그룹 삭제", async () =>
            {
                if (await Dialog.Confirm(this, "그룹 삭제", $"'{g.Name}' 그룹을 삭제할까요? 동료 목록에는 영향이 없습니다.", "삭제", danger: true))
                { _core.Settings.Groups.Remove(g); _core.SaveGroups(); }
            });
            cm.ItemsSource = new Control[] { send, room, members, ren, Menus.Sep(), del };
        }

        async System.Threading.Tasks.Task<PeerGroup> NewGroup(IEnumerable<Peer> members)
        {
            var name = await Dialog.Prompt(this, "새 그룹", "그룹 이름을 입력하세요. (예: 개발팀, 점심 멤버)\n구성원은 그룹 버튼을 오른쪽 클릭 → 구성원 추가 · 빼기, 또는 동료를 오른쪽 클릭 → 그룹에 추가로 넣을 수 있습니다.", "", "만들기");
            if (string.IsNullOrWhiteSpace(name)) return null;
            var g = new PeerGroup { Name = name.Trim() };
            foreach (var p in members ?? new Peer[0]) g.Members.Add(p.Id);
            _core.Settings.Groups.Add(g);
            _core.SaveGroups();
            return g;
        }

        // ───────────────────────── 상태 ─────────────────────────

        void RefreshMe()
        {
            var s = _core.Settings;
            _myName.Text = string.IsNullOrWhiteSpace(s.Name) ? Environment.UserName : s.Name;
            _myDept.Text = string.Join(" · ", new[] { s.Dept, s.Note }.Where(x => !string.IsNullOrWhiteSpace(x)).DefaultIfEmpty(Core.MachineName));
            _myAvatar.Initial = _myName.Text.Substring(0, 1).ToUpperInvariant();
            _myAvatar.Fill = Palette.Avatar(s.Id);
            _myAvatar.Image = AvatarStore.Mine;
            _myDot.Fill = Status.Brush(_core.MyStatus);
            _myStatus.Text = Status.Label(_core.MyStatus);
        }

        void RefreshCounts()
        {
            int online = _core.Peers.Count(p => p.IsOnline && !_core.IsBlocked(p.Id));
            _peerCount.Text = online.ToString();
            int unread = _core.History.Count(m => !m.Outgoing && !m.Read && !m.Chat);
            _unreadBadge.IsVisible = unread > 0;
            _unreadText.Text = unread > 99 ? "99+" : unread.ToString();
            int chatUnread = _core.History.Count(m => !m.Outgoing && !m.Read && m.Chat);
            _chatBadge.IsVisible = chatUnread > 0;
            _chatUnreadText.Text = chatUnread > 99 ? "99+" : chatUnread.ToString();
            QueueConversations();

            bool noPeers = !_peerRows.Any(r => r is PeerRow);
            _peersEmpty.IsVisible = noPeers;
            bool searching = !string.IsNullOrWhiteSpace(_search.Text);
            bool inGroup = _groupFilter != null || _favOnly;
            _peersEmptyTitle.Text = searching ? "검색 결과가 없습니다"
                : _groupFilter != null ? $"'{_groupFilter.Name}' 그룹에 동료가 없습니다"
                : _favOnly ? "즐겨찾기한 동료가 없습니다"
                : "같은 네트워크의 동료를 찾는 중";
            _peersEmptyBody.Text = inGroup
                ? "그룹 버튼을 오른쪽 클릭 → 구성원 추가 · 빼기로 동료를 넣을 수 있습니다."
                : "상대 PC에서도 파란전화기가 실행 중이어야 합니다. 다른 대역이나 포트가 다른 PC는 IP 주소로 직접 추가하세요.";
            _peersEmptyBody.IsVisible = !searching;
            _historyEmpty.IsVisible = _msgRows.Count == 0;
        }

        // ───────────────────────── 창 ─────────────────────────

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            SavePlacement();
            // 닫기(⌘W · 빨간 단추)는 숨기기 — 메뉴 막대 아이콘 · Dock 에서 다시 엶. 종료는 ⌘Q
            if (!_quitting && e.CloseReason == WindowCloseReason.WindowClosing) { e.Cancel = true; Hide(); }
            base.OnClosing(e);
        }

        public void AllowClose() => _quitting = true;

        public void SavePlacement()
        {
            if (WindowState != WindowState.Normal || !IsVisible) return;
            var s = _core.Settings;
            double k = Native.IsMac ? 1 : RenderScaling;
            s.WinLeft = Position.X / k; s.WinTop = Position.Y / k;
            s.WinWidth = Bounds.Width > 0 ? Bounds.Width : Width;
            s.WinHeight = Bounds.Height > 0 ? Bounds.Height : Height;
            Store.SaveSettings(s);
        }

        void More_Click()
        {
            var items = new List<Control>();
            var up = Updater.Ready ?? _core.AvailableUpdate;
            if (up != null && Updater.IsNewer(up))
            {
                var mi = Menus.Item(Updater.Ready != null ? $"⬇ 새 버전 {up.VersionText} 설치하고 다시 시작" : $"⬇ 새 버전 {up.VersionText} 업데이트…", _core.OpenUpdate, bold: true);
                mi.Res(MenuItem.ForegroundProperty, "Accent");
                items.Add(mi);
                items.Add(Menus.Sep());
            }
            items.Add(Menus.Item("새 쪽지", Compose_Click, gesture: Keys.Gesture("N")));
            items.Add(Menus.Item("동료 다시 찾기", _core.Rescan));
            items.Add(Menus.Item("IP 주소로 동료 추가…", AddByAddress));
            items.Add(Menus.Item("내 주소 복사 (IP:포트)", () =>
            {
                var addr = string.Join(", ", Core.LocalIps().Select(ip => $"{ip}:{_core.Settings.Port}"));
                ClipKit.SetText(this, addr);
                _ = Dialog.Alert(this, "내 주소를 복사했습니다", $"{addr}\n\n동료에게 알려 주면 ⋯ 메뉴 › IP 주소로 동료 추가에서 이 주소로 나를 추가할 수 있습니다.");
            }));
            items.Add(Menus.Item("받은 파일 폴더 열기", () => _core.OpenDownloads()));
            items.Add(Menus.Item("화면 캡처해서 보내기", () => _core.StartCapture(), gesture: _core.Settings.CaptureHotkey && !_core.HotkeyFailed && Native.IsMac ? "Ctrl+Alt+S" : null));
            items.Add(Menus.Item("모두 읽음 처리", _core.MarkAllRead));
            var theme = new MenuItem { Header = "테마" };
            foreach (var t in ChatLook.Themes)
            {
                var tt = t;
                bool on = (_core.Settings.ChatTheme ?? "light") == t.Id;
                var sw = new Panel { Width = 16, Height = 16 };
                sw.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Fill = new SolidColorBrush(t.Bg), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), StrokeThickness = 1 });
                sw.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(t.Out) });
                var mi = new MenuItem { Header = on ? t.Name + "  ✓" : t.Name, Icon = sw, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal };
                mi.Click += (o, ev) => _core.SetChatLook(theme: tt.Id);
                theme.Items.Add(mi);
            }
            items.Add(theme);
            var blocked = _core.Settings.Blocked;
            var bm = new MenuItem { Header = blocked.Count > 0 ? $"차단한 동료 ({blocked.Count})" : "차단한 동료" };
            if (blocked.Count == 0) bm.Items.Add(Menus.Item("차단한 동료가 없습니다", enabled: false));
            foreach (var b in blocked.ToList())
            {
                var bb = b;
                bm.Items.Add(Menus.Item($"{b.Name}  — 차단 해제", async () =>
                {
                    if (await Dialog.Confirm(this, "차단 해제", $"{bb.Name}님의 차단을 해제할까요? 다시 목록에 보이고 쪽지 · 대화를 받습니다.", "해제"))
                        _core.Unblock(bb.Id);
                }));
            }
            items.Add(bm);
            items.Add(Menus.Item("오프라인 동료 숨기기", () => { _hideOffline = !_hideOffline; RebuildPeers(); RefreshCounts(); }, check: _hideOffline));
            items.Add(Menus.Sep());
            items.Add(Menus.Item("설정", () => _core.ShowSettings(this), gesture: Native.IsMac ? "Cmd+OemComma" : null));
            items.Add(Menus.Item($"업데이트 확인 (현재 {AppInfo.VersionText})", async () =>
            {
                var msg = await _core.CheckForUpdates(manual: true);
                if (msg != null && !msg.StartsWith("새 버전") && !msg.StartsWith("업데이트가")) _ = Dialog.Alert(this, "업데이트 확인", msg);
            }));
            items.Add(Menus.Item("종료", _core.Quit, gesture: Native.IsMac ? "Cmd+Q" : null));
            Menus.Show(_moreBtn, items, PlacementMode.BottomEdgeAlignedRight);
        }

        void Status_Click()
        {
            var items = new List<Control>();
            foreach (var st in new[] { Status.Online, Status.Busy, Status.Away, Status.Dnd })
            {
                var s = st;
                var mi = new MenuItem { Header = Status.Label(s), Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Status.Brush(s) } };
                if (s == Status.Dnd) ToolTip.SetTip(mi, "긴급 쪽지를 제외하고 알림 창을 띄우지 않습니다");
                if (_core.MyStatus == s) mi.FontWeight = FontWeight.SemiBold;
                mi.Click += (o, ev) => _core.SetStatus(s);
                items.Add(mi);
            }
            items.Add(Menus.Sep());
            items.Add(Menus.Item("프로필 편집…", () => _core.ShowSettings(this)));
            Menus.Show(_statusBtn, items, PlacementMode.BottomEdgeAlignedRight);
        }

        void Search_Changed()
        {
            _clearSearch.IsVisible = !string.IsNullOrEmpty(_search.Text);
            QueueConversations();
            RebuildPeers();
            RebuildHistory();
            RefreshCounts();
        }

        void Tab_Changed()
        {
            if (_peersPane == null || _tabChats == null) return;
            _peersPane.IsVisible = _tabPeers.IsChecked == true;
            _historyPane.IsVisible = _tabHistory.IsChecked == true;
            _chatsPane.IsVisible = _tabChats.IsChecked == true;
            if (_tabChats.IsChecked == true) BuildConversations();
        }

        // ───────────────────────── 동료 메뉴 ─────────────────────────

        Peer[] SelectedPeers() => _peerList.SelectedItems?.OfType<PeerRow>().Select(r => r.Peer).Distinct().ToArray() ?? new Peer[0];

        void FillPeerMenu(ContextMenu cm)
        {
            var sel = SelectedPeers();
            var items = new List<Control>
            {
                Menus.Item("대화 보기", () => { var p = sel.FirstOrDefault(); if (p != null) _core.OpenChat(p); }, bold: true, enabled: sel.Length == 1),
                Menus.Item(sel.Length > 1 ? $"{sel.Length + 1}명 그룹 대화 시작" : "그룹 대화 시작", () =>
                {
                    var list = sel.Where(p => !_core.IsBlocked(p.Id)).ToList();
                    if (list.Count > 0) _core.OpenRoom(_core.CreateRoom(list));
                }, enabled: sel.Length > 0),
                Menus.Item(sel.Length > 1 ? $"{sel.Length}명에게 쪽지 보내기" : "쪽지 보내기", () => _core.Compose(sel), enabled: sel.Length > 0),
                Menus.Sep(),
                Menus.Item(sel.Length > 0 && sel.All(p => p.Favorite) ? "즐겨찾기 해제" : "즐겨찾기에 추가", () =>
                {
                    bool on = !sel.All(p => p.Favorite);
                    foreach (var p in sel) if (p.Favorite != on) _core.ToggleFavorite(p);
                    BuildGroupBar();
                }, enabled: sel.Length > 0),
            };
            var group = new MenuItem { Header = "그룹에 추가", IsEnabled = sel.Length > 0 };
            foreach (var g in _core.Settings.Groups)
            {
                var gg = g;
                bool all = sel.All(p => g.Members.Contains(p.Id));
                group.Items.Add(Menus.Item(g.Name, () =>
                {
                    foreach (var p in sel)
                    {
                        if (all) gg.Members.Remove(p.Id);
                        else if (!gg.Members.Contains(p.Id)) gg.Members.Add(p.Id);
                    }
                    _core.SaveGroups();
                }, check: all));
            }
            if (_core.Settings.Groups.Count > 0) group.Items.Add(Menus.Sep());
            group.Items.Add(Menus.Item("새 그룹 만들기…", async () => await NewGroup(sel)));
            items.Add(group);
            items.Add(Menus.Item(sel.Length == 1 && !string.IsNullOrEmpty(sel[0].Alias) ? "별명 바꾸기…" : "별명 지정…", async () =>
            {
                var p = sel.FirstOrDefault();
                if (p == null) return;
                var v = await Dialog.Prompt(this, "별명 지정", $"{p.Name}님을 내 목록에서 어떻게 표시할까요? 비워 두면 상대가 정한 이름을 씁니다.", p.Alias ?? "", "저장");
                if (v != null) _core.SetAlias(p, v);
            }, enabled: sel.Length == 1));
            items.Add(Menus.Item("IP 주소 복사", () => { var p = sel.FirstOrDefault(); if (p != null) ClipKit.SetText(this, p.Ip ?? ""); }, enabled: sel.Length == 1));
            items.Add(Menus.Item(sel.Length > 1 ? $"{sel.Length}명 차단…" : "차단…", async () =>
            {
                if (sel.Length == 0) return;
                var names = string.Join(", ", sel.Select(p => p.DisplayName).Take(3)) + (sel.Length > 3 ? $" 외 {sel.Length - 3}명" : "");
                if (!await Dialog.Confirm(this, "차단", $"{names}님을 차단할까요?\n목록에서 보이지 않고, 보내는 쪽지 · 대화를 받지 않습니다.\n위쪽 ⋯ 메뉴 › 차단한 동료에서 언제든 해제할 수 있습니다.", "차단", danger: true)) return;
                foreach (var p in sel) _core.Block(p);
            }, enabled: sel.Length > 0));
            items.Add(Menus.Sep());
            items.Add(Menus.Item("목록에서 삭제", () => { foreach (var p in sel) _core.RemovePeer(p); }, enabled: sel.Length > 0 && sel.All(p => !p.IsOnline)));
            cm.ItemsSource = items;
        }

        /// <summary>상단의 업데이트 알림</summary>
        public void ShowUpdateBadge(ReleaseInfo r, bool ready)
        {
            bool on = r != null && Updater.IsNewer(r);
            _updateBadge.IsVisible = _moreDot.IsVisible = on;
            _versionLabel.IsVisible = !on;
            if (!on) return;
            _updateBadgeText.Text = ready ? $"v{r.VersionText} 설치" : $"v{r.VersionText} 업데이트";
            ToolTip.SetTip(_updateBadge, (ready
                ? $"새 버전 {r.VersionText} 를 받아 두었습니다. 누르면 변경 사항을 보고 바로 설치합니다."
                : $"새 버전 {r.VersionText} 이(가) 나왔습니다. 누르면 변경 사항을 보고 업데이트합니다.") + $"\n현재 버전 {AppInfo.VersionText}");
        }

        async void AddByAddress()
        {
            var text = "";
            while (true)
            {
                text = await Dialog.Prompt(this, "IP 주소로 동료 추가",
                    $"상대 PC의 IP 주소와 포트를 입력하세요.\n예) 192.168.0.10:9082 · 포트를 생략하면 내 포트({_core.Settings.Port})를 씁니다.\n상대 포트는 상대 프로그램의 설정 › 네트워크에서 볼 수 있습니다.",
                    text, "추가");
                if (string.IsNullOrWhiteSpace(text)) return;
                try
                {
                    Cursor = new Cursor(StandardCursorType.Wait);
                    var p = await _core.AddByAddress(text);
                    Cursor = null;
                    RebuildPeers(); RefreshCounts();
                    var row = _peerRows.OfType<PeerRow>().FirstOrDefault(r => r.Peer == p);
                    if (row != null) { _peerList.SelectedItem = row; _peerList.ScrollIntoView(row); }
                    if (_core.IsBlocked(p.Id))
                        await Dialog.Alert(this, "동료 추가", $"{p.DisplayName}님은 차단한 동료입니다. ⋯ 메뉴 › 차단한 동료에서 해제하면 목록에 보입니다.");
                    return;
                }
                catch (Exception ex)
                {
                    Cursor = null;
                    await Dialog.Alert(this, "동료를 추가하지 못했습니다", ex.Message);
                }
            }
        }

        void Compose_Click() => _core.Compose(_tabPeers.IsChecked == true ? SelectedPeers() : new Peer[0]);

        void Broadcast_Click()
        {
            var online = _core.Peers.Where(p => p.IsOnline && !_core.IsBlocked(p.Id)).ToArray();
            if (online.Length == 0)
            {
                _ = Dialog.Alert(this, "접속 중인 동료가 없습니다", "전체 공지는 지금 접속해 있는 동료에게만 전달됩니다.");
                return;
            }
            _core.Compose(online, broadcast: true);
        }

        // ───────────────────────── 쪽지함 ─────────────────────────

        ChatMessage[] SelectedMsgs() => _historyList.SelectedItems?.OfType<ChatMessage>().ToArray() ?? new ChatMessage[0];

        void HmOpen() { foreach (var m in SelectedMsgs().Take(6)) OpenMsg(m); }

        void OpenMsg(ChatMessage m)
        {
            if (m.Chat && _core.OpenChatFor(m) != null) return;
            _core.ShowPopup(m, activate: true);
        }

        void HmReply()
        {
            var m = SelectedMsgs().FirstOrDefault();
            if (m == null) return;
            if (m.Outgoing)
            {
                var targets = m.ToIds.Select(id => _core.Peers.FirstOrDefault(p => p.Id == id)).Where(p => p != null).ToArray();
                _core.Compose(targets);
            }
            else
            {
                var p = _core.PeerOf(m);
                if (p != null) _core.Compose(new[] { p }, quote: m.Text);
            }
        }

        void HmChat()
        {
            var m = SelectedMsgs().FirstOrDefault();
            if (m == null) return;
            if (m.Room != null) _core.OpenChatFor(m);
            else { var p = _core.PeerOf(m); if (p != null) _core.OpenChat(p); }
        }

        async void HmCancel()
        {
            var list = SelectedMsgs().Where(m => m.Scheduled).ToArray();
            if (list.Length == 0) return;
            if (await Dialog.Confirm(this, "예약 취소", $"예약된 쪽지 {list.Length}개를 취소할까요?", "예약 취소", danger: true))
                foreach (var m in list) _core.CancelScheduled(m);
        }

        void HmCopy() => ClipKit.SetText(this, string.Join("\n\n", SelectedMsgs().Select(m => m.Text)));

        async void HmDelete()
        {
            var sel = SelectedMsgs();
            if (sel.Length == 0) return;
            if (await Dialog.Confirm(this, "쪽지 삭제", sel.Length == 1 ? "선택한 쪽지를 삭제할까요?" : $"선택한 쪽지 {sel.Length}개를 삭제할까요?", "삭제", danger: true))
                _core.DeleteHistory(sel);
        }

        // ───────────────────────── 대화함 ─────────────────────────

        void QueueConversations()
        {
            if (_convQueued) return;
            _convQueued = true;
            Dispatcher.UIThread.Post(() => { _convQueued = false; BuildConversations(); }, DispatcherPriority.Background);
        }

        void BuildConversations()
        {
            if (_chatList == null) return;
            var selected = _chatList.SelectedItems?.OfType<Conversation>().Select(c => c.Key).ToList() ?? new List<string>();
            var q = _search?.Text?.Trim();
            var rooms = _core.ActiveRooms.ToDictionary(r => r.Id);
            var list = _core.History.Where(m => m.Chat && !m.Broadcast)
                .Select(m => new { m, key = Core.KeyOf(m) })
                .Where(x => x.key != null && (Core.IsRoomKey(x.key) ? rooms.ContainsKey(x.m.Room) : !_core.IsBlocked(x.key)))
                .GroupBy(x => x.key)
                .Select(g =>
                {
                    var last = g.OrderByDescending(x => x.m.Ts).First().m;
                    var room = last.Room != null ? rooms[last.Room] : null;
                    return new Conversation
                    {
                        PeerId = room == null ? g.Key : null,
                        Peer = room == null ? _core.Peers.FirstOrDefault(p => p.Id == g.Key) : null,
                        Room = room,
                        Last = last,
                        Count = g.Count(),
                        Unread = g.Count(x => !x.m.Outgoing && !x.m.Read),
                    };
                }).ToList();
            // 만들었지만 아직 대화가 없는 방도 보이도록
            foreach (var r in rooms.Values.Where(r => list.All(c => c.Room != r)))
                list.Add(new Conversation { Room = r });
            foreach (var c in list) c.Typing = _core.TypingText(c.Key);
            list = list
                .Where(c => string.IsNullOrEmpty(q) || Has(c.Name, q) || _core.History.Any(m => Core.InChat(m, c.Key) && Has(m.Text, q)))
                .OrderByDescending(c => c.Last?.Ts ?? c.Room?.CreatedTs ?? 0)
                .ToList();
            _chatList.ItemsSource = list;
            foreach (var c in list) if (selected.Contains(c.Key)) _chatList.SelectedItems.Add(c);
            _chatsEmpty.IsVisible = list.Count == 0;
            bool searching = !string.IsNullOrEmpty(q);
            _chatsEmptyTitle.Text = searching ? "검색 결과가 없습니다" : "아직 대화가 없어요";
            _chatsEmptyBody.IsVisible = !searching;
        }

        Conversation[] SelectedConvs() => _chatList.SelectedItems?.OfType<Conversation>().ToArray() ?? new Conversation[0];

        void OpenConversation(Conversation c)
        {
            if (c.Room != null) { _core.OpenRoom(c.Room); return; }
            var p = c.Peer ?? _core.Peers.FirstOrDefault(x => x.Id == c.PeerId);
            if (p != null) _core.OpenChat(p);
            else _ = Dialog.Alert(this, "대화 창을 열 수 없습니다", "동료 목록에서 삭제된 동료입니다. 다시 접속하면 열 수 있습니다.");
        }

        void CmOpen() { foreach (var c in SelectedConvs().Take(6)) OpenConversation(c); }

        void CmRead()
        {
            foreach (var c in SelectedConvs()) _core.MarkChatRead(c.Key);
        }

        async void CmDelete()
        {
            var sel = SelectedConvs();
            if (sel.Length == 0) return;
            var names = string.Join(", ", sel.Select(c => c.Name).Take(3)) + (sel.Length > 3 ? $" 외 {sel.Length - 3}개" : "");
            if (!await Dialog.Confirm(this, "대화 내용 삭제", $"{names} 대화 내용을 이 Mac에서 삭제할까요? 쪽지함의 쪽지는 지워지지 않습니다.\n그룹 대화방에서 아예 나가려면 대화 창의 👥 버튼 › 방 나가기를 누르세요.", "삭제", danger: true)) return;
            var keys = sel.Select(c => c.Key).ToList();
            var msgs = _core.History.Where(m => keys.Any(k => Core.InChat(m, k))).ToList();
            _core.DeleteHistory(msgs);
        }
    }

    static class ObjExt
    {
        public static T Also<T>(this T o, Action<T> a) { a(o); return o; }
    }
}
