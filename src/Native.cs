using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace BluePhone
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static TimeSpan IdleTime()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        public static void Flash(Window w, bool untilFocus)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                hwnd = h,
                dwFlags = untilFocus ? 3u | 12u : 3u,   // ALL | TIMERNOFG
                uCount = untilFocus ? uint.MaxValue : 3,
            };
            FlashWindowEx(ref fi);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Windows 11 둥근 모서리 + 시스템 그림자</summary>
        public static void RoundCorners(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(h, 33, ref pref, 4);
            }
            catch { }
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        public static void BringToFront(Window w)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            var top = w.Topmost;
            w.Topmost = true; w.Topmost = top;
            SetForegroundWindow(new WindowInteropHelper(w).Handle);
        }

        // ─────────────── 실행 파일 정보 (파일명을 바꿔도 동작) ───────────────

        public static string ExePath => Process.GetCurrentProcess().MainModule.FileName;
        public static string ExeName => Path.GetFileNameWithoutExtension(ExePath);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "BluePhone";

        public static void SetAutoStart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue(RunValue, $"\"{ExePath}\" --tray");
                else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue, false);
            }
            catch { }
        }

        // ─────────────── 아이콘 ───────────────

        public static Icon LoadIcon(int size)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            return new Icon(s, size, size);
        }

        /// <summary>안 읽은 쪽지가 있을 때 트레이 아이콘 우하단에 점 표시</summary>
        public static Icon IconWithDot(int size)
        {
            using var baseIcon = LoadIcon(size);
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));
                float d = size * 0.5f;
                var r = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                using var ring = new SolidBrush(Color.White);
                g.FillEllipse(ring, RectangleF.Inflate(r, size * 0.06f, size * 0.06f));
                using var dot = new SolidBrush(Color.FromArgb(255, 240, 84, 56));
                g.FillEllipse(dot, r);
            }
            var h = bmp.GetHicon();
            return (Icon)Icon.FromHandle(h).Clone();
        }

        // ─────────────── 알림음 (메모리에서 합성) ───────────────

        static byte[] _chime, _urgent;

        public static void PlayChime(bool urgent)
        {
            try
            {
                var data = urgent ? (_urgent ??= Synth(new[] { (988.0, 0.11), (0, 0.04), (988.0, 0.11), (0, 0.04), (1319.0, 0.22) })) :
                                    (_chime ??= Synth(new[] { (1174.7, 0.09), (1568.0, 0.26) }));
                var p = new SoundPlayer(new MemoryStream(data));
                p.Play();
            }
            catch { SystemSounds.Asterisk.Play(); }
        }

        static byte[] Synth((double hz, double sec)[] notes)
        {
            const int rate = 44100;
            var ms = new MemoryStream();
            var samples = new System.Collections.Generic.List<short>();
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
            using var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true);
            int dataLen = samples.Count * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + dataLen); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(dataLen);
            foreach (var s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
}
