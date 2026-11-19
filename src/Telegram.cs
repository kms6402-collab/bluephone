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

        /// <summary>API 주소 (테스트용으로 환경 변수 BLUEPHONE_TELEGRAM_API 로 바꿀 수 있음)</summary>
        static string ApiBase
        {
            get
            {
                var v = Environment.GetEnvironmentVariable("BLUEPHONE_TELEGRAM_API");
                return string.IsNullOrWhiteSpace(v) ? "https://api.telegram.org" : v.Trim().TrimEnd('/');
            }
        }

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
                        await SendTo(token, chatId, "파란전화기 설정 화면에 나온 연결 코드를 함께 보내 주세요.\n예) /start 123456", ct).ConfigureAwait(false);
                }
            }
            throw new TimeoutException("시간 안에 연결 코드를 받지 못했습니다.");
        }

        /// <summary>연결 직후 안내 · 명령어 메뉴 등록</summary>
        public static async Task Welcome(string token, long chatId, string myName, CancellationToken ct)
        {
            try
            {
                await Call(token, "setMyCommands", new Dictionary<string, string>
                {
                    ["commands"] = "[{\"command\":\"list\",\"description\":\"접속 중인 동료 목록\"}," +
                                   "{\"command\":\"msg\",\"description\":\"쪽지 보내기  /msg 이름 내용\"}," +
                                   "{\"command\":\"chat\",\"description\":\"1:1 대화 보내기  /chat 이름 내용\"}," +
                                   "{\"command\":\"status\",\"description\":\"내 상태 바꾸기\"}," +
                                   "{\"command\":\"help\",\"description\":\"사용법\"}]",
                }, 15000, ct).ConfigureAwait(false);
            }
            catch { }
            await SendTo(token, chatId, $"✅ 파란전화기({myName})와 연결되었습니다.\n\n" + HelpText, ct).ConfigureAwait(false);
        }

        const string HelpText =
            "받은 쪽지 · 대화가 여기로 옵니다.\n" +
            "• 받은 메시지에 '답장'하면 그 동료에게 바로 보냅니다\n" +
            "• /list — 동료 목록 (번호로 고를 수 있음)\n" +
            "• /msg 이름 내용 — 쪽지 보내기\n" +
            "• /chat 이름 내용 — 1:1 대화로 보내기\n" +
            "• /status 대화|자리|용무|방해 — 내 상태 바꾸기\n" +
            "• 사진 · 파일: 받은 메시지에 답장으로 보내거나, 설명에 /msg 이름 을 적기\n" +
            "이름 대신 /list 의 번호를 써도 됩니다. 예) /msg 2 회의 10분 늦습니다";

        // ───────────────────────── 알림 보내기 ─────────────────────────

        /// <summary>받은 쪽지 · 대화를 텔레그램으로 전달 (UI 스레드에서 호출)</summary>
        public void Forward(ChatMessage m, string myStatus)
        {
            if (!Active || m == null || m.Outgoing) return;
            if (m.Chat ? !S.TelegramChats : !S.TelegramNotes) return;
            if (S.TelegramOnlyAway && myStatus == Status.Online && Native.IdleTime().TotalMinutes < 3) return;

            var kind = m.Broadcast ? "📢 전체 공지" : m.Urgent ? "🚨 긴급 쪽지" : m.Chat ? "💬 대화" : "✉️ 쪽지";
            var who = m.FromName + (string.IsNullOrEmpty(m.PeerDept) ? "" : " · " + m.PeerDept);
            var sb = new StringBuilder();
            sb.Append(kind).Append("  <b>").Append(Esc(who)).Append("</b>");
            if (m.Auto) sb.Append("  (자동응답)");
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
            // 첨부파일: 보낸 PC에서 받아 텔레그램에 올림 (내용 숨기기면 보내지 않음)
            var files = S.TelegramFiles && S.TelegramShowText
                ? m.Files.Where(f => f.Size > 0 && f.Size <= MaxUpload).Take(10)
                         .Select(f => (f.Name, f.Size, f.Token, local: Media.Exists(f.LocalPath) ? f.LocalPath : null)).ToList()
                : new List<(string Name, long Size, string Token, string local)>();
            var ip = m.PeerIp; var port = m.PeerPort;
            Task.Run(async () =>
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
            await SendTo(S.TelegramToken.Trim(), S.TelegramChatId, "🔔 파란전화기 테스트 알림입니다.", CancellationToken.None);
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
            var req = (HttpWebRequest)WebRequest.Create($"{ApiBase}/file/bot{token.Trim()}/{fp}");
            Prepare(req);
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (cts.Token.Register(() => { try { req.Abort(); } catch { } }))
            using (var resp = await req.GetResponseAsync().ConfigureAwait(false))
            using (var src = resp.GetResponseStream())
            using (var fs = File.Create(dest))
                await src.CopyToAsync(fs).ConfigureAwait(false);
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
                return "명령어를 알 수 없습니다. 받은 메시지에 '답장'하거나 /help 를 보내 보세요.";

            var sp = text.IndexOfAny(new[] { ' ', '\n' });
            var cmd = (sp < 0 ? text : text.Substring(0, sp)).ToLowerInvariant();
            var at = cmd.IndexOf('@'); if (at > 0) cmd = cmd.Substring(0, at);   // /msg@MyBot
            var rest = sp < 0 ? "" : text.Substring(sp + 1).Trim();

            switch (cmd)
            {
                case "/start":
                case "/help":
                case "/도움말":
                    return HelpText;
                case "/list":
                case "/l":
                case "/목록":
                    return await OnUi(ListPeers);
                case "/msg":
                case "/m":
                case "/쪽지":
                case "/chat":
                case "/c":
                case "/대화":
                {
                    bool chat = cmd == "/chat" || cmd == "/c" || cmd == "/대화";
                    if (rest.Length == 0) return chat ? "사용법: /chat 이름 내용" : "사용법: /msg 이름 내용";
                    return await OnUi(() => SendCommand(rest, chat, files));
                }
                case "/status":
                case "/상태":
                    return await OnUi(() => ChangeStatus(rest));
                default:
                    return "알 수 없는 명령어입니다. /help 를 보내 보세요.";
            }
        }

        Task<string> OnUi(Func<Task<string>> f) => UI.InvokeAsync(f).Task.Unwrap();
        Task<string> OnUi(Func<string> f) => UI.InvokeAsync(f).Task;

        IEnumerable<Peer> Visible => _core.Peers.Where(p => !_core.IsBlocked(p.Id));

        string ListPeers()
        {
            var list = Visible.OrderBy(p => p.IsOnline ? 0 : 1).ThenBy(p => Status.Order(p.Status_)).ThenBy(p => p.DisplayName).Take(60).ToList();
            if (list.Count == 0) return "동료 목록이 비어 있습니다.";
            _listed = list.Select(p => p.Id).ToList();
            var sb = new StringBuilder("동료 목록 — /msg 번호 내용 으로 보낼 수 있습니다\n");
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                var dot = !p.IsOnline ? "⚪" : p.Status_ == Status.Online ? "🟢" : p.Status_ == Status.Away ? "🟡" : "🔴";
                sb.Append('\n').Append(i + 1).Append(". ").Append(dot).Append(' ').Append(p.DisplayName);
                if (!string.IsNullOrEmpty(p.Dept)) sb.Append(" · ").Append(p.Dept);
                if (p.IsOnline && p.Status_ != Status.Online) sb.Append(" (").Append(Status.Label(p.Status_)).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>"/msg 대상 내용" — 대상은 /list 번호, 이름, 이름 일부</summary>
        Task<string> SendCommand(string rest, bool chat, List<string> files = null)
        {
            var peers = Visible.ToList();
            Peer target = null;
            string body = null;

            // 1) 번호
            var sp = rest.IndexOfAny(new[] { ' ', '\n' });
            var first = sp < 0 ? rest : rest.Substring(0, sp);
            if (int.TryParse(first, out int n))
            {
                if (n < 1 || n > _listed.Count) return Task.FromResult("번호가 목록에 없습니다. /list 로 다시 확인해 주세요.");
                target = peers.FirstOrDefault(p => p.Id == _listed[n - 1]);
                if (target == null) return Task.FromResult("그 동료를 찾을 수 없습니다. /list 로 다시 확인해 주세요.");
                body = sp < 0 ? "" : rest.Substring(sp + 1);
            }
            else
            {
                // 2) 이름 (띄어쓰기가 있는 이름 · 별명도 가장 긴 것부터 맞춰 봄)
                var exact = new List<(Peer p, int len)>();
                foreach (var p in peers)
                    foreach (var nm in new[] { p.DisplayName, p.Name }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                        if (rest.StartsWith(nm, StringComparison.OrdinalIgnoreCase) && (rest.Length == nm.Length || char.IsWhiteSpace(rest[nm.Length])))
                        { exact.Add((p, nm.Length)); break; }
                if (exact.Count > 0)
                {
                    int len = exact.Max(x => x.len);
                    var best = exact.Where(x => x.len == len).Select(x => x.p).ToList();
                    if (best.Count > 1 && best.Any(x => x.IsOnline)) best = best.Where(x => x.IsOnline).ToList();   // 같은 이름이면 접속 중인 사람
                    if (best.Count > 1)
                    {
                        _listed = best.Take(20).Select(p => p.Id).ToList();
                        return Task.FromResult($"'{best[0].DisplayName}' 이름이 여러 명입니다. 번호로 보내 주세요.\n" +
                            string.Join("\n", best.Take(20).Select((p, i) => $"{i + 1}. {p.DisplayName}" + (string.IsNullOrEmpty(p.Dept) ? "" : " · " + p.Dept) + (p.IsOnline ? "" : " (오프라인)"))) +
                            $"\n예) {(chat ? "/chat" : "/msg")} 1 내용");
                    }
                    target = best[0];
                    body = rest.Substring(len);
                }
                // 3) 이름 일부
                if (target == null)
                {
                    var hits = peers.Where(p => p.DisplayName.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0
                                             || (p.Name ?? "").IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (hits.Count == 0) return Task.FromResult($"'{first}' 이름의 동료를 찾을 수 없습니다. /list 로 확인해 주세요.");
                    if (hits.Count > 1 && hits.Select(x => x.DisplayName).Distinct().Count() == 1 && hits.Count(x => x.IsOnline) == 1)
                        hits = hits.Where(x => x.IsOnline).ToList();
                    if (hits.Count > 1)
                    {
                        _listed = hits.Take(20).Select(p => p.Id).ToList();
                        return Task.FromResult($"'{first}' 이름이 여러 명입니다. 번호로 보내 주세요.\n" +
                            string.Join("\n", hits.Take(20).Select((p, i) => $"{i + 1}. {p.DisplayName}" + (string.IsNullOrEmpty(p.Dept) ? "" : " · " + p.Dept))) +
                            $"\n예) {(chat ? "/chat" : "/msg")} 1 내용");
                    }
                    target = hits[0];
                    body = sp < 0 ? "" : rest.Substring(sp + 1);
                }
            }
            body = (body ?? "").Trim();
            if (body.Length == 0 && (files == null || files.Count == 0)) return Task.FromResult($"{target.DisplayName}님에게 보낼 내용을 함께 적어 주세요.\n예) {(chat ? "/chat" : "/msg")} {target.DisplayName} 안녕하세요");
            return SendFromTelegram(target.Id, target, body, chat, null, files);
        }

        async Task<string> SendFromTelegram(string peerId, Peer p, string text, bool chat, string replyOf, List<string> files = null)
        {
            p = p ?? _core.Peers.FirstOrDefault(x => x.Id == peerId);
            if (p == null) return "그 동료가 목록에 없습니다.";
            if (_core.IsBlocked(p.Id)) return $"{p.DisplayName}님은 차단한 동료입니다.";
            var m = await _core.Send(new[] { p }, text, files, false, false, chat: chat);
            // 답장한 원래 메시지는 읽음으로
            if (replyOf != null)
            {
                var orig = _core.History.FirstOrDefault(x => x.Id == replyOf && !x.Outgoing);
                if (orig != null && !orig.Read) _core.MarkRead(orig);
            }
            var what = (chat ? "대화" : "쪽지") + (files != null && files.Count > 0 ? "(파일 포함)" : "");
            if (m.PendingTo.Count > 0) return $"🕓 {p.DisplayName}님이 지금 접속해 있지 않아, 접속하면 {what}를 전달합니다.";
            if (m.Failed || m.FailedTo.Count > 0) return $"⚠️ {p.DisplayName}님에게 {what}를 보내지 못했습니다.";
            return $"✅ {p.DisplayName}님에게 {what}를 보냈습니다.";
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
            var req = (HttpWebRequest)WebRequest.Create($"{ApiBase}/bot{token.Trim()}/{method}");
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            Prepare(req);
            var body = Encoding.UTF8.GetBytes(args == null ? "" : string.Join("&", args.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value ?? ""))));
            return await Execute(req, async s => await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false), timeoutMs, ct).ConfigureAwait(false);
        }

        static void Prepare(HttpWebRequest req)
        {
            req.UserAgent = "BluePhone/" + AppInfo.VersionText;
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                proxy.Credentials = CredentialCache.DefaultCredentials;
                req.Proxy = proxy;
            }
            catch { }
        }

        /// <summary>파일 올리기 (multipart/form-data)</summary>
        static async Task<object> Upload(string token, string method, Dictionary<string, string> fields, string fileField, string path, string fileName, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create($"{ApiBase}/bot{token.Trim()}/{method}");
            req.Method = "POST";
            var boundary = "----BluePhone" + Guid.NewGuid().ToString("N");
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.AllowWriteStreamBuffering = false;
            req.SendChunked = false;
            Prepare(req);
            var sb = new StringBuilder();
            foreach (var kv in fields)
                sb.Append("--").Append(boundary).Append("\r\nContent-Disposition: form-data; name=\"").Append(kv.Key).Append("\"\r\n\r\n").Append(kv.Value).Append("\r\n");
            var safe = fileName.Replace("\"", "'").Replace("\r", "").Replace("\n", "");
            sb.Append("--").Append(boundary).Append("\r\nContent-Disposition: form-data; name=\"").Append(fileField)
              .Append("\"; filename=\"").Append(safe).Append("\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            var tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            var len = new FileInfo(path).Length;
            req.ContentLength = head.Length + len + tail.Length;
            return await Execute(req, async s =>
            {
                await s.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                using (var fs = File.OpenRead(path)) await fs.CopyToAsync(s, 81920).ConfigureAwait(false);
                await s.WriteAsync(tail, 0, tail.Length).ConfigureAwait(false);
            }, timeoutMs, CancellationToken.None).ConfigureAwait(false);
        }

        static async Task<object> Execute(HttpWebRequest req, Func<Stream, Task> write, int timeoutMs, CancellationToken ct)
        {
            using (var lim = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                lim.CancelAfter(timeoutMs);
                using (lim.Token.Register(() => { try { req.Abort(); } catch { } }))
                {
                    string json;
                    int http = 200;
                    try
                    {
                        using (var s = await req.GetRequestStreamAsync().ConfigureAwait(false))
                            await write(s).ConfigureAwait(false);
                        using (var resp = await req.GetResponseAsync().ConfigureAwait(false))
                        using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            json = await rd.ReadToEndAsync().ConfigureAwait(false);
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse hr)
                    {
                        http = (int)hr.StatusCode;
                        using (var rd = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) json = rd.ReadToEnd();
                    }
                    catch (WebException) when (lim.IsCancellationRequested)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        throw new TimeoutException("응답 시간 초과");
                    }

                    Dictionary<string, object> d = null;
                    try { d = Store.Json.DeserializeObject(json) as Dictionary<string, object>; } catch { }
                    if (d == null) throw new TelegramException(http, $"알 수 없는 응답 (HTTP {http})");
                    if (d.TryGetValue("ok", out var ok) && ok is bool b && b)
                        return d.TryGetValue("result", out var res) ? res : null;
                    var code = (int)Long(d, "error_code");
                    throw new TelegramException(code != 0 ? code : http, Str(d, "description") ?? $"HTTP {http}");
                }
            }
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
