using System;
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
    /// <summary>한 동료와 주고받은 쪽지를 말풍선 대화로 보여 주는 창</summary>
    public partial class ChatWindow : Window
    {
        readonly Core _core;
        public Peer Peer { get; }
        readonly ListCollectionView _view;
        readonly ObservableCollection<ComposeWindow.Attachment> _files = new ObservableCollection<ComposeWindow.Attachment>();
        DateTime? _at;
        bool _sending;
        int _zipping;

        public ChatWindow(Core core, Peer peer)
        {
            _core = core;
            Peer = peer;
            _look = ChatLook.Apply(this, core.Settings);
            InitializeComponent();
            Chrome.Apply(this, caption: 58);
            Chrome.NoMaximize(this);

            // 처음에는 최근 대화만 (오래 쌓인 기록이 많아도 창이 바로 열리도록) — '이전 대화 더 보기'로 늘림
            _since = SinceFor(PageSize);
            _view = new ListCollectionView(core.History)
            {
                Filter = o => o is ChatMessage m && m.Chat && m.Ts >= _since && m.Involves(peer.Id),   // 쪽지는 쪽지함에서, 대화 창은 1:1 대화만
            };
            _view.SortDescriptions.Add(new SortDescription(nameof(ChatMessage.Ts), ListSortDirection.Ascending));
            List.ItemsSource = _view;
            FileList.ItemsSource = _files;

            foreach (ChatMessage m in _view)
                foreach (var f in m.Files)
                {
                    if (m.Outgoing) f.State = "보낸 파일";
                    else if (string.IsNullOrEmpty(f.State) && !string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath)) f.State = "완료";
                }

            ((INotifyCollectionChanged)_view).CollectionChanged += (s, e) => { RefreshEmpty(); if (e.Action == NotifyCollectionChangedAction.Add) ScrollToEnd(); };
            peer.PropertyChanged += Peer_PropertyChanged;
            Closed += (s, e) => peer.PropertyChanged -= Peer_PropertyChanged;
            RefreshHeader();
            RefreshEmpty();
            RefreshMore();
            // 이 창 안의 오른쪽 클릭 메뉴도 테마 색을 따르도록
            AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((s, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null;
                     d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                    if (d is FrameworkElement fe && fe.ContextMenu != null) { ChatLook.Attach(fe.ContextMenu, _look); break; }
            }), true);
            UpdateSendState();

            Input.TextChanged += (s, e) => UpdateSendState();
            _files.CollectionChanged += (s, e) => UpdateSendState();
            Activated += (s, e) =>
            {
                _core.LastChat = this;
                foreach (var m in _core.History.Where(x => x.Chat && !x.Outgoing && !x.Read && x.PeerId == Peer.Id).ToList()) _core.MarkRead(m);
            };
            Loaded += (s, e) => { ScrollToEnd(); Input.Focus(); };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && Input.Text.Length == 0) Close();
                else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemPlus || e.Key == Key.Add)) { Zoom(+1); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { Zoom(-1); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0)) { _core.SetChatLook(size: ChatLook.DefaultSize); e.Handled = true; }
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
        }

        void Peer_PropertyChanged(object sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(RefreshHeader));

        void RefreshHeader()
        {
            Title = Peer.DisplayName + " — 대화";
            HName.Text = Peer.DisplayName;
            HInitial.Text = Peer.Initial;
            HAvatar.Fill = Peer.AvatarBrush;
            HPhoto.ImageSource = Peer.AvatarImage;
            HDot.Fill = Peer.StatusBrush;
            HStatus.Text = string.Join(" · ", new[] { Peer.StatusText, Peer.Dept }.Where(x => !string.IsNullOrWhiteSpace(x)));
            HSecure.Visibility = Peer.Secure ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshEmpty() => Empty.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

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
            if (!Peer.IsOnline) Hint.Text = "오프라인 · 접속하면 전달";
        }

        // ───────────── input ─────────────

        void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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
            try { await _core.Send(new[] { Peer }, text, files, urgent, false, at: at, chat: true); }
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

        // ───────────── 꾸미기 ─────────────

        ResourceDictionary _look;

        public void ApplyLook() => _look = ChatLook.Apply(this, _core.Settings);

        // ───────────── 최근 대화만 먼저 ─────────────

        const int PageSize = 300;
        long _since;
        int _shown = PageSize;

        /// <summary>최근 n개의 대화가 들어가는 시작 시각</summary>
        long SinceFor(int n)
        {
            var ts = _core.History.Where(m => m.Chat && m.Involves(Peer.Id)).Select(m => m.Ts).OrderByDescending(t => t).Skip(n - 1).FirstOrDefault();
            return ts;   // 대화가 n개보다 적으면 0 → 전부
        }

        void RefreshMore()
        {
            bool more = _since > 0 && _core.History.Any(m => m.Chat && m.Ts < _since && m.Involves(Peer.Id));
            MoreBtn.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        }

        void More_Click(object sender, RoutedEventArgs e)
        {
            var first = List.Items.Count > 0 ? List.Items[0] : null;
            _shown += PageSize;
            _since = SinceFor(_shown);
            _view.Refresh();
            RefreshMore();
            if (first != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => List.ScrollIntoView(first)));
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
