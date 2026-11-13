using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BluePhone
{
    public partial class ComposeWindow : Window
    {
        public sealed class Attachment
        {
            public string Path { get; set; }
            public string Source { get; set; }
            public string Name => System.IO.Path.GetFileName(Path);
            public string SizeText { get; set; }
            public string Kind { get; set; }
        }

        readonly Core _core;
        readonly ObservableCollection<Peer> _to = new ObservableCollection<Peer>();
        readonly ObservableCollection<Attachment> _files = new ObservableCollection<Attachment>();
        readonly bool _broadcast;
        string _quote;
        bool _sending;

        public ComposeWindow(Core core, IList<Peer> to, bool broadcast, string quote)
        {
            _core = core;
            _broadcast = broadcast;
            InitializeComponent();
            Chrome.Apply(this);
            Chrome.NoMaximize(this);

            foreach (var p in to ?? new Peer[0]) _to.Add(p);
            ToList.ItemsSource = _to;
            FileList.ItemsSource = _files;

            if (broadcast)
            {
                Heading.Text = "전체 공지";
                Title = "전체 공지";
                ToList.Visibility = Visibility.Collapsed;
                AddBtn.Visibility = Visibility.Collapsed;
                BroadcastInfo.Visibility = Visibility.Visible;
                BroadcastInfo.Text = $"접속 중인 동료 {_to.Count}명 전체";
            }
            else if (_to.Count == 1)
            {
                Heading.Text = _to[0].DisplayName + "님에게";
                Title = Heading.Text;
            }

            if (!string.IsNullOrWhiteSpace(quote))
            {
                _quote = quote.Trim();
                QuoteBar.Visibility = Visibility.Visible;
                QuoteText.Text = _quote.Replace("\r", "").Replace("\n", "  ");
            }

            UpdateSendState();
            _to.CollectionChanged += (s, e) => UpdateSendState();
            Body.TextChanged += (s, e) => UpdateSendState();
            _files.CollectionChanged += (s, e) => UpdateSendState();

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible; };
            DragLeave += (s, e) => DropOverlay.Visibility = Visibility.Collapsed;
            // 본문 입력칸 위에 놓아도 첨부되도록 터널링 이벤트에서 처리
            PreviewDrop += (s, e) =>
            {
                DropOverlay.Visibility = Visibility.Collapsed;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { AddFiles(paths); e.Handled = true; }
            };
            PreviewDragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };

            Loaded += (s, e) => { Body.Focus(); Body.CaretIndex = Body.Text.Length; };
            KeyDown += (s, e) => { if (e.Key == Key.Escape && Body.Text.Length == 0 && _files.Count == 0) Close(); };
        }

        DateTime? _at;

        public void Attach(string path) => AddFiles(new[] { path });

        void Capture_Click(object sender, RoutedEventArgs e) => _core.StartCapture(this);

        void Emoji_Click(object sender, RoutedEventArgs e) => Emoji.ShowPicker(EmojiBtn, Body);

        void Schedule_Click(object sender, RoutedEventArgs e)
        {
            Schedule.Pick(ScheduleBtn, this, at =>
            {
                _at = at;
                ScheduleBar.Visibility = Visibility.Visible;
                ScheduleText.Text = "예약 · " + at.ToString("M월 d일 (ddd) tt h:mm") + " 에 보냅니다";
                if (!_sending) SendText.Text = "예약";
            });
        }

        void ClearSchedule_Click(object sender, RoutedEventArgs e)
        {
            _at = null;
            ScheduleBar.Visibility = Visibility.Collapsed;
            if (!_sending) SendText.Text = "보내기";
        }

        /// <summary>클립보드의 파일 목록 또는 이미지(PNG 로 저장) — 없으면 null</summary>
        internal static IList<string> FromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) return Clipboard.GetFileDropList().Cast<string>().ToList();
                if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                {
                    var img = Clipboard.GetImage();
                    var dir = Path.Combine(Path.GetTempPath(), "BluePhone");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, $"캡처 {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                    using (var fs = File.Create(path))
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(img));
                        enc.Save(fs);
                    }
                    return new[] { path };
                }
            }
            catch { }
            return null;
        }

        void UpdateSendState()
        {
            bool hasContent = Body.Text.Trim().Length > 0 || _files.Count > 0;
            SendBtn.IsEnabled = !_sending && hasContent && _to.Count > 0 && _zipping == 0;
            if (_to.Count == 0) Hint.Text = "받는 사람을 추가하세요";
            else if (!hasContent) Hint.Text = "Ctrl+Enter";
            else Hint.Text = _to.Count > 1 ? $"{_to.Count}명 · Ctrl+Enter" : "Ctrl+Enter";
        }

        // ───────────── recipients ─────────────

        void RemoveTo_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is Peer p) _to.Remove(p);
        }

        void AddTo_Click(object sender, RoutedEventArgs e)
        {
            var candidates = _core.Peers.Where(p => !_to.Contains(p))
                .OrderBy(p => p.SortKey).ThenBy(p => p.Group).ThenBy(p => p.DisplayName).ToList();
            var m = new ContextMenu { PlacementTarget = AddBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 360 };
            if (candidates.Count == 0)
                m.Items.Add(new MenuItem { Header = "추가할 동료가 없습니다", IsEnabled = false });
            var online = candidates.Where(p => p.IsOnline).ToList();
            bool any = false;
            if (online.Count > 1)
            {
                var all = new MenuItem { Header = $"접속 중인 동료 모두 ({online.Count})", FontWeight = FontWeights.SemiBold };
                all.Click += (o, ev) => { foreach (var p in online) _to.Add(p); };
                m.Items.Add(all);
                any = true;
            }
            foreach (var g in _core.Settings.Groups)
            {
                var members = _core.GroupMembers(g).Where(p => !_to.Contains(p)).ToList();
                if (members.Count == 0) continue;
                var gi = new MenuItem { Header = $"그룹 · {g.Name} ({members.Count})", FontWeight = FontWeights.SemiBold };
                gi.Click += (o, ev) => { foreach (var p in members) if (!_to.Contains(p)) _to.Add(p); };
                m.Items.Add(gi);
                any = true;
            }
            if (any) m.Items.Add(new Separator());
            foreach (var p in candidates.Take(60))
            {
                var peer = p;
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = p.DisplayName });
                if (!string.IsNullOrWhiteSpace(p.Dept))
                    head.Children.Add(new TextBlock { Text = "  " + p.Dept, Foreground = (Brush)FindResource("Faint") });
                var mi = new MenuItem
                {
                    Header = head,
                    Icon = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = p.StatusBrush },
                };
                mi.Click += (o, ev) => _to.Add(peer);
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        }

        // ───────────── attachments ─────────────

        void Attach_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true, Title = "첨부할 파일 선택", Filter = "모든 파일 (*.*)|*.*",
                DereferenceLinks = false, CheckFileExists = true, ValidateNames = false,
            };
            if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
        }

        /// <summary>모든 형식의 파일을 받음. 폴더는 ZIP 으로 묶어서 첨부</summary>
        async void AddFiles(IEnumerable<string> paths)
        {
            foreach (var p in paths.ToList())
            {
                if (_files.Any(f => string.Equals(f.Source ?? f.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                if (Directory.Exists(p))
                {
                    var item = new Attachment { Path = p, Source = p, SizeText = "압축 중…", Kind = "폴더" };
                    _files.Add(item);
                    _zipping++;
                    UpdateSendState();
                    try
                    {
                        var zip = await System.Threading.Tasks.Task.Run(() => ZipFolder(p));
                        var idx = _files.IndexOf(item);
                        if (idx >= 0)
                            _files[idx] = new Attachment { Path = zip, Source = p, SizeText = FileItem.FormatSize(new FileInfo(zip).Length), Kind = "폴더 · ZIP" };
                    }
                    catch (Exception ex)
                    {
                        _files.Remove(item);
                        Dialog.Alert(this, "폴더를 압축하지 못했습니다", ex.Message);
                    }
                    finally { _zipping--; UpdateSendState(); }
                    continue;
                }
                if (!File.Exists(p)) continue;
                long size;
                try { size = new FileInfo(p).Length; } catch { continue; }
                _files.Add(new Attachment { Path = p, SizeText = FileItem.FormatSize(size) });
            }
        }

        int _zipping;

        internal static string ZipFolder(string dir)
        {
            var name = new DirectoryInfo(dir).Name;
            var tmpDir = Path.Combine(Path.GetTempPath(), "BluePhone", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            var zip = Path.Combine(tmpDir, name + ".zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(dir, zip, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true,
                entryNameEncoding: System.Text.Encoding.UTF8);
            return zip;
        }

        void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is Attachment a) _files.Remove(a);
        }

        void DropQuote_Click(object sender, RoutedEventArgs e)
        {
            _quote = null;
            QuoteBar.Visibility = Visibility.Collapsed;
        }

        void Body_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (SendBtn.IsEnabled) Send_Click(null, null);
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var pasted = FromClipboard();
                if (pasted != null) { AddFiles(pasted); e.Handled = true; }
            }
        }

        // ───────────── send ─────────────

        async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_sending) return;
            _sending = true;
            UpdateSendState();
            SendText.Text = "보내는 중…";

            var text = Body.Text.TrimEnd();
            if (_quote != null)
            {
                var lines = _quote.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(3)
                                  .Select(l => "> " + (l.Length > 80 ? l.Substring(0, 80) + "…" : l));
                text = string.Join("\n", lines) + "\n\n" + text;
            }

            var targets = _to.ToList();
            var m = await _core.Send(targets, text, _files.Select(f => f.Path).ToList(), Urgent.IsChecked == true, _broadcast, at: _at);

            if (m.Failed)
            {
                _sending = false;
                SendText.Text = "다시 보내기";
                UpdateSendState();
                _core.DeleteHistory(new[] { m });
                Dialog.Alert(this, "전달하지 못했습니다",
                    "상대 PC에 연결할 수 없습니다. 상대가 프로그램을 종료했거나 방화벽이 막고 있을 수 있습니다.");
                return;
            }
            Close();
            if (m.PendingTo.Count > 0 && !m.Scheduled)
                Toast(m.PendingTo.Count == targets.Count && targets.Count == 1
                    ? $"{targets[0].DisplayName}님이 지금 오프라인이라 대기열에 넣었습니다. 접속하면 자동으로 전달됩니다."
                    : $"{m.PendingTo.Count}명은 오프라인이라 접속하면 자동으로 전달됩니다.");
            if (m.FailedTo.Count > 0)
                Dialog.Alert(null, "일부에게 전달하지 못했습니다", string.Join(", ", m.FailedTo) + "\n\n접속이 끊겼거나 방화벽이 막고 있을 수 있습니다.");
        }

        void Toast(string text) => _core.Notify("대기열에 넣었습니다", text);

        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if ((Body.Text.Trim().Length > 0 || _files.Count > 0) && !_sending &&
                !Dialog.Confirm(this, "작성 중인 쪽지를 닫을까요?", "입력한 내용은 저장되지 않습니다.", "닫기"))
                return;
            Close();
        }
    }
}
