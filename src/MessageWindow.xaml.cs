using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace BluePhone
{
    public partial class MessageWindow : Window
    {
        readonly Core _core;
        public ChatMessage Message { get; }
        public int Slot { get; set; }
        public bool UserMoved { get; set; }
        bool _reminder, _chatAlert;
        /// <summary>1:1 대화로 온 메시지 알림 — 답장 대신 '대화 열기'</summary>
        public bool IsChatAlert
        {
            get => _chatAlert;
            set
            {
                _chatAlert = value;
                if (!value) return;
                if (!Message.Urgent) SetTag("1:1 대화", "Accent", "AccentSoft");
                ReplyText.Text = "대화 열기";
                ChatBtn.Visibility = Visibility.Collapsed;
                if (_privacy < 2) Title = (Message.FromName ?? "") + "님의 대화";
            }
        }
        /// <summary>'다시 알림'으로 다시 뜬 팝업</summary>
        public bool IsReminder
        {
            get => _reminder;
            set { _reminder = value; if (value && !Message.Urgent) SetTag("다시 알림", "Accent", "AccentSoft"); }
        }
        bool _replying, _sending;
        string _lastSaved;

        public MessageWindow(Core core, ChatMessage m)
        {
            _core = core;
            Message = m;
            InitializeComponent();
            Chrome.Apply(this, caption: 64);
            Chrome.NoMaximize(this);
            // 헤더 영역만 드래그 가능하도록 캡션 높이 지정
            WindowChrome.GetWindowChrome(this).CaptionHeight = m.Urgent ? 67 : 64;

            Avatar.Fill = m.AvatarBrush;
            Body.Text = m.Text;
            RefreshHeader();
            core.PeerRenamed += OnPeerRenamed;
            Closed += (s, e) => core.PeerRenamed -= OnPeerRenamed;

            if (m.Outgoing)
            {
                SubLine.Text = "보낸 쪽지 · " + m.FullTime;
                Footnote.Text = m.Meta;
                ReplyText.Text = "다시 쓰기";
                foreach (var f in m.Files) f.State = "보낸 파일";
            }
            else
            {
                var sub = new[] { m.PeerDept, m.FullTime }.Where(x => !string.IsNullOrWhiteSpace(x));
                SubLine.Text = string.Join(" · ", sub);
                SubLine.ToolTip = $"{m.PeerIp}";
                if (m.To.Count > 1 && !m.Broadcast) Footnote.Text = $"나 외 {m.To.Count - 1}명에게 보낸 쪽지";
                foreach (var f in m.Files)
                    if (string.IsNullOrEmpty(f.State)) f.State = !string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath) ? "완료" : "";
            }

            if (m.Urgent) { UrgentLine.Visibility = Visibility.Visible; SetTag("긴급", "Danger", "DangerSoft"); }
            else if (m.Broadcast) SetTag("전체 공지", "Accent", "AccentSoft");
            else if (m.Auto) SetTag("자동응답", "Muted", "Hover");

            if (m.Broadcast || (m.Outgoing && m.ToIds.Count != 1) || _core.PeerOf(m) == null) ChatBtn.Visibility = Visibility.Collapsed;

            FileList.ItemsSource = m.Files;
            FileList.Visibility = m.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (m.Files.Count > 0 && string.IsNullOrWhiteSpace(m.Text)) Body.Visibility = Visibility.Collapsed;

            Activated += (s, e) => MarkRead();
            PreviewMouseDown += (s, e) =>
            {
                // 가려진 창: 버튼이 아닌 곳을 누르면 내용 표시 (닫기 버튼은 안 읽은 채로 닫힘)
                if (_veiled && !(FindButton(e.OriginalSource as DependencyObject))) Reveal();
                MarkRead();
                StopAutoClose();
            };
            Loaded += (s, e) => Animate();
            SizeChanged += (s, e) => { if (e.HeightChanged) _core.LayoutPopups(); };
            LocationChanged += (s, e) => { if (!_core.InLayout && IsLoaded) UserMoved = true; };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (_replying && ReplyBox.Text.Length > 0) { ReplyBox.Clear(); return; }
                    Close();
                }
                else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control && !_replying) { e.Handled = true; Reply_Click(null, null); }
            };
        }

        /// <summary>보낸 사람 이름·사진 (동료가 이름을 바꾸거나 별명을 붙이면 열린 창도 바로 갱신)</summary>
        void RefreshHeader()
        {
            var m = Message;
            if (_privacy >= 2) { ApplyAnonymousHeader(); return; }
            if (m.Outgoing)
            {
                var names = m.ToNames;
                FromName.Text = names.Count > 1 ? $"{names[0]} 외 {names.Count - 1}명" : (names.FirstOrDefault() ?? m.FromName ?? "");
                Title = "보낸 쪽지";
                Body.ToolTip = names.Count > 1 ? string.Join(", ", names) : null;
            }
            else
            {
                FromName.Text = m.FromName;
                Title = (m.FromName ?? "") + "님의 쪽지";
            }
            Initial.Text = string.IsNullOrEmpty(FromName.Text) ? "?" : FromName.Text.Substring(0, 1);
            Photo.ImageSource = m.AvatarImage;
        }

        void OnPeerRenamed(Peer p)
        {
            if (p != null && Message.Involves(p.Id)) RefreshHeader();
        }

        void SetTag(string text, string fg, string bg)
        {
            Tag1.Visibility = Visibility.Visible;
            Tag1Text.Text = text;
            Tag1Text.Foreground = (Brush)FindResource(fg);
            Tag1.Background = (Brush)FindResource(bg);
        }

        void Animate()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        void MarkRead()
        {
            if (_veiled) return;   // 내용을 보기 전에는 읽음으로 만들지 않음
            if (!Message.Outgoing && !Message.Read) _core.MarkRead(Message, fromPopup: true);
        }

        // ───────────── auto close ─────────────

        System.Windows.Threading.DispatcherTimer _autoTimer;
        DateTime _autoEnd;
        int _autoSecs;

        /// <summary>
        /// 설정한 시간이 지나면 창을 닫는다. 마우스를 올려 두거나 답장을 쓰는 동안에는 멈췄다가 다시 처음부터 센다.
        /// 닫혀도 쪽지는 '안 읽음'으로 쪽지함에 남는다.
        /// </summary>
        public void StartAutoClose(int seconds)
        {
            _autoSecs = seconds;
            _autoEnd = DateTime.Now.AddSeconds(seconds);
            AutoBar.Visibility = Visibility.Visible;
            AnimateBar(seconds);
            _autoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _autoTimer.Tick += (s, e) =>
            {
                if (Busy)
                {
                    _autoEnd = DateTime.Now.AddSeconds(_autoSecs);
                    if (AutoBar.Visibility == Visibility.Visible) { AutoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); AutoScale.ScaleX = 1; }
                    _paused = true;
                    return;
                }
                if (_paused) { _paused = false; AnimateBar(_autoSecs); }
                if (DateTime.Now >= _autoEnd) { StopAutoClose(); Close(); }
            };
            _autoTimer.Start();
            Closed += (s, e) => StopAutoClose();
        }
        bool _paused;

        bool Busy => IsMouseOver || _replying || _sending || (IsActive && ReplyBox.IsKeyboardFocusWithin) || ContextMenuOpen;
        bool ContextMenuOpen => RemindBtn.ContextMenu?.IsOpen == true || System.Windows.Input.Mouse.Captured != null && IsActive;

        void AnimateBar(int seconds) =>
            AutoScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds)));

        void StopAutoClose()
        {
            _autoTimer?.Stop();
            _autoTimer = null;
            AutoBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>다른 곳에서 읽었을 때 닫기 (답장을 쓰는 중이면 그대로 둠)</summary>
        public void CloseIfIdle()
        {
            if ((_replying && ReplyBox.Text.Trim().Length > 0) || _sending) { StopAutoClose(); return; }
            Close();
        }

        // ───────────── privacy ─────────────

        int _privacy;
        bool _veiled;

        /// <summary>
        /// 알림 개인정보 보호. 1 = 보낸 사람만 표시(내용 가림), 2 = 보낸 사람·내용 모두 가림.
        /// 누르거나 '내용 보기'·답장을 해야 보이며, 그 전에는 읽음 처리하지 않는다.
        /// </summary>
        public int Privacy
        {
            get => _privacy;
            set
            {
                _privacy = Message.Outgoing ? 0 : Math.Max(0, Math.Min(2, value));
                if (_privacy == 0) return;
                _veiled = true;
                Body.Visibility = Visibility.Collapsed;
                FileList.Visibility = Visibility.Collapsed;
                Veil.Visibility = Visibility.Visible;
                var kind = Message.Chat ? "메시지" : "쪽지";
                int n = Message.Files.Count;
                VeilText.Text = n > 0 ? $"새 {kind}가 도착했습니다 · 첨부 {n}개" : $"새 {kind}가 도착했습니다";
                if (_privacy >= 2) ApplyAnonymousHeader();
            }
        }

        void ApplyAnonymousHeader()
        {
            var kind = Message.Chat ? "새 메시지" : "새 쪽지";
            FromName.Text = kind;
            Title = kind;
            Initial.Text = "";
            Avatar.Fill = (Brush)FindResource("Faint");
            Photo.ImageSource = null;
            SubLine.Text = Message.FullTime;
            SubLine.ToolTip = null;
            Footnote.Text = "";
        }

        void Reveal_Click(object sender, RoutedEventArgs e) { Reveal(); MarkRead(); StopAutoClose(); }

        void Reveal()
        {
            if (!_veiled) return;
            _veiled = false;
            int was = _privacy;
            _privacy = 0;
            Veil.Visibility = Visibility.Collapsed;
            var m = Message;
            FileList.Visibility = m.Files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Body.Visibility = m.Files.Count > 0 && string.IsNullOrWhiteSpace(m.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (was >= 2)
            {
                Avatar.Fill = m.AvatarBrush;
                SubLine.Text = string.Join(" · ", new[] { m.PeerDept, m.FullTime }.Where(x => !string.IsNullOrWhiteSpace(x)));
                SubLine.ToolTip = m.PeerIp;
                if (m.To.Count > 1 && !m.Broadcast) Footnote.Text = $"나 외 {m.To.Count - 1}명에게 보낸 쪽지";
                RefreshHeader();
                if (_chatAlert) Title = (m.FromName ?? "") + "님의 대화";
            }
        }

        static bool FindButton(DependencyObject d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            MarkRead();
            Close();
        }

        void Chat_Click(object sender, RoutedEventArgs e)
        {
            MarkRead();
            var p = _core.PeerOf(Message);
            if (p == null) return;
            _core.OpenChat(p);
            Close();
        }

        void Remind_Click(object sender, RoutedEventArgs e)
        {
            var now = DateTime.Now;
            var menu = new ContextMenu { PlacementTarget = RemindBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            menu.Items.Add(new MenuItem { Header = "다시 알림", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            void Add(string label, DateTime at)
            {
                var mi = new MenuItem { Header = label, InputGestureText = at.Date == now.Date ? at.ToString("tt h:mm") : at.ToString("M/d tt h:mm") };
                mi.Click += (s, ev) =>
                {
                    _core.RemindLater(Message, at);
                    MarkRead();
                    _core.Notify("다시 알려 드릴게요", at.ToString("M월 d일 tt h:mm") + " 에 이 쪽지를 다시 띄웁니다.");
                    Close();
                };
                menu.Items.Add(mi);
            }
            Add("10분 뒤", now.AddMinutes(10));
            Add("30분 뒤", now.AddMinutes(30));
            Add("1시간 뒤", now.AddHours(1));
            var tomorrow = now.Date.AddDays(1).AddHours(9);
            Add("내일 오전 9시", tomorrow);
            menu.IsOpen = true;
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            Reveal();
            try { Clipboard.SetText(Message.Text ?? ""); Footnote.Text = "복사했습니다"; } catch { }
        }

        // ───────────── reply ─────────────

        Peer Target() => _core.PeerOf(Message);

        void Reply_Click(object sender, RoutedEventArgs e)
        {
            Reveal();
            if (_chatAlert) { Chat_Click(sender, e); return; }
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
                ReplyPane.Visibility = Visibility.Visible;
                ReplyText.Text = "보내기";
                Activate();
                ReplyBox.Focus();
                return;
            }
            SendReply();
        }

        void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; SendReply(); }
        }

        async void SendReply()
        {
            var text = ReplyBox.Text.Trim();
            if (text.Length == 0 || _sending) return;
            var peer = Target();
            if (peer == null) { Footnote.Text = "보낸 사람을 찾을 수 없습니다"; return; }
            _sending = true;
            ReplyBtn.IsEnabled = false;
            ReplyText.Text = "보내는 중…";
            var m = await _core.Send(new[] { peer }, text, null, false, false);
            if (m.Failed)
            {
                _sending = false;
                ReplyBtn.IsEnabled = true;
                ReplyText.Text = "다시 보내기";
                Footnote.Text = "전달하지 못했습니다";
                Footnote.Foreground = (Brush)FindResource("Danger");
                return;
            }
            Footnote.Text = "답장을 보냈습니다";
            ReplyText.Text = "보냄";
            await System.Threading.Tasks.Task.Delay(500);
            Close();
        }

        // ───────────── files ─────────────

        void Footnote_Click(object sender, MouseButtonEventArgs e)
        {
            if (_lastSaved != null) _core.OpenDownloads(_lastSaved);
        }

        void FileFolder_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is FileItem f) _core.OpenDownloads(f.LocalPath);
        }

        async void FileAction_Click(object sender, RoutedEventArgs e)
        {
            MarkRead();
            if (!(((Button)sender).Tag is FileItem f)) return;

            if (f.State == "보낸 파일" || f.State == "완료")
            {
                var path = f.LocalPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Footnote.Text = "파일을 찾을 수 없습니다"; return; }
                try
                {
                    if (f.State == "완료") Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    else _core.OpenDownloads(path);
                }
                catch { Process.Start("explorer.exe", $"/select,\"{path}\""); }
                return;
            }

            var dir = Store.DownloadDir(_core.Settings);
            string dest;
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.Name, InitialDirectory = dir, Title = "다른 이름으로 저장" };
                if (dlg.ShowDialog(this) != true) return;
                dest = dlg.FileName;
            }
            else dest = Store.UniquePath(dir, f.Name);

            try
            {
                await _core.DownloadFile(Message, f, dest);
                _lastSaved = dest;
                Footnote.Text = "저장했습니다 · 폴더 열기";
                Footnote.ToolTip = dest;
                Footnote.Cursor = Cursors.Hand;
                Footnote.Foreground = (Brush)FindResource("Accent");
            }
            catch (Exception ex)
            {
                Footnote.Text = ex.Message;
                Footnote.Foreground = (Brush)FindResource("Danger");
            }
        }
    }
}
