using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if !MAC
using System.Windows;
using System.Windows.Threading;
#endif

namespace BluePhone
{
    /// <summary>
    /// 텔레그램 연동 (Bot API · 서버 불필요).
    ///  · 받은 쪽지 · 대화를 내 텔레그램으로 전달
    ///  · 텔레그램의 버튼 메뉴나 '답장'으로 동료에게 바로 보내기
    /// 내가 연결한 텔레그램 대화(ChatId)에서 온 명령만 처리함
    /// </summary>
    public sealed partial class TelegramBridge
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

        public string State { get; private set; } = "";
        public event Action StateChanged;

#if MAC
        static UiDispatcher UI => UiDispatcher.Instance;
#else
        static Dispatcher UI => Application.Current.Dispatcher;
#endif
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
                        ["allowed_updates"] = "[\"message\",\"callback_query\"]",
                    };
                    // 시작할 때 이미 쌓여 있던(꺼져 있는 동안 보낸) 명령은 실행하지 않음
                    if (first) args["offset"] = "-1";
                    else if (_offset != 0) args["offset"] = _offset.ToString();
                    var r = await Call(token, "getUpdates", args, 40000, ct).ConfigureAwait(false);
                    foreach (var u in Arr(r))
                    {
                        var id = Long(u, "update_id");
                        if (id >= _offset) _offset = id + 1;
                        if (first) continue;
                        var cb = Obj(u, "callback_query");
                        if (cb != null) await HandleCallback(token, cb).ConfigureAwait(false);
                        else await Handle(token, Obj(u, "message")).ConfigureAwait(false);
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

        static string HelpText =>
            "📖 사용 방법\n\n" +
            "• 받은 쪽지 · 대화 · 사진 · 파일이 여기로 옵니다.\n" +
            "• 아래쪽 버튼 ✉️ 쪽지 · 💬 대화 를 누르고 → 동료를 고르고 → 내용(사진 · 파일 가능)을 보내면 됩니다.\n" +
            "• 📋 메뉴 에서 긴급 · 예약 · 그룹 · 전체 공지 · 안 읽은 메시지 · 대화 기록 · 내 상태 · 알림 설정 등을 고를 수 있습니다.\n" +
            "• 받은 알림 아래의 ↩ 쪽지로 답장 · 💬 대화로 답장 버튼(또는 텔레그램의 '답장')으로 바로 답할 수 있습니다.\n" +
            "• 그냥 글이나 사진을 보내면 누구에게 보낼지 목록이 나옵니다.\n" +
            "• 사진 여러 장을 한 번에 보내면 하나의 메시지로 묶어 보냅니다. (파일 하나당 20MB 까지)\n" +
            "• 진행 중인 단계를 그만두려면 ❌ 취소";

        /// <summary>텔레그램 왼쪽 아래 메뉴 버튼에는 '메뉴 열기' 하나만 (명령어는 쓰지 않음)</summary>
        static async Task SetCommands(string token, CancellationToken ct)
        {
            try
            {
                await Call(token, "setMyCommands", new Dictionary<string, string>
                {
                    ["commands"] = "[{\"command\":\"menu\",\"description\":\"📋 메뉴 열기\"}]"
                }, 15000, ct).ConfigureAwait(false);
            }
            catch { }
        }

        /// <summary>연결 직후 안내 · 명령어 메뉴 등록</summary>
        public static async Task Welcome(string token, long chatId, string myName, CancellationToken ct)
        {
            await SetCommands(token, ct).ConfigureAwait(false);
            await Call(token, "sendMessage", new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["text"] = $"✅ {AppInfo.Name}({myName})와 연결되었습니다.\n\n" + HelpText,
                ["disable_web_page_preview"] = "true",
                ["reply_markup"] = ReplyKeyboard,
            }, 20000, ct).ConfigureAwait(false);
        }

        // ───────────────────────── 알림 보내기 ─────────────────────────

        DateTime _muteUntil = DateTime.MinValue;

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
            if (!m.Broadcast && !m.Auto) sb.Append("\n<i>↩ 아래 버튼이나 '답장'으로 바로 보낼 수 있습니다</i>");

            var token = S.TelegramToken.Trim();
            var chatId = S.TelegramChatId;
            var html = sb.ToString();
            var key = (m.PeerId, m.Chat, m.Id);
            // 첨부파일: 보낸 PC에서 받아 텔레그램에 올림 (내용 숨기기면 보내지 않음)
            var files = S.TelegramFiles && S.TelegramShowText
                ? m.Files.Where(f => f.Size > 0 && f.Size <= MaxUpload).Take(10)
                         .Select(f => (f.Name, f.Size, f.Token, local: Media.Exists(f.LocalPath) ? f.LocalPath : null)).ToList()
                : new List<(string Name, long Size, string Token, string local)>();
            var ip = m.PeerIp; var port = m.PeerPort;
            var canReply = !m.Broadcast && !m.Auto;
            return Task.Run(async () =>
            {
                long head = await SendHead(token, chatId, html, key, canReply).ConfigureAwait(false);
                if (head == 0 && files.Count == 0) return;
                foreach (var f in files) await SendFile(token, chatId, f.Name, f.Size, f.Token, f.local, ip, port, head, key).ConfigureAwait(false);
            });
        }

        /// <summary>텔레그램에 올릴 수 있는 파일 크기 (Bot API 한도 50MB)</summary>
        const long MaxUpload = 50L * 1024 * 1024;

        async Task<long> SendHead(string token, long chatId, string html, (string, bool, string) key, bool canReply = true)
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
                            ["reply_markup"] = AlertButtons(canReply),
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
                TgLog($"out ok {name} ({new FileInfo(path).Length}B) via {method}");
            }
            catch (Exception ex)
            {
                TgLog($"out FAIL {name}: {ex}");
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

            // 사진 여러 장(앨범)은 따로따로 오므로 잠깐 모았다가 한 메시지로
            var group = Str(msg, "media_group_id");
            if (!string.IsNullOrEmpty(group) && att != null)
            {
                Album al; bool first = false;
                lock (_albums)
                {
                    if (!_albums.TryGetValue(group, out al)) { al = new Album { ReplyTo = replyTo }; _albums[group] = al; first = true; }
                    al.Items.Add(att.Value);
                    if (text.Length > 0) al.Caption = text;
                    al.Last = DateTime.Now;
                }
                if (first) _ = Task.Run(async () =>
                {
                    // 마지막 장이 오고 1.5초 동안 더 오지 않으면 처리
                    while (true)
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        lock (_albums) if ((DateTime.Now - al.Last).TotalMilliseconds > 1500) { _albums.Remove(group); break; }
                    }
                    await Process(token, chatId, al.Caption ?? "", al.ReplyTo, al.Items).ConfigureAwait(false);
                });
                return;
            }
            await Process(token, chatId, text, replyTo, att == null ? null : new List<(string id, string name, long size)> { att.Value }).ConfigureAwait(false);
        }

        sealed class Album
        {
            public List<(string id, string name, long size)> Items = new List<(string id, string name, long size)>();
            public string Caption;
            public Dictionary<string, object> ReplyTo;
            public DateTime Last = DateTime.Now;
        }
        readonly Dictionary<string, Album> _albums = new Dictionary<string, Album>();
        readonly SemaphoreSlim _handling = new SemaphoreSlim(1, 1);

        /// <summary>글 · 첨부 하나(또는 앨범 묶음)를 처리</summary>
        async Task Process(string token, long chatId, string text, Dictionary<string, object> replyTo, List<(string id, string name, long size)> atts)
        {
            await _handling.WaitAsync().ConfigureAwait(false);
            try
            {
                List<string> files = null;
                if (atts != null && atts.Count > 0)
                {
                    files = new List<string>();
                    var failed = new List<string>();
                    if (atts.Count > 1) await Reply(token, chatId, $"📥 파일 {atts.Count}개를 받는 중…");
                    foreach (var a in atts)
                    {
                        if (a.size > 20L * 1024 * 1024) { failed.Add($"{a.name}: 20MB 가 넘어 텔레그램에서 받을 수 없습니다"); TgLog($"in skip {a.name} {a.size}B (>20MB)"); continue; }
                        try
                        {
                            var path = await Fetch(token, a.id, a.name).ConfigureAwait(false);
                            files.Add(path);
                            TgLog($"in ok {a.name} → {path} ({new FileInfo(path).Length}B)");
                        }
                        catch (Exception ex) { failed.Add($"{a.name}: {Short(ex)}"); TgLog($"in FAIL {a.name}: {ex}"); }
                    }
                    if (failed.Count > 0) await Reply(token, chatId, "⚠️ 텔레그램에서 파일을 받지 못했습니다.\n" + string.Join("\n", failed));
                    if (files.Count == 0) return;
                }
                Screen scr;
                (string peerId, bool chat, string msgId) t = default;
                bool known = false;
                if (replyTo != null) lock (_sent) known = _sent.TryGetValue(Long(replyTo, "message_id"), out t);
                if (text.StartsWith("/") && (files == null || files.Count == 0))
                {
                    // 명령어는 쓰지 않음: /start · /menu 등 무엇이든 메뉴를 엶
                    _pend = null; _draft = null; _kbShown = false;
                    scr = await Ui(MainMenu);
                }
                else if (known)
                {
                    // 받은 알림에 텔레그램 '답장' → 그 동료에게 같은 종류로
                    _pend = null;
                    scr = await Ui(async () =>
                    {
                        var p = _core.Peers.FirstOrDefault(x => x.Id == t.peerId);
                        var res = await SendFromTelegram(t.peerId, p, text, t.chat, t.msgId, files);
                        return Done(res, p);
                    });
                }
                else
                {
                    if (text.StartsWith("/")) text = "";   // 사진 설명에 남은 명령어는 무시
                    scr = await OnText(text, files);
                }
                if (scr != null) await Show(token, chatId, 0, scr);
            }
            catch (Exception ex) { TgLog("handle FAIL: " + ex); await Reply(token, chatId, "⚠️ " + ex.Message); }
            finally { _handling.Release(); }
        }

        /// <summary>파일 주고받기 기록 (데이터 폴더\telegram\log.txt, 1MB 넘으면 새로)</summary>
        static void TgLog(string line)
        {
            try
            {
                var dir = Path.Combine(Store.Dir, "telegram");
                Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("MM-dd HH:mm:ss ") + line + Environment.NewLine);
            }
            catch { }
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

        Task<string> OnUi(Func<Task<string>> f) => UI.InvokeAsync(f).Task.Unwrap();
        Task<string> OnUi(Func<string> f) => UI.InvokeAsync(f).Task;

        IEnumerable<Peer> Visible => _core.Peers.Where(p => !_core.IsBlocked(p.Id));

        static string Dot(Peer p) => !p.IsOnline ? "⚪" : p.Status_ == Status.Online ? "🟢" : p.Status_ == Status.Away ? "🟡" : "🔴";

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

        async Task<string> Unread()
        {
            var list = _core.History.Where(m => !m.Outgoing && !m.Read).OrderBy(m => m.Ts).ToList();
            if (list.Count == 0) return "안 읽은 메시지가 없습니다.";
            var show = list.Skip(Math.Max(0, list.Count - 10)).ToList();
            foreach (var m in show) await Push(m);   // 시간 순서대로 하나씩
            return $"↑ 안 읽은 메시지 {list.Count}개" + (list.Count > show.Count ? $" 중 최근 {show.Count}개" : "") + "입니다. 알림 아래 버튼으로 답장할 수 있습니다.";
        }

        string Memo(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0) return $"지금 상태 메시지: {(string.IsNullOrEmpty(S.Note) ? "(없음)" : S.Note)}\n";
            S.Note = arg == "-" || arg == "지우기" ? "" : (arg.Length > 60 ? arg.Substring(0, 60) : arg);
            _core.ProfileChanged();
            return S.Note.Length == 0 ? "상태 메시지를 지웠습니다." : $"상태 메시지를 '{S.Note}'(으)로 바꿨습니다.";
        }

        string AutoReply(string arg)
        {
            arg = (arg ?? "").Trim();
            if (arg.Length == 0)
                return string.IsNullOrWhiteSpace(S.AutoReply) ? "자동응답이 꺼져 있습니다." : $"자동응답: {S.AutoReply}";
            S.AutoReply = arg == "끄기" || arg == "-" || arg.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : (arg.Length > 300 ? arg.Substring(0, 300) : arg);
            Store.SaveSettings(S);
            return S.AutoReply.Length == 0 ? "자동응답을 껐습니다." : $"자동응답을 설정했습니다. ('대화 가능'이 아닐 때 받은 쪽지에 답장)\n{S.AutoReply}";
        }

        string InfoOf(Peer p)
        {
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
