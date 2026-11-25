using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
#if MAC
using Avalonia.Threading;
#else
using System.Windows.Threading;
#endif

namespace BluePhone
{
    using Packet = Dictionary<string, object>;

    /// <summary>
    /// Windows · macOS 판이 함께 쓰는 대화 기능: 그룹 대화방 · 입력 중 표시 · 한꺼번에 읽음 처리.
    /// 화면(창)과 관계없는 부분만 여기에 둔다.
    /// </summary>
    public sealed partial class Core
    {
        // ───────────────────────── 열쇠 ─────────────────────────

        /// <summary>대화 창 · 입력 중 표시의 열쇠: 1:1 은 동료 ID, 그룹은 room:방ID (쪽지 · 여러 명에게 보낸 쪽지는 null)</summary>
        public static string KeyOf(ChatMessage m)
        {
            if (m.Room != null) return Typists.RoomKey(m.Room);
            if (!m.Outgoing) return m.PeerId;
            return m.ToIds.Count == 1 ? m.ToIds[0] : null;
        }

        public static bool IsRoomKey(string key) => key != null && key.StartsWith("room:", StringComparison.Ordinal);
        static string RoomIdOf(string key) => IsRoomKey(key) ? key.Substring(5) : null;

        /// <summary>이 대화 창(열쇠)에 보일 메시지인가</summary>
        public static bool InChat(ChatMessage m, string key)
        {
            if (!m.Chat || key == null) return false;
            var room = RoomIdOf(key);
            return room != null ? m.Room == room : m.IsDirectWith(key);
        }

        // ───────────────────────── 그룹 대화방 ─────────────────────────

        public event Action RoomsChanged;

        public ChatRoom RoomOf(string id) => id == null ? null : Settings.Rooms.FirstOrDefault(r => r.Id == id);
        public IEnumerable<ChatRoom> ActiveRooms => Settings.Rooms.Where(r => !r.Left);

        RoomMember MemberOf(Peer p) => new RoomMember { Id = p.Id, Name = p.DisplayName, Ip = p.Ip, Port = p.Port };
        RoomMember MeMember => new RoomMember { Id = Settings.Id, Name = Settings.Name, Ip = "", Port = Settings.Port };

        void SaveRooms()
        {
            Store.SaveSettings(Settings);
            RoomsChanged?.Invoke();
        }

        /// <summary>새 그룹 대화방 (나 + 고른 동료). 같은 구성원의 방이 이미 있으면 그 방을 씀</summary>
        public ChatRoom CreateRoom(IList<Peer> peers, string name = null)
        {
            var ids = peers.Where(p => p != null && p.Id != Settings.Id).Select(p => p.Id).Distinct().ToList();
            var same = ActiveRooms.FirstOrDefault(r => string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(r.Name)
                && r.Others.Select(m => m.Id).OrderBy(x => x).SequenceEqual(ids.OrderBy(x => x)));
            if (same != null) return same;
            var room = new ChatRoom
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = (name ?? "").Trim(),
                CreatedTs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            };
            room.Members.Add(MeMember);
            foreach (var p in peers.Where(p => p != null && p.Id != Settings.Id).GroupBy(p => p.Id).Select(g => g.First())) room.Members.Add(MemberOf(p));
            Settings.Rooms.Add(room);
            SaveRooms();
            return room;
        }

        /// <summary>방 구성원 중 메시지를 보낼 동료 (나 · 차단한 동료 제외). 목록에 없는 구성원은 받은 주소로 추가</summary>
        public List<Peer> RoomPeers(ChatRoom r)
        {
            var list = new List<Peer>();
            bool added = false;
            foreach (var mem in r.Others)
            {
                if (IsBlocked(mem.Id)) continue;
                var p = Peers.FirstOrDefault(x => x.Id == mem.Id);
                if (p == null)
                {
                    if (string.IsNullOrEmpty(mem.Ip)) continue;
                    p = new Peer { Id = mem.Id, Name = mem.Name, Ip = mem.Ip, Port = mem.Port > 0 ? mem.Port : Settings.Port };
                    Peers.Add(p);
                    added = true;
                }
                else if (string.IsNullOrEmpty(p.Ip) && !string.IsNullOrEmpty(mem.Ip)) { p.Ip = mem.Ip; p.Port = mem.Port > 0 ? mem.Port : Settings.Port; }
                list.Add(p);
            }
            if (added) Store.SaveContacts(Peers);
            return list;
        }

        public Task<ChatMessage> SendRoom(ChatRoom r, string text, IList<string> files, bool urgent, DateTime? at = null, string sys = null)
        {
            r.Left = false;
            return Send(RoomPeers(r), text, files, urgent, false, at: at, chat: true, room: r, sys: sys);
        }

        /// <summary>대화방에 동료 초대</summary>
        public async Task InviteToRoom(ChatRoom r, IList<Peer> peers)
        {
            var add = peers.Where(p => p != null && p.Id != Settings.Id && r.Members.All(m => m.Id != p.Id)).ToList();
            if (add.Count == 0) return;
            foreach (var p in add) r.Members.Add(MemberOf(p));
            SaveRooms();
            var names = string.Join(", ", add.Select(p => p.DisplayName + "님"));
            await SendRoom(r, $"{Settings.Name}님이 {names}을 초대했습니다.", null, false, sys: "invite");
        }

        public async Task RenameRoom(ChatRoom r, string name)
        {
            name = (name ?? "").Trim();
            if (name == (r.Name ?? "")) return;
            r.Name = name;
            SaveRooms();
            await SendRoom(r, name.Length == 0 ? $"{Settings.Name}님이 방 이름을 지웠습니다." : $"{Settings.Name}님이 방 이름을 '{name}'(으)로 바꿨습니다.", null, false, sys: "rename");
        }

        /// <summary>대화방 나가기 — 다른 구성원에게 알리고, 내 기록에서 지움</summary>
        public async Task LeaveRoom(ChatRoom r)
        {
            try { await SendRoom(r, $"{Settings.Name}님이 나갔습니다.", null, false, sys: "leave"); } catch { }
            r.Left = true;
            r.Members.RemoveAll(m => m.Id != Settings.Id);
            var key = Typists.RoomKey(r.Id);
            DeleteHistory(History.Where(m => m.Room == r.Id).ToList());
            SaveRooms();
            CloseChatWindow(key);
        }

        /// <summary>받은 그룹 메시지의 방 정보(구성원 · 이름)를 반영. 받지 않을 메시지면 false</summary>
        bool ApplyRoom(Packet p, Peer from, IPAddress ip, ChatMessage m)
        {
            var id = NetService.Str(p, "room");
            if (string.IsNullOrEmpty(id)) return true;
            m.Room = id;
            var sys = NetService.Str(p, "sys");
            m.Sys = string.IsNullOrEmpty(sys) ? null : sys;
            var r = RoomOf(id);
            var mems = NetService.ObjList(p, "members").Select(x => new RoomMember
            {
                Id = NetService.Str(x, "id"),
                Name = NetService.Str(x, "name"),
                Ip = NetService.Str(x, "ip"),
                Port = NetService.Int(x, "port", Settings.Port),
            }).Where(x => !string.IsNullOrEmpty(x.Id)).GroupBy(x => x.Id).Select(g => g.First()).ToList();
            bool includesMe = mems.Count == 0 || mems.Any(x => x.Id == Settings.Id);
            if (r != null && r.Left)
            {
                // 나간 방: 다시 초대받았을 때만 다시 들어감
                if (sys != "invite" || !includesMe) return false;
                r.Left = false;
            }
            if (!includesMe && sys != "leave") return false;
            if (r == null)
            {
                if (sys == "leave") return false;
                r = new ChatRoom { Id = id, CreatedTs = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
                Settings.Rooms.Add(r);
            }
            if (mems.Count > 0)
            {
                var old = r.Members;
                r.Members = mems;
                foreach (var x in r.Members)
                {
                    if (x.Id == from.Id) { x.Ip = ip.ToString(); x.Name = from.Name ?? x.Name; }
                    else if (string.IsNullOrEmpty(x.Ip)) x.Ip = old.FirstOrDefault(o => o.Id == x.Id)?.Ip ?? Peers.FirstOrDefault(pp => pp.Id == x.Id)?.Ip;
                }
                if (r.Members.All(x => x.Id != Settings.Id)) r.Members.Add(MeMember);
            }
            if (sys == "leave") r.Members.RemoveAll(x => x.Id == from.Id);
            if (p.ContainsKey("roomName")) r.Name = NetService.Str(p, "roomName") ?? "";
            SaveRooms();
            return true;
        }

        /// <summary>그룹 메시지에 붙이는 방 정보</summary>
        void AddRoomFields(Packet packet, ChatMessage m)
        {
            if (m.Room == null) return;
            var r = RoomOf(m.Room);
            packet["room"] = m.Room;
            if (m.IsSys) packet["sys"] = m.Sys;
            if (r == null) return;
            packet["roomName"] = r.Name ?? "";
            packet["members"] = r.Members.Select(x =>
            {
                var peer = x.Id == Settings.Id ? null : Peers.FirstOrDefault(pp => pp.Id == x.Id);
                return new Packet
                {
                    ["id"] = x.Id,
                    ["name"] = x.Id == Settings.Id ? Settings.Name : peer?.Name ?? x.Name,
                    ["ip"] = x.Id == Settings.Id ? "" : peer?.Ip ?? x.Ip ?? "",
                    ["port"] = x.Id == Settings.Id ? Settings.Port : peer?.Port ?? x.Port,
                };
            }).ToList();
        }

        // ───────────────────────── 입력 중 ─────────────────────────

        public Typists Typing { get; } = new Typists();
        /// <summary>열쇠(1:1 = 동료 ID, 그룹 = room:ID)의 입력 중 표시가 바뀜</summary>
        public event Action<string> TypingChanged;
        readonly Dictionary<string, long> _typingSent = new Dictionary<string, long>();
        DispatcherTimer _typingTimer;

        /// <summary>그 대화에서 지금 입력 중인 동료 이름</summary>
        public List<string> TypingNames(string key) =>
            Typing.Who(key).Select(id => Peers.FirstOrDefault(p => p.Id == id)?.DisplayName ?? RoomOf(RoomIdOf(key))?.NameOf(id) ?? "").Where(n => n.Length > 0).ToList();

        public string TypingText(string key) => Typists.Text(TypingNames(key));

        /// <summary>내가 입력 중임을 알림 (3초에 한 번까지). on=false 는 입력을 멈췄거나 보냈을 때</summary>
        public void NotifyTyping(string key, bool on)
        {
            if (key == null || !Settings.ShareTyping) return;
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (on)
            {
                if (_typingSent.TryGetValue(key, out var last) && now - last < Typists.SendEveryMs) return;
                _typingSent[key] = now;
            }
            else if (!_typingSent.Remove(key)) return;

            var roomId = RoomIdOf(key);
            List<Peer> to;
            if (roomId != null) { var r = RoomOf(roomId); to = r == null ? new List<Peer>() : RoomPeers(r); }
            else { var p = Peers.FirstOrDefault(x => x.Id == key); to = p == null ? new List<Peer>() : new List<Peer> { p }; }
            to = to.Where(p => p.IsOnline && !string.IsNullOrEmpty(p.Ip)).ToList();
            if (to.Count == 0) return;
            var pk = new Packet { ["t"] = "typing", ["id"] = Settings.Id, ["on"] = on };
            if (roomId != null) pk["room"] = roomId;
            foreach (var p in to)
            {
                var ip = p.Ip; var port = p.Port;
                _ = Task.Run(async () => { try { await Net.Request(ip, port, new Packet(pk), 2500).ConfigureAwait(false); } catch { } });
            }
        }

        void OnTyping(Packet p)
        {
            var from = NetService.Str(p, "id");
            if (string.IsNullOrEmpty(from) || from == Settings.Id || IsBlocked(from)) return;
            var room = NetService.Str(p, "room");
            if (room != null && (RoomOf(room) == null || RoomOf(room).Left)) return;
            var key = room != null ? Typists.RoomKey(room) : from;
            bool on = NetService.Bool(p, "on");
            if (Typing.Set(key, from, on)) TypingChanged?.Invoke(key);
            if (on) StartTypingTimer();
        }

        /// <summary>메시지가 도착하면 그 사람의 입력 중 표시는 바로 끔</summary>
        void ClearTyping(string key, string from)
        {
            if (key != null && Typing.Set(key, from, false)) TypingChanged?.Invoke(key);
        }

        void StartTypingTimer()
        {
            if (_typingTimer == null)
            {
                _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _typingTimer.Tick += (s, e) =>
                {
                    foreach (var k in Typing.Expire()) TypingChanged?.Invoke(k);
                    if (!Typing.Any) _typingTimer.Stop();
                };
            }
            if (!_typingTimer.IsEnabled) _typingTimer.Start();
        }

        // ───────────────────────── 읽음 ─────────────────────────

        /// <summary>
        /// 여러 메시지를 한꺼번에 읽음 처리 — 기록 저장 · 안 읽은 수 갱신 · 알림을 한 번씩만 해서
        /// 안 읽은 메시지가 많이 쌓인 대화 창도 바로 열림
        /// </summary>
        public void MarkReadMany(IEnumerable<ChatMessage> items)
        {
            var list = items.Where(m => !m.Outgoing && !m.Read).ToList();
            if (list.Count == 0) return;
            foreach (var w in _popups.Where(x => list.Contains(x.Message)).ToList()) w.CloseIfIdle();
            foreach (var m in list) m.Read = true;
            Store.AppendHistory(list);
            var peerIds = new HashSet<string>(list.Select(m => m.PeerId).Where(x => x != null));
            var still = new HashSet<string>(History.Where(x => !x.Outgoing && !x.Read && x.PeerId != null).Select(x => x.PeerId));
            foreach (var p in Peers.Where(p => peerIds.Contains(p.Id))) p.HasUnread = still.Contains(p.Id);
            UnreadChanged?.Invoke();
            UpdateTray();
            if (!Settings.ReadReceipts) return;
            foreach (var g in list.Where(m => !m.Auto && !m.IsSys).GroupBy(m => m.PeerId))
            {
                var peer = Peers.FirstOrDefault(p => p.Id == g.Key);
                if (peer == null || string.IsNullOrEmpty(peer.Ip)) continue;
                var ip = peer.Ip; var port = peer.Port;
                var ids = g.Select(m => m.Id).ToList();
                var me = Settings.Id;
                _ = Task.Run(async () =>
                {
                    foreach (var id in ids)
                    {
                        try { await Net.Request(ip, port, new Packet { ["t"] = "read", ["msg"] = id, ["id"] = me }, 3000).ConfigureAwait(false); }
                        catch { break; }
                    }
                });
            }
        }

        /// <summary>그 대화(열쇠)의 안 읽은 메시지를 모두 읽음으로</summary>
        public void MarkChatRead(string key) => MarkReadMany(History.Where(m => !m.Outgoing && !m.Read && InChat(m, key)).ToList());

        /// <summary>대화 창을 연 뒤 한가할 때: 읽음 처리 · 아직 안 받은 이미지 받기 (최근 것부터)</summary>
        void AfterChatOpened(string key)
        {
            MarkChatRead(key);
            foreach (var m in History.Where(x => !x.Outgoing && x.Files.Count > 0 && InChat(x, key)).Take(30).ToList()) AutoFetchImages(m);
        }

        /// <summary>메시지가 속한 대화 창 열기 (그룹 메시지는 그 방, 1:1 은 그 동료)</summary>
        public ChatWindow OpenChatFor(ChatMessage m)
        {
            if (m == null) return null;
            if (m.Room != null)
            {
                var r = RoomOf(m.Room);
                return r == null || r.Left ? null : OpenRoom(r);
            }
            return OpenChat(PeerOf(m));
        }
    }
}
