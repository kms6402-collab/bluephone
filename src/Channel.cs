using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace BluePhone
{
    using Packet = Dictionary<string, object>;

    /// <summary>
    /// TCP 연결 하나를 감싸는 프레임 채널.
    ///  · 평문 모드 : [길이 4B][UTF-8 JSON]                         (2.1.x 이하와 호환)
    ///  · 암호 모드 : [길이 4B][IV 16B | AES-256-CBC 암호문 | HMAC-SHA256 32B]
    /// 암호 모드는 연결마다 ECDH(P-256) 로 새 키를 만든다(전방 비밀성).
    /// HMAC 에 방향·순번을 넣어 재전송·순서 바꾸기·반사 공격을 막는다.
    /// </summary>
    public sealed class Channel
    {
        const int MaxFrame = 16 * 1024 * 1024;
        readonly Stream _s;
        byte[] _encKey, _macKey;
        long _sendSeq, _recvSeq;
        byte _sendDir, _recvDir;

        public bool Secure { get; private set; }

        Channel(Stream s) { _s = s; }

        // ───────────── handshake ─────────────

        /// <summary>클라이언트: 암호 연결 시도. 상대가 구버전이면 null</summary>
        public static async Task<Channel> ConnectSecure(Stream s)
        {
            using var ecdh = new ECDiffieHellmanCng(256);
            var myPub = ecdh.PublicKey.ToByteArray();
            var ch = new Channel(s);
            await ch.SendJson(new Packet { ["app"] = NetService.AppTag, ["t"] = "hs", ["v"] = 1, ["pub"] = Convert.ToBase64String(myPub) });
            var resp = await ch.RecvJson();
            var theirs = NetService.Str(resp, "pub");
            if (string.IsNullOrEmpty(theirs)) return null;
            ch.Derive(ecdh, myPub, Convert.FromBase64String(theirs), client: true);
            return ch;
        }

        public static Channel Plain(Stream s) => new Channel(s);

        /// <summary>서버: 첫 프레임을 보고 암호 핸드셰이크면 응답 후 암호 채널, 아니면 평문 채널</summary>
        public static async Task<(Channel ch, Packet first)> Accept(Stream s)
        {
            var ch = new Channel(s);
            var p = await ch.RecvJson();
            if (p == null || NetService.Str(p, "t") != "hs") return (ch, p);

            using var ecdh = new ECDiffieHellmanCng(256);
            var myPub = ecdh.PublicKey.ToByteArray();
            var theirs = Convert.FromBase64String(NetService.Str(p, "pub") ?? "");
            await ch.SendJson(new Packet { ["app"] = NetService.AppTag, ["ok"] = true, ["pub"] = Convert.ToBase64String(myPub) });
            ch.Derive(ecdh, myPub, theirs, client: false);
            return (ch, await ch.RecvJson());
        }

        void Derive(ECDiffieHellmanCng ecdh, byte[] myPub, byte[] theirPub, bool client)
        {
            var other = ECDiffieHellmanCngPublicKey.FromByteArray(theirPub, CngKeyBlobFormat.EccPublicBlob);
            ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
            ecdh.HashAlgorithm = CngAlgorithm.Sha256;
            var shared = ecdh.DeriveKeyMaterial(other);
            // 두 공개키를 정해진 순서로 섞어 세션 키 유도
            var c = client ? myPub : theirPub;
            var sv = client ? theirPub : myPub;
            _encKey = Kdf(shared, "bluephone-enc", c, sv);
            _macKey = Kdf(shared, "bluephone-mac", c, sv);
            _sendDir = client ? (byte)1 : (byte)2;
            _recvDir = client ? (byte)2 : (byte)1;
            Secure = true;
        }

        static byte[] Kdf(byte[] secret, string label, byte[] a, byte[] b)
        {
            using var h = new HMACSHA256(secret);
            var l = Encoding.ASCII.GetBytes(label);
            return h.ComputeHash(l.Concat(a).Concat(b).ToArray());
        }

        // ───────────── frames ─────────────

        public Task SendJson(Packet p) => SendBytes(Encoding.UTF8.GetBytes(Store.Json.Serialize(p)));

        public async Task<Packet> RecvJson()
        {
            var b = await RecvBytes();
            if (b == null) return null;
            try { return Store.Json.Deserialize<Packet>(Encoding.UTF8.GetString(b)); }
            catch { return null; }
        }

        public async Task SendBytes(byte[] data, int off = 0, int count = -1)
        {
            if (count < 0) count = data.Length - off;
            byte[] body;
            if (!Secure)
            {
                if (off == 0 && count == data.Length) body = data;
                else { body = new byte[count]; Buffer.BlockCopy(data, off, body, 0, count); }
            }
            else
            {
                using var aes = Aes.Create();
                aes.Key = _encKey; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                byte[] ct;
                using (var enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(data, off, count);
                var mac = Mac(_sendDir, _sendSeq++, aes.IV, ct);
                body = new byte[16 + ct.Length + 32];
                Buffer.BlockCopy(aes.IV, 0, body, 0, 16);
                Buffer.BlockCopy(ct, 0, body, 16, ct.Length);
                Buffer.BlockCopy(mac, 0, body, 16 + ct.Length, 32);
            }
            var head = BitConverter.GetBytes(body.Length);
            await _s.WriteAsync(head, 0, 4).ConfigureAwait(false);
            await _s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await _s.FlushAsync().ConfigureAwait(false);
        }

        public async Task<byte[]> RecvBytes()
        {
            var head = await ReadExact(4).ConfigureAwait(false);
            if (head == null) return null;
            int len = BitConverter.ToInt32(head, 0);
            if (len < 0 || len > MaxFrame) return null;
            var body = await ReadExact(len).ConfigureAwait(false);
            if (body == null || !Secure) return body;

            if (len < 16 + 16 + 32) throw new CryptographicException("잘못된 암호 프레임");
            var iv = new byte[16];
            var ct = new byte[len - 48];
            var mac = new byte[32];
            Buffer.BlockCopy(body, 0, iv, 0, 16);
            Buffer.BlockCopy(body, 16, ct, 0, ct.Length);
            Buffer.BlockCopy(body, 16 + ct.Length, mac, 0, 32);
            if (!FixedEquals(mac, Mac(_recvDir, _recvSeq++, iv, ct)))
                throw new CryptographicException("무결성 검증 실패 — 변조되었거나 잘못된 키입니다.");
            using var aes = Aes.Create();
            aes.Key = _encKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(ct, 0, ct.Length);
        }

        byte[] Mac(byte dir, long seq, byte[] iv, byte[] ct)
        {
            using var h = new HMACSHA256(_macKey);
            var hdr = new byte[9];
            hdr[0] = dir;
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, hdr, 1, 8);
            h.TransformBlock(hdr, 0, 9, null, 0);
            h.TransformBlock(iv, 0, iv.Length, null, 0);
            h.TransformFinalBlock(ct, 0, ct.Length);
            return h.Hash;
        }

        static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        /// <summary>평문 모드에서 파일 본문을 그대로 읽을 때 사용</summary>
        public Stream Raw => _s;

        async Task<byte[]> ReadExact(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = await _s.ReadAsync(b, got, n - got).ConfigureAwait(false);
                if (r <= 0) return null;
                got += r;
            }
            return b;
        }
    }
}
