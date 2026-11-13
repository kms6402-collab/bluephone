using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace BluePhone
{
    public partial class MainWindow : Window
    {
        readonly Core _core;
        readonly ListCollectionView _peers, _history;
        bool _hideOffline;
        bool _quitting;

        public MainWindow(Core core)
        {
            _core = core;
            InitializeComponent();
            Chrome.Apply(this);

            var s = core.Settings;
            Width = Math.Max(MinWidth, s.WinWidth);
            Height = Math.Max(MinHeight, s.WinHeight);
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (s.WinLeft > -1e5 && vs.Contains(new Point(s.WinLeft + 40, s.WinTop + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WinLeft; Top = s.WinTop;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - Width - 40; Top = wa.Top + (wa.Height - Height) / 2;
            }

            _peers = new ListCollectionView(core.Peers);
            _peers.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Peer.Group)));
            _peers.SortDescriptions.Add(new SortDescription(nameof(Peer.Group), ListSortDirection.Ascending));
            _peers.SortDescriptions.Add(new SortDescription(nameof(Peer.SortKey), ListSortDirection.Ascending));
            _peers.SortDescriptions.Add(new SortDescription(nameof(Peer.DisplayName), ListSortDirection.Ascending));
            _peers.IsLiveSorting = true;
            _peers.LiveSortingProperties.Add(nameof(Peer.SortKey));
            _peers.LiveSortingProperties.Add(nameof(Peer.DisplayName));
            _peers.LiveSortingProperties.Add(nameof(Peer.Group));
            _peers.IsLiveGrouping = true;
            _peers.LiveGroupingProperties.Add(nameof(Peer.Group));
            _peers.IsLiveFiltering = true;
            _peers.LiveFilteringProperties.Add(nameof(Peer.IsOnline));
            _peers.Filter = o => PeerFilter((Peer)o);
            PeerList.ItemsSource = _peers;

            _history = new ListCollectionView(core.History) { Filter = o => MsgFilter((ChatMessage)o) };
            HistoryList.ItemsSource = _history;

            core.StatusChanged += RefreshMe;
            core.UnreadChanged += RefreshCounts;
            core.Peers.CollectionChanged += (o, e) => RefreshCounts();
            core.History.CollectionChanged += (o, e) => RefreshCounts();
            foreach (var p in core.Peers) p.PropertyChanged += Peer_PropertyChanged;
            core.Peers.CollectionChanged += (o, e) =>
            {
                if (e.NewItems != null) foreach (Peer p in e.NewItems) p.PropertyChanged += Peer_PropertyChanged;
            };

            RefreshMe();
            RefreshCounts();
            VersionLabel.Text = AppInfo.VersionText;

            InputBindings.Add(new KeyBinding(new Relay(() => { Search.Focus(); Search.SelectAll(); }), Key.F, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new Relay(() => Compose_Click(null, null)), Key.N, ModifierKeys.Control));
            Application.Current.Exit += (o, e) => _quitting = true;
        }

        void Peer_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Peer.Status_)) RefreshCounts();
        }

        bool PeerFilter(Peer p)
        {
            if (_hideOffline && !p.IsOnline) return false;
            var q = Search?.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return Contains(p.DisplayName, q) || Contains(p.Dept, q) || Contains(p.Ip, q) || Contains(p.Host, q);
        }

        bool MsgFilter(ChatMessage m)
        {
            var q = Search?.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            return Contains(m.Who, q) || Contains(m.Text, q) || Contains(m.PeerDept, q) || m.Files.Any(f => Contains(f.Name, q));
        }

        static bool Contains(string s, string q) => s != null && s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;

        void RefreshMe()
        {
            var s = _core.Settings;
            MyName.Text = string.IsNullOrWhiteSpace(s.Name) ? Environment.UserName : s.Name;
            MyDept.Text = string.Join(" · ", new[] { s.Dept, s.Note }.Where(x => !string.IsNullOrWhiteSpace(x)).DefaultIfEmpty(Environment.MachineName));
            MyInitial.Text = MyName.Text.Substring(0, 1).ToUpperInvariant();
            MyAvatar.Fill = Palette.Avatar(s.Id);
            MyPhoto.ImageSource = AvatarStore.Mine;
            MyDot.Fill = Status.Brush(_core.MyStatus);
            MyStatus.Text = Status.Label(_core.MyStatus);
        }

        void RefreshCounts()
        {
            int online = _core.Peers.Count(p => p.IsOnline);
            PeerCount.Text = online.ToString();
            int unread = _core.Unread;
            UnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
            UnreadText.Text = unread > 99 ? "99+" : unread.ToString();

            bool noPeers = _peers.IsEmpty;
            PeersEmpty.Visibility = noPeers ? Visibility.Visible : Visibility.Collapsed;
            bool searching = !string.IsNullOrWhiteSpace(Search.Text);
            PeersEmptyTitle.Text = searching ? "검색 결과가 없습니다" : "같은 네트워크의 동료를 찾는 중";
            PeersEmptyBody.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            HistoryEmpty.Visibility = _history.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        // ───────────── window ─────────────

        protected override void OnClosing(CancelEventArgs e)
        {
            SavePlacement();
            if (!_quitting) { e.Cancel = true; Hide(); }
            base.OnClosing(e);
        }

        public void SavePlacement()
        {
            if (WindowState != WindowState.Normal) return;
            var s = _core.Settings;
            s.WinLeft = Left; s.WinTop = Top; s.WinWidth = ActualWidth > 0 ? ActualWidth : Width; s.WinHeight = ActualHeight > 0 ? ActualHeight : Height;
            Store.SaveSettings(s);
        }

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        public void ShowTrayMenu(ContextMenu menu) => TrayMenu.OpenAtCursor(menu);

        void More_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = MoreBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string text, Action a, bool check = false, string key = null)
            {
                var mi = new MenuItem { Header = text, IsChecked = check, InputGestureText = key ?? "" };
                mi.Click += (o, ev) => a();
                m.Items.Add(mi);
            }
            Add("새 쪽지", () => Compose_Click(null, null), key: "Ctrl+N");
            Add("동료 다시 찾기", () => _core.Rescan());
            Add("받은 파일 폴더 열기", () => _core.OpenDownloads());
            Add("모두 읽음 처리", _core.MarkAllRead);
            Add("오프라인 동료 숨기기", () => { _hideOffline = !_hideOffline; _peers.Refresh(); RefreshCounts(); }, _hideOffline);
            m.Items.Add(new Separator());
            Add("설정", () => _core.ShowSettings(this));
            Add($"업데이트 확인 (현재 {AppInfo.VersionText})", async () =>
            {
                var msg = await _core.CheckForUpdates(manual: true);
                if (msg != null && !msg.StartsWith("새 버전") && !msg.StartsWith("업데이트가")) Dialog.Alert(this, "업데이트 확인", msg);
            });
            Add("종료", _core.Quit);
            m.IsOpen = true;
        }

        void Status_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu { PlacementTarget = StatusBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var st in new[] { Status.Online, Status.Busy, Status.Away, Status.Dnd })
            {
                var s = st;
                var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Status.Brush(s) };
                var mi = new MenuItem { Header = Status.Label(s), Icon = dot };
                if (s == Status.Dnd) mi.ToolTip = "긴급 쪽지를 제외하고 알림 창을 띄우지 않습니다";
                if (_core.MyStatus == s) mi.FontWeight = FontWeights.SemiBold;
                mi.Click += (o, ev) => _core.SetStatus(s);
                m.Items.Add(mi);
            }
            m.Items.Add(new Separator());
            var edit = new MenuItem { Header = "프로필 편집…" };
            edit.Click += (o, ev) => _core.ShowSettings(this);
            m.Items.Add(edit);
            m.IsOpen = true;
        }

        // ───────────── search & tabs ─────────────

        void Search_Changed(object sender, TextChangedEventArgs e)
        {
            ClearSearch.Visibility = Search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _peers?.Refresh();
            _history?.Refresh();
            if (_peers != null) RefreshCounts();
        }

        void ClearSearch_Click(object sender, RoutedEventArgs e) { Search.Clear(); Search.Focus(); }

        void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (PeersPane == null) return;
            bool peers = TabPeers.IsChecked == true;
            PeersPane.Visibility = peers ? Visibility.Visible : Visibility.Collapsed;
            HistoryPane.Visibility = peers ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && Search.Text.Length > 0) { Search.Clear(); e.Handled = true; }
            base.OnPreviewKeyDown(e);
        }

        // ───────────── peers ─────────────

        Peer[] SelectedPeers() => PeerList.SelectedItems.Cast<Peer>().ToArray();

        void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int n = PeerList.SelectedItems.Count;
            ComposeText.Text = n > 1 ? $"{n}명에게 쪽지" : "쪽지 보내기";
        }

        void PeerList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Peer p) _core.Compose(new[] { p });
        }

        void PeerList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && PeerList.SelectedItems.Count > 0) _core.Compose(SelectedPeers());
        }

        void PeerMenu_Opened(object sender, RoutedEventArgs e)
        {
            var sel = SelectedPeers();
            PmSend.IsEnabled = sel.Length > 0;
            PmSend.Header = sel.Length > 1 ? $"{sel.Length}명에게 쪽지 보내기" : "쪽지 보내기";
            PmCopy.IsEnabled = sel.Length == 1;
            PmAlias.IsEnabled = sel.Length == 1;
            PmAlias.Header = sel.Length == 1 && !string.IsNullOrEmpty(sel[0].Alias) ? "별명 바꾸기…" : "별명 지정…";
            PmRemove.IsEnabled = sel.Length > 0 && sel.All(p => !p.IsOnline);
        }

        void PmSend_Click(object sender, RoutedEventArgs e) => _core.Compose(SelectedPeers());
        void PmAlias_Click(object sender, RoutedEventArgs e)
        {
            var p = SelectedPeers().FirstOrDefault();
            if (p == null) return;
            var v = Dialog.Prompt(this, "별명 지정", $"{p.Name}님을 내 목록에서 어떻게 표시할까요? 비워 두면 상대가 정한 이름을 씁니다.", p.Alias ?? "", "저장");
            if (v != null) _core.SetAlias(p, v);
        }

        public void ShowUpdateBadge(bool on) => UpdateBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        async void UpdateBadge_Click(object sender, MouseButtonEventArgs e) => await _core.CheckForUpdates(manual: true);

        void PmCopy_Click(object sender, RoutedEventArgs e)
        {
            var p = SelectedPeers().FirstOrDefault();
            if (p != null) try { Clipboard.SetText(p.Ip ?? ""); } catch { }
        }
        void PmRemove_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in SelectedPeers()) _core.RemovePeer(p);
        }

        void Rescan_Click(object sender, RoutedEventArgs e) => _core.Rescan();

        void Compose_Click(object sender, RoutedEventArgs e) => _core.Compose(TabPeers.IsChecked == true ? SelectedPeers() : new Peer[0]);

        void Broadcast_Click(object sender, RoutedEventArgs e)
        {
            var online = _core.Peers.Where(p => p.IsOnline).ToArray();
            if (online.Length == 0)
            {
                Dialog.Alert(this, "접속 중인 동료가 없습니다", "전체 공지는 지금 접속해 있는 동료에게만 전달됩니다.");
                return;
            }
            _core.Compose(online, broadcast: true);
        }

        // ───────────── history ─────────────

        ChatMessage[] SelectedMsgs() => HistoryList.SelectedItems.Cast<ChatMessage>().ToArray();

        void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is ChatMessage m) _core.ShowPopup(m, activate: true);
        }

        void HistoryList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) HmOpen_Click(null, null);
            else if (e.Key == Key.Delete) HmDelete_Click(null, null);
        }

        void HmOpen_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in SelectedMsgs().Take(6)) _core.ShowPopup(m, activate: true);
        }

        void HmReply_Click(object sender, RoutedEventArgs e)
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

        void HmCopy_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join("\n\n", SelectedMsgs().Select(m => m.Text));
            try { Clipboard.SetText(text); } catch { }
        }

        void HmDelete_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedMsgs();
            if (sel.Length == 0) return;
            if (Dialog.Confirm(this, "쪽지 삭제", sel.Length == 1 ? "선택한 쪽지를 삭제할까요?" : $"선택한 쪽지 {sel.Length}개를 삭제할까요?", "삭제", danger: true))
                _core.DeleteHistory(sel);
        }
    }

    public sealed class Relay : ICommand
    {
        readonly Action _a;
        public Relay(Action a) { _a = a; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object p) => true;
        public void Execute(object p) => _a();
    }
}
