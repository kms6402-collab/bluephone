using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BluePhone
{
    /// <summary>텔레그램 연결 방법 설정</summary>
    public sealed class TgNetConfig
    {
        /// <summary>auto · system · direct · http · socks5</summary>
        public string Mode = "auto";
        /// <summary>프록시 "주소:포트"</summary>
        public string Proxy = "";
        public string User = "";
        public string Pass = "";
        /// <summary>API 서버 주소 (비우면 https://api.telegram.org). 중계 서버를 쓸 때</summary>
        public string Api = "";

        public static TgNetConfig From(AppSettings s) => new TgNetConfig
        {
            Mode = string.IsNullOrWhiteSpace(s.TelegramNet) ? "auto" : s.TelegramNet,
            Proxy = s.TelegramProxy ?? "",
            User = s.TelegramProxyUser ?? "",
            Pass = Secret.Unprotect(s.TelegramProxyPass),
            Api = s.TelegramApi ?? "",
        };
    }

    /// <summary>
    /// 텔레그램 Bot API 통신.
    ///  · 요청마다 새 연결 (쉬고 있던 연결을 서버가 끊어 생기는 'KeepAliveFailure' 방지)
    ///  · 자동: Windows 기본 방식(시스템 프록시) → 직접 연결(주소마다 시도) 순서로 시도하고 성공한 방법을 기억
    ///  · 외부 프록시: HTTP(CONNECT) · SOCKS5 (사용자 인증 포함)
    ///  · API 서버 주소를 바꿔 중계 서버를 쓸 수 있음
    /// </summary>
    public static class TgNet
    {
        public static TgNetConfig Config = new TgNetConfig();
        static string _good;                 // 마지막으로 성공한 방법
        public static string LastRoute { get; private set; } = "";

        public const string DefaultApi = "https://api.telegram.org";

        public static string ApiBase
        {
            get
            {
                var v = Environment.GetEnvironmentVariable("BLUEPHONE_TELEGRAM_API");
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim().TrimEnd('/');
                var c = (Config.Api ?? "").Trim().TrimEnd('/');
                if (c.Length == 0) return DefaultApi;
                if (!c.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) c = "https://" + c;
                return c;
            }
        }

        public static string RouteLabel(string r) => r switch
        {
#if MAC
            "system" => "macOS 기본(시스템 프록시)",
#else
            "system" => "Windows 기본(시스템 프록시)",
#endif
            "direct" => "직접 연결",
            "http" => "HTTP 프록시",
            "socks5" => "SOCKS5 프록시",
            _ => r,
        };

        static IEnumerable<string> Routes(TgNetConfig c)
        {
            switch (c.Mode)
            {
                case "system": return new[] { "system" };
                case "direct": return new[] { "direct" };
                case "http": return new[] { "http" };
                case "socks5": return new[] { "socks5" };
                default:
                    var list = new List<string> { "system", "direct" };
                    if (_good != null && list.Remove(_good)) list.Insert(0, _good);
                    return list;
            }
        }

        public sealed class Response
        {
            public int Status;
            public byte[] Body;
            public string Text => Encoding.UTF8.GetString(Body ?? new byte[0]);
        }

        /// <summary>요청 보내기. 연결 방법을 차례로 시도 (HTTP 응답을 받으면 상태 코드와 관계없이 성공)</summary>
        public static async Task<Response> Send(string method, string url, string contentType, long length, Func<Stream, Task> write,
                                                int timeoutMs, CancellationToken ct, TgNetConfig cfg = null, string onlyRoute = null)
        {
            cfg = cfg ?? Config;
            var uri = new Uri(url);
            Exception last = null;
            foreach (var route in onlyRoute != null ? new[] { onlyRoute } : Routes(cfg))
            {
                // 순간적인 끊김(연결 · 보안 연결 도중 끊김)은 같은 방법으로 한 번 더
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var r = route == "system"
                            ? await Web(uri, method, contentType, length, write, timeoutMs, ct).ConfigureAwait(false)
                            : await Raw(uri, method, contentType, length, write, route, cfg, timeoutMs, ct).ConfigureAwait(false);
                        if (onlyRoute == null) { _good = route; LastRoute = route; }
                        return r;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        bool transient = !(ex is TimeoutException) && !(ex.Message ?? "").Contains("인증에 실패") && !(ex.Message ?? "").Contains("형식으로");
                        if (!transient) break;
                        await Task.Delay(300, ct).ConfigureAwait(false);
                    }
                }
            }
            throw last ?? new IOException("연결할 방법이 없습니다.");
        }

        // ───────────── 1) Windows 기본 (HttpWebRequest · 시스템 프록시) ─────────────

        static async Task<Response> Web(Uri uri, string method, string contentType, long length, Func<Stream, Task> write, int timeoutMs, CancellationToken ct)
        {
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = method;
            req.KeepAlive = false;   // 쉬던 연결 재사용 금지
            req.UserAgent = "BluePhone/" + AppInfo.VersionText;
            if (contentType != null) req.ContentType = contentType;
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                proxy.Credentials = CredentialCache.DefaultCredentials;
                req.Proxy = proxy;
            }
            catch { }
            if (write != null) { req.AllowWriteStreamBuffering = false; req.ContentLength = length; }

            using (var lim = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                lim.CancelAfter(timeoutMs);
                using (lim.Token.Register(() => { try { req.Abort(); } catch { } }))
                {
                    try
                    {
                        if (write != null)
                            using (var s = await req.GetRequestStreamAsync().ConfigureAwait(false)) await write(s).ConfigureAwait(false);
                        using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                            return new Response { Status = (int)resp.StatusCode, Body = await ReadAll(resp.GetResponseStream()).ConfigureAwait(false) };
                    }
                    catch (WebException ex) when (ex.Response is HttpWebResponse hr)
                    {
                        using (hr) return new Response { Status = (int)hr.StatusCode, Body = await ReadAll(hr.GetResponseStream()).ConfigureAwait(false) };
                    }
                    catch (WebException) when (lim.IsCancellationRequested)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        throw new TimeoutException("응답 시간 초과");
                    }
                }
            }
        }

        static async Task<byte[]> ReadAll(Stream s)
        {
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms).ConfigureAwait(false);
                return ms.ToArray();
            }
        }

        // ───────────── 2) 직접 소켓 (직접 연결 · HTTP 프록시 · SOCKS5) ─────────────

        static async Task<Response> Raw(Uri uri, string method, string contentType, long length, Func<Stream, Task> write,
                                        string route, TgNetConfig cfg, int timeoutMs, CancellationToken ct)
        {
            using (var lim = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                lim.CancelAfter(timeoutMs);
                TcpClient tcp = null;
                try
                {
                    int port = uri.Port;
                    if (route == "direct") tcp = await ConnectAny(uri.Host, port, lim.Token).ConfigureAwait(false);
                    else
                    {
                        if (!SplitHostPort(cfg.Proxy, out var ph, out var pp)) throw new IOException("프록시 주소를 '주소:포트' 형식으로 입력해 주세요.");
                        tcp = await ConnectAny(ph, pp, lim.Token).ConfigureAwait(false);
                    }
                    using (lim.Token.Register(() => { try { tcp.Close(); } catch { } }))
                    {
                        Stream s = tcp.GetStream();
                        if (route == "http") await HttpConnect(s, uri.Host, port, cfg).ConfigureAwait(false);
                        else if (route == "socks5") await Socks5(s, uri.Host, port, cfg).ConfigureAwait(false);

                        if (uri.Scheme == Uri.UriSchemeHttps)
                        {
                            var ssl = new SslStream(s, false);
                            await ssl.AuthenticateAsClientAsync(uri.Host, null, SslProtocols.None, true).ConfigureAwait(false);   // 버전은 Windows 가 고름 (TLS 1.2/1.3)
                            s = ssl;
                        }

                        var head = new StringBuilder();
                        head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
                        head.Append("Host: ").Append(uri.Host).Append(uri.IsDefaultPort ? "" : ":" + port).Append("\r\n");
                        head.Append("User-Agent: BluePhone/").Append(AppInfo.VersionText).Append("\r\n");
                        head.Append("Accept: */*\r\nConnection: close\r\n");
                        if (write != null)
                        {
                            if (contentType != null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
                            head.Append("Content-Length: ").Append(length).Append("\r\n");
                        }
                        head.Append("\r\n");
                        var hb = Encoding.ASCII.GetBytes(head.ToString());
                        await s.WriteAsync(hb, 0, hb.Length).ConfigureAwait(false);
                        if (write != null) await write(s).ConfigureAwait(false);
                        await s.FlushAsync().ConfigureAwait(false);
                        return await ReadResponse(s).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (lim.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException("응답 시간 초과");
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                finally { try { tcp?.Close(); } catch { } }
            }
        }

        /// <summary>이름의 모든 주소(IPv4 먼저)로 차례로 연결 시도</summary>
        static async Task<TcpClient> ConnectAny(string host, int port, CancellationToken ct)
        {
            IPAddress[] addrs;
            if (IPAddress.TryParse(host, out var ip)) addrs = new[] { ip };
            else
            {
                try { addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false); }
                catch (Exception ex) { throw new IOException($"'{host}' 주소를 찾을 수 없습니다 (DNS). {ex.Message}"); }
            }
            Exception last = null;
            foreach (var a in addrs.OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
            {
                ct.ThrowIfCancellationRequested();
                var tcp = new TcpClient(a.AddressFamily) { NoDelay = true };
                try
                {
                    var connect = tcp.ConnectAsync(a, port);
                    var done = await Task.WhenAny(connect, Task.Delay(8000, ct)).ConfigureAwait(false);
                    if (done != connect) { tcp.Close(); last = new TimeoutException($"{a}:{port} 연결 시간 초과"); continue; }
                    await connect.ConfigureAwait(false);
                    return tcp;
                }
                catch (Exception ex) { try { tcp.Close(); } catch { } last = ex; }
            }
            ct.ThrowIfCancellationRequested();
            throw new IOException($"{host}:{port} 에 연결할 수 없습니다. ({last?.Message})", last);
        }

        static bool SplitHostPort(string text, out string host, out int port)
        {
            text = (text ?? "").Trim();
            foreach (var pre in new[] { "http://", "https://", "socks5://", "socks://" })
                if (text.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) text = text.Substring(pre.Length);
            text = text.TrimEnd('/');
            host = text; port = 0;
            var i = text.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(text.Substring(i + 1), out port) || port < 1 || port > 65535) return false;
            host = text.Substring(0, i).Trim('[', ']');
            return host.Length > 0;
        }

        static async Task HttpConnect(Stream s, string host, int port, TgNetConfig cfg)
        {
            var sb = new StringBuilder();
            sb.Append("CONNECT ").Append(host).Append(':').Append(port).Append(" HTTP/1.1\r\nHost: ").Append(host).Append(':').Append(port).Append("\r\n");
            if (!string.IsNullOrEmpty(cfg.User))
                sb.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.User + ":" + cfg.Pass))).Append("\r\n");
            sb.Append("\r\n");
            var b = Encoding.ASCII.GetBytes(sb.ToString());
            await s.WriteAsync(b, 0, b.Length).ConfigureAwait(false);
            var head = await ReadHead(s).ConfigureAwait(false);
            var line = head.Split('\n')[0].Trim();
            var parts = line.Split(' ');
            if (parts.Length < 2 || parts[1] != "200")
            {
                if (parts.Length >= 2 && parts[1] == "407") throw new IOException("HTTP 프록시 인증에 실패했습니다. 사용자 · 비밀번호를 확인해 주세요.");
                throw new IOException("HTTP 프록시가 연결을 거부했습니다: " + line);
            }
        }

        static async Task Socks5(Stream s, string host, int port, TgNetConfig cfg)
        {
            bool auth = !string.IsNullOrEmpty(cfg.User);
            var hello = auth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 };
            await s.WriteAsync(hello, 0, hello.Length).ConfigureAwait(false);
            var r = await ReadExact(s, 2).ConfigureAwait(false);
            if (r[0] != 5) throw new IOException("SOCKS5 프록시가 아닙니다.");
            if (r[1] == 2)
            {
                var u = Encoding.UTF8.GetBytes(cfg.User ?? ""); var p = Encoding.UTF8.GetBytes(cfg.Pass ?? "");
                var a = new List<byte> { 1, (byte)u.Length }; a.AddRange(u); a.Add((byte)p.Length); a.AddRange(p);
                await s.WriteAsync(a.ToArray(), 0, a.Count).ConfigureAwait(false);
                var ar = await ReadExact(s, 2).ConfigureAwait(false);
                if (ar[1] != 0) throw new IOException("SOCKS5 프록시 인증에 실패했습니다. 사용자 · 비밀번호를 확인해 주세요.");
            }
            else if (r[1] != 0) throw new IOException(auth ? "SOCKS5 프록시가 이 인증 방식을 지원하지 않습니다." : "SOCKS5 프록시에 사용자 · 비밀번호가 필요합니다.");

            // 주소는 이름 그대로 넘겨 프록시가 찾게 함 (내 PC의 DNS 가 막혀 있어도 됨)
            var hb = Encoding.ASCII.GetBytes(host);
            var req = new List<byte> { 5, 1, 0, 3, (byte)hb.Length }; req.AddRange(hb); req.Add((byte)(port >> 8)); req.Add((byte)(port & 0xFF));
            await s.WriteAsync(req.ToArray(), 0, req.Count).ConfigureAwait(false);
            var rep = await ReadExact(s, 4).ConfigureAwait(false);
            if (rep[1] != 0)
            {
                string why = rep[1] switch { 2 => "규칙상 허용되지 않음", 3 => "네트워크에 닿을 수 없음", 4 => "호스트에 닿을 수 없음", 5 => "연결 거부", _ => "오류 " + rep[1] };
                throw new IOException("SOCKS5 프록시가 텔레그램에 연결하지 못했습니다: " + why);
            }
            int skip = rep[3] == 1 ? 4 : rep[3] == 4 ? 16 : (await ReadExact(s, 1).ConfigureAwait(false))[0];
            await ReadExact(s, skip + 2).ConfigureAwait(false);
        }

        static async Task<byte[]> ReadExact(Stream s, int n)
        {
            var b = new byte[n]; int got = 0;
            while (got < n)
            {
                int k = await s.ReadAsync(b, got, n - got).ConfigureAwait(false);
                if (k <= 0) throw new IOException("상대가 연결을 끊었습니다.");
                got += k;
            }
            return b;
        }

        /// <summary>헤더 끝(빈 줄)까지 한 바이트씩 읽음 (그 뒤의 본문은 건드리지 않음)</summary>
        static async Task<string> ReadHead(Stream s)
        {
            var ms = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                int k = await s.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (k <= 0) throw new IOException("응답 도중 연결이 끊겼습니다.");
                ms.WriteByte(one[0]);
                var len = ms.Length;
                if (len >= 4)
                {
                    var buf = ms.GetBuffer();
                    if (buf[len - 4] == '\r' && buf[len - 3] == '\n' && buf[len - 2] == '\r' && buf[len - 1] == '\n') break;
                }
                if (len > 64 * 1024) throw new IOException("응답 헤더가 너무 깁니다.");
            }
            return Encoding.ASCII.GetString(ms.ToArray());
        }

        static async Task<Response> ReadResponse(Stream s)
        {
            var head = await ReadHead(s).ConfigureAwait(false);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var st = lines[0].Split(' ');
            if (st.Length < 2 || !int.TryParse(st[1], out int status)) throw new IOException("알 수 없는 응답: " + lines[0]);
            long contentLength = -1; bool chunked = false;
            foreach (var l in lines.Skip(1))
            {
                var i = l.IndexOf(':'); if (i <= 0) continue;
                var k = l.Substring(0, i).Trim(); var v = l.Substring(i + 1).Trim();
                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) long.TryParse(v, out contentLength);
                else if (k.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && v.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) chunked = true;
            }
            var body = new MemoryStream();
            if (chunked)
            {
                while (true)
                {
                    var sizeLine = await ReadLine(s).ConfigureAwait(false);
                    var semi = sizeLine.IndexOf(';'); if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                    int size = Convert.ToInt32(sizeLine.Trim(), 16);
                    if (size == 0) { await ReadLine(s).ConfigureAwait(false); break; }
                    var chunk = await ReadExact(s, size).ConfigureAwait(false);
                    body.Write(chunk, 0, chunk.Length);
                    await ReadLine(s).ConfigureAwait(false);
                }
            }
            else if (contentLength >= 0)
            {
                var b = await ReadExact(s, (int)contentLength).ConfigureAwait(false);
                body.Write(b, 0, b.Length);
            }
            else
            {
                var buf = new byte[16384]; int k;
                while ((k = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0) body.Write(buf, 0, k);
            }
            return new Response { Status = status, Body = body.ToArray() };
        }

        static async Task<string> ReadLine(Stream s)
        {
            var sb = new StringBuilder(); var one = new byte[1];
            while (true)
            {
                int k = await s.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (k <= 0) throw new IOException("응답 도중 연결이 끊겼습니다.");
                if (one[0] == '\n') break;
                if (one[0] != '\r') sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        // ───────────── 연결 진단 ─────────────

        /// <summary>각 연결 방법으로 텔레그램 서버에 닿는지 시험 (토큰 없이: 401/404 응답도 '닿음')</summary>
        public static async Task<string> Diagnose(TgNetConfig cfg, CancellationToken ct)
        {
            var url = (cfg.Api ?? "").Trim().Length > 0 && Environment.GetEnvironmentVariable("BLUEPHONE_TELEGRAM_API") == null
                ? NormalizeApi(cfg.Api) : ApiBase;
            url += "/bot0:diagnose/getMe";
            var routes = new List<string> { "system", "direct" };
            if (!string.IsNullOrWhiteSpace(cfg.Proxy)) { if (cfg.Mode == "socks5") routes.Add("socks5"); else if (cfg.Mode == "http") routes.Add("http"); else { routes.Add("http"); routes.Add("socks5"); } }
            var sb = new StringBuilder($"연결 진단 — {new Uri(url).Host}\n");
            var tasks = routes.Select(async r =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var resp = await Send("GET", url, null, 0, null, 12000, ct, cfg, r).ConfigureAwait(false);
                    return (r, ok: true, msg: $"성공 ({sw.ElapsedMilliseconds}ms)");
                }
                catch (Exception ex) { return (r, ok: false, msg: "실패 — " + Short(ex)); }
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var x in results) sb.Append(x.ok ? "✅ " : "❌ ").Append(RouteLabel(x.r)).Append(": ").Append(x.msg).Append('\n');
            if (results.All(x => !x.ok))
                sb.Append("\n모든 방법이 막혀 있습니다. 외부 프록시(HTTP · SOCKS5)를 지정하거나, 중계 서버 주소를 넣어 보세요.");
            else if (cfg.Mode != "auto" && !results.Any(x => x.ok && x.r == cfg.Mode))
                sb.Append("\n지금 고른 방법은 실패했습니다. 성공한 방법으로 바꾸거나 '자동'을 고르세요.");
            return sb.ToString().TrimEnd();
        }

        static string NormalizeApi(string c)
        {
            c = c.Trim().TrimEnd('/');
            if (!c.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) c = "https://" + c;
            return c;
        }

        public static string Short(Exception ex)
        {
            while (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
            string m = ex is WebException w && w.Response == null ? $"{w.Status} ({w.Message})" : ex.Message;
            if (ex.InnerException != null && !(ex is WebException) && ex.InnerException.Message != ex.Message) m += " / " + ex.InnerException.Message;
            m = m ?? "";
            return m.Length > 140 ? m.Substring(0, 140) + "…" : m;
        }
    }

    /// <summary>비밀번호 저장 (Windows 계정으로 암호화 — 이 PC · 이 계정에서만 풀 수 있음)</summary>
    public static class Secret
    {
#if MAC
        // macOS: 이 사용자만 읽을 수 있는 키 파일(데이터 폴더 · 권한 600)로 AES 암호화
        static byte[] Key()
        {
            var path = Path.Combine(Store.Dir, ".key");
            try
            {
                if (File.Exists(path)) { var k = File.ReadAllBytes(path); if (k.Length == 32) return k; }
                var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(path, key);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return key;
            }
            catch { return null; }
        }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var key = Key(); if (key == null) return "";
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key; aes.GenerateIV();
                var ct = aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), aes.IV);
                return "mk:" + Convert.ToBase64String(aes.IV.Concat(ct).ToArray());
            }
            catch { return ""; }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("mk:")) return "";
            try
            {
                var key = Key(); if (key == null) return "";
                var b = Convert.FromBase64String(stored.Substring(3));
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key = key;
                return Encoding.UTF8.GetString(aes.DecryptCbc(b.AsSpan(16).ToArray(), b.AsSpan(0, 16).ToArray()));
            }
            catch { return ""; }
        }
#else
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var b = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return "dp:" + Convert.ToBase64String(b);
            }
            catch { return ""; }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dp:")) return "";
            try
            {
                var b = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(3)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
#endif
    }
}
