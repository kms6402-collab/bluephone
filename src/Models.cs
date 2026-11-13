using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using System.Windows.Media;

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
        static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze(); return b;
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
        public string LocalPath { get; set; }

        [ScriptIgnore] public string SizeText => FormatSize(Size);
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
        [ScriptIgnore] public string TimeText => Time.Date == DateTime.Today ? Time.ToString("tt h:mm") : Time.ToString("M월 d일");
        [ScriptIgnore] public string FullTime => Time.ToString("yyyy. M. d. (ddd) tt h:mm");
        [ScriptIgnore] public string Preview
        {
            get
            {
                var t = (Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
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
                if (Outgoing && !Scheduled)
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
        public List<Reminder> Reminders { get; set; } = new List<Reminder>();
        public bool CaptureHotkey { get; set; } = true;
        public bool EnterToSend { get; set; } = true;
        public double WinLeft { get; set; } = -1e6;
        public double WinTop { get; set; } = -1e6;
        public double WinWidth { get; set; } = 340;
        public double WinHeight { get; set; } = 640;
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
}
