using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace BluePhone
{
    using Packet = Dictionary<string, object>;

    public partial class App : Application
    {
        public static Core Core { get; private set; }
        Mutex _mutex;
        EventWaitHandle _showSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 단일 실행: 이미 떠 있으면 기존 창을 앞으로 불러오고 종료
            bool restart = e.Args.Any(a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase));

            // --data <폴더> : 설정·기록을 다른 폴더에 두고 독립 실행 (같은 PC에서 두 개 띄우기, 포터블 사용)
            var di = Array.FindIndex(e.Args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
            string suffix = "";
            if (di >= 0 && di + 1 < e.Args.Length)
            {
                Store.UseDir(e.Args[di + 1]);
                suffix = "." + AvatarStore.Hash(System.Text.Encoding.UTF8.GetBytes(Store.Dir.ToLowerInvariant()));
            }
            _mutex = new Mutex(false, @"Local\BluePhone.Instance" + suffix);
            bool first;
            try { first = _mutex.WaitOne(restart ? 8000 : 0); }
            catch (AbandonedMutexException) { first = true; }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BluePhone.Show" + suffix);
            if (!first)
            {
                _showSignal.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, a) =>
            {
                try { File.AppendAllText(Path.Combine(Store.Dir, "error.log"), DateTime.Now + "\n" + a.Exception + "\n\n"); } catch { }
                a.Handled = true;
            };

            Updater.Cleanup();
            Core = new Core();
            bool tray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            Core.Start(startHidden: tray || Core.Settings.StartHidden);

            var t = new Thread(() =>
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(new Action(() => Core.ShowMain()));
            }) { IsBackground = true };
            t.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Core?.Stop();
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }

    /// <summary>앱 전체 상태와 동작을 담당</summary>
    public sealed class Core
    {
        public AppSettings Settings { get; }
        public ObservableCollection<Peer> Peers { get; } = new ObservableCollection<Peer>();
        public ObservableCollection<ChatMessage> History { get; } = new ObservableCollection<ChatMessage>();
        public NetService Net { get; } = new NetService();

        public event Action StatusChanged;
        public event Action UnreadChanged;

        string _status;
        bool _autoAway;
        MainWindow _main;
        WinForms.NotifyIcon _tray;
        System.Drawing.Icon _iconNormal, _iconDot;
        DispatcherTimer _tick;
        DateTime _lastAnnounce;
        DispatcherTimer _updateTimer;
        UpdateWindow _updateWin;
        bool _checking;
        readonly List<MessageWindow> _popups = new List<MessageWindow>();

        readonly bool _firstRun;

        public Core()
        {
            _firstRun = !Store.HasSettings;
            Settings = Store.LoadSettings();
            Store.SaveSettings(Settings); // 고유 ID 고정
            _status = Settings.LastStatus == Status.Offline ? Status.Online : (Settings.LastStatus ?? Status.Online);
            ChatMessage.PeerLookup = id => Peers.FirstOrDefault(x => x.Id == id);
            Peers.CollectionChanged += (s, e) => { if (e.NewItems != null) foreach (Peer p in e.NewItems) Track(p); };
            foreach (var p in Store.LoadContacts())
            {
                p.Status_ = Status.Offline;
                Peers.Add(p);
            }
            var hist = Store.LoadHistory();
            foreach (var m in Enumerable.Reverse(hist)) History.Add(m);
            Store.RewriteHistory(hist); // 기록 파일 압축(중복 상태 줄 제거)

            // 예전에 보낸 첨부파일도 계속 받아갈 수 있게 다시 등록
            foreach (var m in hist.Where(x => x.Outgoing))
                foreach (var f in m.Files)
                    if (!string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath)) Net.Offer(f.Token, f.LocalPath);

            AvatarStore.Changed += id =>
            {
                if (id == null) { StatusChanged?.Invoke(); return; }
                Peers.FirstOrDefault(x => x.Id == id)?.RefreshAvatar();
                foreach (var m in History.Where(x => x.Involves(id))) m.RefreshNames();
                PeerRenamed?.Invoke(Peers.FirstOrDefault(x => x.Id == id));
            };
        }

        /// <summary>동료 이름(또는 내가 붙인 별명)이 바뀌면 쪽지함·열린 창에 즉시 반영</summary>
        void Track(Peer p)
        {
            p.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(Peer.DisplayName)) return;
                foreach (var m in History.Where(x => x.Involves(p.Id))) m.RefreshNames();
                PeerRenamed?.Invoke(p);
            };
        }

        /// <summary>동료의 이름·별명·사진이 바뀜</summary>
        public event Action<Peer> PeerRenamed;

        public string MyStatus => _status;
        public int Unread => History.Count(m => !m.Outgoing && !m.Read);
        static Dispatcher UI => Application.Current.Dispatcher;

        // ───────────────────────── lifecycle ─────────────────────────

        public void Start(bool startHidden)
        {
            if (!Store.CustomDir) Native.SetAutoStart(Settings.AutoStart); // 파일명/위치가 바뀌어도 시작 프로그램 경로 갱신

            Net.HelloFactory = MakeHello;
            // 수동 등록 PC + 현재 접속 중인 동료에게도 직접 알림 → 이름·사진 변경이 다른 대역에도 바로 전달
            Net.ExtraTargets = () => Settings.ManualHosts
                .Concat(Peers.Where(x => x.IsOnline && !string.IsNullOrEmpty(x.Ip)).Select(x => $"{x.Ip}:{x.Port}"))
                .ToList();
            Net.AvatarProvider = AvatarStore.MineBytes;
            Net.HelloReceived += (p, ip) => UI.BeginInvoke(new Action(() => OnHello(p, ip)));
            Net.ByeReceived += id => UI.BeginInvoke(new Action(() => OnBye(id)));
            Net.MessageReceived += (p, ip) => UI.BeginInvoke(new Action(() => OnMessage(p, ip)));
            Net.ReadReceived += (msg, from) => UI.BeginInvoke(new Action(() => OnRead(msg, from)));

            string netError = null;
            try { Net.Start(Settings.Port); }
            catch (Exception ex) { netError = ex.Message; }

            CreateTray();
            _main = new MainWindow(this);
            if (!startHidden) _main.Show();

            if (netError != null)
            {
                Dialog.Alert(_main.IsVisible ? _main : null, "네트워크를 시작하지 못했습니다",
                    $"포트 {Settings.Port} 를 열 수 없습니다. 다른 프로그램이 사용 중인지 확인하거나 설정에서 포트를 바꿔 주세요.\n\n{netError}");
            }

            if (_firstRun)
                UI.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ShowSettings(_main)));

            // 업데이트 직후 첫 실행이면 변경 사항 안내
            var cur = AppInfo.VersionText;
            if (!_firstRun && !string.IsNullOrEmpty(Settings.LastRunVersion) && Settings.LastRunVersion != cur)
            {
                var notes = AppInfo.ChangesFor(cur);
                if (!string.IsNullOrWhiteSpace(notes))
                    UI.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => new UpdateWindow(this, cur, notes).Show()));
            }
            Settings.LastRunVersion = cur;
            Store.SaveSettings(Settings);

            // 자동 업데이트: 시작 20초 뒤, 이후 6시간마다
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(6);
                if (Settings.AutoUpdate) await CheckForUpdates(manual: false);
            };
            _updateTimer.Start();

            Net.Announce(wantReply: true);
            _lastAnnounce = DateTime.Now;
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _tick.Tick += (s, e) => Tick();
            _tick.Start();
            UpdateTray();
        }

        public void Stop()
        {
            // 받아 둔 업데이트가 있으면 종료하면서 교체 (다음 실행부터 새 버전)
            if (Updater.ReadyPath != null) Updater.Apply(restart: false);
            try { Net.SayBye(Settings.Id); } catch { }
            Settings.LastStatus = _autoAway ? Status.Online : _status;
            Store.SaveSettings(Settings);
            Store.SaveContacts(Peers);
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Net.Dispose();
        }

        public void Quit()
        {
            _main?.SavePlacement();
            Application.Current.Shutdown();
        }

        void Tick()
        {
            // 자동 자리비움
            var idle = Native.IdleTime();
            if (Settings.AutoAwayMinutes > 0)
            {
                if (_status == Status.Online && idle.TotalMinutes >= Settings.AutoAwayMinutes)
                {
                    _autoAway = true; SetStatus(Status.Away, auto: true);
                }
                else if (_autoAway && _status == Status.Away && idle.TotalSeconds < 5)
                {
                    _autoAway = false; SetStatus(Status.Online, auto: true);
                }
            }

            if ((DateTime.Now - _lastAnnounce).TotalSeconds >= 25)
            {
                Net.Announce(wantReply: false);
                _lastAnnounce = DateTime.Now;
            }

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            foreach (var p in Peers)
                if (p.IsOnline && now - p.LastSeenTs > 80_000) p.Status_ = Status.Offline;
        }

        Packet MakeHello() => new Packet
        {
            ["app"] = NetService.AppTag,
            ["v"] = NetService.Proto,
            ["id"] = Settings.Id,
            ["name"] = Settings.Name,
            ["dept"] = Settings.Dept,
            ["note"] = Settings.Note,
            ["status"] = _status,
            ["port"] = Settings.Port,
            ["host"] = Environment.MachineName,
            ["avatar"] = Settings.AvatarHash ?? "",
            ["ver"] = AppInfo.VersionText,
        };

        // ───────────────────────── update ─────────────────────────

        /// <summary>GitHub 최신 릴리스 확인. 자동 모드에서는 조용히 내려받은 뒤 '준비 완료' 안내만 띄움</summary>
        public async Task<string> CheckForUpdates(bool manual)
        {
            if (_checking) return "확인 중입니다";
            _checking = true;
            try
            {
                if (Updater.Ready != null) { ShowUpdate(Updater.Ready, true); return "업데이트가 준비되어 있습니다"; }
                var r = await Updater.CheckAsync();
                if (!Updater.IsNewer(r)) return $"최신 버전입니다 ({AppInfo.VersionText})";
                if (!manual && r.VersionText == Settings.SkippedVersion) return null;
                if (manual) { ShowUpdate(r, true); return $"새 버전 {r.VersionText}"; }
                try { await Updater.DownloadAsync(r, null); }
                catch { }
                ShowUpdate(Updater.Ready ?? r, false);
                return null;
            }
            catch (Exception ex)
            {
                return manual ? "업데이트 서버에 연결하지 못했습니다: " + ex.Message : null;
            }
            finally { _checking = false; }
        }

        void ShowUpdate(ReleaseInfo r, bool activate)
        {
            _updateWin?.Close();
            _updateWin = new UpdateWindow(this, r) { ShowActivated = activate, Topmost = !activate };
            _updateWin.Closed += (s, e) => { _updateWin = null; _main?.ShowUpdateBadge(Updater.Ready != null); };
            _main?.ShowUpdateBadge(Updater.Ready != null);
            _updateWin.Show();
        }

        public void OpenDownloads(string select = null)
        {
            try
            {
                if (select != null && File.Exists(select)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{select}\"");
                else System.Diagnostics.Process.Start("explorer.exe", $"\"{Store.DownloadDir(Settings)}\"");
            }
            catch { }
        }

        public void ApplyUpdateAndRestart()
        {
            _main?.SavePlacement();
            if (Updater.Apply(restart: true)) Application.Current.Shutdown();
            else Dialog.Alert(null, "업데이트를 적용하지 못했습니다", "실행 파일을 교체할 수 없습니다. 프로그램을 종료하면 다시 시도합니다.");
        }

        public void SetStatus(string s, bool auto = false)
        {
            if (!auto) _autoAway = false;
            if (_status == s) return;
            _status = s;
            if (!auto) { Settings.LastStatus = s; Store.SaveSettings(Settings); }
            Net.Announce(false);
            _lastAnnounce = DateTime.Now;
            StatusChanged?.Invoke();
            UpdateTray();
        }

        public void ProfileChanged()
        {
            Store.SaveSettings(Settings);
            Net.Announce(false);
            StatusChanged?.Invoke();
        }

        public void Rescan() { Net.Announce(true); _lastAnnounce = DateTime.Now; }

        // ───────────────────────── peers ─────────────────────────

        Peer FindOrAdd(string id)
        {
            var p = Peers.FirstOrDefault(x => x.Id == id);
            if (p == null) { p = new Peer { Id = id }; Peers.Add(p); }
            return p;
        }

        void OnHello(Packet h, IPAddress ip)
        {
            var id = NetService.Str(h, "id");
            if (string.IsNullOrEmpty(id) || id == Settings.Id) return;
            var p = FindOrAdd(id);
            bool wasOffline = !p.IsOnline;
            p.Name = NetService.Str(h, "name");
            p.Dept = NetService.Str(h, "dept");
            p.Note = NetService.Str(h, "note");
            p.Host = NetService.Str(h, "host");
            p.Ip = ip.ToString();
            p.Port = NetService.Int(h, "port", Settings.Port);
            p.LastSeenTs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            p.Status_ = NetService.Str(h, "status") ?? Status.Online;
            p.RefreshSub();

            // 프로필 사진: 해시가 바뀌었을 때만 받아 옴
            var hash = NetService.Str(h, "avatar") ?? "";
            if (hash != (p.AvatarHash ?? "") || (hash.Length > 0 && !AvatarStore.Has(id)))
            {
                p.AvatarHash = hash;
                if (hash.Length == 0) AvatarStore.SavePeer(id, null);
                else FetchAvatar(p);
                wasOffline = true;
            }
            if (wasOffline) Store.SaveContacts(Peers);
        }

        async void FetchAvatar(Peer p)
        {
            try
            {
                var r = await Net.Request(p.Ip, p.Port, new Packet { ["t"] = "avatar" });
                var b64 = NetService.Str(r, "data");
                AvatarStore.SavePeer(p.Id, string.IsNullOrEmpty(b64) ? null : Convert.FromBase64String(b64));
            }
            catch { p.AvatarHash = ""; }
        }

        public void SetAlias(Peer p, string alias)
        {
            p.Alias = alias;
            Store.SaveContacts(Peers);
        }

        void OnBye(string id)
        {
            var p = Peers.FirstOrDefault(x => x.Id == id);
            if (p != null) p.Status_ = Status.Offline;
        }

        public void RemovePeer(Peer p)
        {
            Peers.Remove(p);
            Store.SaveContacts(Peers);
        }

        // ───────────────────────── messaging ─────────────────────────

        public async Task<ChatMessage> Send(IList<Peer> to, string text, IList<string> files, bool urgent, bool broadcast, bool auto = false)
        {
            var m = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Outgoing = true,
                PeerId = to.Count == 1 ? to[0].Id : null,
                PeerName = to.Count == 1 ? to[0].DisplayName : null,
                PeerDept = to.Count == 1 ? to[0].Dept : null,
                PeerIp = to.Count == 1 ? to[0].Ip : null,
                PeerPort = to.Count == 1 ? to[0].Port : 0,
                Text = text,
                Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Urgent = urgent,
                Broadcast = broadcast,
                Auto = auto,
                To = to.Select(p => p.DisplayName).ToList(),
                ToIds = to.Select(p => p.Id).ToList(),
            };
            foreach (var f in files ?? new List<string>())
            {
                var fi = new FileInfo(f);
                if (!fi.Exists) continue;
                m.Files.Add(new FileItem { Name = fi.Name, Size = fi.Length, Token = Net.Offer(fi.FullName), LocalPath = fi.FullName });
            }

            var packet = new Packet
            {
                ["t"] = "msg",
                ["id"] = m.Id,
                ["from"] = Settings.Id,
                ["name"] = Settings.Name,
                ["dept"] = Settings.Dept,
                ["port"] = Settings.Port,
                ["text"] = text,
                ["ts"] = m.Ts,
                ["urgent"] = urgent,
                ["broadcast"] = broadcast,
                ["auto"] = auto,
                ["to"] = m.To,
                ["files"] = m.Files.Select(f => new Packet { ["name"] = f.Name, ["size"] = f.Size, ["token"] = f.Token }).ToList(),
            };

            History.Insert(0, m);
            var tasks = to.Select(async p =>
            {
                try
                {
                    var r = await Net.Request(p.Ip, p.Port, new Packet(packet)).ConfigureAwait(false);
                    return NetService.Bool(r, "ok") ? null : p.DisplayName;
                }
                catch { return p.DisplayName; }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            m.FailedTo = results.Where(x => x != null).ToList();
            m.Failed = m.FailedTo.Count == to.Count;
            m.Touch();
            Store.AppendHistory(m);
            return m;
        }

        void OnMessage(Packet p, IPAddress ip)
        {
            var from = NetService.Str(p, "from");
            if (string.IsNullOrEmpty(from)) return;
            var id = NetService.Str(p, "id");
            if (History.Any(x => !x.Outgoing && x.Id == id)) return; // 중복 수신 방지

            var peer = FindOrAdd(from);
            peer.Name = NetService.Str(p, "name");
            peer.Dept = NetService.Str(p, "dept");
            peer.Ip = ip.ToString();
            peer.Port = NetService.Int(p, "port", Settings.Port);
            if (!peer.IsOnline) { peer.Status_ = Status.Online; peer.LastSeenTs = DateTimeOffset.Now.ToUnixTimeMilliseconds(); }

            var m = new ChatMessage
            {
                Id = id,
                Outgoing = false,
                PeerId = from,
                PeerName = peer.DisplayName,
                PeerDept = peer.Dept,
                PeerIp = peer.Ip,
                PeerPort = peer.Port,
                Text = NetService.Str(p, "text") ?? "",
                Ts = NetService.Long(p, "ts") is long ts && ts > 0 ? ts : DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Urgent = NetService.Bool(p, "urgent"),
                Broadcast = NetService.Bool(p, "broadcast"),
                Auto = NetService.Bool(p, "auto"),
                To = NetService.StrList(p, "to"),
                Files = NetService.ObjList(p, "files").Select(f => new FileItem
                {
                    Name = Path.GetFileName(NetService.Str(f, "name") ?? "file"),
                    Size = NetService.Long(f, "size"),
                    Token = NetService.Str(f, "token"),
                }).ToList(),
            };
            History.Insert(0, m);
            Store.AppendHistory(m);
            peer.HasUnread = true;
            UnreadChanged?.Invoke();
            UpdateTray();

            bool dnd = _status == Status.Dnd && !m.Urgent;
            if (!dnd)
            {
                if (Settings.Sound) Native.PlayChime(m.Urgent);
                ShowPopup(m);
            }

            // 부재 시 자동응답
            if (!m.Auto && !string.IsNullOrWhiteSpace(Settings.AutoReply) && _status != Status.Online)
                _ = Send(new[] { peer }, Settings.AutoReply.Trim(), null, false, false, auto: true);
        }

        public void MarkRead(ChatMessage m)
        {
            if (m.Outgoing || m.Read) return;
            m.Read = true;
            Store.AppendHistory(m);
            var peer = Peers.FirstOrDefault(p => p.Id == m.PeerId);
            if (peer != null) peer.HasUnread = History.Any(x => !x.Outgoing && !x.Read && x.PeerId == peer.Id);
            UnreadChanged?.Invoke();
            UpdateTray();
            if (Settings.ReadReceipts && peer != null && !m.Auto)
            {
                var pk = new Packet { ["t"] = "read", ["msg"] = m.Id, ["id"] = Settings.Id };
                _ = Task.Run(async () => { try { await Net.Request(peer.Ip, peer.Port, pk); } catch { } });
            }
        }

        public void MarkAllRead()
        {
            foreach (var m in History.Where(x => !x.Outgoing && !x.Read).ToList()) MarkRead(m);
        }

        void OnRead(string msgId, string fromId)
        {
            var m = History.FirstOrDefault(x => x.Outgoing && x.Id == msgId);
            if (m == null || string.IsNullOrEmpty(fromId)) return;
            if (!m.ReadBy.Contains(fromId)) m.ReadBy.Add(fromId);
            m.Read = true;
            m.Touch();
            Store.AppendHistory(m);
        }

        public void DeleteHistory(IEnumerable<ChatMessage> items)
        {
            foreach (var m in items.ToList()) History.Remove(m);
            Store.RewriteHistory(History.Reverse());
            UnreadChanged?.Invoke();
            UpdateTray();
        }

        public async Task DownloadFile(ChatMessage m, FileItem f, string dest)
        {
            f.State = "받는 중";
            f.Progress = 0;
            var prog = new Progress<double>(v => f.Progress = v);
            try
            {
                await Net.Download(m.PeerIp, m.PeerPort, f.Token, dest, prog, CancellationToken.None);
                f.LocalPath = dest;
                f.State = "완료";
                Store.AppendHistory(m);
            }
            catch (Exception ex)
            {
                f.State = "실패";
                throw new Exception(ex.Message);
            }
        }

        // ───────────────────────── windows ─────────────────────────

        public void ShowMain()
        {
            Native.BringToFront(_main);
        }

        public Peer PeerOf(ChatMessage m) => Peers.FirstOrDefault(p => p.Id == m.PeerId);

        public void Compose(IList<Peer> to, bool broadcast = false, string quote = null)
        {
            var w = new ComposeWindow(this, to, broadcast, quote);
            w.Show();
            w.Activate();
        }

        public void ShowPopup(ChatMessage m, bool activate = false)
        {
            var existing = _popups.FirstOrDefault(p => p.Message == m);
            if (existing != null) { Native.BringToFront(existing); return; }

            var w = new MessageWindow(this, m);
            w.Closed += (s, e) => { _popups.Remove(w); LayoutPopups(); };
            w.Slot = _popups.Count == 0 ? 0 : _popups.Max(p => p.Slot) + 1;
            _popups.Add(w);
            w.ShowActivated = activate || !Settings.PopupNoFocus;
            w.Topmost = true;
            var area = SystemParameters.WorkArea;
            w.Left = area.Right - w.Width - 16;
            w.Top = area.Bottom - 240;
            w.Show();
            LayoutPopups();
            if (m.Urgent && !w.IsActive) Native.Flash(w, untilFocus: true);
        }

        public bool InLayout { get; private set; }

        /// <summary>쪽지 창을 화면 오른쪽 아래부터 위로 쌓아 배치 (넘치면 계단식)</summary>
        public void LayoutPopups()
        {
            InLayout = true;
            try
            {
                var area = SystemParameters.WorkArea;
                double bottom = area.Bottom - 16;
                int cascade = 0;
                foreach (var w in _popups.Where(p => !p.UserMoved).OrderBy(p => p.Slot))
                {
                    double h = w.ActualHeight > 0 ? w.ActualHeight : 240;
                    double top = bottom - h;
                    if (top < area.Top + 16)
                    {
                        // 화면이 가득 차면 맨 아래 위치에서 왼쪽 위로 비켜 겹침
                        cascade++;
                        w.Left = area.Right - w.ActualWidth - 16 - cascade * 28;
                        w.Top = area.Bottom - 16 - h - cascade * 28;
                        continue;
                    }
                    w.Left = area.Right - (w.ActualWidth > 0 ? w.ActualWidth : w.Width) - 16;
                    w.Top = top;
                    bottom = top - 10;
                }
            }
            finally { InLayout = false; }
        }

        public void ShowSettings(Window owner)
        {
            var w = new SettingsWindow(this) { Owner = owner?.IsVisible == true ? owner : null };
            w.ShowDialog();
        }

        // ───────────────────────── tray ─────────────────────────

        void CreateTray()
        {
            int sz = WinForms.SystemInformation.SmallIconSize.Width;
            _iconNormal = Native.LoadIcon(sz);
            _iconDot = Native.IconWithDot(sz);
            _tray = new WinForms.NotifyIcon { Icon = _iconNormal, Visible = true, Text = "파란전화기" };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) UI.BeginInvoke(new Action(OnTrayClick));
            };
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Right) UI.BeginInvoke(new Action(ShowTrayMenu));
            };
        }

        void OnTrayClick()
        {
            var unread = History.Where(m => !m.Outgoing && !m.Read).OrderBy(m => m.Ts).ToList();
            if (unread.Count > 0)
                foreach (var m in unread.Take(6)) ShowPopup(m, activate: true);
            else if (_main.IsVisible && _main.IsActive) _main.Hide();
            else ShowMain();
        }

        void ShowTrayMenu()
        {
            // WPF 스타일 메뉴를 트레이 위치에 띄움
            var menu = TrayMenu.Build(this);
            _main.ShowTrayMenu(menu);
        }

        public void UpdateTray()
        {
            if (_tray == null) return;
            int n = Unread;
            _tray.Icon = n > 0 ? _iconDot : _iconNormal;
            var text = $"파란전화기 {AppInfo.VersionText} — {Status.Label(_status)}";
            if (n > 0) text += $"\n안 읽은 쪽지 {n}개";
            _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
    }
}
