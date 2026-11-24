using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace BluePhone
{
    /// <summary>
    /// 개발용 화면 점검 (BLUEPHONE_UITEST, --data 로 따로 띄운 경우에만).
    /// 예) BLUEPHONE_UITEST="chat:안녕하세요;compose;settings;notice;update"
    /// </summary>
    static class UiTest
    {
        public static void Run(Core core)
        {
            var spec = Environment.GetEnvironmentVariable("BLUEPHONE_UITEST");
            if (string.IsNullOrWhiteSpace(spec) || !Store.CustomDir) return;
            Dispatcher.UIThread.Post(async () =>
            {
                foreach (var step in spec.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    try { await Step(core, step); }
                    catch (Exception ex) { Log($"{step}: {ex}"); }
                    await Task.Delay(1500);
                }
                Log("done");
            }, DispatcherPriority.ApplicationIdle);
        }

        static void Log(string s)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Dir, "uitest.log"), DateTime.Now.ToString("HH:mm:ss ") + s + "\n"); } catch { }
        }

        static async Task<Peer> WaitPeer(Core core)
        {
            for (int i = 0; i < 60; i++)
            {
                var p = core.Peers.FirstOrDefault(x => x.IsOnline);
                if (p != null) return p;
                await Task.Delay(500);
            }
            throw new TimeoutException("접속한 동료 없음");
        }

        static async Task Step(Core core, string step)
        {
            var i = step.IndexOf(':');
            var cmd = i < 0 ? step : step.Substring(0, i);
            var arg = i < 0 ? "" : step.Substring(i + 1);
            switch (cmd)
            {
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
                    var m = await core.Send(new[] { p }, "", new System.Collections.Generic.List<string> { arg }, false, false);
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
                case "wait":
                    await Task.Delay(int.TryParse(arg, out var ms) ? ms : 2000);
                    break;
            }
            Log("ok " + step);
        }
    }
}
