using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
#if MAC
using Avalonia.Threading;
#else
using System.Windows;
using System.Windows.Threading;
#endif

namespace BluePhone
{
    /// <summary>
    /// 개발용 화면 · 기능 점검 (BLUEPHONE_UITEST, --data 로 따로 띄운 경우에만). 결과는 데이터 폴더의 uitest.log
    /// 예) BLUEPHONE_UITEST="peers:2;room:안녕하세요;type:4000;perf;dump"
    /// </summary>
    static class UiTest
    {
        public static void Run(Core core)
        {
            var spec = Environment.GetEnvironmentVariable("BLUEPHONE_UITEST");
            if (string.IsNullOrWhiteSpace(spec) || !Store.CustomDir) return;
            core.TypingChanged += k => Log($"typing {Short(k)} = {core.TypingText(k) ?? "(없음)"}");
            Func<Task> run = async () =>
            {
                foreach (var step in spec.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    try { await Step(core, step); }
                    catch (Exception ex) { Log($"{step}: {ex}"); }
                    await Task.Delay(1500);
                }
                Log("done");
            };
#if MAC
            Dispatcher.UIThread.Post(async () => await run(), DispatcherPriority.ApplicationIdle);
#else
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await run()));
#endif
        }

        static string Short(string key) => key == null ? "-" : key.Length > 13 ? key.Substring(0, 13) : key;

        static void Log(string s)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Dir, "uitest.log"), DateTime.Now.ToString("HH:mm:ss ") + s + "\n"); } catch { }
        }

        static async Task<List<Peer>> WaitPeers(Core core, int n)
        {
            for (int i = 0; i < 80; i++)
            {
                var list = core.Peers.Where(x => x.IsOnline).ToList();
                if (list.Count >= n) return list;
                await Task.Delay(500);
            }
            throw new TimeoutException($"접속한 동료 {n}명을 찾지 못함");
        }

        static async Task<Peer> WaitPeer(Core core) => (await WaitPeers(core, 1)).First();

        static async Task<ChatRoom> WaitRoom(Core core)
        {
            for (int i = 0; i < 60; i++)
            {
                var r = core.ActiveRooms.FirstOrDefault();
                if (r != null) return r;
                await Task.Delay(500);
            }
            throw new TimeoutException("대화방 없음");
        }

        static async Task Step(Core core, string step)
        {
            var i = step.IndexOf(':');
            var cmd = i < 0 ? step : step.Substring(0, i);
            var arg = i < 0 ? "" : step.Substring(i + 1);
            switch (cmd)
            {
                case "peers":
                {
                    var list = await WaitPeers(core, int.TryParse(arg, out var n) ? n : 1);
                    Log("peers " + string.Join(", ", list.Select(p => p.DisplayName)));
                    break;
                }
                case "chat":
                {
                    var p = await WaitPeer(core);
                    core.OpenChat(p);
                    if (arg.Length > 0)
                    {
                        var m = await core.Send(new[] { p }, arg, null, false, false, chat: true);
                        Log($"chat sent to {p.DisplayName} secure={p.Secure} failed={m.Failed} pending={m.PendingTo.Count}");
                    }
                    break;
                }
                case "room":
                {
                    // 접속한 동료 모두와 그룹 대화방을 만들고 (이미 있으면 그 방) 메시지 보내기
                    var r = core.ActiveRooms.FirstOrDefault() ?? core.CreateRoom(await WaitPeers(core, 1));
                    core.OpenRoom(r);
                    if (arg.Length > 0)
                    {
                        var m = await core.SendRoom(r, arg, null, false);
                        Log($"room sent members={r.Count} secure={m.Secure} failed={m.Failed} pending={m.PendingTo.Count} meta={m.Meta}");
                    }
                    break;
                }
                case "openroom":
                    core.OpenRoom(await WaitRoom(core));
                    break;
                case "type":
                {
                    // 그룹 방(없으면 1:1)에 '입력 중' 알림을 arg ms 동안 보냄
                    var r = core.ActiveRooms.FirstOrDefault();
                    var key = r != null ? Typists.RoomKey(r.Id) : (await WaitPeer(core)).Id;
                    var until = DateTime.Now.AddMilliseconds(int.TryParse(arg, out var ms) ? ms : 4000);
                    while (DateTime.Now < until) { core.NotifyTyping(key, true); await Task.Delay(500); }
                    core.NotifyTyping(key, false);
                    break;
                }
                case "invite":
                {
                    var r = await WaitRoom(core);
                    var add = core.Peers.Where(p => p.IsOnline && r.Members.All(m => m.Id != p.Id)).ToList();
                    await core.InviteToRoom(r, add);
                    Log($"invited {add.Count} → members={r.Count}");
                    break;
                }
                case "leave":
                    await core.LeaveRoom(await WaitRoom(core));
                    break;
                case "perf":
                {
                    // 대화 창 여는 시간 (처음 · 닫았다가 다시)
                    var p = await WaitPeer(core);
                    for (int k = 0; k < 3; k++)
                    {
                        var sw = Stopwatch.StartNew();
                        var w = core.OpenChat(p);
                        var t1 = sw.ElapsedMilliseconds;
                        await Task.Delay(800);
                        Log($"perf open#{k + 1} {t1}ms");
                        w?.Close();
                        await Task.Delay(300);
                    }
                    break;
                }
                case "dump":
                {
                    foreach (var r in core.Settings.Rooms)
                        Log($"room {Short(r.Id)} left={r.Left} title={r.Title} members={string.Join(",", r.Members.Select(m => m.Name))}");
                    foreach (var m in core.History.Where(x => x.Room != null).Take(12))
                        Log($"  msg {(m.Outgoing ? "→" : "←")} {m.FromName}: {m.Text} sys={m.Sys} read={m.Read} meta={m.Meta}");
                    break;
                }
#if MAC
                case "note":
                {
                    var p = await WaitPeer(core);
                    var m = await core.Send(new[] { p }, arg, null, false, false);
                    Log($"note sent failed={m.Failed}");
                    break;
                }
                case "file":
                {
                    var p = await WaitPeer(core);
                    var m = await core.Send(new[] { p }, "", new List<string> { arg }, false, false);
                    Log($"file sent failed={m.Failed}");
                    break;
                }
                case "compose":
                    core.Compose((await WaitPeer(core)) is Peer cp ? new[] { cp } : null);
                    break;
                case "settings":
                    new SettingsWindow(core).Show();
                    break;
                case "notice":
                    core.ShowNotice("안내 테스트", "**굵게** 와 링크 https://example.com 가 있는 안내입니다.", "확인", null);
                    break;
                case "update":
                    new UpdateWindow(core, AppInfo.VersionText, "- 첫째 변경\n- 둘째 변경").Show();
                    break;
#endif
#if !MAC
                case "chats":
                {
                    // 메인 창의 대화함 탭
                    var mw = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (mw != null) { mw.Show(); mw.TabChats.IsChecked = true; }
                    break;
                }
#endif
                case "wait":
                    await Task.Delay(int.TryParse(arg, out var w2) ? w2 : 2000);
                    break;
            }
            Log("ok " + step);
        }
    }
}
