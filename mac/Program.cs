using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;

namespace BluePhone
{
    using Packet = Dictionary<string, object>;

    public static class Program
    {
        public static string[] Args { get; private set; } = new string[0];
        static FileStream _lock;

        [STAThread]
        public static int Main(string[] args)
        {
            Args = args;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject?.ToString());
            var ko = new CultureInfo("ko-KR");
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = ko;
            Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = ko;

            if (Has("--selftest")) return SelfTest();

            // --data <폴더> : 설정 · 기록을 다른 폴더에 두고 독립 실행
            var di = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
            if (di >= 0 && di + 1 < args.Length) Store.UseDir(args[di + 1]);

            // 단일 실행: 이미 떠 있으면 기존 창을 앞으로 부르고 종료
            if (!AcquireLock())
            {
                try { File.WriteAllText(Path.Combine(Store.Dir, "show.signal"), DateTime.Now.Ticks.ToString()); } catch { }
                return 0;
            }

            TaskScheduler.UnobservedTaskException += (s, e) => { Log(e.Exception?.ToString()); e.SetObserved(); };

            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception ex) { Log(ex.ToString()); throw; }
            finally
            {
                App.Core?.Stop();
                try { _lock?.Dispose(); } catch { }
            }
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = OperatingSystem.IsMacOS() ? "Apple SD Gothic Neo" : null })
            .With(new MacOSPlatformOptions { ShowInDock = true, DisableDefaultApplicationMenuItems = false })
            .LogToTrace();

        public static bool Has(string flag) => Args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        static bool AcquireLock()
        {
            try { Directory.CreateDirectory(Store.Dir); } catch { }
            var path = Path.Combine(Store.Dir, "instance.lock");
            for (int i = 0; i < (Has("--restart") ? 40 : 1); i++)
            {
                try
                {
                    // macOS/Linux 의 .NET 은 FileShare.None 을 flock(배타 잠금)으로 처리한다.
                    // (FileStream.Lock 은 macOS 에서 지원되지 않아 예외가 나므로 쓰지 않음)
                    _lock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _lock?.Dispose(); _lock = null;
                    Thread.Sleep(200);
                }
            }
            return false;
        }

        public static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(Store.Dir, "error.log"), DateTime.Now + "\n" + text + "\n\n"); } catch { }
        }

        /// <summary>
        /// 설치 파일 검증용: 암호 연결(ECDH · AES · HMAC) · JSON · 쪽지 주고받기 · 파일 전송을 이 컴퓨터 안에서 확인
        /// </summary>
        static int SelfTest()
        {
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "bluephone-selftest-" + Guid.NewGuid().ToString("N"));
                Store.UseDir(tmp);
                var json = Store.Json.Serialize(new Packet { ["a"] = 1, ["b"] = "한글 \"따옴표\"", ["c"] = new List<object> { 1L << 40, 1.5, true, null } });
                var back = Store.Json.Deserialize<Packet>(json);
                if (NetService.Str(back, "b") != "한글 \"따옴표\"" || NetService.Long(back, "a") != 1) throw new Exception("JSON 실패: " + json);
                var s = new AppSettings { Name = "테스트" };
                var s2 = Store.Json.Deserialize<AppSettings>(Store.Json.Serialize(s));
                if (s2.Name != "테스트" || s2.Port != 9631) throw new Exception("설정 JSON 실패");

                int port1 = 39000 + Random.Shared.Next(2000), port2 = port1 + 1;
                using var a = new NetService();
                using var b = new NetService();
                a.HelloFactory = () => new Packet { ["app"] = NetService.AppTag, ["id"] = "A", ["port"] = port1 };
                b.HelloFactory = () => new Packet { ["app"] = NetService.AppTag, ["id"] = "B", ["port"] = port2 };
                a.Start(port1); b.Start(port2);
                var got = new TaskCompletionSource<Packet>();
                b.MessageReceived += (p, ip) => got.TrySetResult(p);
                var r = a.Request("127.0.0.1", port2, new Packet { ["t"] = "msg", ["id"] = "m1", ["from"] = "A", ["text"] = "안녕하세요" }).GetAwaiter().GetResult();
                if (!NetService.Bool(r, "ok")) throw new Exception("쪽지 응답 없음");
                if (!NetService.Bool(r, "_secure")) throw new Exception("암호 연결이 아님");
                if (!got.Task.Wait(3000) || NetService.Str(got.Task.Result, "text") != "안녕하세요") throw new Exception("쪽지 받기 실패");

                var src = Path.Combine(tmp, "src.bin");
                var data = new byte[300_000];
                Random.Shared.NextBytes(data);
                File.WriteAllBytes(src, data);
                var token = b.Offer(src);
                var dst = Path.Combine(tmp, "dst.bin");
                a.Download("127.0.0.1", port2, token, dst, null, CancellationToken.None).GetAwaiter().GetResult();
                if (!File.ReadAllBytes(dst).SequenceEqual(data)) throw new Exception("파일 전송 내용이 다름");

                var enc = Secret.Protect("비밀");
                if (Secret.Unprotect(enc) != "비밀") throw new Exception("비밀 저장 실패");

                try { Directory.Delete(tmp, true); } catch { }
                Console.WriteLine($"selftest ok ({AppInfo.VersionText}, {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest failed: " + ex);
                return 1;
            }
        }
    }
}
