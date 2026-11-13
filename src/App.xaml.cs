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
            Native.ClearMarkOfTheWeb();
            Core = new Core();
            bool relaunched = e.Args.Any(a => a.Equals("--relaunched", StringComparison.OrdinalIgnoreCase));
            // 이전 버전(2.1.0)이 소켓 핸들을 상속시킨 채 재시작한 경우: 셸로 한 번 더 띄워 상속 핸들을 끊는다
            Core.RelaunchOnNetFail = () =>
            {
                if (!restart || relaunched) return false;
                var args = string.Join(" ", e.Args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)) + " --relaunched";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Native.ExePath, args) { UseShellExecute = true }); }
                catch { return false; }
                Dispatcher.BeginInvoke(new Action(Shutdown));
                return true;
            };
            bool tray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            Core.Start(startHidden: tray || Core.Settings.StartHidden, retryNetwork: restart);

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
        public NetService Net { get; private set; } = new NetService();

        public event Action StatusChanged;
        public Func<bool> RelaunchOnNetFail { get; set; }
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

        public void Start(bool startHidden, bool retryNetwork = false)
        {
            if (!Store.CustomDir) Native.SetAutoStart(Settings.AutoStart); // 파일명/위치가 바뀌어도 시작 프로그램 경로 갱신

            // 수동 등록 PC + 현재 접속 중인 동료에게도 직접 알림 → 이름·사진 변경이 다른 대역에도 바로 전달 (ExtraTargets)

            string netError = null;
            for (int attempt = 0; ; attempt++)
            {
                try { Net.Start(Settings.Port); netError = null; break; }
                catch (Exception ex)
                {
                    netError = ex.Message;
                    Net.Dispose();
                    Net = new NetService();
                    if (!retryNetwork || attempt >= 15) break;
                    Thread.Sleep(400);
                }
            }
            Net.HelloFactory = MakeHello;
            Net.ExtraTargets = () => Settings.ManualHosts
                .Concat(Peers.Where(x => x.IsOnline && !string.IsNullOrEmpty(x.Ip)).Select(x => $"{x.Ip}:{x.Port}"))
                .ToList();
            Net.AvatarProvider = AvatarStore.MineBytes;
            HookNet();
            if (netError != null && RelaunchOnNetFail != null && RelaunchOnNetFail()) return;

            // 예전에 보낸 첨부파일도 계속 받아갈 수 있게 다시 등록
            foreach (var m in History.Where(x => x.Outgoing))
                foreach (var f in m.Files)
                    if (!string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath)) Net.Offer(f.Token, f.LocalPath);

            CreateTray();
            ApplyHotkey();
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
                await CheckForUpdates(manual: false);
            };
            _updateTimer.Start();

            Net.Announce(wantReply: true);
            _lastAnnounce = DateTime.Now;
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _tick.Tick += (s, e) => Tick();
            _tick.Start();
            UpdateTray();
        }

        void HookNet()
        {
            Net.HelloReceived += (p, ip) => UI.BeginInvoke(new Action(() => OnHello(p, ip)));
            Net.ByeReceived += id => UI.BeginInvoke(new Action(() => OnBye(id)));
            Net.MessageReceived += (p, ip) => UI.BeginInvoke(new Action(() => OnMessage(p, ip)));
            Net.ReadReceived += (msg, from) => UI.BeginInvoke(new Action(() => OnRead(msg, from)));
        }

        HotKey _hotkey;

        /// <summary>화면 캡처 전역 단축키 Ctrl+Alt+S (설정에서 끌 수 있음)</summary>
        public void ApplyHotkey()
        {
            _hotkey?.Dispose();
            _hotkey = null;
            HotkeyFailed = false;
            if (!Settings.CaptureHotkey) return;
            try
            {
                _hotkey = new HotKey(HotKey.Ctrl | HotKey.Alt, System.Windows.Input.Key.S, () => StartCapture());
                if (!_hotkey.Registered) { _hotkey.Dispose(); _hotkey = null; HotkeyFailed = true; }
            }
            catch { HotkeyFailed = true; }
        }
        public bool HotkeyFailed { get; private set; }

        /// <summary>트레이 풍선 알림</summary>
        public void Notify(string title, string text)
        {
            try { _tray?.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.None); } catch { }
        }

        public void Stop()
        {
            _hotkey?.Dispose();
            // 받아 둔 업데이트가 있으면 종료하면서 교체 (다음 실행부터 새 버전)
            if (Updater.ReadyPath != null) Updater.Apply(restart: false);
            try { Net.SayBye(Settings.Id); } catch { }
            if (_main == null) { Net.Dispose(); return; }
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
            RunSchedules();
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
            ["enc"] = 1,
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
                // 건너뛴 버전·자동 업데이트 꺼짐이어도 상단 메뉴에는 새 버전 알림을 표시
                SetAvailable(r);
                if (!manual && (r.VersionText == Settings.SkippedVersion || !Settings.AutoUpdate)) return null;
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

        /// <summary>새 버전이 있으면(받기 전이라도) 상단 메뉴에 알림</summary>
        public ReleaseInfo AvailableUpdate { get; private set; }

        void SetAvailable(ReleaseInfo r)
        {
            AvailableUpdate = r;
            _main?.ShowUpdateBadge(r, Updater.Ready != null);
            UpdateTray();
        }

        /// <summary>상단 알림을 눌렀을 때: 받아 둔 것이 있으면 바로 설치 안내, 아니면 변경 사항과 함께 업데이트 창</summary>
        public void OpenUpdate()
        {
            var r = Updater.Ready ?? AvailableUpdate;
            if (r != null) ShowUpdate(r, true);
            else _ = CheckForUpdates(manual: true);
        }

        void ShowUpdate(ReleaseInfo r, bool activate)
        {
            _updateWin?.Close();
            _updateWin = new UpdateWindow(this, r) { ShowActivated = activate, Topmost = !activate };
            if (AvailableUpdate == null || Updater.IsNewer(r) && r != AvailableUpdate) AvailableUpdate = r;
            _updateWin.Closed += (s, e) => { _updateWin = null; _main?.ShowUpdateBadge(AvailableUpdate, Updater.Ready != null); };
            _main?.ShowUpdateBadge(AvailableUpdate, Updater.Ready != null);
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
            if (NetService.Int(h, "enc", 0) >= 1) Net.MarkSecureCapable(p.Ip, p.Port);
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
            FlushPending(p);
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

        /// <summary>쪽지 보내기. at 을 주면 예약, 받지 못한 동료에게는 접속할 때 다시 보냄</summary>
        public async Task<ChatMessage> Send(IList<Peer> to, string text, IList<string> files, bool urgent, bool broadcast, bool auto = false, DateTime? at = null, bool chat = false)
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
                Chat = chat,
                To = to.Select(p => p.DisplayName).ToList(),
                ToIds = to.Select(p => p.Id).ToList(),
            };
            foreach (var f in files ?? new List<string>())
            {
                var fi = new FileInfo(f);
                if (!fi.Exists) continue;
                m.Files.Add(new FileItem { Name = fi.Name, Size = fi.Length, Token = Net.Offer(fi.FullName), LocalPath = fi.FullName });
            }

            if (at.HasValue && at.Value > DateTime.Now.AddSeconds(5))
            {
                m.Scheduled = true;
                m.ScheduledTs = new DateTimeOffset(at.Value).ToUnixTimeMilliseconds();
                m.Ts = m.ScheduledTs;
                History.Insert(0, m);
                Store.AppendHistory(m);
                return m;
            }

            History.Insert(0, m);
            await Deliver(m, to);
            return m;
        }

        Packet BuildPacket(ChatMessage m) => new Packet
        {
            ["t"] = "msg",
            ["id"] = m.Id,
            ["from"] = Settings.Id,
            ["name"] = Settings.Name,
            ["dept"] = Settings.Dept,
            ["port"] = Settings.Port,
            ["text"] = m.Text,
            ["ts"] = m.Ts,
            ["urgent"] = m.Urgent,
            ["broadcast"] = m.Broadcast,
            ["auto"] = m.Auto,
            ["kind"] = m.Chat ? "chat" : "note",
            ["to"] = m.To,
            ["files"] = m.Files.Select(f => new Packet { ["name"] = f.Name, ["size"] = f.Size, ["token"] = f.Token }).ToList(),
        };

        /// <summary>실제 전송. 실패한 동료는 대기열(PendingTo)에 넣어 접속 시 재전송 (자동응답·전체 공지는 제외)</summary>
        async Task Deliver(ChatMessage m, IList<Peer> to)
        {
            var packet = BuildPacket(m);
            bool queue = !m.Auto && !m.Broadcast;
            var tasks = to.Select(async p =>
            {
                try
                {
                    if (!p.IsOnline && queue) return (p, ok: false, secure: false);
                    var r = await Net.Request(p.Ip, p.Port, new Packet(packet)).ConfigureAwait(false);
                    return (p, ok: NetService.Bool(r, "ok"), secure: NetService.Bool(r, "_secure"));
                }
                catch { return (p, ok: false, secure: false); }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                if (r.ok) { r.p.Secure = r.secure; m.Secure |= r.secure; m.PendingTo.Remove(r.p.Id); }
                else if (queue) { if (!m.PendingTo.Contains(r.p.Id)) m.PendingTo.Add(r.p.Id); }
                else m.FailedTo.Add(r.p.DisplayName);
            }
            m.Failed = m.FailedTo.Count == to.Count && to.Count > 0;
            m.Touch();
            Store.AppendHistory(m);
        }

        readonly HashSet<string> _flushing = new HashSet<string>();

        /// <summary>동료가 접속하면 그 동료에게 밀린 쪽지를 보냄</summary>
        async void FlushPending(Peer p)
        {
            if (_flushing.Contains(p.Id)) return;
            var list = History.Where(m => m.Outgoing && !m.Scheduled && m.PendingTo.Contains(p.Id)).OrderBy(m => m.Ts).ToList();
            if (list.Count == 0) return;
            _flushing.Add(p.Id);
            try
            {
                foreach (var m in list)
                {
                    try
                    {
                        var r = await Net.Request(p.Ip, p.Port, BuildPacket(m));
                        if (!NetService.Bool(r, "ok")) break;
                        p.Secure = NetService.Bool(r, "_secure");
                        m.PendingTo.Remove(p.Id);
                        m.Touch();
                        Store.AppendHistory(m);
                    }
                    catch { break; }
                }
            }
            finally { _flushing.Remove(p.Id); }
        }

        /// <summary>예약 시각이 된 쪽지 발송, 7일 넘게 전달 못 한 쪽지는 실패 처리</summary>
        async void RunSchedules()
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            foreach (var m in History.Where(x => x.Outgoing && x.Scheduled && x.ScheduledTs <= now).ToList())
            {
                m.Scheduled = false;
                m.Ts = now;
                var peers = m.ToIds.Select(id => Peers.FirstOrDefault(x => x.Id == id)).Where(x => x != null).ToList();
                m.Touch();
                await Deliver(m, peers);
            }
            var expire = now - TimeSpan.FromDays(7).Ticks / TimeSpan.TicksPerMillisecond;
            foreach (var m in History.Where(x => x.Outgoing && !x.Scheduled && x.PendingTo.Count > 0 && x.Ts < expire).ToList())
            {
                foreach (var id in m.PendingTo) m.FailedTo.Add(Peers.FirstOrDefault(x => x.Id == id)?.DisplayName ?? id);
                m.PendingTo.Clear();
                m.Failed = m.FailedTo.Count >= m.To.Count;
                m.Touch();
                Store.AppendHistory(m);
            }
            // 다시 알림
            var due = Settings.Reminders.Where(x => x.At <= now).ToList();
            if (due.Count > 0)
            {
                Settings.Reminders.RemoveAll(x => due.Contains(x));
                Store.SaveSettings(Settings);
            }
            foreach (var r in due)
            {
                var m = History.FirstOrDefault(x => x.Id == r.MsgId);
                if (m == null) continue;
                if (Settings.Sound) Native.PlayChime(false);
                ShowPopup(m, activate: false, reminder: true);
            }
        }

        public void CancelScheduled(ChatMessage m)
        {
            if (!m.Scheduled) return;
            DeleteHistory(new[] { m });
        }

        public void RemindLater(ChatMessage m, DateTime at)
        {
            Settings.Reminders.RemoveAll(r => r.MsgId == m.Id);
            Settings.Reminders.Add(new Reminder { MsgId = m.Id, At = new DateTimeOffset(at).ToUnixTimeMilliseconds() });
            Store.SaveSettings(Settings);
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
            peer.Secure = NetService.Bool(p, "_secure");

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
                Secure = NetService.Bool(p, "_secure"),
                Chat = NetService.Str(p, "kind") == "chat",
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
            // 그 동료와의 대화 창을 보고 있으면 팝업 없이 바로 읽음 처리
            if (_chats.TryGetValue(peer.Id, out var chat) && chat.IsActive && chat.WindowState != WindowState.Minimized && !m.Urgent)
            {
                MarkRead(m);
            }
            else if (!dnd)
            {
                if (Settings.Sound) Native.PlayChime(m.Urgent);
                // 1:1 대화 메시지는 열려 있는 대화 창으로, 쪽지는 쪽지 창으로
                if (m.Chat && chat != null && chat.IsVisible) Native.Flash(chat, false);
                else ShowPopup(m);
            }

            // 부재 시 자동응답
            if (!m.Auto && !string.IsNullOrWhiteSpace(Settings.AutoReply) && _status != Status.Online)
                _ = Send(new[] { peer }, Settings.AutoReply.Trim(), null, false, false, auto: true);
        }

        /// <summary>읽음 처리. 쪽지 창 밖(대화 창·쪽지함)에서 읽으면 떠 있는 그 쪽지 창도 닫음</summary>
        public void MarkRead(ChatMessage m, bool fromPopup = false)
        {
            if (!fromPopup)
                foreach (var w in _popups.Where(x => x.Message == m).ToList()) w.CloseIfIdle();
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

        readonly Dictionary<string, ChatWindow> _chats = new Dictionary<string, ChatWindow>();
        public ChatWindow LastChat { get; set; }

        /// <summary>1:1 대화 창 (동료마다 하나)</summary>
        public ChatWindow OpenChat(Peer p, string attach = null)
        {
            if (p == null) return null;
            if (!_chats.TryGetValue(p.Id, out var w))
            {
                w = new ChatWindow(this, p);
                _chats[p.Id] = w;
                w.Closed += (s, e) => { _chats.Remove(p.Id); if (LastChat == w) LastChat = null; };
                w.Show();
            }
            Native.BringToFront(w);
            if (attach != null) w.Attach(attach);
            foreach (var m in History.Where(x => !x.Outgoing && !x.Read && x.PeerId == p.Id).ToList()) MarkRead(m);
            return w;
        }

        // ───────────────────────── groups ─────────────────────────

        public void SaveGroups() { Store.SaveSettings(Settings); GroupsChanged?.Invoke(); }
        public event Action GroupsChanged;

        public void ToggleFavorite(Peer p)
        {
            p.Favorite = !p.Favorite;
            Store.SaveContacts(Peers);
        }

        public IList<Peer> GroupMembers(PeerGroup g) =>
            g.Members.Select(id => Peers.FirstOrDefault(x => x.Id == id)).Where(x => x != null).ToList();

        // ───────────────────────── capture ─────────────────────────

        /// <summary>화면 영역 캡처 후 최근 대화 창 또는 새 쪽지 창에 첨부</summary>
        public void StartCapture(Window target = null)
        {
            CaptureWindow.Capture(path =>
            {
                if (path == null) return;
                if (target is ChatWindow cw && cw.IsVisible) { Native.BringToFront(cw); cw.Attach(path); }
                else if (target is ComposeWindow comp && comp.IsVisible) { Native.BringToFront(comp); comp.Attach(path); }
                else if (LastChat != null && LastChat.IsVisible) { Native.BringToFront(LastChat); LastChat.Attach(path); }
                else
                {
                    var w = new ComposeWindow(this, new Peer[0], false, null);
                    w.Show();
                    w.Attach(path);
                }
            });
        }

        public void ShowPopup(ChatMessage m, bool activate = false, bool reminder = false)
        {
            var existing = _popups.FirstOrDefault(p => p.Message == m);
            if (existing != null) { Native.BringToFront(existing); return; }

            // 대화 메시지를 직접 열면 대화 창으로
            if (m.Chat && activate && PeerOf(m) is Peer cp) { OpenChat(cp); return; }
            // 같은 동료의 대화 알림은 하나만 (가장 최근 것으로 교체)
            if (m.Chat && !m.Outgoing)
                foreach (var old in _popups.Where(x => x.Message.Chat && !x.Message.Outgoing && x.Message.PeerId == m.PeerId).ToList()) old.CloseIfIdle();

            var w = new MessageWindow(this, m) { IsReminder = reminder, IsChatAlert = m.Chat && !m.Outgoing };
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
            // 새로 도착한(또는 다시 알림) 쪽지만 자동으로 닫음. 직접 연 창·긴급 쪽지는 그대로 둠
            // 대화 알림은 5초 뒤 바로 사라짐(대화 창에서 이어서 봄)
            if (!activate && !m.Urgent && !m.Outgoing)
            {
                int secs = m.Chat && !reminder ? 5 : Settings.PopupAutoClose;
                if (secs > 0) w.StartAutoClose(secs);
            }
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
