using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BluePhone
{
    /// <summary>
    /// 텔레그램 버튼 메뉴 — 명령어를 외우지 않아도 버튼을 차례로 눌러 진행.
    ///  메뉴 → (동료 목록에서 고르기) → 내용 입력 → 보내기
    /// </summary>
    public sealed partial class TelegramBridge
    {
        /// <summary>화면 하나 (보낼 글 + 버튼). Edit 이면 누른 메뉴 메시지를 그 자리에서 바꿈</summary>
        sealed class Screen
        {
            public string Html;
            public List<List<(string text, string data)>> Buttons;
            public bool New;
            public Screen(string html, params (string, string)[][] rows)
            {
                Html = html;
                Buttons = rows.Select(r => r.ToList()).ToList();
            }
            public Screen Row(params (string, string)[] r) { Buttons.Add(r.ToList()); return this; }
            public Screen AsNew() { New = true; return this; }
        }

        /// <summary>다음에 입력할 글이 무엇에 쓰일지 (내용 입력 기다리는 중)</summary>
        sealed class Pend
        {
            public string Act;        // msg · chat · urg · late · grp · bc · find · memo · ar · add
            public string Peer;
            public string ReplyOf;
            public int Group = -1;
            public DateTime At;
            public string FindFor;
            public DateTime Until = DateTime.Now.AddMinutes(15);
        }

        Pend _pend;
        (string text, List<string> files)? _draft;
        string _pickFilter;
        bool _kbShown;
        const int PageSize = 8;

        // 아래쪽에 늘 보이는 버튼
        const string KbMenu = "📋 메뉴", KbNote = "✉️ 쪽지", KbChat = "💬 대화", KbUnread = "📬 안 읽음", KbCancel = "❌ 취소";

        static string ReplyKeyboard => Store.Json.Serialize(new
        {
            keyboard = new[]
            {
                new[] { new { text = KbNote }, new { text = KbChat }, new { text = KbUnread } },
                new[] { new { text = KbMenu }, new { text = KbCancel } },
            },
            resize_keyboard = true,
            is_persistent = true,
        });

        static string InlineJson(List<List<(string text, string data)>> rows) => Store.Json.Serialize(new
        {
            inline_keyboard = rows.Where(r => r.Count > 0).Select(r => r.Select(b => new { text = b.text, callback_data = b.data }).ToArray()).ToArray()
        });

        static readonly (string, string) BtnMenu = ("📋 메뉴", "menu");
        static readonly (string, string) BtnCancel = ("❌ 취소", "cx");

        // ───────────── 버튼 ID (동료 ID 가 길면 짧은 번호로) ─────────────
        readonly Dictionary<string, string> _keys = new Dictionary<string, string>();
        string Key(string id)
        {
            if (id.Length <= 40 && !id.Contains("|")) return id;
            var k = "#" + (Math.Abs(id.GetHashCode()) % 1000000);
            lock (_keys) _keys[k] = id;
            return k;
        }
        string Unkey(string k) { if (!k.StartsWith("#")) return k; lock (_keys) return _keys.TryGetValue(k, out var v) ? v : null; }

        Peer PeerByKey(string k)
        {
            var id = Unkey(k);
            return id == null ? null : _core.Peers.FirstOrDefault(p => p.Id == id);
        }

        static string Trunc(string s, int n) => s == null ? "" : s.Length > n ? s.Substring(0, n - 1) + "…" : s;

        // ───────────── 화면들 (UI 스레드에서 만듦) ─────────────

        Screen MainMenu()
        {
            int online = Visible.Count(p => p.IsOnline), unread = _core.Unread;
            var mute = DateTime.Now < _muteUntil ? $"\n🔕 {_muteUntil:tt h:mm}까지 알림 끔" : "";
            return new Screen(
                $"📋 <b>{Esc(AppInfo.Name)} 메뉴</b>\n" +
                $"내 상태: {MyDot()} {Esc(Status.Label(_core.MyStatus))} · 접속 동료 {online}명" + (unread > 0 ? $" · 안 읽음 {unread}개" : "") + mute +
                "\n\n무엇을 할까요?",
                new[] { ("✉️ 쪽지 보내기", "new|msg"), ("💬 대화 보내기", "new|chat") },
                new[] { ("🚨 긴급 쪽지", "new|urg"), ("⏰ 예약 쪽지", "new|late") },
                new[] { ("👥 그룹에 보내기", "gl"), ("📢 전체 공지", "bc") },
                new[] { (unread > 0 ? $"📬 안 읽은 메시지 ({unread})" : "📬 안 읽은 메시지", "unread"), ("🕘 대화 기록", "new|hist") },
                new[] { ("🟢 내 상태 바꾸기", "st"), ("🔔 알림 설정", "nt") },
                new[] { ("👤 동료 보기", "new|info"), ("⚙️ 더 보기", "more") });
        }

        string MyDot()
        {
            var s = _core.MyStatus;
            return s == Status.Online ? "🟢" : s == Status.Away ? "🟡" : "🔴";
        }

        Screen MoreMenu() => new Screen("⚙️ <b>더 보기</b>",
            new[] { ("📝 상태 메시지", "memo"), ("🤖 자동응답", "ar") },
            new[] { ("⛔ 동료 차단", "new|blk"), ("✅ 차단 풀기", "ub") },
            new[] { ("➕ IP로 동료 추가", "add"), ("🔄 동료 다시 찾기", "rescan") },
            new[] { ("ℹ️ 내 정보", "me"), ("❓ 사용 방법", "help") },
            new[] { ("◀ 메뉴", "menu") });

        static string ActTitle(string act)
        {
            switch (act)
            {
                case "msg": return "✉️ 쪽지를 보낼 동료를 고르세요";
                case "chat": return "💬 대화를 보낼 동료를 고르세요";
                case "urg": return "🚨 긴급 쪽지를 보낼 동료를 고르세요";
                case "late": return "⏰ 예약 쪽지를 보낼 동료를 고르세요";
                case "hist": return "🕘 대화 기록을 볼 동료를 고르세요";
                case "info": return "👤 정보를 볼 동료를 고르세요";
                case "blk": return "⛔ 차단할 동료를 고르세요";
                case "dr": return "📨 이 내용을 누구에게 보낼까요?";
                default: return "동료를 고르세요";
            }
        }

        /// <summary>동료 목록 (접속 중 먼저, 한 쪽에 8명)</summary>
        Screen Picker(string act, int page)
        {
            var f = (_pickFilter ?? "").Trim();
            var all = Visible.Where(p => f.Length == 0
                                       || p.DisplayName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                                       || (p.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                                       || (p.Dept ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                             .OrderBy(p => p.IsOnline ? 0 : 1).ThenBy(p => Status.Order(p.Status_)).ThenBy(p => p.DisplayName).ToList();
            int pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            var sb = new StringBuilder("<b>" + ActTitle(act) + "</b>");
            if (act == "dr" && _draft != null)
            {
                var d = _draft.Value;
                var t = Trunc((d.text ?? "").Trim(), 300);
                if (t.Length > 0) sb.Append("\n\n“").Append(Esc(t)).Append('”');
                if (d.files != null && d.files.Count > 0) sb.Append(t.Length > 0 ? "\n" : "\n\n").Append("📎 파일 ").Append(d.files.Count).Append("개");
            }
            sb.Append("\n\n");
            if (f.Length > 0) sb.Append("🔎 '").Append(Esc(f)).Append("' 검색 결과 · ");
            sb.Append($"{all.Count}명 (접속 {all.Count(p => p.IsOnline)})");
            if (pages > 1) sb.Append($" · {page + 1}/{pages}쪽");
            if (all.Count == 0) sb.Append(f.Length > 0 ? "\n\n맞는 동료가 없습니다." : "\n\n동료 목록이 비어 있습니다.");
            sb.Append("\n🟢 대화 가능 🟡 자리 비움 🔴 다른 용무 ⚪ 꺼짐");

            var scr = new Screen(sb.ToString());
            var items = all.Skip(page * PageSize).Take(PageSize).ToList();
            for (int i = 0; i < items.Count; i += 2)
                scr.Row(items.Skip(i).Take(2).Select(p => (Dot(p) + " " + Trunc(p.DisplayName + (string.IsNullOrEmpty(p.Dept) ? "" : " · " + p.Dept), 22), $"to|{act}|{Key(p.Id)}")).ToArray());
            var nav = new List<(string, string)>();
            if (page > 0) nav.Add(("◀ 이전", $"pg|{act}|{page - 1}"));
            if (page < pages - 1) nav.Add(("다음 ▶", $"pg|{act}|{page + 1}"));
            if (nav.Count > 0) scr.Row(nav.ToArray());
            scr.Row(("🔎 이름으로 찾기", $"find|{act}"), f.Length > 0 ? ("전체 보기", $"new|{act}") : act == "dr" ? BtnCancel : BtnMenu);
            if (f.Length > 0 || act == "dr") scr.Row(act == "dr" && f.Length > 0 ? BtnCancel : BtnMenu);
            return scr;
        }

        /// <summary>동료를 고른 다음 단계</summary>
        Screen Chosen(string act, Peer p)
        {
            if (p == null) return new Screen("그 동료를 찾을 수 없습니다. 목록을 다시 열어 주세요.", new[] { BtnMenu });
            var name = Esc(p.DisplayName);
            var state = p.IsOnline ? Status.Label(p.Status_) : "꺼져 있음 — 접속하면 전달합니다";
            switch (act)
            {
                case "msg":
                case "chat":
                case "urg":
                {
                    _pend = new Pend { Act = act, Peer = p.Id };
                    var what = act == "chat" ? "💬 대화" : act == "urg" ? "🚨 긴급 쪽지" : "✉️ 쪽지";
                    return new Screen($"{what} → <b>{name}</b> {Dot(p)} ({Esc(state)})\n\n보낼 내용을 입력하세요. 사진 · 파일도 보낼 수 있습니다." +
                                      (act == "chat" ? "\n<i>대화를 끝낼 때까지 입력하는 글은 모두 이 동료에게 갑니다.</i>" : ""),
                                      new[] { BtnCancel });
                }
                case "late":
                    return new Screen($"⏰ <b>{name}</b>님에게 언제 보낼까요?",
                        new[] { ("30분 뒤", $"lt|{Key(p.Id)}|30"), ("1시간 뒤", $"lt|{Key(p.Id)}|60"), ("2시간 뒤", $"lt|{Key(p.Id)}|120") },
                        new[] { ("오늘 오후 6시", $"lt|{Key(p.Id)}|t18"), ("내일 오전 9시", $"lt|{Key(p.Id)}|n9") },
                        new[] { BtnCancel });
                case "hist":
                    return HistoryScreen(p, 10);
                case "info":
                    return PeerScreen(p);
                case "blk":
                    return new Screen($"⛔ <b>{name}</b>님을 차단할까요?\n차단하면 쪽지 · 대화를 받지 않습니다.",
                        new[] { ("⛔ 차단하기", $"blk|{Key(p.Id)}"), BtnCancel });
                case "dr":
                    return new Screen($"📨 <b>{name}</b> {Dot(p)} 님에게 어떻게 보낼까요?",
                        new[] { ("✉️ 쪽지로", $"sd|msg|{Key(p.Id)}"), ("💬 대화로", $"sd|chat|{Key(p.Id)}") },
                        new[] { ("🚨 긴급 쪽지로", $"sd|urg|{Key(p.Id)}"), BtnCancel });
            }
            return MainMenu();
        }

        Screen PeerScreen(Peer p) => new Screen(Esc(InfoOf(p)),
            new[] { ("✉️ 쪽지", $"to|msg|{Key(p.Id)}"), ("💬 대화", $"to|chat|{Key(p.Id)}") },
            new[] { ("🕘 대화 기록", $"to|hist|{Key(p.Id)}"), ("⛔ 차단", $"to|blk|{Key(p.Id)}") },
            new[] { ("◀ 동료 목록", "pg|info|0"), BtnMenu });

        Screen HistoryScreen(Peer p, int n)
        {
            var list = _core.History.Where(m => m.Involves(p.Id)).OrderByDescending(m => m.Ts).Take(n).Reverse().ToList();
            var sb = new StringBuilder($"🕘 <b>{Esc(p.DisplayName)}</b>님과 최근 {list.Count}개\n");
            if (list.Count == 0) sb.Append("\n주고받은 메시지가 없습니다.");
            foreach (var m in list)
            {
                var t = (Markdown.Plain(m.Text) ?? "").Replace("\r", "").Replace("\n", " ").Trim();
                if (t.Length > 150) t = t.Substring(0, 150) + "…";
                if (m.Files.Count > 0) t += (t.Length > 0 ? " " : "") + "📎" + string.Join(", ", m.Files.Select(f => f.Name));
                sb.Append("\n<i>").Append(m.Time.ToString("M'/'d HH:mm")).Append("</i> ")
                  .Append(m.Outgoing ? "나" : Esc(p.DisplayName)).Append(m.Chat ? "" : "(쪽지)").Append(": ").Append(Esc(t));
            }
            var html = sb.ToString();
            if (html.Length > 3800) html = html.Substring(html.Length - 3800);
            var scr = new Screen(html);
            if (n < 30 && list.Count == n) scr.Row(("더 보기 (30개)", $"hm|{Key(p.Id)}"));
            scr.Row(("✉️ 쪽지", $"to|msg|{Key(p.Id)}"), ("💬 대화", $"to|chat|{Key(p.Id)}"));
            scr.Row(("◀ 동료 목록", "pg|hist|0"), BtnMenu);
            return scr;
        }

        Screen StatusScreen(string done = null)
        {
            var cur = _core.MyStatus;
            (string, string) B(string s, string icon) => ((s == cur ? "✔ " : "") + icon + " " + Status.Label(s), "st|" + s);
            return new Screen((done != null ? done + "\n\n" : "") + $"🟢 <b>내 상태</b>: {Esc(Status.Label(cur))}" +
                              (string.IsNullOrEmpty(S.Note) ? "" : $"\n상태 메시지: {Esc(S.Note)}") + "\n\n바꿀 상태를 고르세요.",
                new[] { B(Status.Online, "🟢"), B(Status.Away, "🟡") },
                new[] { B(Status.Busy, "🔴"), B(Status.Dnd, "⛔") },
                new[] { ("📝 상태 메시지", "memo"), BtnMenu });
        }

        Screen NotifyScreen(string done = null)
        {
            (string, string) T(string label, bool on, string key) => ((on ? "✅ " : "⬜ ") + label, "nt|" + key);
            var muted = DateTime.Now < _muteUntil;
            var scr = new Screen((done != null ? done + "\n\n" : "") + "🔔 <b>텔레그램 알림 설정</b>\n누르면 켜고 끕니다." +
                                 (muted ? $"\n\n🔕 지금 {_muteUntil:tt h:mm}까지 알림을 끈 상태입니다. (긴급 쪽지는 예외)" : ""),
                new[] { T("받은 쪽지", S.TelegramNotes, "note"), T("받은 대화", S.TelegramChats, "chat") },
                new[] { T("첨부파일", S.TelegramFiles, "file"), T("내용 보이기", S.TelegramShowText, "text") },
                new[] { T("자리 비울 때만", S.TelegramOnlyAway, "away") });
            if (muted) scr.Row(("🔔 알림 다시 켜기", "mu|0"));
            else scr.Row(("🔕 30분", "mu|30"), ("🔕 1시간", "mu|60"), ("🔕 2시간", "mu|120"), ("🔕 오늘", "mu|day"));
            scr.Row(BtnMenu);
            return scr;
        }

        Screen GroupList()
        {
            var gs = S.Groups ?? new List<PeerGroup>();
            if (gs.Count == 0) return new Screen("만든 그룹이 없습니다.\n(PC의 메인 창에서 그룹을 만들 수 있습니다)", new[] { BtnMenu });
            var scr = new Screen("👥 <b>쪽지를 보낼 그룹을 고르세요</b>");
            for (int i = 0; i < gs.Count && i < 30; i++)
            {
                var ms = _core.GroupMembers(gs[i]);
                scr.Row(($"{gs[i].Name} ({ms.Count(p => p.IsOnline)}/{ms.Count}명)", "gs|" + i));
            }
            scr.Row(BtnMenu);
            return scr;
        }

        Screen Ask(Pend p, string html, params (string, string)[] extra)
        {
            _pend = p;
            var scr = new Screen(html);
            scr.Row(extra.Concat(new[] { BtnCancel }).ToArray());
            return scr;
        }

        /// <summary>결과 글 + 다음에 할 일 버튼</summary>
        Screen Done(string text, Peer p = null, bool html = false)
        {
            var scr = new Screen(html ? text : Esc(text));
            if (p != null) scr.Row(("✉️ 또 쪽지", $"to|msg|{Key(p.Id)}"), ("💬 대화", $"to|chat|{Key(p.Id)}"));
            scr.Row(BtnMenu);
            return scr;
        }

        // ───────────── 버튼 누름 ─────────────

        async Task HandleCallback(string token, Dictionary<string, object> cb)
        {
            var id = Str(cb, "id");
            var msg = Obj(cb, "message");
            var chatId = Long(Obj(msg, "chat"), "id");
            if (chatId != S.TelegramChatId) { _ = Answer(token, id, null); return; }
            long mid = Long(msg, "message_id");
            var data = Str(cb, "data") ?? "";
            string toast = null;
            Screen scr;
            try
            {
                var r = await Press(data, mid);
                scr = r.scr; toast = r.toast;
            }
            catch (Exception ex) { scr = Done("⚠️ " + ex.Message).AsNew(); }
            _ = Answer(token, id, toast);
            if (scr != null) await Show(token, chatId, mid, scr);
        }

        async Task<(Screen scr, string toast)> Press(string data, long mid)
        {
            var a = data.Split('|');
            string A(int i) => i < a.Length ? a[i] : "";
            switch (A(0))
            {
                case "menu": _pend = null; _draft = null; return (await Ui(MainMenu), null);
                case "more": return (await Ui(MoreMenu), null);
                case "cx": _pend = null; _draft = null; return (await Ui(() => { var m = MainMenu(); m.Html = "취소했습니다.\n\n" + m.Html; return m; }), "취소");
                case "new": _pickFilter = null; return (await Ui(() => Picker(A(1), 0)), null);
                case "pg": return (await Ui(() => Picker(A(1), int.TryParse(A(2), out var pg) ? pg : 0)), null);
                case "find":
                    return (Ask(new Pend { Act = "find", FindFor = A(1) }, "🔎 찾을 이름(또는 부서)을 입력하세요."), null);
                case "to": return (await Ui(() => Chosen(A(1), PeerByKey(A(2)))), null);
                case "sd":
                {
                    var d = _draft; _draft = null;
                    if (d == null) return (await Ui(MainMenu), "보낼 내용이 없습니다");
                    var r = await Ui(async () =>
                    {
                        var p = PeerByKey(A(2));
                        if (p == null) return Done("그 동료를 찾을 수 없습니다.");
                        var res = await SendFromTelegram(p.Id, p, d.Value.text ?? "", A(1) == "chat", null, d.Value.files, A(1) == "urg");
                        if (A(1) == "chat") return ChatMode(p, res);
                        return Done(res, p);
                    });
                    return (r, null);
                }
                case "lt":
                {
                    var scr = await Ui(() =>
                    {
                        var p = PeerByKey(A(1));
                        if (p == null) return Done("그 동료를 찾을 수 없습니다.");
                        var w = A(2); DateTime at;
                        if (w == "t18") { at = DateTime.Today.AddHours(18); if (at <= DateTime.Now) at = at.AddDays(1); }
                        else if (w == "n9") at = DateTime.Today.AddDays(1).AddHours(9);
                        else at = DateTime.Now.AddMinutes(int.TryParse(w, out var mm) ? mm : 30);
                        return Ask(new Pend { Act = "late", Peer = p.Id, At = at },
                                   $"⏰ <b>{Esc(p.DisplayName)}</b>님에게 <b>{at:M월 d일 tt h:mm}</b>에 보낼 쪽지 내용을 입력하세요.");
                    });
                    return (scr, null);
                }
                case "hm": return (await Ui(() => { var p = PeerByKey(A(1)); return p == null ? MainMenu() : HistoryScreen(p, 30); }), null);
                case "blk":
                    return (await Ui(() =>
                    {
                        var p = PeerByKey(A(1));
                        if (p == null) return Done("그 동료를 찾을 수 없습니다.");
                        _core.Block(p);
                        return new Screen($"⛔ {Esc(p.DisplayName)}님을 차단했습니다.", new[] { ("되돌리기", "ubk|" + Key(p.Id)), BtnMenu });
                    }), "차단했습니다");
                case "ub":
                    return (await Ui(() =>
                    {
                        if (S.Blocked.Count == 0) return new Screen("차단한 동료가 없습니다.", new[] { BtnMenu });
                        var scr = new Screen("✅ <b>차단을 풀 동료를 고르세요</b>");
                        foreach (var b in S.Blocked.Take(30)) scr.Row((b.Name ?? b.Id, "ubk|" + Key(b.Id)));
                        scr.Row(BtnMenu);
                        return scr;
                    }), null);
                case "ubk":
                    return (await Ui(() =>
                    {
                        var id = Unkey(A(1));
                        var b = S.Blocked.FirstOrDefault(x => x.Id == id);
                        if (b == null) return Done("이미 차단이 풀려 있습니다.");
                        _core.Unblock(b.Id);
                        return Done($"{b.Name}님의 차단을 풀었습니다.");
                    }), "차단을 풀었습니다");
                case "gl": return (await Ui(GroupList), null);
                case "gs":
                    return (await Ui(() =>
                    {
                        var gs = S.Groups ?? new List<PeerGroup>();
                        if (!int.TryParse(A(1), out var gi) || gi < 0 || gi >= gs.Count) return GroupList();
                        var ms = _core.GroupMembers(gs[gi]);
                        return Ask(new Pend { Act = "grp", Group = gi },
                                   $"👥 <b>{Esc(gs[gi].Name)}</b> 그룹({ms.Count}명)에 보낼 쪽지 내용을 입력하세요.\n" + Esc(string.Join(", ", ms.Take(10).Select(p => p.DisplayName)) + (ms.Count > 10 ? " …" : "")));
                    }), null);
                case "bc":
                    return (await Ui(() => Ask(new Pend { Act = "bc" }, $"📢 접속 중인 동료 <b>{Visible.Count(p => p.IsOnline)}명</b> 모두에게 보낼 공지 내용을 입력하세요.")), null);
                case "unread":
                {
                    var s = await Ui(() => _core.Unread);
                    if (s == 0) return (await Ui(() => { var m = MainMenu(); m.Html = "안 읽은 메시지가 없습니다.\n\n" + m.Html; return m; }), "안 읽은 메시지 없음");
                    var res = await OnUi(Unread);
                    return (new Screen(Esc(res), new[] { ("✔ 모두 읽음으로", "readall"), BtnMenu }).AsNew(), $"{s}개를 보냅니다");
                }
                case "readall":
                    return (await Ui(() => { int n = _core.Unread; _core.MarkAllRead(); return Done(n == 0 ? "안 읽은 메시지가 없습니다." : $"안 읽은 메시지 {n}개를 읽음으로 바꿨습니다."); }), "읽음 처리");
                case "st":
                    if (A(1).Length == 0) return (await Ui(() => StatusScreen()), null);
                    return (await Ui(() => { _core.SetStatus(A(1)); return StatusScreen("✅ 상태를 바꿨습니다."); }), Status.Label(A(1)));
                case "nt":
                    if (A(1).Length == 0) return (await Ui(() => NotifyScreen()), null);
                    return (await Ui(() =>
                    {
                        switch (A(1))
                        {
                            case "note": S.TelegramNotes = !S.TelegramNotes; break;
                            case "chat": S.TelegramChats = !S.TelegramChats; break;
                            case "file": S.TelegramFiles = !S.TelegramFiles; break;
                            case "text": S.TelegramShowText = !S.TelegramShowText; break;
                            case "away": S.TelegramOnlyAway = !S.TelegramOnlyAway; break;
                        }
                        Store.SaveSettings(S);
                        return NotifyScreen();
                    }), "바꿨습니다");
                case "mu":
                {
                    var w = A(1);
                    if (w == "0") _muteUntil = DateTime.MinValue;
                    else if (w == "day") _muteUntil = DateTime.Today.AddDays(1);
                    else _muteUntil = DateTime.Now.AddMinutes(int.TryParse(w, out var m) ? m : 60);
                    return (await Ui(() => NotifyScreen(w == "0" ? "🔔 알림을 다시 보냅니다." : $"🔕 {_muteUntil:M월 d일 tt h:mm}까지 알림을 보내지 않습니다.")), null);
                }
                case "memo":
                    if (A(1) == "clr") return (await Ui(() => { S.Note = ""; _core.ProfileChanged(); return StatusScreen("상태 메시지를 지웠습니다."); }), "지웠습니다");
                    return (await Ui(() => Ask(new Pend { Act = "memo" },
                        $"📝 지금 상태 메시지: {(string.IsNullOrEmpty(S.Note) ? "(없음)" : Esc(S.Note))}\n\n새 상태 메시지를 입력하세요.",
                        string.IsNullOrEmpty(S.Note) ? new (string, string)[0] : new[] { ("🗑 지우기", "memo|clr") })), null);
                case "ar":
                    if (A(1) == "off") return (await Ui(() => { S.AutoReply = ""; Store.SaveSettings(S); return Done("🤖 자동응답을 껐습니다."); }), "껐습니다");
                    return (await Ui(() => Ask(new Pend { Act = "ar" },
                        "🤖 <b>자동응답</b> ('대화 가능'이 아닐 때 받은 쪽지에 자동으로 답장)\n" +
                        (string.IsNullOrWhiteSpace(S.AutoReply) ? "지금: 꺼짐" : "지금: " + Esc(S.AutoReply)) + "\n\n자동응답 내용을 입력하세요.",
                        string.IsNullOrWhiteSpace(S.AutoReply) ? new (string, string)[0] : new[] { ("끄기", "ar|off") })), null);
                case "add":
                    return (Ask(new Pend { Act = "add" }, "➕ 추가할 동료의 IP 주소를 입력하세요.\n예) 192.168.0.10 또는 192.168.0.10:9082"), null);
                case "rescan":
                    return (await Ui(() => { _core.Rescan(); return new Screen("🔄 동료를 다시 찾고 있습니다. 잠시 뒤 목록을 열어 보세요.", new[] { ("✉️ 쪽지 보내기", "new|msg"), BtnMenu }); }), null);
                case "me": return (await Ui(() => new Screen(Esc(Me()), new[] { ("◀ 더 보기", "more"), BtnMenu })), null);
                case "help": return (new Screen(Esc(HelpText), new[] { BtnMenu }).AsNew(), null);
                case "end": _pend = null; return (await Ui(() => { var m = MainMenu(); m.Html = "💬 대화를 끝냈습니다.\n\n" + m.Html; return m; }), null);

                // 받은 알림 아래 버튼
                case "rp":
                case "rd":
                {
                    (string peerId, bool chat, string msgId) t;
                    bool known;
                    lock (_sent) known = _sent.TryGetValue(mid, out t);
                    if (!known) return (null, "프로그램을 다시 시작하기 전 알림이라 알 수 없습니다");
                    if (A(0) == "rd")
                    {
                        await Ui(() => { var o = _core.History.FirstOrDefault(x => x.Id == t.msgId && !x.Outgoing); if (o != null && !o.Read) _core.MarkRead(o); return 0; });
                        return (null, "읽음으로 바꿨습니다");
                    }
                    bool chat = A(1) == "c";
                    var scr = await Ui(() =>
                    {
                        var p = _core.Peers.FirstOrDefault(x => x.Id == t.peerId);
                        if (p == null) return Done("그 동료가 목록에 없습니다.");
                        _pend = new Pend { Act = chat ? "chat" : "msg", Peer = p.Id, ReplyOf = t.msgId };
                        return new Screen($"↩ <b>{Esc(p.DisplayName)}</b>님에게 보낼 {(chat ? "대화" : "쪽지")} 답장을 입력하세요.", new[] { BtnCancel }).AsNew();
                    });
                    return (scr, null);
                }
            }
            return (await Ui(MainMenu), null);
        }

        Screen ChatMode(Peer p, string result)
        {
            _pend = new Pend { Act = "chat", Peer = p.Id };
            return new Screen(Esc(result) + $"\n<i>💬 {Esc(p.DisplayName)}님과 대화 중 — 이어서 입력하면 계속 보냅니다.</i>",
                new[] { ("대화 끝내기", "end"), ("✉️ 다른 동료", "new|msg"), BtnMenu });
        }

        // ───────────── 글 입력 ─────────────

        /// <summary>명령어가 아닌 글 · 사진 (아래 버튼, 입력 기다리던 내용, 아니면 받는 사람 고르기)</summary>
        async Task<Screen> OnText(string text, List<string> files)
        {
            switch (text)
            {
                case KbMenu: _pend = null; _draft = null; return await Ui(MainMenu);
                case KbNote: _pend = null; _pickFilter = null; return await Ui(() => Picker("msg", 0));
                case KbChat: _pend = null; _pickFilter = null; return await Ui(() => Picker("chat", 0));
                case KbUnread: return (await Press("unread", 0)).scr;
                case KbCancel: _pend = null; _draft = null; return await Ui(() => { var m = MainMenu(); m.Html = "취소했습니다.\n\n" + m.Html; return m; });
            }

            var p = _pend;
            if (p != null && p.Until > DateTime.Now)
            {
                if (p.Act != "chat") _pend = null;
                else p.Until = DateTime.Now.AddMinutes(30);
                return await Ui(() => Fill(p, text, files));
            }
            _pend = null;
            if (text.Length == 0 && (files == null || files.Count == 0)) return null;
            // 누구에게 보낼지 모르는 글 → 받는 사람 고르기
            _draft = (text, files);
            _pickFilter = null;
            return await Ui(() => Picker("dr", 0));
        }

        async Task<Screen> Fill(Pend p, string text, List<string> files)
        {
            bool hasFiles = files != null && files.Count > 0;
            switch (p.Act)
            {
                case "msg":
                case "urg":
                case "chat":
                {
                    var peer = _core.Peers.FirstOrDefault(x => x.Id == p.Peer);
                    if (peer == null) return Done("그 동료가 목록에 없습니다.");
                    var res = await SendFromTelegram(peer.Id, peer, text, p.Act == "chat", p.ReplyOf, files, p.Act == "urg");
                    return p.Act == "chat" ? ChatMode(peer, res) : Done(res, peer);
                }
                case "late":
                {
                    var peer = _core.Peers.FirstOrDefault(x => x.Id == p.Peer);
                    if (peer == null) return Done("그 동료가 목록에 없습니다.");
                    await _core.Send(new[] { peer }, text, files, false, false, at: p.At);
                    return Done($"⏰ {p.At:M월 d일 tt h:mm}에 {peer.DisplayName}님에게 쪽지를 보내도록 예약했습니다. (PC의 쪽지함에서 취소할 수 있습니다)", peer);
                }
                case "grp":
                {
                    var gs = S.Groups ?? new List<PeerGroup>();
                    if (p.Group < 0 || p.Group >= gs.Count) return Done("그 그룹이 없습니다.");
                    var g = gs[p.Group];
                    var to = _core.GroupMembers(g).Where(x => !_core.IsBlocked(x.Id)).ToList();
                    if (to.Count == 0) return Done($"'{g.Name}' 그룹에 동료가 없습니다.");
                    var m = await _core.Send(to, text, files, false, false);
                    return Done(Result(m, to.Count, $"'{g.Name}' 그룹 쪽지"));
                }
                case "bc": return Done(await Broadcast(text, files));
                case "find":
                    _pickFilter = text;
                    var scr = Picker(p.FindFor ?? "msg", 0);
                    return scr;
                case "memo":
                    if (text.Length == 0) return Done("글자로 입력해 주세요.");
                    return StatusScreen(Esc(Memo(text)));
                case "ar":
                    if (text.Length == 0) return Done("글자로 입력해 주세요.");
                    return Done(AutoReply(text));
                case "add":
                    try { var np = await _core.AddByAddress(text.Trim()); return Done($"✅ {np?.DisplayName ?? text}님을 추가했습니다.", np); }
                    catch (Exception ex) { return Done("⚠️ " + ex.Message); }
            }
            return MainMenu();
        }

        // ───────────── 보내기 ─────────────

        async Task Show(string token, long chatId, long editMid, Screen scr)
        {
            var markup = scr.Buttons.Count > 0 ? InlineJson(scr.Buttons) : null;
            var html = scr.Html.Length > 4000 ? scr.Html.Substring(0, 4000) + "…" : scr.Html;
            if (editMid != 0 && !scr.New)
            {
                var args = new Dictionary<string, string>
                {
                    ["chat_id"] = chatId.ToString(),
                    ["message_id"] = editMid.ToString(),
                    ["text"] = html,
                    ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = "true",
                };
                if (markup != null) args["reply_markup"] = markup;
                try { await Call(token, "editMessageText", args, 20000, CancellationToken.None).ConfigureAwait(false); return; }
                catch (TelegramException ex) when ((ex.Message ?? "").Contains("not modified")) { return; }
                catch { /* 너무 오래된 메시지 등 → 새로 보냄 */ }
            }
            // 아래쪽 버튼은 처음 한 번 깔아 둠
            if (!_kbShown)
            {
                _kbShown = true;
                try
                {
                    await Call(token, "sendMessage", new Dictionary<string, string>
                    {
                        ["chat_id"] = chatId.ToString(),
                        ["text"] = "⌨️ 아래 버튼으로 언제든 메뉴를 열 수 있습니다.",
                        ["reply_markup"] = ReplyKeyboard,
                    }, 20000, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            var send = new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["text"] = html,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = "true",
            };
            if (markup != null) send["reply_markup"] = markup;
            try { await Call(token, "sendMessage", send, 20000, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        static async Task Answer(string token, string callbackId, string text)
        {
            var args = new Dictionary<string, string> { ["callback_query_id"] = callbackId ?? "" };
            if (!string.IsNullOrEmpty(text)) args["text"] = text;
            try { await Call(token, "answerCallbackQuery", args, 10000, CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        /// <summary>받은 알림 아래에 붙는 버튼</summary>
        static string AlertButtons(bool canReply) => InlineJson(canReply
            ? new List<List<(string, string)>> { new List<(string, string)> { ("↩ 쪽지로 답장", "rp|n"), ("💬 대화로 답장", "rp|c"), ("✔ 읽음", "rd") } }
            : new List<List<(string, string)>> { new List<(string, string)> { ("✔ 읽음", "rd") } });

        Task<T> Ui<T>(Func<T> f) => UI.InvokeAsync(f).Task;
        Task<T> Ui<T>(Func<Task<T>> f) => UI.InvokeAsync(f).Task.Unwrap();
    }
}
