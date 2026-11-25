using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
#if MAC
using Avalonia.Media;
using Brush = Avalonia.Media.IBrush;
using ImageSource = Avalonia.Media.Imaging.Bitmap;
#else
using System.Windows.Media;
#endif

namespace BluePhone
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; Raise(name); return true;
        }
    }

    public static class Status
    {
        public const string Online = "online", Away = "away", Busy = "busy", Dnd = "dnd", Offline = "offline";

        public static string Label(string s) => s switch
        {
            Online => "대화 가능",
            Away => "자리 비움",
            Busy => "다른 용무 중",
            Dnd => "방해 금지",
            _ => "오프라인",
        };

        public static int Order(string s) => s switch
        {
            Online => 0, Busy => 1, Away => 2, Dnd => 3, _ => 9,
        };

        public static Brush Brush(string s) => s switch
        {
            Online => Palette.Online,
            Away => Palette.Away,
            Busy => Palette.Busy,
            Dnd => Palette.Busy,
            _ => Palette.Offline,
        };
    }

    public static class Palette
    {
        static Brush B(uint argb)
        {
#if MAC
            return new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
#else
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze(); return b;
#endif
        }
        public static readonly Brush Online = B(0xFF1FA971), Away = B(0xFFE0A100), Busy = B(0xFFE5484D), Offline = B(0xFFB9BFC8);

        // 이름 기반 아바타 색 (채도 낮춘 톤)
        static readonly uint[] Tones = { 0xFF4C6FD6, 0xFF2F8F7E, 0xFFB0603A, 0xFF7A5BC0, 0xFF3C8DBC, 0xFFA34D74, 0xFF5E7D3A, 0xFF8A6F2E };
        public static Brush Avatar(string key)
        {
            unchecked
            {
                int h = 17;
                foreach (var c in key ?? "") h = h * 31 + c;
                return B(Tones[(h & 0x7fffffff) % Tones.Length]);
            }
        }
    }

    public class Peer : Observable
    {
        string _name, _alias, _dept, _status = Status.Offline, _ip, _host, _note, _avatarHash;
        int _port;
        bool _hasUnread;

        public string Id { get; set; }
        public string Name { get => _name; set { if (Set(ref _name, value)) NamesChanged(); } }
        /// <summary>내가 붙인 별명 (내 PC에만 저장, 상대 이름보다 우선)</summary>
        public string Alias { get => _alias; set { if (Set(ref _alias, string.IsNullOrWhiteSpace(value) ? null : value.Trim())) NamesChanged(); } }
        public string Dept { get => _dept; set { if (Set(ref _dept, value)) { Raise(nameof(Group)); Raise(nameof(Tooltip)); } } }
        public string Status_ { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); Raise(nameof(IsOnline)); Raise(nameof(SortKey)); Raise(nameof(Opacity)); Raise(nameof(SubText)); } } }
        public string Ip { get => _ip; set { if (Set(ref _ip, value)) { Raise(nameof(Address)); Raise(nameof(Tooltip)); } } }
        public int Port { get => _port; set { if (Set(ref _port, value)) Raise(nameof(Address)); } }
        public string Host { get => _host; set { if (Set(ref _host, value)) NamesChanged(); } }
        public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(SubText)); } }
        public string AvatarHash { get => _avatarHash; set => Set(ref _avatarHash, value); }
        bool _favorite, _secure;
        /// <summary>즐겨찾기 (내 PC에만 저장) — 목록 맨 위에 고정</summary>
        public bool Favorite { get => _favorite; set { if (Set(ref _favorite, value)) { Raise(nameof(Group)); Raise(nameof(GroupOrder)); } } }
        /// <summary>마지막 연결이 암호화되었는지</summary>
        [ScriptIgnore] public bool Secure { get => _secure; set => Set(ref _secure, value); }
        [ScriptIgnore] public int GroupOrder => Favorite ? 0 : 1;
        public long LastSeenTs { get; set; }
        public bool HasUnread { get => _hasUnread; set => Set(ref _hasUnread, value); }

        void NamesChanged()
        {
            Raise(nameof(DisplayName)); Raise(nameof(Initial)); Raise(nameof(Tooltip)); Raise(nameof(RealNameHint));
        }

        [ScriptIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(Alias) ? Alias : string.IsNullOrWhiteSpace(Name) ? (Host ?? Ip) : Name;
        [ScriptIgnore] public string RealNameHint => !string.IsNullOrWhiteSpace(Alias) && Alias != Name ? Name : null;
        [ScriptIgnore] public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName.Substring(0, 1).ToUpperInvariant();
        [ScriptIgnore] public Brush AvatarBrush => Palette.Avatar(Id ?? DisplayName);
        [ScriptIgnore] public ImageSource AvatarImage => AvatarStore.Get(Id);
        [ScriptIgnore] public string StatusText => Status.Label(Status_);
        [ScriptIgnore] public Brush StatusBrush => Status.Brush(Status_);
        [ScriptIgnore] public bool IsOnline => Status_ != Status.Offline;
        [ScriptIgnore] public double Opacity => IsOnline ? 1.0 : 0.55;
        [ScriptIgnore] public string Group => Favorite ? "즐겨찾기" : string.IsNullOrWhiteSpace(Dept) ? "부서 미지정" : Dept.Trim();
        [ScriptIgnore] public string Address => $"{Ip}:{Port}";
        [ScriptIgnore] public string SubText => string.IsNullOrWhiteSpace(Note) ? StatusText : Note;
        [ScriptIgnore] public int SortKey => Status.Order(Status_);
        [ScriptIgnore] public string Tooltip => (RealNameHint != null ? $"{DisplayName} ({Name})" : DisplayName) + $"\n{Group} · {Host}\n{Address}";

        public void RefreshSub() => Raise(nameof(SubText));
        public void RefreshAvatar() => Raise(nameof(AvatarImage));
    }

    public class FileItem : Observable
    {
        string _state = "";
        double _progress;
        public string Name { get; set; }
        public long Size { get; set; }
        public string Token { get; set; }
        string _local;
        public string LocalPath
        {
            get => _local;
            set { if (Set(ref _local, value)) { Raise(nameof(PreviewPath)); Raise(nameof(CanPreviewNow)); Raise(nameof(HasPreview)); } }
        }

        [ScriptIgnore] public string SizeText => FormatSize(Size);
        /// <summary>바로 보기 이미지 (이미지 파일이 내 PC에 있을 때)</summary>
        [ScriptIgnore] public string PreviewPath => Media.IsImage(Name) && Media.Exists(LocalPath) ? LocalPath : null;
        /// <summary>미리 보기 창으로 볼 수 있는지 (이미지 · 텍스트 · 동영상, 내 PC에 있을 때)</summary>
        [ScriptIgnore] public bool HasPreview => PreviewPath != null;
        [ScriptIgnore] public bool CanPreviewNow => Media.CanPreview(Name) && Media.Exists(LocalPath);
        [ScriptIgnore] public string State { get => _state; set => Set(ref _state, value); }
        [ScriptIgnore] public double Progress { get => _progress; set => Set(ref _progress, value); }

        public static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1024.0 / 1024).ToString("0.#") + " MB";
            return (b / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }
    }

    public class ChatMessage : Observable
    {
        bool _read, _failed;
        public string Id { get; set; }
        public bool Outgoing { get; set; }
        public string PeerId { get; set; }
        public string PeerName { get; set; }
        public string PeerDept { get; set; }
        public string PeerIp { get; set; }
        public int PeerPort { get; set; }
        public string Text { get; set; }
        public long Ts { get; set; }
        public bool Urgent { get; set; }
        public bool Broadcast { get; set; }
        public bool Auto { get; set; }
        public List<string> To { get; set; } = new List<string>();
        public List<FileItem> Files { get; set; } = new List<FileItem>();
        public List<string> ToIds { get; set; } = new List<string>();
        public List<string> ReadBy { get; set; } = new List<string>();
        public List<string> FailedTo { get; set; } = new List<string>();
        /// <summary>아직 전달하지 못해 상대가 접속하면 보낼 동료 ID</summary>
        public List<string> PendingTo { get; set; } = new List<string>();
        /// <summary>예약 발송 시각(ms). 0 이면 즉시</summary>
        public long ScheduledTs { get; set; }
        public bool Scheduled { get; set; }
        /// <summary>1:1 대화 창에서 보낸(받은) 메시지. false 면 쪽지</summary>
        public bool Chat { get; set; }
        /// <summary>그룹 대화방 ID. null 이면 1:1 대화(또는 쪽지)</summary>
        public string Room { get; set; }
        /// <summary>그룹 대화방의 알림 줄 종류 (invite · leave · rename). 말풍선 대신 가운데 작은 글씨로</summary>
        public string Sys { get; set; }
        [ScriptIgnore] public bool IsSys => !string.IsNullOrEmpty(Sys);
        /// <summary>@로 부른 사람 ID (모두는 "all"). 없으면 null</summary>
        public List<string> Mentions { get; set; }
        /// <summary>초대 알림(sys=invite)으로 새로 들어온 사람 ID</summary>
        public List<string> Invited { get; set; }
        /// <summary>받은 메시지에서 나를 @로 불렀는가</summary>
        [ScriptIgnore] public bool MentionsMe => !Outgoing && Mentions != null && (Mentions.Contains(Mention.All) || Mentions.Contains(ChatRoom.MyId));
        /// <summary>나를 대화방에 초대한 알림인가</summary>
        [ScriptIgnore] public bool InvitesMe => !Outgoing && Sys == "invite" && Invited != null && Invited.Contains(ChatRoom.MyId);
        [ScriptIgnore] public bool IsRoom => Room != null;
        /// <summary>그룹 대화방에서 받은 메시지 — 말풍선 위에 보낸 사람 이름</summary>
        [ScriptIgnore] public bool ShowSender => Room != null && !Outgoing && !IsSys;
        /// <summary>1:1 대화 창에 보일 메시지 (그룹 대화 · 알림 줄 제외)</summary>
        public bool IsDirectWith(string peerId) => Room == null && Involves(peerId);
        [ScriptIgnore] public bool IsNote => !Chat;
        public bool Secure { get; set; }
        [ScriptIgnore] public bool IsPending => Outgoing && (Scheduled || PendingTo.Count > 0);
        [ScriptIgnore] public string ScheduleText => DateTimeOffset.FromUnixTimeMilliseconds(ScheduledTs).LocalDateTime.ToString("M월 d일 tt h:mm");
        public void Touch() { Raise(nameof(Meta)); Raise(nameof(IsProblem)); Raise(nameof(IsPending)); Raise(nameof(TimeText)); }
        [ScriptIgnore] public bool IsProblem => Outgoing && (Failed || FailedTo.Count > 0);
        [ScriptIgnore] public bool IsUnread => !Outgoing && !Read;
        public bool Read { get => _read; set { if (Set(ref _read, value)) { Raise(nameof(Meta)); Raise(nameof(IsUnread)); } } }
        public bool Failed { get => _failed; set { if (Set(ref _failed, value)) Raise(nameof(Meta)); } }

        [ScriptIgnore] public DateTime Time => DateTimeOffset.FromUnixTimeMilliseconds(Ts).LocalDateTime;
        /// <summary>마크다운 서식이 있는 메시지</summary>
        [ScriptIgnore] public bool HasMarkup => Markdown.HasMarkup(Text);
        [ScriptIgnore] public string TimeText => Time.Date == DateTime.Today ? Time.ToString("tt h:mm") : Time.ToString("M월 d일");
        [ScriptIgnore] public string FullTime => Time.ToString("yyyy. M. d. (ddd) tt h:mm");
        [ScriptIgnore] public string Preview
        {
            get
            {
                var t = (Markdown.Plain(Text) ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (t.Length == 0 && Files.Count > 0) t = "파일 " + Files.Count + "개";
                return t;
            }
        }
        /// <summary>현재 동료 목록에서 이름을 찾아 쓰기 위한 조회 함수 (이름 변경이 기록에도 즉시 반영)</summary>
        public static Func<string, Peer> PeerLookup;

        string NameOf(string id, string fallback) => (id != null ? PeerLookup?.Invoke(id)?.DisplayName : null) ?? fallback;

        [ScriptIgnore] public string FromName => NameOf(PeerId, PeerName);
        [ScriptIgnore] public List<string> ToNames
        {
            get
            {
                var list = new List<string>();
                for (int i = 0; i < To.Count; i++) list.Add(NameOf(i < ToIds.Count ? ToIds[i] : null, To[i]));
                return list;
            }
        }
        [ScriptIgnore] public string Who
        {
            get
            {
                if (!Outgoing) return FromName;
                var n = ToNames;
                return n.Count > 1 ? $"{n[0]} 외 {n.Count - 1}명" : (n.FirstOrDefault() ?? FromName);
            }
        }
        public bool Involves(string peerId) => PeerId == peerId || ToIds.Contains(peerId);
        public void RefreshNames() { Raise(nameof(Who)); Raise(nameof(FromName)); Raise(nameof(Initial)); Raise(nameof(AvatarImage)); }
        [ScriptIgnore] public string Direction => Outgoing ? "보냄" : "받음";
        [ScriptIgnore] public string Meta
        {
            get
            {
                var parts = new List<string>();
                if (Broadcast) parts.Add("전체 공지");
                if (Urgent) parts.Add("긴급");
                if (Auto) parts.Add("자동응답");
                if (Outgoing && Scheduled) parts.Add("예약 · " + ScheduleText);
                else if (Outgoing && PendingTo.Count > 0)
                    parts.Add(To.Count <= 1 ? "대기 중 · 상대가 접속하면 전달" : $"대기 {PendingTo.Count}명");
                if (Outgoing && !Scheduled && Room != null)
                {
                    // 그룹 대화: 카카오톡처럼 아직 안 읽은 사람 수
                    int unread = Math.Max(0, ToIds.Count - FailedTo.Count - ReadBy.Count);
                    if (IsSys) { }
                    else if (Failed) parts.Add("전달 실패");
                    else if (ToIds.Count > 0) parts.Add(unread > 0 ? $"안 읽음 {unread}" : "모두 읽음");
                    if (FailedTo.Count > 0 && !Failed) parts.Add($"실패 {FailedTo.Count}");
                }
                else if (Outgoing && !Scheduled)
                {
                    if (To.Count <= 1 && PendingTo.Count > 0) { }
                    else if (To.Count <= 1) parts.Add(Failed ? "전달 실패" : Read ? "읽음" : "전달됨");
                    else
                    {
                        parts.Add($"읽음 {ReadBy.Count}/{To.Count - FailedTo.Count}");
                        if (FailedTo.Count > 0) parts.Add($"실패 {FailedTo.Count}");
                    }
                }
                else if (!Outgoing && !Read) parts.Add("안 읽음");
                if (Files.Count > 0) parts.Add("첨부 " + Files.Count);
                return string.Join(" · ", parts);
            }
        }
        [ScriptIgnore] public Brush AvatarBrush => Palette.Avatar(PeerId ?? PeerName);
        [ScriptIgnore] public ImageSource AvatarImage => Outgoing && To.Count > 1 ? null : AvatarStore.Get(PeerId);
        [ScriptIgnore] public string Initial => string.IsNullOrEmpty(Who) ? "?" : Who.Substring(0, 1);
    }

    public class AppSettings
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = Environment.UserName;
        public string Dept { get; set; } = "";
        public string Note { get; set; } = "";
        public int Port { get; set; } = 9631;
        public bool AutoStart { get; set; } = false;
        public bool Sound { get; set; } = true;
        public bool ReadReceipts { get; set; } = true;
        public bool PopupNoFocus { get; set; } = true;
        /// <summary>받은 쪽지 창 자동 닫기(초). 0 이면 닫지 않음</summary>
        public int PopupAutoClose { get; set; }
        /// <summary>1:1 대화 알림 창 자동 닫기(초). 0 이면 닫지 않음</summary>
        public int ChatAlertAutoClose { get; set; } = 5;
        /// <summary>대화 창 꾸미기</summary>
        public string ChatTheme { get; set; } = "light";
        public string ChatFont { get; set; } = "";
        public double ChatFontSize { get; set; } = 13.5;
        /// <summary>차단한 동료 — 목록에서 숨기고 쪽지 · 대화를 받지 않음</summary>
        public List<BlockedPeer> Blocked { get; set; } = new List<BlockedPeer>();
        /// <summary>받은 이미지(20MB 이하)를 자동으로 받아 바로 보기</summary>
        public bool AutoImage { get; set; } = true;
        /// <summary>알림 개인정보 보호: 0 = 보낸 사람·내용 모두 표시, 1 = 보낸 사람만, 2 = 모두 숨기기</summary>
        public int AlertPrivacy { get; set; } = 1;
        public bool StartHidden { get; set; } = false;
        public int AutoAwayMinutes { get; set; } = 10;
        public string AutoReply { get; set; } = "";
        public string DownloadDir { get; set; } = "";
        public List<string> ManualHosts { get; set; } = new List<string>();
        public string LastStatus { get; set; } = Status.Online;
        public string AvatarHash { get; set; } = "";
        public bool AutoUpdate { get; set; } = true;
        public string SkippedVersion { get; set; } = "";
        public string LastRunVersion { get; set; } = "";
        public List<PeerGroup> Groups { get; set; } = new List<PeerGroup>();
        /// <summary>그룹 대화방</summary>
        public List<ChatRoom> Rooms { get; set; } = new List<ChatRoom>();
        /// <summary>글자를 입력하는 동안 상대에게 "입력 중" 표시</summary>
        public bool ShareTyping { get; set; } = true;
        public List<Reminder> Reminders { get; set; } = new List<Reminder>();
        public bool CaptureHotkey { get; set; } = true;
        /// <summary>텔레그램 연동: 받은 쪽지 · 대화를 텔레그램으로 받고, 텔레그램에서 명령으로 보내기</summary>
        public bool TelegramEnabled { get; set; }
        public string TelegramToken { get; set; } = "";
        public long TelegramChatId { get; set; }
        public string TelegramWho { get; set; } = "";
        public string TelegramBot { get; set; } = "";
        public bool TelegramNotes { get; set; } = true;
        public bool TelegramChats { get; set; } = true;
        public bool TelegramShowText { get; set; } = true;
        /// <summary>받은 첨부파일도 텔레그램으로 (50MB 이하)</summary>
        public bool TelegramFiles { get; set; } = true;
        /// <summary>자리를 비웠을 때만 보내기 (상태가 '대화 가능'이 아니거나 3분 넘게 PC를 쓰지 않을 때)</summary>
        public bool TelegramOnlyAway { get; set; }
        /// <summary>텔레그램 연결 방법: auto · system · direct · http · socks5</summary>
        public string TelegramNet { get; set; } = "auto";
        /// <summary>외부 프록시 "주소:포트"</summary>
        public string TelegramProxy { get; set; } = "";
        public string TelegramProxyUser { get; set; } = "";
        /// <summary>프록시 비밀번호 (Windows 계정으로 암호화해 저장)</summary>
        public string TelegramProxyPass { get; set; } = "";
        /// <summary>API 서버 주소 (비우면 api.telegram.org). 중계 서버를 쓸 때</summary>
        public string TelegramApi { get; set; } = "";
        public bool EnterToSend { get; set; } = true;
        public double WinLeft { get; set; } = -1e6;
        public double WinTop { get; set; } = -1e6;
        public double WinWidth { get; set; } = 340;
        public double WinHeight { get; set; } = 640;
    }

    /// <summary>그룹 대화방 — 여러 동료가 함께 쓰는 대화 창</summary>
    public class ChatRoom
    {
        public string Id { get; set; }
        public string Name { get; set; } = "";
        /// <summary>구성원 (나 포함)</summary>
        public List<RoomMember> Members { get; set; } = new List<RoomMember>();
        public long CreatedTs { get; set; }
        /// <summary>내가 나간 방</summary>
        public bool Left { get; set; }

        public static Func<string, Peer> Lookup;
        public static string MyId;

        [ScriptIgnore] public IEnumerable<RoomMember> Others => Members.Where(m => m.Id != MyId);
        public string NameOf(string id) => Lookup?.Invoke(id)?.DisplayName ?? Members.FirstOrDefault(m => m.Id == id)?.Name ?? "";
        /// <summary>방 이름을 정하지 않았으면 구성원 이름으로</summary>
        [ScriptIgnore] public string Title
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
                var names = Others.Select(m => NameOf(m.Id)).Where(n => n.Length > 0).ToList();
                if (names.Count == 0) return "그룹 대화";
                return names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" 외 {names.Count - 3}명";
            }
        }
        [ScriptIgnore] public int Count => Members.Count;
    }

    public class RoomMember
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
    }

    public class PeerGroup
    {
        public string Name { get; set; }
        public List<string> Members { get; set; } = new List<string>();
    }

    public class Reminder
    {
        public string MsgId { get; set; }
        public long At { get; set; }
    }

    /// <summary>대화함의 한 줄 — 동료 한 명과의 1:1 대화, 또는 그룹 대화방 요약</summary>
    public sealed class Conversation
    {
        public string PeerId { get; set; }
        public Peer Peer { get; set; }
        public ChatRoom Room { get; set; }
        public ChatMessage Last { get; set; }
        public int Unread { get; set; }
        public int Count { get; set; }
        /// <summary>"OO님이 입력 중…" (없으면 null)</summary>
        public string Typing { get; set; }
        /// <summary>안 읽은 메시지 중에 나를 @로 부른 것이 있음</summary>
        public bool Mentioned { get; set; }
        /// <summary>대화 창 · 입력 중 표시에 쓰는 열쇠 (1:1 은 동료 ID, 그룹은 room:방ID)</summary>
        public string Key => Room != null ? Typists.RoomKey(Room.Id) : PeerId;
        public bool IsRoom => Room != null;
        public bool IsTyping => Typing != null;
        public string Name => Room != null ? Room.Title : Peer?.DisplayName ?? Last?.Who ?? "";
        public string MemberText => Room != null ? Room.Count.ToString() : "";
        public string Initial => Room != null ? "👥" : string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1);
        public Brush AvatarBrush => Palette.Avatar(Room != null ? "room:" + Room.Id : PeerId ?? Name);
        public ImageSource AvatarImage => Room != null || PeerId == null ? null : AvatarStore.Get(PeerId);
        public Brush StatusBrush => Room != null ? null : Peer?.StatusBrush ?? Status.Brush(Status.Offline);
        public string Preview => Typing ?? (Mentioned ? "[나를 언급] " : "") + (Last == null ? "" : Last.IsSys ? Last.Text : (Last.Outgoing ? "나: " : Room != null ? Last.FromName + ": " : "") + Last.Preview);
        public string TimeText => Last?.TimeText ?? "";
        public bool HasUnread => Unread > 0;
        public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
    }

    /// <summary>
    /// 상대가 글자를 입력하고 있는지 (대화 창 · 대화함의 "입력 중…").
    /// 상대는 입력하는 동안 3초마다 알려 주고, 7초 동안 소식이 없으면 멈춘 것으로 봄
    /// </summary>
    public sealed class Typists
    {
        public const int SendEveryMs = 3000;
        public const int ExpireMs = 7000;
        public static string RoomKey(string roomId) => "room:" + roomId;

        // 열쇠(1:1 = 동료 ID, 그룹 = room:ID) → 동료 ID → 마지막 알림 시각
        readonly Dictionary<string, Dictionary<string, long>> _map = new Dictionary<string, Dictionary<string, long>>();
        static long Now => DateTimeOffset.Now.ToUnixTimeMilliseconds();

        /// <summary>바뀌었으면 true</summary>
        public bool Set(string key, string peerId, bool on)
        {
            if (!_map.TryGetValue(key, out var d)) { if (!on) return false; d = _map[key] = new Dictionary<string, long>(); }
            bool had = d.ContainsKey(peerId);
            if (on) d[peerId] = Now; else d.Remove(peerId);
            if (d.Count == 0) _map.Remove(key);
            return had != on;
        }

        public List<string> Who(string key)
        {
            if (key == null || !_map.TryGetValue(key, out var d)) return new List<string>();
            var now = Now;
            return d.Where(x => now - x.Value < ExpireMs).Select(x => x.Key).ToList();
        }

        public bool Any => _map.Count > 0;

        /// <summary>오래된 표시 정리. 바뀐 열쇠 목록</summary>
        public List<string> Expire()
        {
            var now = Now;
            var changed = new List<string>();
            foreach (var kv in _map.ToList())
            {
                foreach (var id in kv.Value.Where(x => now - x.Value >= ExpireMs).Select(x => x.Key).ToList()) { kv.Value.Remove(id); if (!changed.Contains(kv.Key)) changed.Add(kv.Key); }
                if (kv.Value.Count == 0) _map.Remove(kv.Key);
            }
            return changed;
        }

        /// <summary>"홍길동님이 입력 중…" / "홍길동님 외 2명이 입력 중…"</summary>
        public static string Text(IList<string> names)
        {
            if (names == null || names.Count == 0) return null;
            return names.Count == 1 ? $"{names[0]}님이 입력 중…" : $"{names[0]}님 외 {names.Count - 1}명이 입력 중…";
        }
    }

    public sealed class BlockedPeer
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>@ 부르기 목록의 한 줄</summary>
    public sealed class MentionPick
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Sub { get; set; }
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Id == Mention.All ? "@" : Name.Substring(0, 1);
    }

    /// <summary>카카오톡처럼 '@이름'으로 대화방의 동료 부르기</summary>
    public static class Mention
    {
        public const string All = "all";
        public const string AllName = "모두";

        /// <summary>이름 속 빈칸은 붙여 씀 ('@홍 길동' 대신 '@홍길동' — 빈칸에서 부르기가 끝나므로)</summary>
        public static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "" : string.Concat(name.Where(c => !char.IsWhiteSpace(c) && c != '@'));

        /// <summary>커서 바로 앞이 '@검색어'면 '@' 위치 (아니면 -1)</summary>
        public static int Find(string text, int caret, out string query)
        {
            query = null;
            if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length) return -1;
            for (int i = caret - 1; i >= 0 && caret - i <= 21; i--)
            {
                char c = text[i];
                if (c == '@')
                {
                    if (i > 0 && !char.IsWhiteSpace(text[i - 1])) return -1;   // 메일 주소 등
                    query = text.Substring(i + 1, caret - i - 1);
                    return i;
                }
                if (char.IsWhiteSpace(c)) return -1;
            }
            return -1;
        }

        /// <summary>'@검색어'를 '@이름 '으로 바꾼 글과 새 커서 위치</summary>
        public static string Insert(string text, int at, int caret, string name, out int newCaret)
        {
            var ins = "@" + name + " ";
            var rest = text.Substring(caret);
            if (rest.StartsWith(" ")) rest = rest.Substring(1);
            newCaret = at + ins.Length;
            return text.Substring(0, at) + ins + rest;
        }

        /// <summary>말풍선 글자에서 '@이름' 조각 (강조 표시용)</summary>
        public static readonly System.Text.RegularExpressions.Regex Rx = new System.Text.RegularExpressions.Regex(@"(?<=^|\s)@[^\s@]{1,20}", System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
