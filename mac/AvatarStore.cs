using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace BluePhone
{
    /// <summary>프로필 사진: 내 사진(avatar.jpg)과 동료 사진 캐시(avatars/{id}.jpg). 내용은 PNG/JPEG 모두 가능</summary>
    public static class AvatarStore
    {
        public const int Size = 160;
        static string PeerDir => Path.Combine(Store.Dir, "avatars");
        static string MinePath => Path.Combine(Store.Dir, "avatar.jpg");
        static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>();
        static Bitmap _mine;
        static bool _mineLoaded;

        public static event Action<string> Changed;

        public static Bitmap Mine
        {
            get
            {
                if (!_mineLoaded) { _mine = Load(MinePath); _mineLoaded = true; }
                return _mine;
            }
        }

        public static byte[] MineBytes() => File.Exists(MinePath) ? File.ReadAllBytes(MinePath) : null;

        /// <summary>이미지 파일을 가운데 정사각형으로 잘라 160px PNG 로 (투명 부분은 흰 바탕)</summary>
        public static byte[] Prepare(string sourcePath)
        {
            using var src = new Bitmap(sourcePath);
            int w = src.PixelSize.Width, h = src.PixelSize.Height;
            int side = Math.Min(w, h);
            int outSide = Math.Min(Size, side);
            using var rtb = new RenderTargetBitmap(new PixelSize(outSide, outSide), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.FillRectangle(Brushes.White, new Rect(0, 0, outSide, outSide));
                ctx.DrawImage(src, new Rect((w - side) / 2.0, (h - side) / 2.0, side, side), new Rect(0, 0, outSide, outSide));
            }
            using var ms = new MemoryStream();
            rtb.Save(ms);
            return ms.ToArray();
        }

        public static string Hash(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(data), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        public static string SetMine(byte[] data)
        {
            try
            {
                if (data == null) { if (File.Exists(MinePath)) File.Delete(MinePath); }
                else File.WriteAllBytes(MinePath, data);
            }
            catch { }
            _mine = data == null ? null : FromBytes(data);
            _mineLoaded = true;
            Changed?.Invoke(null);
            return Hash(data);
        }

        public static Bitmap Get(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return null;
            if (Cache.TryGetValue(peerId, out var img)) return img;
            img = Load(Path.Combine(PeerDir, Safe(peerId) + ".jpg"));
            Cache[peerId] = img;
            return img;
        }

        public static bool Has(string peerId) => File.Exists(Path.Combine(PeerDir, Safe(peerId) + ".jpg"));

        public static void SavePeer(string peerId, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(PeerDir);
                var path = Path.Combine(PeerDir, Safe(peerId) + ".jpg");
                if (data == null || data.Length == 0) { if (File.Exists(path)) File.Delete(path); Cache[peerId] = null; }
                else { File.WriteAllBytes(path, data); Cache[peerId] = FromBytes(data); }
            }
            catch { }
            Changed?.Invoke(peerId);
        }

        static string Safe(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id.Replace(':', '_');
        }

        static Bitmap Load(string path)
        {
            try { return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : null; }
            catch { return null; }
        }

        public static Bitmap FromBytes(byte[] data)
        {
            try { return new Bitmap(new MemoryStream(data)); }
            catch { return null; }
        }
    }
}
