using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BluePhone
{
    using Packet = Dictionary<string, object>;

    /// <summary>
    /// 서버 없는 LAN 메신저 프로토콜.
    ///  - UDP  : 접속 알림(hello/bye) 브로드캐스트로 동료 자동 검색
    ///  - TCP  : 쪽지, 수신확인, 파일 전송 (4바이트 길이 + UTF-8 JSON 프레임)
    /// </summary>
    public sealed class NetService : IDisposable
    {
        public const string AppTag = "bluephone";
        public const int Proto = 2;

        UdpClient _udp;
        TcpListener _tcp;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly ConcurrentDictionary<string, string> _offers = new ConcurrentDictionary<string, string>();

        public int Port { get; private set; }
        public Func<Packet> HelloFactory { get; set; }
        public Func<IEnumerable<string>> ExtraTargets { get; set; }
        public Func<byte[]> AvatarProvider { get; set; }

        public event Action<Packet, IPAddress> HelloReceived;
        public event Action<string> ByeReceived;
        public event Action<Packet, IPAddress> MessageReceived;
        public event Action<string, string> ReadReceived;   // msgId, fromId

        public void Start(int port)
        {
            Port = port;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udp.EnableBroadcast = true;
            try { _udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }

            _tcp = new TcpListener(IPAddress.Any, port);
            _tcp.Start(64);

            Task.Run(UdpLoop);
            Task.Run(AcceptLoop);
        }

        // ───────────────────────── discovery ─────────────────────────

        public void Announce(bool wantReply)
        {
            var p = HelloFactory();
            p["t"] = "hello";
            p["reply"] = wantReply;
            var data = Encode(p);
            foreach (var ep in BroadcastTargets())
                try { _udp?.Send(data, data.Length, ep); } catch { }
        }

        public void SayBye(string myId)
        {
            var data = Encode(new Packet { ["app"] = AppTag, ["t"] = "bye", ["id"] = myId });
            foreach (var ep in BroadcastTargets())
                try { _udp?.Send(data, data.Length, ep); } catch { }
        }

        IEnumerable<IPEndPoint> BroadcastTargets()
        {
            var set = new HashSet<string> { "255.255.255.255" };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        set.Add(new IPAddress(bc).ToString());
                    }
                }
            }
            catch { }
            var list = set.Select(s => new IPEndPoint(IPAddress.Parse(s), Port)).ToList();
            foreach (var host in ExtraTargets?.Invoke() ?? Enumerable.Empty<string>())
            {
                var ep = ParseHost(host, Port);
                if (ep != null) list.Add(ep);
            }
            return list;
        }

        public static IPEndPoint ParseHost(string text, int defPort)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.StartsWith("#")) return null;
            int port = defPort;
            var idx = text.LastIndexOf(':');
            if (idx > 0 && int.TryParse(text.Substring(idx + 1), out var p)) { port = p; text = text.Substring(0, idx); }
            if (IPAddress.TryParse(text, out var ip)) return new IPEndPoint(ip, port);
            try
            {
                var a = Dns.GetHostAddresses(text).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                return a == null ? null : new IPEndPoint(a, port);
            }
            catch { return null; }
        }

        async Task UdpLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch { continue; }
                try
                {
                    var p = Decode(r.Buffer, 0, r.Buffer.Length);
                    if (p == null || Str(p, "app") != AppTag) continue;
                    switch (Str(p, "t"))
                    {
                        case "hello":
                            HelloReceived?.Invoke(p, r.RemoteEndPoint.Address);
                            if (Bool(p, "reply"))
                            {
                                var me = HelloFactory();
                                if (Str(me, "id") == Str(p, "id")) break;
                                me["t"] = "hello"; me["reply"] = false;
                                var data = Encode(me);
                                var port = Int(p, "port", Port);
                                _udp.Send(data, data.Length, new IPEndPoint(r.RemoteEndPoint.Address, port));
                            }
                            break;
                        case "bye":
                            ByeReceived?.Invoke(Str(p, "id"));
                            break;
                    }
                }
                catch { }
            }
        }

        // ───────────────────────── TCP server ─────────────────────────

        async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _tcp.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch { continue; }
                _ = Task.Run(() => Handle(c));
            }
        }

        async Task Handle(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 15000;
                    var (ch, p) = await Channel.Accept(c.GetStream()).ConfigureAwait(false);
                    if (p == null || Str(p, "app") != AppTag) return;
                    var ip = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
                    p["_secure"] = ch.Secure;
                    var ok = new Packet { ["app"] = AppTag, ["ok"] = true };
                    switch (Str(p, "t"))
                    {
                        case "msg":
                            MessageReceived?.Invoke(p, ip);
                            await ch.SendJson(ok).ConfigureAwait(false);
                            break;
                        case "read":
                            ReadReceived?.Invoke(Str(p, "msg"), Str(p, "id"));
                            await ch.SendJson(ok).ConfigureAwait(false);
                            break;
                        case "get":
                            await ServeFile(ch, Str(p, "token")).ConfigureAwait(false);
                            break;
                        case "avatar":
                            var img = AvatarProvider?.Invoke();
                            await ch.SendJson(new Packet { ["app"] = AppTag, ["ok"] = true, ["data"] = img == null ? "" : Convert.ToBase64String(img) }).ConfigureAwait(false);
                            break;
                    }
                }
                catch { }
            }
        }

        public string Offer(string path)
        {
            var token = Guid.NewGuid().ToString("N");
            _offers[token] = path;
            return token;
        }

        /// <summary>재시작 후에도 예전에 보낸 파일을 받을 수 있도록 기존 토큰 재등록</summary>
        public void Offer(string token, string path) { if (!string.IsNullOrEmpty(token)) _offers[token] = path; }

        const int Chunk = 1 << 16;

        async Task ServeFile(Channel ch, string token)
        {
            if (token == null || !_offers.TryGetValue(token, out var path) || !File.Exists(path))
            {
                await ch.SendJson(new Packet { ["app"] = AppTag, ["ok"] = false, ["err"] = "보낸 사람이 파일 제공을 중단했습니다." });
                return;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk, true);
            await ch.SendJson(new Packet { ["app"] = AppTag, ["ok"] = true, ["size"] = fs.Length, ["chunked"] = ch.Secure });
            if (!ch.Secure) { await fs.CopyToAsync(ch.Raw, Chunk); return; }
            var buf = new byte[Chunk];
            int n;
            while ((n = await fs.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                await ch.SendBytes(buf, 0, n).ConfigureAwait(false);
        }

        // ───────────────────────── client ─────────────────────────

        /// <summary>
        /// 연결 후 암호 채널을 먼저 시도하고, 상대가 구버전(2.1.x 이하)이면 평문으로 다시 연결.
        /// </summary>
        /// <summary>암호 핸드셰이크를 모르는 구버전 상대 (다음부터 바로 평문으로 연결)</summary>
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _legacy = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

        /// <summary>상대가 새 버전으로 바뀌었을 수 있으니(hello 에 enc 표시) 평문 기억을 지움</summary>
        public void MarkSecureCapable(string ip, int port) => _legacy.TryRemove(ip + ":" + port, out _);

        async Task<(TcpClient c, Channel ch)> Open(string ip, int port, int timeoutMs)
        {
            var key = ip + ":" + port;
            int first = _legacy.TryGetValue(key, out var since) && DateTime.Now - since < TimeSpan.FromHours(1) ? 1 : 0;
            for (int attempt = first; attempt < 2; attempt++)
            {
                bool secure = attempt == 0;
                var c = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    var connect = c.ConnectAsync(ip, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false) != connect)
                        throw new TimeoutException("상대 PC에 연결할 수 없습니다.");
                    await connect.ConfigureAwait(false);
                    if (!secure) return (c, Channel.Plain(c.GetStream()));
                    var hs = Channel.ConnectSecure(c.GetStream());
                    if (await Task.WhenAny(hs, Task.Delay(timeoutMs)).ConfigureAwait(false) == hs && await hs.ConfigureAwait(false) is Channel ch)
                        return (c, ch);
                    c.Dispose();   // 구버전: 핸드셰이크를 모르고 연결을 닫음 → 평문 재시도
                    _legacy[key] = DateTime.Now;
                }
                catch (TimeoutException) { c.Dispose(); throw; }
                catch (SocketException) { c.Dispose(); throw; }
                catch { c.Dispose(); if (!secure) throw; }
            }
            throw new IOException("상대 PC와 통신할 수 없습니다.");
        }

        public async Task<Packet> Request(string ip, int port, Packet p, int timeoutMs = 5000)
        {
            var (c, ch) = await Open(ip, port, timeoutMs).ConfigureAwait(false);
            using (c)
            {
                p["app"] = AppTag;
                await ch.SendJson(p).ConfigureAwait(false);
                var read = ch.RecvJson();
                if (await Task.WhenAny(read, Task.Delay(timeoutMs)).ConfigureAwait(false) != read)
                    throw new TimeoutException("응답이 없습니다.");
                var r = await read.ConfigureAwait(false);
                if (r != null) r["_secure"] = ch.Secure;
                return r;
            }
        }

        public async Task Download(string ip, int port, string token, string dest, IProgress<double> progress, CancellationToken ct)
        {
            var (c, ch) = await Open(ip, port, 6000).ConfigureAwait(false);
            using (c)
            {
                await ch.SendJson(new Packet { ["app"] = AppTag, ["t"] = "get", ["token"] = token }).ConfigureAwait(false);
                var head = await ch.RecvJson().ConfigureAwait(false);
                if (head == null || !Bool(head, "ok")) throw new IOException(Str(head, "err") ?? "파일을 받을 수 없습니다.");
                long size = Long(head, "size");
                bool chunked = Bool(head, "chunked");
                var tmp = dest + ".part";
                try
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, true))
                    {
                        long got = 0;
                        var buf = new byte[Chunk];
                        while (got < size)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (chunked)
                            {
                                var part = await ch.RecvBytes().ConfigureAwait(false);
                                if (part == null) throw new IOException("전송이 중간에 끊겼습니다.");
                                await fs.WriteAsync(part, 0, part.Length, ct).ConfigureAwait(false);
                                got += part.Length;
                            }
                            else
                            {
                                int n = await ch.Raw.ReadAsync(buf, 0, (int)Math.Min(buf.Length, size - got), ct).ConfigureAwait(false);
                                if (n <= 0) throw new IOException("전송이 중간에 끊겼습니다.");
                                await fs.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                                got += n;
                            }
                            progress?.Report(size == 0 ? 1 : (double)got / size);
                        }
                    }
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                    progress?.Report(1);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
            }
        }

        // ───────────────────────── framing (UDP) ─────────────────────────

        static byte[] Encode(Packet p) => Encoding.UTF8.GetBytes(Store.Json.Serialize(p));

        static Packet Decode(byte[] b, int off, int len)
        {
            try { return Store.Json.Deserialize<Packet>(Encoding.UTF8.GetString(b, off, len)); }
            catch { return null; }
        }

        // ───────────────────────── helpers ─────────────────────────

        public static string Str(Packet p, string k) => p != null && p.TryGetValue(k, out var v) && v != null ? Convert.ToString(v) : null;
        public static bool Bool(Packet p, string k) => p != null && p.TryGetValue(k, out var v) && v is bool b && b;
        public static int Int(Packet p, string k, int def = 0) { try { return p != null && p.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : def; } catch { return def; } }
        public static long Long(Packet p, string k) { try { return p != null && p.TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : 0; } catch { return 0; } }

        public static List<string> StrList(Packet p, string k)
        {
            var list = new List<string>();
            if (p != null && p.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && !(v is string))
                foreach (var x in e) if (x != null) list.Add(Convert.ToString(x));
            return list;
        }

        public static List<Packet> ObjList(Packet p, string k)
        {
            var list = new List<Packet>();
            if (p != null && p.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && !(v is string))
                foreach (var x in e) if (x is Packet d) list.Add(d);
            return list;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _udp?.Close(); } catch { }
            try { _tcp?.Stop(); } catch { }
        }
    }
}
