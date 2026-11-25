using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace BluePhone
{
    using Packet = Dictionary<string, object>;

    /// <summary>앱 전체 상태와 동작 (Windows 판 Core 와 같은 동작 · 같은 통신)</summary>
    public sealed partial class Core
    {
        public AppSettings Settings { get; }
        public ObservableCollection<Peer> Peers { get; } = new ObservableCollection<Peer>();
        public ObservableCollection<ChatMessage> History { get; } = new ObservableCollection<ChatMessage>();
        public NetService Net { get; private set; } = new NetService();
        public TelegramBridge Telegram { get; private set; }

        public event Action StatusChanged;
        public event Action UnreadChanged;
        public event Action<Peer> PeerRenamed;

        string _status;
        bool _autoAway;
        MainWindow _main;
        TrayIcon _tray;
        WindowIcon _iconNormal, _iconDot;
        DispatcherTimer _tick, _updateTimer;
        DateTime _lastAnnounce;
        UpdateWindow _updateWin;
        bool _checking;
        readonly List<MessageWindow> _popups = new List<MessageWindow>();
        readonly bool _firstRun;

        public Core()
        {
            _firstRun = !Store.HasSettings;
            Settings = Store.LoadSettings();
            Store.SaveSettings(Settings);
            _status = Settings.LastStatus == Status.Offline ? Status.Online : (Settings.LastStatus ?? Status.Online);
            ChatMessage.PeerLookup = id => Peers.FirstOrDefault(x => x.Id == id);
            ChatRoom.Lookup = ChatMessage.PeerLookup;
            ChatRoom.MyId = Settings.Id;
            Peers.CollectionChanged += (s, e) => { if (e.NewItems != null) foreach (Peer p in e.NewItems) Track(p); };
            foreach (var p in Store.LoadContacts())
            {
                p.Status_ = Status.Offline;
                Peers.Add(p);
            }
            var hist = Store.LoadHistory();
            foreach (var m in Enumerable.Reverse(hist)) History.Add(m);
            Store.RewriteHistory(hist);

            AvatarStore.Changed += id =>
            {
                if (id == null) { StatusChanged?.Invoke(); return; }
                Peers.FirstOrDefault(x => x.Id == id)?.RefreshAvatar();
                foreach (var m in History.Where(x => x.Involves(id))) m.RefreshNames();
                PeerRenamed?.Invoke(Peers.FirstOrDefault(x => x.Id == id));
            };
        }

        void Track(Peer p)
        {
            p.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(Peer.DisplayName)) return;
                foreach (var m in History.Where(x => x.Involves(p.Id))) m.RefreshNames();
                PeerRenamed?.Invoke(p);
            };
        }

        public string MyStatus => _status;
        public int Unread => History.Count(m => !m.Outgoing && !m.Read);
        static void Post(Action a) => Dispatcher.UIThread.Post(a);

        // ───────────────────────── lifecycle ─────────────────────────

        public void Start(bool startHidden)
        {
            if (!Store.CustomDir) Native.SetAutoStart(Settings.AutoStart);

            string netError = null;
            for (int attempt = 0; ; attempt++)
            {
                try { Net.Start(Settings.Port); netError = null; break; }
                catch (Exception ex)
                {
                    netError = ex.Message;
                    Net.Dispose();
                    Net = new NetService();
                    if (attempt >= 3) break;
                    Thread.Sleep(300);
                }
            }
            Net.HelloFactory = MakeHello;
            Net.ExtraTargets = () => Settings.ManualHosts
                .Concat(Peers.Where(x => x.IsOnline && !string.IsNullOrEmpty(x.Ip)).Select(x => $"{x.Ip}:{x.Port}"))
                .ToList();
            Net.AvatarProvider = AvatarStore.MineBytes;
            HookNet();

            foreach (var m in History.Where(x => x.Outgoing))
                foreach (var f in m.Files)
                    if (!string.IsNullOrEmpty(f.LocalPath) && File.Exists(f.LocalPath)) Net.Offer(f.Token, f.LocalPath);

            CreateTray();
            ApplyHotkey();
            ChatLook.ApplyGlobal(Settings);
            _main = new MainWindow(this);
            if (!startHidden) { _main.Show(); _main.Activate(); }

            if (netError != null)
                _ = Dialog.Alert(_main.IsVisible ? _main : null, "네트워크를 시작하지 못했습니다",
                    $"포트 {Settings.Port} 를 열 수 없습니다. 다른 프로그램이 사용 중인지 확인하거나 설정에서 포트를 바꿔 주세요.\n\n{netError}");

            if (_firstRun)
                Dispatcher.UIThread.Post(() => ShowSettings(_main), DispatcherPriority.ApplicationIdle);

            var cur = AppInfo.VersionText;
            if (!_firstRun && !string.IsNullOrEmpty(Settings.LastRunVersion) && Settings.LastRunVersion != cur)
            {
                var notes = AppInfo.ChangesFor(cur);
                if (!string.IsNullOrWhiteSpace(notes))
                    Dispatcher.UIThread.Post(() => new UpdateWindow(this, cur, notes).Show(), DispatcherPriority.ApplicationIdle);
            }
            Settings.LastRunVersion = cur;
            UiTest.Run(this);
            Store.SaveSettings(Settings);

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _updateTimer.Tick += async (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(6);
                await CheckForUpdates(manual: false);
            };
            _updateTimer.Start();

            Net.Announce(wantReply: true);
            PingManualHosts();
            _lastAnnounce = DateTime.Now;
            TgNet.Config = TgNetConfig.From(Settings);
            Telegram = new TelegramBridge(this);
            Telegram.Apply();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _tick.Tick += (s, e) => Tick();
            _tick.Start();
            UpdateTray();
        }

        void HookNet()
        {
            Net.HelloReceived += (p, ip) => Post(() => OnHello(p, ip));
            Net.ByeReceived += id => Post(() => OnBye(id));
            Net.MessageReceived += (p, ip) => Post(() => OnMessage(p, ip));
            Net.ReadReceived += (msg, from) => Post(() => OnRead(msg, from));
            Net.TypingReceived += p => Post(() => OnTyping(p));
        }

        /// <summary>화면 캡처 전역 단축키 ⌃⌥S (설정에서 끌 수 있음)</summary>
        public void ApplyHotkey()
        {
            Native.UnregisterHotkey();
            HotkeyFailed = false;
            if (!Settings.CaptureHotkey || !Native.IsMac) return;
            HotkeyFailed = !Native.RegisterCaptureHotkey(() => StartCapture());
        }
        public bool HotkeyFailed { get; private set; }

        public void Notify(string title, string text) => ShowNotice(title, text);

        bool _stopped;

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            Native.UnregisterHotkey();
            Telegram?.Stop();
            try { Net.SayBye(Settings.Id); } catch { }
            if (_main == null) { Net.Dispose(); return; }
            Settings.LastStatus = _autoAway ? Status.Online : _status;
            Store.SaveSettings(Settings);
            Store.SaveContacts(Peers);
            try { if (_tray != null) _tray.IsVisible = false; } catch { }
            Net.Dispose();
        }

        public void Quit()
        {
            _main?.SavePlacement();
            _main?.AllowClose();
            Stop();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }

        void Tick()
        {
            RunSchedules();
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
                PingManualHosts();
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
            ["host"] = MachineName,
            ["avatar"] = Settings.AvatarHash ?? "",
            ["ver"] = AppInfo.VersionText,
            ["enc"] = 1,
        };

        /// <summary>PC 이름 (macOS 는 "MacBook-Pro.local" 의 앞부분)</summary>
        public static string MachineName
        {
            get
            {
                var n = Environment.MachineName ?? "Mac";
                var i = n.IndexOf('.');
                return i > 0 ? n.Substring(0, i) : n;
            }
        }

        public static string[] LocalIps()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                    .Select(a => a.Address.ToString()).Distinct().ToArray();
            }
            catch { return new string[0]; }
        }

        // ───────────────────────── update ─────────────────────────

        public async Task<string> CheckForUpdates(bool manual)
        {
            if (_checking) return "확인 중입니다";
            _checking = true;
            try
            {
                if (Updater.Ready != null) { ShowUpdate(Updater.Ready, true); return "업데이트가 준비되어 있습니다"; }
                var r = await Updater.CheckAsync();
                if (!Updater.IsNewer(r)) return $"최신 버전입니다 ({AppInfo.VersionText})";
                SetAvailable(r);
                if (!manual && (r.VersionText == Settings.SkippedVersion || !Settings.AutoUpdate)) return null;
                if (manual) { ShowUpdate(r, true); return $"새 버전 {r.VersionText}"; }
                try { await Updater.DownloadAsync(r, null); } catch { }
                ShowUpdate(Updater.Ready ?? r, false);
                return null;
            }
            catch (Exception ex)
            {
                return manual ? "업데이트를 확인하지 못했습니다: " + ex.Message : null;
            }
            finally { _checking = false; }
        }

        public ReleaseInfo AvailableUpdate { get; private set; }

        void SetAvailable(ReleaseInfo r)
        {
            AvailableUpdate = r;
            _main?.ShowUpdateBadge(r, Updater.Ready != null);
            UpdateTray();
        }

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
            if (select != null && File.Exists(select)) Native.Reveal(select);
            else Native.Open(Store.DownloadDir(Settings));
        }

        public void ApplyUpdateAndRestart()
        {
            _main?.SavePlacement();
            if (Updater.Apply(restart: true)) Quit();
            else _ = Dialog.Alert(null, "업데이트를 적용하지 못했습니다", "설치 파일을 열 수 없습니다. 잠시 후 다시 시도해 주세요.");
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

        public void Rescan() { Net.Announce(true); PingManualHosts(); _lastAnnounce = DateTime.Now; }

        void PingManualHosts()
        {
            foreach (var h in Settings.ManualHosts.ToList())
            {
                var host = h;
                Task.Run(async () =>
                {
                    try
                    {
                        var (r, ep) = await Net.Probe(host, Settings.Port, 3000).ConfigureAwait(false);
                        if (r != null && NetService.Str(r, "id") != Settings.Id)
                            Post(() => OnHello(r, ep.Address));
                    }
                    catch { }
                });
            }
        }

        public async Task<Peer> AddByAddress(string text)
        {
            text = (text ?? "").Trim();
            if (!NetService.TrySplit(text, out var host, out var port, Settings.Port))
                throw new ArgumentException("주소 형식이 올바르지 않습니다.\n예) 192.168.0.10 또는 192.168.0.10:9082");
            var addr = $"{host}:{port}";
            Packet r; IPEndPoint ep;
            try { (r, ep) = await Net.Probe(addr, Settings.Port); }
            catch (ArgumentException) { throw new ArgumentException($"'{host}' 주소를 찾을 수 없습니다."); }
            catch
            {
                throw new IOException($"{addr} 에 연결할 수 없습니다.\n\n· 상대 PC에서 파란전화기가 실행 중인지\n· 포트 번호가 상대 프로그램의 설정 › 네트워크 › 포트와 같은지\n· 상대 PC 방화벽에서 이 포트(TCP)가 허용되어 있는지 확인해 주세요.");
            }

            Peer p = null;
            if (r != null)
            {
                var id = NetService.Str(r, "id");
                if (id == Settings.Id) throw new InvalidOperationException("내 PC의 주소입니다.");
                OnHello(r, ep.Address);
                p = Peers.FirstOrDefault(x => x.Id == id);
            }
            else
            {
                Net.AnnounceTo(ep, true);
                var ip = ep.Address.ToString();
                for (int i = 0; i < 15 && p == null; i++)
                {
                    await Task.Delay(200);
                    p = Peers.FirstOrDefault(x => x.Ip == ip && x.Port == ep.Port && x.IsOnline);
                }
                if (p == null)
                    throw new IOException($"{addr} 에 연결은 되었지만 응답이 없습니다.\n상대 PC의 파란전화기를 최신 버전으로 업데이트해 주세요.");
            }

            if (!Settings.ManualHosts.Any(x => string.Equals(x.Trim(), addr, StringComparison.OrdinalIgnoreCase)
                                             || (port == Settings.Port && string.Equals(x.Trim(), host, StringComparison.OrdinalIgnoreCase))))
            {
                Settings.ManualHosts.Add(port == Settings.Port ? host : addr);
                Store.SaveSettings(Settings);
            }
            return p;
        }

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
            int before = Settings.ManualHosts.Count;
            Settings.ManualHosts.RemoveAll(h =>
            {
                if (!NetService.TrySplit(h, out var host, out var port, Settings.Port)) return false;
                return port == p.Port && (string.Equals(host, p.Ip, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(host, p.Host, StringComparison.OrdinalIgnoreCase));
            });
            if (Settings.ManualHosts.Count != before) Store.SaveSettings(Settings);
        }

        // ───────────────────────── messaging ─────────────────────────

        public async Task<ChatMessage> Send(IList<Peer> to, string text, IList<string> files, bool urgent, bool broadcast, bool auto = false, DateTime? at = null, bool chat = false,
                                            ChatRoom room = null, string sys = null, Action<ChatMessage> prep = null)
        {
            bool one = to.Count == 1 && room == null;
            var m = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Outgoing = true,
                PeerId = one ? to[0].Id : null,
                PeerName = one ? to[0].DisplayName : null,
                PeerDept = one ? to[0].Dept : null,
                PeerIp = one ? to[0].Ip : null,
                PeerPort = one ? to[0].Port : 0,
                Text = text,
                Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Urgent = urgent,
                Broadcast = broadcast,
                Auto = auto,
                Chat = chat || room != null,
                Room = room?.Id,
                Sys = sys,
                To = to.Select(p => p.DisplayName).ToList(),
                ToIds = to.Select(p => p.Id).ToList(),
            };
            prep?.Invoke(m);
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

        Packet BuildPacket(ChatMessage m)
        {
            var packet = new Packet
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
            AddRoomFields(packet, m);
            return packet;
        }

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
            var expire = now - (long)TimeSpan.FromDays(7).TotalMilliseconds;
            foreach (var m in History.Where(x => x.Outgoing && !x.Scheduled && x.PendingTo.Count > 0 && x.Ts < expire).ToList())
            {
                foreach (var id in m.PendingTo) m.FailedTo.Add(Peers.FirstOrDefault(x => x.Id == id)?.DisplayName ?? id);
                m.PendingTo.Clear();
                m.Failed = m.FailedTo.Count >= m.To.Count;
                m.Touch();
                Store.AppendHistory(m);
            }
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
            if (IsBlocked(from)) return;
            if (History.Any(x => !x.Outgoing && x.Id == id)) return;

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
            if (!ApplyRoom(p, peer, ip, m)) return;
            var key = KeyOf(m);
            if (m.Chat) ClearTyping(key, from);
            if (m.IsSys) m.Read = true;   // 초대 · 나감 알림 줄은 안 읽은 수에 넣지 않음
            History.Insert(0, m);
            Store.AppendHistory(m);
            if (m.IsSys && !m.InvitesMe) return;   // 나감 · 이름 변경 · 다른 사람 초대는 알림 없이 대화방 안에만
            if (!m.IsSys)
            {
                AutoFetchImages(m);
                Telegram?.Forward(m, _status);
                peer.HasUnread = true;
                UnreadChanged?.Invoke();
                UpdateTray();
            }

            bool dnd = _status == Status.Dnd && !m.Urgent;
            ChatWindow chat = null;
            if (m.Chat && key != null) _chats.TryGetValue(key, out chat);
            if (chat != null && chat.IsActive && chat.WindowState != WindowState.Minimized && !m.Urgent)
            {
                if (!m.IsSys) MarkRead(m);
            }
            else if (!dnd)
            {
                if (Settings.Sound) Native.PlayChime(m.Urgent);
                if (m.Chat && chat != null && chat.IsVisible) Native.Attention(false);
                else ShowPopup(m);
            }

            if (!m.Auto && m.Room == null && !string.IsNullOrWhiteSpace(Settings.AutoReply) && _status != Status.Online)
                _ = Send(new[] { peer }, Settings.AutoReply.Trim(), null, false, false, auto: true);
        }

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

        public static void BringToFront(Window w)
        {
            if (w == null) return;
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            Native.ActivateApp();
            w.Activate();
        }

        public void ShowMain() => BringToFront(_main);
        public MainWindow Main => _main;

        public Peer PeerOf(ChatMessage m) => Peers.FirstOrDefault(p => p.Id == m.PeerId);

        public void Compose(IList<Peer> to, bool broadcast = false, string quote = null)
        {
            var w = new ComposeWindow(this, to, broadcast, quote);
            w.Show();
            BringToFront(w);
        }

        readonly Dictionary<string, ChatWindow> _chats = new Dictionary<string, ChatWindow>();
        public ChatWindow LastChat { get; set; }

        public ChatWindow OpenChat(Peer p, string attach = null) =>
            p == null ? null : OpenChatWindow(p.Id, () => new ChatWindow(this, p), attach);

        /// <summary>그룹 대화방 창</summary>
        public ChatWindow OpenRoom(ChatRoom r, string attach = null) =>
            r == null ? null : OpenChatWindow(Typists.RoomKey(r.Id), () => new ChatWindow(this, r), attach);

        /// <summary>대화 창은 바로 띄우고, 읽음 처리 · 이미지 받기 같은 일은 창이 그려진 뒤에</summary>
        ChatWindow OpenChatWindow(string key, Func<ChatWindow> make, string attach)
        {
            if (!_chats.TryGetValue(key, out var w))
            {
                w = make();
                _chats[key] = w;
                var ww = w;
                w.Closed += (s, e) => { if (_chats.TryGetValue(key, out var cur) && cur == ww) _chats.Remove(key); if (LastChat == ww) LastChat = null; NotifyTyping(key, false); };
                w.Show();
            }
            BringToFront(w);
            if (attach != null) w.Attach(attach);
            Dispatcher.UIThread.Post(() => AfterChatOpened(key), DispatcherPriority.Background);
            return w;
        }

        void CloseChatWindow(string key)
        {
            if (_chats.TryGetValue(key, out var w)) w.Close();
        }

        public void SetChatLook(string theme = null, string font = null, double? size = null)
        {
            if (theme != null) Settings.ChatTheme = theme;
            if (font != null) Settings.ChatFont = font;
            if (size.HasValue) Settings.ChatFontSize = size.Value;
            ChatLook.ApplyGlobal(Settings);
            foreach (var w in _chats.Values) w.ApplyLook();
            Store.SaveSettings(Settings);
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
            g.Members.Select(id => Peers.FirstOrDefault(x => x.Id == id)).Where(x => x != null && !IsBlocked(x.Id)).ToList();

        // ───────────────────────── block ─────────────────────────

        public bool IsBlocked(string peerId) => peerId != null && Settings.Blocked.Any(b => b.Id == peerId);
        public event Action BlockedChanged;

        public void Block(Peer p)
        {
            if (p == null || IsBlocked(p.Id)) return;
            Settings.Blocked.Add(new BlockedPeer { Id = p.Id, Name = p.DisplayName });
            Store.SaveSettings(Settings);
            if (_chats.TryGetValue(p.Id, out var w)) w.Close();
            foreach (var pw in _popups.Where(x => x.Message.PeerId == p.Id).ToList()) pw.Close();
            BlockedChanged?.Invoke();
        }

        public void Unblock(string peerId)
        {
            Settings.Blocked.RemoveAll(b => b.Id == peerId);
            Store.SaveSettings(Settings);
            BlockedChanged?.Invoke();
        }

        async void AutoFetchImages(ChatMessage m)
        {
            if (!Settings.AutoImage) return;
            foreach (var f in m.Files.Where(x => Media.IsImage(x.Name) && x.Size > 0 && x.Size <= 20L * 1024 * 1024
                                                 && !Media.Exists(x.LocalPath) && x.State != "받는 중" && x.State != "실패").ToList())
            {
                try
                {
                    var dest = Store.UniquePath(Store.DownloadDir(Settings), f.Name);
                    await DownloadFile(m, f, dest);
                }
                catch { }
            }
        }

        // ───────────────────────── capture ─────────────────────────

        public async void StartCapture(Window target = null)
        {
            var path = await Native.CaptureAsync();
            if (path == null) return;
            if (target is ChatWindow cw && cw.IsVisible) { BringToFront(cw); cw.Attach(path); }
            else if (target is ComposeWindow comp && comp.IsVisible) { BringToFront(comp); comp.Attach(path); }
            else if (LastChat != null && LastChat.IsVisible) { BringToFront(LastChat); LastChat.Attach(path); }
            else
            {
                var w = new ComposeWindow(this, new Peer[0], false, null);
                w.Show();
                BringToFront(w);
                w.Attach(path);
            }
        }

        public void ShowPopup(ChatMessage m, bool activate = false, bool reminder = false)
        {
            var existing = _popups.FirstOrDefault(p => p.Message == m);
            if (existing != null) { BringToFront(existing); return; }

            if (m.Chat && activate && OpenChatFor(m) != null) return;
            if (m.Chat && !m.Outgoing)
                foreach (var old in _popups.Where(x => x.Message.Chat && !x.Message.Outgoing && KeyOf(x.Message) == KeyOf(m)).ToList()) old.CloseIfIdle();

            var w = new MessageWindow(this, m) { IsReminder = reminder, IsChatAlert = m.Chat && !m.Outgoing };
            if (!activate && !m.Outgoing) w.Privacy = Settings.AlertPrivacy;
            w.Closed += (s, e) => { _popups.Remove(w); LayoutPopups(); };
            w.Slot = _popups.Count == 0 ? 0 : _popups.Max(p => p.Slot) + 1;
            _popups.Add(w);
            w.ShowActivated = activate || !Settings.PopupNoFocus;
            w.Topmost = true;
            var area = WorkArea(out _);
            w.Position = new PixelPoint((int)(area.Right - w.Width - 16), (int)(area.Bottom - 240));
            w.Show();
            if (activate) { Native.ActivateApp(); w.Activate(); }
            Dispatcher.UIThread.Post(LayoutPopups, DispatcherPriority.Loaded);
            if (m.Urgent) Native.Attention(true);
            if (!activate && !m.Urgent && !m.Outgoing)
            {
                int secs = m.Chat && !reminder && !m.MentionsMe && !m.InvitesMe ? Settings.ChatAlertAutoClose : Settings.PopupAutoClose;
                if (secs > 0) w.StartAutoClose(secs);
            }
        }

        public MessageWindow ShowNotice(string title, string text, string button = null, Action action = null)
        {
            var m = new ChatMessage
            {
                Id = "notice-" + Guid.NewGuid().ToString("N"),
                PeerId = "notice:" + title,
                PeerName = title,
                PeerDept = AppInfo.Name,
                Text = text,
                Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Read = true,
            };
            ShowPopup(m, activate: true);
            var w = _popups.FirstOrDefault(p => p.Message == m);
            w?.SetNotice(button, action);
            return w;
        }

        public bool InLayout { get; private set; }

        /// <summary>주 화면의 작업 영역 (화면 좌표 · 논리 단위)과 배율</summary>
        Rect WorkArea(out double scale)
        {
            scale = 1;
            try
            {
                var screens = (_main ?? (Window)_popups.FirstOrDefault())?.Screens;
                var sc = screens?.Primary ?? screens?.All.FirstOrDefault();
                if (sc != null)
                {
                    scale = sc.Scaling;
                    var wa = sc.WorkingArea;
                    // macOS 는 화면 좌표가 논리 단위(포인트)라 배율을 나누지 않음
                    double k = Native.IsMac ? 1 : scale;
                    return new Rect(wa.X / k, wa.Y / k, wa.Width / k, wa.Height / k);
                }
            }
            catch { }
            return new Rect(0, 0, 1440, 860);
        }

        /// <summary>쪽지 창을 화면 오른쪽 아래부터 위로 쌓아 배치 (넘치면 계단식)</summary>
        public void LayoutPopups()
        {
            InLayout = true;
            try
            {
                var area = WorkArea(out var scale);
                double k = Native.IsMac ? 1 : scale;
                double bottom = area.Bottom - 16;
                int cascade = 0;
                foreach (var w in _popups.Where(p => !p.UserMoved).OrderBy(p => p.Slot))
                {
                    double width = w.Bounds.Width > 0 ? w.Bounds.Width : w.Width;
                    double h = w.Bounds.Height > 0 ? w.Bounds.Height : 240;
                    double top = bottom - h;
                    double left;
                    if (top < area.Top + 16)
                    {
                        cascade++;
                        left = area.Right - width - 16 - cascade * 28;
                        top = area.Bottom - 16 - h - cascade * 28;
                    }
                    else
                    {
                        left = area.Right - width - 16;
                        bottom = top - 10;
                    }
                    w.Position = new PixelPoint((int)(left * k), (int)(top * k));
                }
            }
            finally { InLayout = false; }
        }

        public async void ShowSettings(Window owner)
        {
            var w = new SettingsWindow(this);
            await Dialog.ShowWindow(w, owner?.IsVisible == true ? owner : null);
        }

        // ───────────────────────── tray (메뉴 막대) ─────────────────────────

        void CreateTray()
        {
            try
            {
                _iconNormal = Icons.Tray(false);
                _iconDot = Icons.Tray(true);
                _tray = new TrayIcon { Icon = _iconNormal, ToolTipText = AppInfo.Name, IsVisible = true };
                if (Native.IsMac) MacTemplate(_tray);
                _tray.Menu = BuildTrayMenu();
                _tray.Clicked += (s, e) => OnTrayClick();
                var icons = new TrayIcons { _tray };
                TrayIcon.SetIcons(Application.Current, icons);
            }
            catch { _tray = null; }
        }

        static void MacTemplate(TrayIcon t)
        {
            try { MacOSProperties.SetIsTemplateIcon(t, true); } catch { }
        }

        void OnTrayClick()
        {
            var unread = History.Where(m => !m.Outgoing && !m.Read).OrderBy(m => m.Ts).ToList();
            if (unread.Count > 0)
                foreach (var m in unread.Take(6)) ShowPopup(m, activate: true);
            else ShowMain();
        }

        NativeMenu BuildTrayMenu()
        {
            var menu = new NativeMenu();
            menu.Opening += (s, e) => FillTrayMenu(menu);
            FillTrayMenu(menu);
            return menu;
        }

        static NativeMenuItem Item(string header, Action a, bool enabled = true)
        {
            var i = new NativeMenuItem(header) { IsEnabled = enabled };
            if (a != null) i.Click += (s, e) => a();
            return i;
        }

        void FillTrayMenu(NativeMenu menu)
        {
            menu.Items.Clear();
            int n = Unread;
            menu.Items.Add(Item($"{AppInfo.Name} {AppInfo.VersionText} — {Status.Label(_status)}", null, false));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("파란전화기 열기", ShowMain));
            if (n > 0) menu.Items.Add(Item($"안 읽은 쪽지 {n}개 보기", OnTrayClick));
            menu.Items.Add(Item("새 쪽지…", () => Compose(new Peer[0])));
            menu.Items.Add(Item("화면 캡처해서 보내기", () => StartCapture()));
            menu.Items.Add(new NativeMenuItemSeparator());
            var st = new NativeMenuItem("내 상태");
            var sub = new NativeMenu();
            foreach (var s in new[] { Status.Online, Status.Away, Status.Busy, Status.Dnd })
            {
                var item = new NativeMenuItem(Status.Label(s)) { ToggleType = NativeMenuItemToggleType.Radio, IsChecked = _status == s };
                var v = s;
                item.Click += (o, e) => SetStatus(v);
                sub.Items.Add(item);
            }
            st.Menu = sub;
            menu.Items.Add(st);
            if (AvailableUpdate != null && Updater.IsNewer(AvailableUpdate))
                menu.Items.Add(Item($"새 버전 {AvailableUpdate.VersionText} 설치…", OpenUpdate));
            menu.Items.Add(Item("설정…", () => ShowSettings(_main)));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("파란전화기 종료", Quit));
        }

        public void UpdateTray()
        {
            int n = Unread;
            Native.SetBadge(n);
            if (_tray == null) return;
            try
            {
                _tray.Icon = n > 0 ? _iconDot : _iconNormal;
                if (Native.IsMac) MacTemplate(_tray);
                var text = $"{AppInfo.Name} {AppInfo.VersionText} — {Status.Label(_status)}";
                if (n > 0) text += $"\n안 읽은 쪽지 {n}개";
                _tray.ToolTipText = text;
                if (!Native.IsMac && _tray.Menu != null) FillTrayMenu(_tray.Menu);
            }
            catch { }
        }
    }
}
