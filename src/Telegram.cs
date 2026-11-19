using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BluePhone
{
    /// <summary>
    /// 텔레그램 연동 (Bot API · 서버 불필요).
    ///  · 받은 쪽지 · 대화를 내 텔레그램으로 전달
    ///  · 텔레그램에서 명령어(/msg, /chat …)나 '답장'으로 동료에게 바로 보내기
    /// 내가 연결한 텔레그램 대화(ChatId)에서 온 명령만 처리함
    /// </summary>
    public sealed class TelegramBridge
    {
        readonly Core _core;
        CancellationTokenSource _cts;
        Task _loop;
        long _offset;
        string _runningToken;
        long _runningChat;

        /// <summary>텔레그램으로 보낸 알림 → 원래 쪽지 (답장하면 그 동료에게 보내기 위함)</summary>
        readonly Dictionary<long, (string peerId, bool chat, string msgId)> _sent = new Dictionary<long, (string, bool, string)>();
        readonly Queue<long> _sentOrder = new Queue<long>();
        /// <summary>마지막 /list 결과 (번호로 고르기)</summary>
        List<string> _listed = new List<string>();

        public string State { get; private set; } = "";
        public event Action StateChanged;

        static Dispatcher UI => Application.Current.Dispatcher;
        AppSettings S => _core.Settings;

        public TelegramBridge(Core core)
        {
            _core = core;
            // 텔레그램에서 받아 동료에게 보낸 파일은 30일 뒤 정리
            Task.Run(() =>
            {
                try
                {
                    var dir = Path.Combine(Store.Dir, "telegram");
                    if (!Directory.Exists(dir)) return;
                    foreach (var f in new DirectoryInfo(dir).GetFiles())
                        if (f.LastWriteTime < DateTime.Now.AddDays(-30)) try { f.Delete(); } catch { }
                }
                catch { }
            });
        }

        static TelegramBridge() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        static string ApiBase => TgNet.ApiBase;

        public static bool TestApi => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BLUEPHONE_TELEGRAM_API"));

        public bool Connected => !string.IsNullOrWhiteSpace(S.TelegramToken) && S.TelegramChatId != 0;
        public bool Active => S.TelegramEnabled && Connected;

        void SetState(string s)
        {
            State = s;
            UI.BeginInvoke(new Action(() => StateChanged?.Invoke()));
        }

        // ───────────────────────── lifecycle ─────────────────────────

        /// <summary>설정에 맞게 명령 수신을 시작하거나 멈춤</summary>
        public void Apply()
        {
            if (!Active) { Stop(); SetState(Connected ? "꺼짐" : ""); return; }
            var token = S.TelegramToken.Trim();
            if (_loop != null && !_loop.IsCompleted && token == _runningToken && S.TelegramChatId == _runningChat) return;
            Stop();
            _runningToken = token;
            _runningChat = S.TelegramChatId;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => Loop(token, ct));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _loop = null;
            _runningToken = null;
        }

        async Task Loop(string token, CancellationToken ct)
        {
            int fails = 0;
            bool first = true;
            SetState("연결 중…");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var args = new Dictionary<string, string>
                    {
                        ["timeout"] = "25",
                        ["allowed_updates"] = "[\"message\"]",
                    };
                    // 시작할 때 이미 쌓여 있던(꺼져 있는 동안 보낸) 명령은 실행하지 않음
                    if (first) args["offset"] = "-1";
                    else if (_offset != 0) args["offset"] = _offset.ToString();
                    var r = await Call(token, "getUpdates", args, 40000, ct).ConfigureAwait(false);
                    foreach (var u in Arr(r))
                    {
                        var id = Long(u, "update_id");
                        if (id >= _offset) _offset = id + 1;
                        if (!first) await Handle(token, Obj(u, "message")).ConfigureAwait(false);
                    }
                    if (first) _ = SetCommands(token, ct);
                    first = false;
                    if (fails > 0 || State != "연결됨") SetState("연결됨");
                    fails = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (TelegramException ex) when (ex.Code == 401 || ex.Code == 404)
                {
                    SetState("봇 토큰이 올바르지 않습니다");
                    return;
                }
                catch (TelegramException ex) when (ex.Code == 409)
                {
                    SetState("다른 곳에서 같은 봇을 쓰고 있습니다 (봇 하나는 PC 한 대에서만)");
                    await Delay(15000, ct);
                }
                catch (Exception ex)
                {
                    fails++;
                    SetState("텔레그램에 연결할 수 없습니다 — 다시 시도 중 (" + Short(ex) + ")");
                    await Delay(Math.Min(60000, 3000 * fails), ct);
                }
            }
        }

        static async Task Delay(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct).ConfigureAwait(false); } catch { }
        }

        static string Short(Exception ex)
        {
            var m = (ex is WebException w && w.Response == null ? w.Status.ToString() : ex.Message) ?? "";
            return m.Length > 60 ? m.Substring(0, 60) + "…" : m;
        }

        // ───────────────────────── pairing (설정 화면) ─────────────────────────

        /// <summary>봇 토큰 확인 → (봇 이름, 사용자 이름)</summary>
        public static async Task<string> CheckToken(string token, CancellationToken ct)
        {
            var me = await Call(token, "getMe", null, 15000, ct).ConfigureAwait(false) as Dictionary<string, object>;
            var user = Str(me, "username");
            if (string.IsNullOrEmpty(user)) throw new TelegramException(0, "봇 정보를 읽지 못했습니다.");
            return user;
        }

        /// <summary>
        /// 연결: 사용자가 봇에게 "/start 코드" 를 보낼 때까지 기다렸다가 그 대화를 내 텔레그램으로 등록.
        /// 코드가 맞아야 등록되므로 다른 사람이 봇에게 말을 걸어도 연결되지 않음
        /// </summary>
        public async Task<(long chatId, string who)> Pair(string token, string code, CancellationToken ct, int timeoutSec = 180)
        {
            Stop();   // 같은 봇으로 동시에 getUpdates 하면 충돌
            try { await Call(token, "deleteWebhook", null, 15000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; } catch { }
            long offset = 0;
            // 이전 메시지는 버림
            var old = await Call(token, "getUpdates", new Dictionary<string, string> { ["offset"] = "-1", ["timeout"] = "0" }, 15000, ct).ConfigureAwait(false);
            foreach (var u in Arr(old)) offset = Math.Max(offset, Long(u, "update_id") + 1);

            var until = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < until)
            {
                ct.ThrowIfCancellationRequested();
                var args = new Dictionary<string, string> { ["timeout"] = "20", ["allowed_updates"] = "[\"message\"]" };
                if (offset != 0) args["offset"] = offset.ToString();
                var r = await Call(token, "getUpdates", args, 35000, ct).ConfigureAwait(false);
                foreach (var u in Arr(r))
                {
                    offset = Math.Max(offset, Long(u, "update_id") + 1);
                    var msg = Obj(u, "message");
                    var text = (Str(msg, "text") ?? "").Trim();
                    var chat = Obj(msg, "chat");
                    if (chat == null) continue;
                    var chatId = Long(chat, "id");
                    if (text.EndsWith(code) && (text == code || text.StartsWith("/start")))
                    {
                        // 처리한 메시지 확정 (다음 수신에서 다시 받지 않도록)
                        try { await Call(token, "getUpdates", new Dictionary<string, string> { ["offset"] = offset.ToString(), ["timeout"] = "0" }, 15000, ct).ConfigureAwait(false); } catch { }
                        _offset = offset;
                        var from = Obj(msg, "from");
                        var who = Str(from, "first_name") ?? Str(chat, "title") ?? "";
                        var un = Str(from, "username");
                        if (!string.IsNullOrEmpty(un)) who += " (@" + un + ")";
                        return (chatId, who.Trim());
                    }
                    if (text.StartsWith("/start"))
                        await SendTo(token, chatId, AppInfo.Name + " 설정 화면에 나온 연결 코드를 함께 보내 주세요.\n예) /start 123456", ct).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("시간 안에 연결 코드를 받지 못했습니다.");
        }

        // ───────────────────────── 명령 목록 ─────────────────────────

        /// <summary>(명령, 별칭, 텔레그램 메뉴 설명, 도움말 한 줄)</summary>
        static readonly (string cmd, string[] alias, string menu, string help)[] Commands =
        {
            ("help",      new[] { "start", "도움말", "h" },        "모든 명령어 보기",                 "/help — 이 도움말"),
            ("list",      new[] { "l", "목록", "동료" },            "동료 목록 (검색어 가능)",          "/list [검색어] — 동료 목록 (번호로 고르기)"),
            ("info",      new[] { "정보", "who" },                 "동료 정보  /info 이름",            "/info 이름 — 동료 상태 · 부서 · 주소"),
            ("msg",       new[] { "m", "쪽지" },                   "쪽지 보내기  /msg 이름 내용",      "/msg 이름 내용 — 쪽지 보내기"),
            ("chat",      new[] { "c", "대화" },                   "대화 보내기  /chat 이름 내용",     "/chat 이름 내용 — 1:1 대화로 보내기"),
            ("urgent",    new[] { "u", "긴급" },                   "긴급 쪽지  /urgent 이름 내용",     "/urgent 이름 내용 — 긴급 쪽지"),
            ("r",         new[] { "reply", "답장" },               "마지막 받은 메시지에 답장",        "/r 내용 — 마지막으로 받은 메시지에 답장"),
            ("all",       new[] { "공지", "broadcast" },           "전체 공지  /all 내용",             "/all 내용 — 접속 중인 모두에게 전체 공지"),
            ("groups",    new[] { "그룹목록" },                    "그룹 목록",                        "/groups — 그룹 목록"),
            ("group",     new[] { "g", "그룹" },                   "그룹 쪽지  /group 그룹 내용",      "/group 그룹 내용 — 그룹 전체에 쪽지"),
            ("later",     new[] { "예약" },                        "예약 쪽지  /later 30m 이름 내용",  "/later 시간 이름 내용 — 예약 쪽지 (30m · 2h · 18:30)"),
            ("unread",    new[] { "안읽음", "new" },               "안 읽은 메시지 다시 받기",         "/unread — 안 읽은 메시지를 다시 보내기"),
            ("readall",   new[] { "모두읽음" },                    "모두 읽음으로",                    "/readall — 안 읽은 메시지 모두 읽음 처리"),
            ("history",   new[] { "기록" },                        "최근 대화  /history 이름 [개수]",  "/history 이름 [개수] — 그 동료와 최근에 주고받은 메시지"),
            ("status",    new[] { "s", "상태" },                   "내 상태  /status 자리",            "/status 대화|자리|용무|방해 — 내 상태 바꾸기"),
            ("memo",      new[] { "상태메시지" },                  "상태 메시지  /memo 내용",          "/memo 내용 — 상태 메시지 (/memo - 로 지우기)"),
            ("autoreply", new[] { "자동응답" },                    "자동응답  /autoreply 내용|끄기",   "/autoreply 내용 — 부재 중 자동응답 (/autoreply 끄기)"),
            ("mute",      new[] { "알림끄기" },                    "텔레그램 알림 잠시 끄기",          "/mute [분] — 텔레그램 알림 잠시 끄기 (기본 60분) · /unmute"),
            ("unmute",    new[] { "알림켜기" },                    "텔레그램 알림 다시 켜기",          null),
            ("notify",    new[] { "알림" },                        "알림 설정 보기 · 바꾸기",          "/notify [쪽지|대화|파일|내용|자리] [켜기|끄기] — 알림 설정"),
            ("add",       new[] { "추가" },                        "IP 로 동료 추가  /add IP:포트",    "/add 192.168.0.10:9082 — IP 주소로 동료 추가"),
            ("rescan",    new[] { "다시찾기" },                    "동료 다시 찾기",                   "/rescan — 동료 다시 찾기"),
            ("block",     new[] { "차단" },                        "동료 차단  /block 이름",           "/block 이름 · /unblock 이름 — 차단 · 해제"),
            ("unblock",   new[] { "차단해제" },                    "차단 해제  /unblock 이름",         null),
            ("me",        new[] { "나", "내정보" },                "내 정보",                          "/me — 내 이름 · 상태 · 주소 · 버전"),
        };

        static string HelpText =>
            "받은 쪽지 · 대화 · 파일이 여기로 옵니다.\n" +
            "↩ 받은 메시지에 '답장'하면 그 동료에게 바로 보냅니다.\n" +
            "📎 사진 · 파일은 답장으로 보내거나 설명에 /msg 이름 을 적으세요.\n\n" +
            string.Join("\n", Commands.Where(c => c.help != null).Select(c => "• " + c.help)) +
            "\n\n이름 대신 /list 의 번호를 써도 됩니다. 예) /msg 2 회의 10분 늦습니다";

        /// <summary>텔레그램 명령어 메뉴 등록</summary>
        static async Task SetCommands(string token, CancellationToken ct)
        {
            var json = "[" + string.Join(",", Commands.Select(c =>
                "{\"command\":\"" + c.cmd + "\",\"description\":\"" + c.menu.Replace("\"", "'") + "\"}")) + "]";
            try { await Call(token, "setMyCommands", new Dictionary<string, string> { ["commands"] = json }, 15000, ct).ConfigureAwait(false); } catch { }
        }

        /// <summary>연결 직후 안내 · 명령어 메뉴 등록</summary>
        public static async Task Welcome(string token, long chatId, string myName, CancellationToken ct)
        {
            await SetCommands(token, ct).ConfigureAwait(false);
            await SendTo(token, chatId, $"✅ {AppInfo.Name}({myName})와 연결되었습니다.\n\n" + HelpText, ct).ConfigureAwait(false);
        }

        // ───────────────────────── 알림 보내기 ─────────────────────────

        DateTime _muteUntil = DateTime.MinValue;
        /// <summary>마지막으로 텔레그램에 보낸 받은 메시지 (/r 답장 대상)</summary>
        (string peerId, bool chat, string msgId)? _last;

        /// <summary>받은 쪽지 · 대화를 텔레그램으로 전달 (UI 스레드에서 호출)</summary>
        public void Forward(ChatMessage m, string myStatus)
        {
            if (!Active || m == null || m.Outgoing) return;
            if (m.Chat ? !S.TelegramChats : !S.TelegramNotes) return;
            if (DateTime.Now < _muteUntil && !m.Urgent) return;
            if (S.TelegramOnlyAway && myStatus == Status.Online && Native.IdleTime().TotalMinutes < 3) return;
            Push(m);
        }

        /// <summary>메시지 하나를 텔레그램으로 (설정 · 알림 끄기와 관계없이)</summary>
        Task Push(ChatMessage m)
        {
            var kind = m.Broadcast ? "📢 전체 공지" : m.Urgent ? "🚨 긴급 쪽지" : m.Chat ? "💬 대화" : "✉️ 쪽지";
            var who = m.FromName + (string.IsNullOrEmpty(m.PeerDept) ? "" : " · " + m.PeerDept);
            var sb = new StringBuilder();
            sb.Append(kind).Append("  <b>").Append(Esc(who)).Append("</b>");
            if (m.Auto) sb.Append("  (자동응답)");
            if (m.Time.Date != DateTime.Today || (DateTime.Now - m.Time).TotalMinutes > 5) sb.Append("  <i>").Append(Esc(m.Time.ToString("M'/'d tt h:mm"))).Append("</i>");
            if (S.TelegramShowText)
            {
                var text = Markdown.Plain(m.Text) ?? m.Text ?? "";
                if (text.Length > 3500) text = text.Substring(0, 3500) + "…";
                if (text.Trim().Length > 0) sb.Append("\n").Append(Esc(text.Trim()));
                if (m.Files.Count > 0)
                {
                    sb.Append("\n📎 ").Append(Esc(string.Join(", ", m.Files.Take(5).Select(f => f.Name + " (" + f.SizeText + ")"))))
                      .Append(m.Files.Count > 5 ? $" 외 {m.Files.Count - 5}개" : "");
                    if (S.TelegramFiles && m.Files.Any(f => f.Size > MaxUpload))
                        sb.Append("\n<i>50MB 가 넘는 파일은 PC에서 받아 주세요</i>");
                }
            }
            else sb.Append("\n(내용은 PC에서 확인하세요)");
            if (!m.Broadcast && !m.Auto) sb.Append("\n<i>↩ 답장하면 ").Append(m.Chat ? "대화로" : "쪽지로").Append(" 보냅니다</i>");

            var token = S.TelegramToken.Trim();
            var chatId = S.TelegramChatId;
            var html = sb.ToString();
            var key = (m.PeerId, m.Chat, m.Id);
            if (!m.Broadcast && !m.Auto) _last = key;
            // 첨부파일: 보낸 PC에서 받아 텔레그램에 올림 (내용 숨기기면 보내지 않음)
            var files = S.TelegramFiles && S.TelegramShowText
                ? m.Files.Where(f => f.Size > 0 && f.Size <= MaxUpload).Take(10)
                         .Select(f => (f.Name, f.Size, f.Token, local: Media.Exists(f.LocalPath) ? f.LocalPath : null)).ToList()
                : new List<(string Name, long Size, string Token, string local)>();
            var ip = m.PeerIp; var port = m.PeerPort;
            return Task.Run(async () =>
            {
                long head = await SendHead(token, chatId, html, key).ConfigureAwait(false);
                if (head == 0 && files.Count == 0) return;
                foreach (var f in files) await SendFile(token, chatId, f.Name, f.Size, f.Token, f.local, ip, port, head, key).ConfigureAwait(false);
            });
        }

        /// <summary>텔레그램에 올릴 수 있는 파일 크기 (Bot API 한도 50MB)</summary>
        const long MaxUpload = 50L * 1024 * 1024;

        async Task<long> SendHead(string token, long chatId, string html, (string, bool, string) key)
        {
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        var r = await Call(token, "sendMessage", new Dictionary<string, string>
                        {
                            ["chat_id"] = chatId.ToString(),
                            ["text"] = html,
                            ["parse_mode"] = "HTML",
                            ["disable_web_page_preview"] = "true",
                        }, 20000, CancellationToken.None).ConfigureAwait(false);
                        var mid = Long(r, "message_id");
                        if (mid != 0) lock (_sent) Remember(mid, key);
                        return mid;
                    }
                    catch (TelegramException ex) when (ex.Code == 400 || ex.Code == 401 || ex.Code == 403) { SetState("알림을 보내지 못했습니다: " + ex.Message); return 0; }
                    catch { await Task.Delay(3000 * (i + 1)).ConfigureAwait(false); }
                }
                return 0;
        }

        /// <summary>첨부파일 하나를 (필요하면 보낸 PC에서 받아) 텔레그램에 올림. 사진 · GIF 는 바로 보이게</summary>
        async Task SendFile(string token, long chatId, string name, long size, string fileToken, string local, string ip, int port,
                            long replyTo, (string, bool, string) key)
        {
            string tmp = null;
            try
            {
                var path = local;
                if (path == null)
                {
                    var dir = Path.Combine(Path.GetTempPath(), "BluePhone-telegram", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    tmp = dir;
                    path = Path.Combine(dir, Path.GetFileName(name));
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
                        await _core.Net.Download(ip, port, fileToken, path, null, cts.Token).ConfigureAwait(false);
                }
                var ext = Path.GetExtension(name).ToLowerInvariant();
                string method = "sendDocument", field = "document";
                if (ext == ".gif") { method = "sendAnimation"; field = "animation"; }
                else if ((ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".bmp") && size <= 10L * 1024 * 1024) { method = "sendPhoto"; field = "photo"; }
                else if ((ext == ".mp4" || ext == ".mov") ) { method = "sendVideo"; field = "video"; }

                var fields = new Dictionary<string, string> { ["chat_id"] = chatId.ToString() };
                if (replyTo != 0) { fields["reply_to_message_id"] = replyTo.ToString(); fields["allow_sending_without_reply"] = "true"; }
                if (method != "sendDocument") fields["caption"] = name;
                int timeout = 60000 + (int)Math.Min(20 * 60000L, size / 50);   // 50KB/s 기준
                object r;
                try { r = await Upload(token, method, fields, field, path, name, timeout).ConfigureAwait(false); }
                catch (TelegramException ex) when (method != "sendDocument" && ex.Code == 400)
                {
                    // 사진으로 못 올리는 이미지(크기 · 형식)는 파일로
                    fields.Remove("caption");
                    r = await Upload(token, "sendDocument", fields, "document", path, name, timeout).ConfigureAwait(false);
                }
                var mid = Long(r, "message_id");
                if (mid != 0) lock (_sent) Remember(mid, key);
            }
            catch (Exception ex)
            {
                try { await SendTo(token, chatId, $"📎 {name} 을(를) 텔레그램으로 보내지 못했습니다 — PC에서 받아 주세요. ({Short(ex)})", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            finally
            {
                if (tmp != null) try { Directory.Delete(tmp, true); } catch { }
            }
        }

        void Remember(long tgMsg, (string, bool, string) v)
        {
            _sent[tgMsg] = v;
            _sentOrder.Enqueue(tgMsg);
            while (_sentOrder.Count > 1000) _sent.Remove(_sentOrder.Dequeue());
        }

        public async Task SendTest()
        {
            await SendTo(S.TelegramToken.Trim(), S.TelegramChatId, "🔔 " + AppInfo.Name + " 테스트 알림입니다.", CancellationToken.None);
        }

        // ───────────────────────── 명령 처리 ─────────────────────────

        async Task Handle(string token, Dictionary<string, object> msg)
        {
            if (msg == null) return;
            var chat = Obj(msg, "chat");
            var chatId = Long(chat, "id");
            if (chatId != S.TelegramChatId) return;   // 연결한 내 텔레그램이 아니면 무시
            var text = (Str(msg, "text") ?? Str(msg, "caption") ?? "").Trim();
            var att = Attachment(msg);
            var replyTo = Obj(msg, "reply_to_message");
            if (text.Length == 0 && att == null)
            {
                await Reply(token, chatId, "글자 · 사진 · 파일만 보낼 수 있습니다.");
                return;
            }
            if (att != null && replyTo == null && !text.StartsWith("/"))
            {
                await Reply(token, chatId, "사진 · 파일은 받은 메시지에 '답장'으로 보내거나, 설명(캡션)에 '/msg 이름' 또는 '/chat 이름' 을 적어 주세요.");
                return;
            }

            string answer;
            try
            {
                List<string> files = null;
                if (att != null)
                {
                    if (att.Value.size > 20L * 1024 * 1024) throw new InvalidOperationException("텔레그램에서 보내는 파일은 20MB 까지 전달할 수 있습니다.");
                    files = new List<string> { await Fetch(token, att.Value.id, att.Value.name).ConfigureAwait(false) };
                }
                answer = await Run(text, replyTo, files);
            }
            catch (Exception ex) { answer = "⚠️ " + ex.Message; }
            if (!string.IsNullOrEmpty(answer)) await Reply(token, chatId, answer);
        }

        async Task Reply(string token, long chatId, string text)
        {
            try { await SendTo(token, chatId, text, CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        /// <summary>메시지의 사진 · 파일 (없으면 null). 사진은 가장 큰 크기</summary>
        static (string id, string name, long size)? Attachment(Dictionary<string, object> msg)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var photo = Arr(msg != null && msg.TryGetValue("photo", out var ph) ? ph : null).LastOrDefault();
            if (photo != null) return (Str(photo, "file_id"), $"사진_{stamp}.jpg", Long(photo, "file_size"));
            foreach (var (k, ext) in new[] { ("document", ""), ("video", ".mp4"), ("audio", ".mp3"), ("voice", ".ogg"), ("video_note", ".mp4") })
            {
                var o = Obj(msg, k);
                if (o == null) continue;
                var name = Str(o, "file_name");
                if (string.IsNullOrWhiteSpace(name)) name = (k == "voice" ? "음성_" : "파일_") + stamp + ext;
                return (Str(o, "file_id"), name, Long(o, "file_size"));
            }
            return null;
        }

        /// <summary>텔레그램 파일을 내 PC에 받아 둠 (동료가 받아 갈 수 있도록 데이터 폴더에 보관)</summary>
        async Task<string> Fetch(string token, string fileId, string name)
        {
            var info = await Call(token, "getFile", new Dictionary<string, string> { ["file_id"] = fileId }, 20000, CancellationToken.None).ConfigureAwait(false);
            var fp = Str(info, "file_path");
            if (string.IsNullOrEmpty(fp)) throw new InvalidOperationException("텔레그램에서 파일을 받지 못했습니다.");
            var dir = Path.Combine(Store.Dir, "telegram");
            Directory.CreateDirectory(dir);
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var dest = Store.UniquePath(dir, name);
            var resp = await TgNet.Send("GET", $"{ApiBase}/file/bot{token.Trim()}/{fp}", null, 0, null, 5 * 60000, CancellationToken.None).ConfigureAwait(false);
            if (resp.Status != 200) throw new InvalidOperationException($"텔레그램에서 파일을 받지 못했습니다 (HTTP {resp.Status}).");
            File.WriteAllBytes(dest, resp.Body);
            return dest;
        }

        async Task<string> Run(string text, Dictionary<string, object> replyTo, List<string> files = null)
        {
            // 받은 알림에 '답장'
            if (replyTo != null && !text.StartsWith("/"))
            {
                (string peerId, bool chat, string msgId) t;
                bool known;
                lock (_sent) known = _sent.TryGetValue(Long(replyTo, "message_id"), out t);
                if (!known) return "어느 메시지에 대한 답장인지 알 수 없습니다. (프로그램을 다시 시작하기 전 알림이면 /msg 이름 내용 으로 보내 주세요)";
                return await OnUi(() => SendFromTelegram(t.peerId, null, text, t.chat, t.msgId, files));
            }

            if (!text.StartsWith("/"))
                return "명령어를 알 수 없습니다. 받은 메시지에 '답장'하거나, /r 내용 으로 마지막 메시지에 답장하거나, /help 를 보내 보세요.";

            var sp = text.IndexOfAny(new[] { ' ', '\n' });
            var word = (sp < 0 ? text : text.Substring(0, sp)).Substring(1).ToLowerInvariant();
            var at = word.IndexOf('@'); if (at > 0) word = word.Substring(0, at);   // /msg@MyBot
            var rest = sp < 0 ? "" : text.Substring(sp + 1).Trim();
            var cmd = Commands.FirstOrDefault(c => c.cmd == word || c.alias.Contains(word)).cmd;

            switch (cmd)
            {
                case "help": return HelpText;
                case "list": return await OnUi(() => ListPeers(rest));
                case "info": return await OnUi(() => Info(rest));
                case "msg":
                case "chat":
                case "urgent":
                    if (rest.Length == 0) return $"사용법: /{cmd} 이름 내용";
                    return await OnUi(() => SendCommand(rest, cmd == "chat", files, urgent: cmd == "urgent"));
                case "r":
                {
                    var last = _last;
                    if (last == null) return "답장할 메시지가 없습니다. (텔레그램으로 받은 메시지가 아직 없습니다)";
                    if (rest.Length == 0 && files == null) return "사용법: /r 내용";
                    return await OnUi(() => SendFromTelegram(last.Value.peerId, null, rest, last.Value.chat, last.Value.msgId, files));
                }
                case "all":
                    if (rest.Length == 0 && files == null) return "사용법: /all 내용";
                    return await OnUi(() => Broadcast(rest, files));
                case "groups": return await OnUi(Groups);
                case "group":
                    if (rest.Length == 0) return "사용법: /group 그룹이름 내용   (그룹 목록은 /groups)";
                    return await OnUi(() => GroupSend(rest, files));
                case "later": return await OnUi(() => Later(rest, files));
                case "unread": return await OnUi(Unread);
                case "readall": return await OnUi(() => { int n = _core.Unread; _core.MarkAllRead(); return n == 0 ? "안 읽은 메시지가 없습니다." : $"안 읽은 메시지 {n}개를 읽음으로 바꿨습니다."; });
                case "history": return await OnUi(() => History(rest));
                case "status": return await OnUi(() => ChangeStatus(rest));
                case "memo": return await OnUi(() => Memo(rest));
                case "autoreply": return await OnUi(() => AutoReply(rest));
                case "mute":
                {
                    int min = int.TryParse(rest.TrimEnd('분', 'm', 'M'), out var v) && v > 0 ? Math.Min(v, 24 * 60) : 60;
                    _muteUntil = DateTime.Now.AddMinutes(min);
                    return $"🔕 {_muteUntil:tt h:mm}까지 텔레그램 알림을 보내지 않습니다. (긴급 쪽지는 예외) · 다시 켜기: /unmute";
                }
                case "unmute": _muteUntil = DateTime.MinValue; return "🔔 텔레그램 알림을 다시 보냅니다.";
                case "notify": return await OnUi(() => Notify(rest));
                case "add":
                    if (rest.Length == 0) return "사용법: /add 192.168.0.10:9082";
                    return await OnUi(async () =>
                    {
                        try { var p = await _core.AddByAddress(rest); return $"✅ {p?.DisplayName ?? rest} 님을 추가했습니다."; }
                        catch (Exception ex) { return "⚠️ " + ex.Message; }
                    });
                case "rescan": return await OnUi(() => { _core.Rescan(); return "동료를 다시 찾고 있습니다. 잠시 뒤 /list 로 확인해 보세요."; });
                case "block":
                case "unblock": return await OnUi(() => Block(rest, cmd == "block"));
                case "me": return await OnUi(Me);
                default:
                    return "알 수 없는 명령어입니다. /help 를 보내 보세요.";
            }
        }

        Task<string> OnUi(Func<Task<string>> f) => UI.InvokeAsync(f).Task.Unwrap();
        Task<string> OnUi(Func<string> f) => UI.InvokeAsync(f).Task;

        IEnumerable<Peer> Visible => _core.Peers.Where(p => !_core.IsBlocked(p.Id));

        static string Dot(Peer p) => !p.IsOnline ? "⚪" : p.Status_ == Status.Online ? "🟢" : p.Status_ == Status.Away ? "🟡" : "🔴";

        string ListPeers(string filter)
        {
            filter = (filter ?? "").Trim();
            var list = Visible.Where(p => filter.Length == 0
                                       || p.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                                       || (p.Dept ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                              .OrderBy(p => p.IsOnline ? 0 : 1).ThenBy(p => Status.Order(p.Status_)).ThenBy(p => p.DisplayName).Take(80).ToList();
            if (list.Count == 0) return filter.Length > 0 ? $"'{filter}' 에 맞는 동료가 없습니다." : "동료 목록이 비어 있습니다.";
            _listed = list.Select(p => p.Id).ToList();
            var sb = new StringBuilder($"동료 {list.Count}명 (접속 {list.Count(p => p.IsOnline)}) — /msg 번호 내용 으로 보낼 수 있습니다\n");
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                sb.Append('\n').Append(i + 1).Append(". ").Append(Dot(p)).Append(' ').Append(p.DisplayName);
                if (!string.IsNullOrEmpty(p.Dept)) sb.Append(" · ").Append(p.Dept);
                if (p.IsOnline && p.Status_ != Status.Online) sb.Append(" (").Append(Status.Label(p.Status_)).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>
        /// "대상 나머지" 에서 대상 동료를 찾음 — /list 번호, 이름(띄어쓰기 · 별명 포함), 이름 일부.
        /// 못 찾거나 여러 명이면 안내 문구를 돌려줌
        /// </summary>
        string Resolve(string rest, string usage, out Peer target, out string body)
        {
            target = null; body = "";
            var peers = (usage.StartsWith("/unblock") ? _core.Peers : Visible).ToList();
            var sp = rest.IndexOfAny(new[] { ' ', '\n' });
            var first = sp < 0 ? rest : rest.Substring(0, sp);
            if (first.Length == 0) return "사용법: " + usage;

            if (int.TryParse(first, out int n))
            {
                if (n < 1 || n > _listed.Count) return "번호가 목록에 없습니다. /list 로 다시 확인해 주세요.";
                var id = _listed[n - 1];
                target = peers.FirstOrDefault(p => p.Id == id);
                if (target == null) return "그 동료를 찾을 수 없습니다. /list 로 다시 확인해 주세요.";
                body = sp < 0 ? "" : rest.Substring(sp + 1).Trim();
                return null;
            }

            var exact = new List<(Peer p, int len)>();
            foreach (var p in peers)
                foreach (var nm in new[] { p.DisplayName, p.Name }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                    if (rest.StartsWith(nm, StringComparison.OrdinalIgnoreCase) && (rest.Length == nm.Length || char.IsWhiteSpace(rest[nm.Length])))
                    { exact.Add((p, nm.Length)); break; }
            List<Peer> hits;
            int cut;
            if (exact.Count > 0)
            {
                cut = exact.Max(x => x.len);
                hits = exact.Where(x => x.len == cut).Select(x => x.p).ToList();
            }
            else
            {
                cut = first.Length;
                hits = peers.Where(p => p.DisplayName.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0
                                     || (p.Name ?? "").IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (hits.Count == 0) return $"'{first}' 이름의 동료를 찾을 수 없습니다. /list 로 확인해 주세요.";
            }
            // 같은 이름이 여럿이면 접속 중인 사람
            if (hits.Count > 1 && hits.Select(x => x.DisplayName).Distinct().Count() == 1 && hits.Count(x => x.IsOnline) == 1)
                hits = hits.Where(x => x.IsOnline).ToList();
            if (hits.Count > 1)
            {
                _listed = hits.Take(20).Select(p => p.Id).ToList();
                var cmdWord = usage.Split(' ')[0];
                return $"'{first}' 에 맞는 동료가 여러 명입니다. 번호로 다시 보내 주세요.\n" +
                       string.Join("\n", hits.Take(20).Select((p, i) => $"{i + 1}. {Dot(p)} {p.DisplayName}" + (string.IsNullOrEmpty(p.Dept) ? "" : " · " + p.Dept))) +
                       $"\n예) {cmdWord} 1" + (usage.Contains("내용") ? " 내용" : "");
            }
            target = hits[0];
            body = rest.Length > cut ? rest.Substring(cut).Trim() : "";
            return null;
        }

        Task<string> SendCommand(string rest, bool chat, List<string> files, bool urgent = false)
        {
            var usage = urgent ? "/urgent 이름 내용" : chat ? "/chat 이름 내용" : "/msg 이름 내용";
            var err = Resolve(rest, usage, out var target, out var body);
            if (err != null) return Task.FromResult(err);
            if (body.Length == 0 && (files == null || files.Count == 0))
                return Task.FromResult($"{target.DisplayName}님에게 보낼 내용을 함께 적어 주세요.\n예) {usage.Split(' ')[0]} {target.DisplayName} 안녕하세요");
            return SendFromTelegram(target.Id, target, body, chat, null, files, urgent);
        }

        async Task<string> SendFromTelegram(string peerId, Peer p, string text, bool chat, string replyOf, List<string> files = null, bool urgent = false)
        {
            p = p ?? _core.Peers.FirstOrDefault(x => x.Id == peerId);
            if (p == null) return "그 동료가 목록에 없습니다.";
            if (_core.IsBlocked(p.Id)) return $"{p.DisplayName}님은 차단한 동료입니다.";
            var m = await _core.Send(new[] { p }, text, files, urgent && !chat, false, chat: chat);
            // 답장한 원래 메시지는 읽음으로
            if (replyOf != null)
            {
                var orig = _core.History.FirstOrDefault(x => x.Id == replyOf && !x.Outgoing);
                if (orig != null && !orig.Read) _core.MarkRead(orig);
            }
            var what = (chat ? "대화" : urgent ? "긴급 쪽지" : "쪽지") + (files != null && files.Count > 0 ? "(파일 포함)" : "");
            if (m.PendingTo.Count > 0) return $"🕓 {p.DisplayName}님이 지금 접속해 있지 않아, 접속하면 {what}를 전달합니다.";
            if (m.Failed || m.FailedTo.Count > 0) return $"⚠️ {p.DisplayName}님에게 {what}를 보내지 못했습니다.";
            return $"✅ {p.DisplayName}님에게 {what}를 보냈습니다.";
        }

        static string Result(ChatMessage m, int count, string what)
        {
            int failed = m.FailedTo.Count + m.PendingTo.Count;
            if (failed == 0) return $"✅ {count}명에게 {what}를 보냈습니다.";
            return $"✅ {count - failed}명에게 {what}를 보냈습니다. · {failed}명은 접속하면 전달하거나 보내지 못했습니다.";
        }

        async Task<string> Broadcast(string text, List<string> files)
        {
            var online = Visible.Where(p => p.IsOnline).ToList();
            if (online.Count == 0) return "접속 중인 동료가 없습니다.";
            var m = await _core.Send(online, text, files, false, true);
            return Result(m, online.Count, "전체 공지");
        }

        string Groups()
        {
            var gs = S.Groups ?? new List<PeerGroup>();
            if (gs.Count == 0) return "만든 그룹이 없습니다. (메인 창에서 그룹을 만들 수 있습니다)";
            return "그룹 목록 — /group 그룹이름 내용\n\n" + string.Join("\n", gs.Select((g, i) =>
            {
                var ms = _core.GroupMembers(g);
                return $"{i + 1}. {g.Name} ({ms.Count}명, 접속 {ms.Count(p => p.IsOnline)}) — " + string.Join(", ", ms.Take(8).Select(p => p.DisplayName)) + (ms.Count > 8 ? " …" : "");
            }));
        }

        async Task<string> GroupSend(string rest, List<string> files)
        {
            var gs = S.Groups ?? new List<PeerGroup>();
            PeerGroup g = null; string body = "";
            var sp = rest.IndexOfAny(new[] { ' ', '\n' });
            var first = sp < 0 ? rest : rest.Substring(0, sp);
            if (int.TryParse(first, out int n) && n >= 1 && n <= gs.Count) { g = gs[n - 1]; body = sp < 0 ? "" : rest.Substring(sp + 1).Trim(); }
            else
            {
                g = gs.Where(x => !string.IsNullOrEmpty(x.Name) && rest.StartsWith(x.Name, StringComparison.OrdinalIgnoreCase)
                                  && (rest.Length == x.Name.Length || char.IsWhiteSpace(rest[x.Name.Length])))
                      .OrderByDescending(x => x.Name.Length).FirstOrDefault();
                if (g != null) body = rest.Substring(g.Name.Length).Trim();
            }
            if (g == null) return $"'{first}' 그룹이 없습니다.\n\n" + Groups();
            if (body.Length == 0 && files == null) return $"'{g.Name}' 그룹에 보낼 내용을 적어 주세요. 예) /group {g.Name} 회의 시작합니다";
            var to = _core.GroupMembers(g).Where(p => !_core.IsBlocked(p.Id)).ToList();
            if (to.Count == 0) return $"'{g.Name}' 그룹에 동료가 없습니다.";
            var m = await _core.Send(to, body, files, false, false);
            return Result(m, to.Count, $"'{g.Name}' 그룹 쪽지");
        }

        /// <summary>/later 30m 이름 내용 · /later 2h … · /later 18:30 …</summary>
        async Task<string> Later(string rest, List<string> files)
        {
            const string usage = "/later 시간 이름 내용\n예) /later 30m 홍길동 회의 자료 확인 부탁드립니다 · /later 2h … · /later 18:30 …";
            var sp = rest.IndexOf(' ');
            if (sp < 0) return "사용법: " + usage;
            var when = rest.Substring(0, sp).Trim().ToLowerInvariant();
            DateTime at;
            var mm = System.Text.RegularExpressions.Regex.Match(when, @"^(\d+)(m|분|h|시간)$");
            if (mm.Success)
            {
                int v = int.Parse(mm.Groups[1].Value);
                at = mm.Groups[2].Value == "m" || mm.Groups[2].Value == "분" ? DateTime.Now.AddMinutes(v) : DateTime.Now.AddHours(v);
            }
            else if (DateTime.TryParseExact(when, new[] { "H:mm", "HH:mm" }, null, System.Globalization.DateTimeStyles.None, out var t))
            {
                at = DateTime.Today.Add(t.TimeOfDay);
                if (at <= DateTime.Now) at = at.AddDays(1);
            }
            else return "시간 형식을 알 수 없습니다.\n사용법: " + usage;
            if (at > DateTime.Now.AddDays(30)) return "30일 안으로만 예약할 수 있습니다.";

            var err = Resolve(rest.Substring(sp + 1).Trim(), "/later 시간 이름 내용", out var target, out var body);
            if (err != null) return err;
            if (body.Length == 0 && files == null) return "보낼 내용을 적어 주세요.\n사용법: " + usage;
            await _core.Send(new[] { target }, body, files, false, false, at: at);
            return $"⏰ {at:M월 d일 tt h:mm}에 {target.DisplayName}님에게 쪽지를 보내도록 예약했습니다. (PC의 쪽지함에서 취소할 수 있습니다)";
        }

        async Task<string> Unread()
        {
            var list = _core.History.Where(m => !m.Outgoing && !m.Read).OrderBy(m => m.Ts).ToList();
            if (list.Count == 0) return "안 읽은 메시지가 없습니다.";
            var show = list.Skip(Math.Max(0, list.Count - 10)).ToList();
            foreach (var m in show) await Push(m);   // 시간 순서대로 하나씩
            return $"↑ 안 읽은 메시지 {list.Count}개" + (list.Count > show.Count ? $" 중 최근 {show.Count}개" : "") + "입니다. 답장하면 그 동료에게 보냅니다 · 모두 읽음 처리: /readall";
        }

        string History(string rest)
        {
            var err = Resolve(rest, "/history 이름 [개수]", out var p, out var tail);
            if (err != null) return err;
            int n = int.TryParse(tail, out var v) ? Math.Max(1, Math.Min(50, v)) : 10;
            var list = _core.History.Where(m => m.Involves(p.Id)).OrderByDescending(m => m.Ts).Take(n).Reverse().ToList();
            if (list.Count == 0) return $"{p.DisplayName}님과 주고받은 메시지가 없습니다.";
            var sb = new StringBuilder($"{p.DisplayName}님과 최근 {list.Count}개\n");
            foreach (var m in list)
            {
                var t = (Markdown.Plain(m.Text) ?? "").Replace("\r", "").Replace("\n", " ").Trim();
                if (t.Length > 200) t = t.Substring(0, 200) + "…";
                if (m.Files.Count > 0) t += (t.Length > 0 ? " " : "") + "📎" + string.Join(", ", m.Files.Select(f => f.Name));
                sb.Append('\n').Append(m.Time.ToString("M'/'d HH:mm")).Append(' ')
                  .Append(m.Outgoing ? "나" : p.DisplayName).Append(m.Chat ? "" : "(쪽지)").Append(": ").Append(t);
            }
            return sb.ToString();
        }

        string ChangeStatus(string arg)
        {
            arg = (arg ?? "").Trim();
            string s = null;
            if (arg.StartsWith("대화") || arg.Equals("online", StringComparison.OrdinalIgnoreCase)) s = Status.Online;
            else if (arg.StartsWith("자리") || arg.Equals("away", StringComparison.OrdinalIgnoreCase)) s = Status.Away;
            else if (arg.Contains("용무") || arg.Equals("busy", StringComparison.OrdinalIgnoreCase)) s = Status.Busy;
            else if (arg.StartsWith("방해") || arg.Equals("dnd", StringComparison.OrdinalIgnoreCase)) s = Status.Dnd;
            if (s == null)
                return $"지금 상태: {Status.Label(_core.MyStatus)}\n바꾸려면 /status 대화 · /status 자리 · /status 용무 · /status 방해";
            _core.SetStatus(s);
            return $"내 상태를 '{Status.Label(s)}'(으)로 바꿨습니다.";
        }

        string Memo(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0) return $"지금 상태 메시지: {(string.IsNullOrEmpty(S.Note) ? "(없음)" : S.Note)}\n바꾸려면 /memo 내용 · 지우려면 /memo -";
            S.Note = arg == "-" || arg == "지우기" ? "" : (arg.Length > 60 ? arg.Substring(0, 60) : arg);
            _core.ProfileChanged();
            return S.Note.Length == 0 ? "상태 메시지를 지웠습니다." : $"상태 메시지를 '{S.Note}'(으)로 바꿨습니다.";
        }

        string AutoReply(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0)
                return string.IsNullOrWhiteSpace(S.AutoReply) ? "자동응답이 꺼져 있습니다. 켜려면 /autoreply 내용" : $"자동응답: {S.AutoReply}\n끄려면 /autoreply 끄기";
            S.AutoReply = arg == "끄기" || arg == "-" || arg.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : (arg.Length > 300 ? arg.Substring(0, 300) : arg);
            Store.SaveSettings(S);
            return S.AutoReply.Length == 0 ? "자동응답을 껐습니다." : $"자동응답을 설정했습니다. ('대화 가능'이 아닐 때 받은 쪽지에 답장)\n{S.AutoReply}";
        }

        string Notify(string arg)
        {
            var parts = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string OnOff(bool b) => b ? "켜짐" : "꺼짐";
            if (parts.Length >= 2)
            {
                var v = parts[1];
                bool? on = v == "켜기" || v == "켬" || v.Equals("on", StringComparison.OrdinalIgnoreCase) ? true
                         : v == "끄기" || v == "끔" || v.Equals("off", StringComparison.OrdinalIgnoreCase) ? false : (bool?)null;
                if (on == null) return "켜기 또는 끄기로 적어 주세요. 예) /notify 대화 끄기";
                switch (parts[0])
                {
                    case "쪽지": S.TelegramNotes = on.Value; break;
                    case "대화": S.TelegramChats = on.Value; break;
                    case "파일": S.TelegramFiles = on.Value; break;
                    case "내용": S.TelegramShowText = on.Value; break;
                    case "자리": S.TelegramOnlyAway = on.Value; break;
                    default: return "쪽지 · 대화 · 파일 · 내용 · 자리 중에서 골라 주세요. 예) /notify 파일 끄기";
                }
                Store.SaveSettings(S);
            }
            var mute = DateTime.Now < _muteUntil ? $"\n🔕 {_muteUntil:tt h:mm}까지 알림 끔 (/unmute)" : "";
            return "텔레그램 알림 설정\n" +
                   $"• 쪽지: {OnOff(S.TelegramNotes)}\n• 대화: {OnOff(S.TelegramChats)}\n• 파일: {OnOff(S.TelegramFiles)}\n" +
                   $"• 내용: {OnOff(S.TelegramShowText)}\n• 자리 비울 때만: {OnOff(S.TelegramOnlyAway)}" + mute +
                   "\n\n바꾸기: /notify 대화 끄기 · /notify 자리 켜기";
        }

        string Info(string rest)
        {
            var err = Resolve(rest, "/info 이름", out var p, out _);
            if (err != null) return err;
            var sb = new StringBuilder($"{Dot(p)} {p.DisplayName}");
            if (!string.IsNullOrEmpty(p.Dept)) sb.Append(" · ").Append(p.Dept);
            sb.Append("\n상태: ").Append(p.IsOnline ? Status.Label(p.Status_) : "오프라인");
            if (!string.IsNullOrEmpty(p.Note)) sb.Append("\n상태 메시지: ").Append(p.Note);
            if (!string.IsNullOrEmpty(p.Ip)) sb.Append("\n주소: ").Append(p.Ip).Append(':').Append(p.Port);
            if (p.Name != p.DisplayName && !string.IsNullOrEmpty(p.Name)) sb.Append("\n본래 이름: ").Append(p.Name);
            int unread = _core.History.Count(m => !m.Outgoing && !m.Read && m.PeerId == p.Id);
            if (unread > 0) sb.Append("\n안 읽은 메시지: ").Append(unread).Append("개");
            return sb.ToString();
        }

        string Block(string rest, bool block)
        {
            if (!block)
            {
                var b = S.Blocked.FirstOrDefault(x => string.Equals(x.Name, rest.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? S.Blocked.FirstOrDefault(x => (x.Name ?? "").IndexOf(rest.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (rest.Trim().Length == 0 || b == null)
                    return S.Blocked.Count == 0 ? "차단한 동료가 없습니다." : "차단한 동료: " + string.Join(", ", S.Blocked.Select(x => x.Name)) + "\n해제: /unblock 이름";
                _core.Unblock(b.Id);
                return $"{b.Name}님의 차단을 풀었습니다.";
            }
            var err = Resolve(rest, "/block 이름", out var p, out _);
            if (err != null) return err;
            _core.Block(p);
            return $"⛔ {p.DisplayName}님을 차단했습니다. 쪽지 · 대화를 받지 않습니다. (해제: /unblock {p.DisplayName})";
        }

        string Me()
        {
            var ips = SettingsWindow.LocalIps();
            return $"{AppInfo.Name} {AppInfo.VersionText}\n" +
                   $"이름: {S.Name}" + (string.IsNullOrEmpty(S.Dept) ? "" : $" · {S.Dept}") + "\n" +
                   $"상태: {Status.Label(_core.MyStatus)}" + (string.IsNullOrEmpty(S.Note) ? "" : $" · {S.Note}") + "\n" +
                   $"주소: {string.Join(", ", ips.Select(ip => ip + ":" + S.Port))}\n" +
                   $"동료: {Visible.Count(p => p.IsOnline)}명 접속 / {Visible.Count()}명\n" +
                   $"안 읽은 메시지: {_core.Unread}개" +
                   (string.IsNullOrWhiteSpace(S.AutoReply) ? "" : $"\n자동응답: {S.AutoReply}");
        }

        // ───────────────────────── Bot API ─────────────────────────

        static Task SendTo(string token, long chatId, string text, CancellationToken ct)
        {
            if (text.Length > 4000) text = text.Substring(0, 4000) + "…";
            return Call(token, "sendMessage", new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["text"] = text,
                ["disable_web_page_preview"] = "true",
            }, 20000, ct);
        }

        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>Bot API 호출 → result 값. 실패하면 TelegramException (Code = HTTP/API 오류 코드)</summary>
        static async Task<object> Call(string token, string method, Dictionary<string, string> args, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new TelegramException(401, "봇 토큰이 없습니다.");
            var body = Encoding.UTF8.GetBytes(args == null ? "" : string.Join("&", args.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value ?? ""))));
            var r = await TgNet.Send("POST", $"{ApiBase}/bot{token.Trim()}/{method}", "application/x-www-form-urlencoded", body.Length,
                                     s => s.WriteAsync(body, 0, body.Length), timeoutMs, ct).ConfigureAwait(false);
            return Parse(r);
        }

        /// <summary>파일 올리기 (multipart/form-data)</summary>
        static async Task<object> Upload(string token, string method, Dictionary<string, string> fields, string fileField, string path, string fileName, int timeoutMs)
        {
            var boundary = "----BluePhone" + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            foreach (var kv in fields)
                sb.Append("--").Append(boundary).Append("\r\nContent-Disposition: form-data; name=\"").Append(kv.Key).Append("\"\r\n\r\n").Append(kv.Value).Append("\r\n");
            var safe = fileName.Replace("\"", "'").Replace("\r", "").Replace("\n", "");
            sb.Append("--").Append(boundary).Append("\r\nContent-Disposition: form-data; name=\"").Append(fileField)
              .Append("\"; filename=\"").Append(safe).Append("\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            var tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            var len = new FileInfo(path).Length;
            var r = await TgNet.Send("POST", $"{ApiBase}/bot{token.Trim()}/{method}", "multipart/form-data; boundary=" + boundary,
                head.Length + len + tail.Length, async s =>
                {
                    await s.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                    using (var fs = File.OpenRead(path)) await fs.CopyToAsync(s, 81920).ConfigureAwait(false);
                    await s.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
                }, timeoutMs, CancellationToken.None).ConfigureAwait(false);
            return Parse(r);
        }

        static object Parse(TgNet.Response r)
        {
            Dictionary<string, object> d = null;
            try { d = Store.Json.DeserializeObject(r.Text) as Dictionary<string, object>; } catch { }
            if (d == null)
            {
                // 텔레그램이 아닌 곳(회사 차단 페이지 · 프록시 오류 화면)의 응답
                if (r.Status == 403 || r.Status == 451) throw new TelegramException(r.Status, $"접속이 차단되었습니다 (HTTP {r.Status}). 회사 방화벽이 텔레그램을 막고 있을 수 있습니다 — 설정 › 텔레그램 › 연결 방법에서 프록시나 중계 서버를 지정해 보세요.");
                throw new TelegramException(r.Status, $"텔레그램이 아닌 곳에서 응답했습니다 (HTTP {r.Status}). 방화벽 · 프록시 · 중계 서버 주소를 확인해 주세요.");
            }
            if (d.TryGetValue("ok", out var ok) && ok is bool b && b)
                return d.TryGetValue("result", out var res) ? res : null;
            var code = (int)Long(d, "error_code");
            throw new TelegramException(code != 0 ? code : r.Status, Str(d, "description") ?? $"HTTP {r.Status}");
        }

        // ── JSON helpers ──
        static Dictionary<string, object> Obj(object o, string k) =>
            o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        static IEnumerable<Dictionary<string, object>> Arr(object o) =>
            (o as System.Collections.IEnumerable)?.OfType<Dictionary<string, object>>() ?? Enumerable.Empty<Dictionary<string, object>>();
        static string Str(object o, string k) =>
            o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        static long Long(object o, string k)
        {
            if (!(o is Dictionary<string, object> d) || !d.TryGetValue(k, out var v) || v == null) return 0;
            try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }

    public sealed class TelegramException : Exception
    {
        public int Code { get; }
        public TelegramException(int code, string message) : base(message) { Code = code; }
    }
}
