using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace BluePhone
{
    /// <summary>
    /// macOS 기능 (유휴 시간 · 알림음 · 로그인 시 실행 · Dock 배지/튕기기 · 전역 단축키 · 화면 캡처 · 파일 열기).
    /// 개발 중 Windows 에서 실행해도 죽지 않도록 각 기능은 OS 를 확인하고 실패를 삼킨다.
    /// </summary>
    public static class Native
    {
        public static bool IsMac => OperatingSystem.IsMacOS();

        // ───────────── 실행 파일 · 앱 번들 ─────────────

        public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

        /// <summary>…/파란전화기.app (번들에서 실행 중이 아니면 null)</summary>
        public static string BundlePath
        {
            get
            {
                var d = Path.GetDirectoryName(ExePath);
                while (!string.IsNullOrEmpty(d))
                {
                    if (d.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return d;
                    d = Path.GetDirectoryName(d);
                }
                return null;
            }
        }

        // ───────────── 유휴 시간 (자동 자리 비움) ─────────────

        const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(CoreGraphics)] static extern double CGEventSourceSecondsSinceLastEventType(int source, uint eventType);

        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static TimeSpan IdleTime()
        {
            try
            {
                if (IsMac) return TimeSpan.FromSeconds(Math.Max(0, CGEventSourceSecondsSinceLastEventType(0 /* combined session */, 0xFFFFFFFF /* any input */)));
                if (OperatingSystem.IsWindows())
                {
                    var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                    if (GetLastInputInfo(ref li)) return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
                }
            }
            catch { }
            return TimeSpan.Zero;
        }

        // ───────────── 파일 · 주소 열기 ─────────────

        static void Run(string file, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            catch { }
        }

        /// <summary>기본 프로그램으로 열기 (파일 · 폴더 · 주소)</summary>
        public static void Open(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            if (IsMac) Run("/usr/bin/open", target);
            else try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Finder 에서 파일 위치 보기</summary>
        public static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (IsMac) { if (File.Exists(path)) Run("/usr/bin/open", "-R", path); else Run("/usr/bin/open", path); }
            else if (File.Exists(path)) Run("explorer.exe", $"/select,\"{path}\"");
            else Open(path);
        }

        // ───────────── 로그인 시 실행 (LaunchAgent) ─────────────

        const string AgentLabel = "com.bluephone.messenger";
        static string AgentPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", AgentLabel + ".plist");

        public static void SetAutoStart(bool on)
        {
            if (!IsMac) return;
            try
            {
                if (!on) { if (File.Exists(AgentPath)) File.Delete(AgentPath); return; }
                var bundle = BundlePath;
                var args = bundle != null
                    ? new[] { "/usr/bin/open", "-a", bundle, "--args", "--tray" }
                    : new[] { ExePath, "--tray" };
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
                sb.Append("<plist version=\"1.0\">\n<dict>\n  <key>Label</key>\n  <string>").Append(AgentLabel).Append("</string>\n  <key>ProgramArguments</key>\n  <array>\n");
                foreach (var a in args) sb.Append("    <string>").Append(System.Security.SecurityElement.Escape(a)).Append("</string>\n");
                sb.Append("  </array>\n  <key>RunAtLoad</key>\n  <true/>\n  <key>ProcessType</key>\n  <string>Interactive</string>\n</dict>\n</plist>\n");
                Directory.CreateDirectory(Path.GetDirectoryName(AgentPath));
                File.WriteAllText(AgentPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // ───────────── 알림음 (메모리에서 합성 → afplay) ─────────────

        static string _chime, _urgent;

        public static void PlayChime(bool urgent)
        {
            if (!IsMac) return;
            try
            {
                var path = urgent
                    ? (_urgent ??= WriteWav("urgent", Synth(new[] { (988.0, 0.11), (0, 0.04), (988.0, 0.11), (0, 0.04), (1319.0, 0.22) })))
                    : (_chime ??= WriteWav("chime", Synth(new[] { (1174.7, 0.09), (1568.0, 0.26) })));
                Run("/usr/bin/afplay", path);
            }
            catch { }
        }

        static string WriteWav(string name, byte[] data)
        {
            var dir = Path.Combine(Path.GetTempPath(), "BluePhone");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, name + ".wav");
            File.WriteAllBytes(p, data);
            return p;
        }

        static byte[] Synth((double hz, double sec)[] notes)
        {
            const int rate = 44100;
            var samples = new List<short>();
            foreach (var (hz, sec) in notes)
            {
                int n = (int)(rate * sec);
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Min(1.0, i / (rate * 0.004)) * Math.Exp(-t * 9.0);
                    double v = hz <= 0 ? 0 : (Math.Sin(2 * Math.PI * hz * t) * 0.8 + Math.Sin(4 * Math.PI * hz * t) * 0.15) * env;
                    samples.Add((short)(v * 0.42 * short.MaxValue));
                }
            }
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII, true);
            int dataLen = samples.Count * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + dataLen); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(dataLen);
            foreach (var s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        // ───────────── Cocoa (Dock 배지 · 튕기기 · 앞으로 가져오기) ─────────────

        const string ObjC = "/usr/lib/libobjc.dylib";
        [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr r, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr r, IntPtr sel, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern long MsgLong(IntPtr r, IntPtr sel, long a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void MsgBool(IntPtr r, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);

        static IntPtr NSApp => Msg(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));

        static IntPtr NSString(string s)
        {
            var p = Marshal.StringToCoTaskMemUTF8(s);
            try { return Msg(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        /// <summary>Dock 아이콘의 빨간 숫자 (0 이면 지움)</summary>
        public static void SetBadge(int count)
        {
            if (!IsMac) return;
            try
            {
                var tile = Msg(NSApp, sel_registerName("dockTile"));
                Msg(tile, sel_registerName("setBadgeLabel:"), count > 0 ? NSString(count > 99 ? "99+" : count.ToString()) : IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Dock 아이콘 튕기기 (긴급이면 내가 볼 때까지 계속)</summary>
        public static void Attention(bool critical)
        {
            if (!IsMac) return;
            try { MsgLong(NSApp, sel_registerName("requestUserAttention:"), critical ? 0 : 10); } catch { }
        }

        /// <summary>다른 앱이 앞에 있어도 이 앱을 앞으로</summary>
        public static void ActivateApp()
        {
            if (!IsMac) return;
            try { MsgBool(NSApp, sel_registerName("activateIgnoringOtherApps:"), true); } catch { }
        }

        // ───────────── 화면 캡처 (screencapture -i) ─────────────

        /// <summary>영역을 드래그해 캡처 (Esc 면 null). 처음 한 번은 '화면 기록' 권한을 물어봄</summary>
        public static async Task<string> CaptureAsync()
        {
            if (!IsMac) return null;
            var dir = Path.Combine(Path.GetTempPath(), "BluePhone");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"캡처 {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            try
            {
                var p = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture") { ArgumentList = { "-i", "-x", "-t", "png", path }, UseShellExecute = false });
                if (p == null) return null;
                await p.WaitForExitAsync();
            }
            catch { return null; }
            return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
        }

        // ───────────── 전역 단축키 (Carbon RegisterEventHotKey) ─────────────

        const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
        [StructLayout(LayoutKind.Sequential)] struct EventTypeSpec { public uint eventClass; public uint eventKind; }
        [StructLayout(LayoutKind.Sequential)] struct EventHotKeyID { public uint signature; public uint id; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EventHandlerProc(IntPtr next, IntPtr evt, IntPtr user);
        [DllImport(Carbon)] static extern IntPtr GetApplicationEventTarget();
        [DllImport(Carbon)] static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint numTypes, EventTypeSpec[] list, IntPtr userData, out IntPtr handlerRef);
        [DllImport(Carbon)] static extern int RemoveEventHandler(IntPtr handlerRef);
        [DllImport(Carbon)] static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);
        [DllImport(Carbon)] static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

        static EventHandlerProc _proc;
        static IntPtr _handlerRef, _hotKeyRef;
        static Action _hotkeyAction;

        /// <summary>⌃⌥S (Control+Option+S) — 어디서든 화면 캡처. 등록에 실패하면 false</summary>
        public static bool RegisterCaptureHotkey(Action onPressed)
        {
            UnregisterHotkey();
            if (!IsMac) return false;
            try
            {
                _hotkeyAction = onPressed;
                _proc = (next, evt, user) =>
                {
                    try { Avalonia.Threading.Dispatcher.UIThread.Post(() => _hotkeyAction?.Invoke()); } catch { }
                    return 0;
                };
                var spec = new[] { new EventTypeSpec { eventClass = 0x6B657962 /* 'keyb' */, eventKind = 5 /* kEventHotKeyPressed */ } };
                if (InstallEventHandler(GetApplicationEventTarget(), Marshal.GetFunctionPointerForDelegate(_proc), 1, spec, IntPtr.Zero, out _handlerRef) != 0) return false;
                var id = new EventHotKeyID { signature = 0x424C5048 /* 'BLPH' */, id = 1 };
                // kVK_ANSI_S = 1, controlKey = 0x1000, optionKey = 0x0800
                if (RegisterEventHotKey(1, 0x1000 | 0x0800, id, GetApplicationEventTarget(), 0, out _hotKeyRef) != 0) { UnregisterHotkey(); return false; }
                return true;
            }
            catch { UnregisterHotkey(); return false; }
        }

        public static void UnregisterHotkey()
        {
            try
            {
                if (_hotKeyRef != IntPtr.Zero) UnregisterEventHotKey(_hotKeyRef);
                if (_handlerRef != IntPtr.Zero) RemoveEventHandler(_handlerRef);
            }
            catch { }
            _hotKeyRef = _handlerRef = IntPtr.Zero;
            _hotkeyAction = null;
        }

        /// <summary>상황에 맞는 수정키 이름 (macOS: ⌘, 그 밖: Ctrl)</summary>
        public static string Mod => IsMac ? "⌘" : "Ctrl+";
    }
}
