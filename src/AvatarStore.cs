using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BluePhone
{
    /// <summary>프로필 사진: 내 사진(avatar.jpg)과 동료 사진 캐시(avatars\{id}.jpg)</summary>
    public static class AvatarStore
    {
        public const int Size = 160;
        static readonly string PeerDir = Path.Combine(Store.Dir, "avatars");
        static readonly string MinePath = Path.Combine(Store.Dir, "avatar.jpg");
        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();
        static ImageSource _mine;
        static bool _mineLoaded;

        public static event Action<string> Changed; // peer id, 내 사진이면 null

        public static ImageSource Mine
        {
            get
            {
                if (!_mineLoaded) { _mine = Load(MinePath); _mineLoaded = true; }
                return _mine;
            }
        }

        public static byte[] MineBytes() => File.Exists(MinePath) ? File.ReadAllBytes(MinePath) : null;

        /// <summary>이미지 파일을 가운데 정사각형으로 잘라 줄인 JPEG 바이트로 변환</summary>
        public static byte[] Prepare(string sourcePath)
        {
            var src = new BitmapImage();
            using (var fs = File.OpenRead(sourcePath))
            {
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                src.StreamSource = fs;
                src.EndInit();
            }
            int side = Math.Min(src.PixelWidth, src.PixelHeight);
            var crop = new CroppedBitmap(src, new Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
            double scale = (double)Size / side;
            BitmapSource final = scale < 1 ? new TransformedBitmap(crop, new ScaleTransform(scale, scale)) : (BitmapSource)crop;
            // 투명 PNG 대비 흰 배경 합성
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, final.PixelWidth, final.PixelHeight));
                dc.DrawImage(final, new Rect(0, 0, final.PixelWidth, final.PixelHeight));
            }
            var rtb = new RenderTargetBitmap(final.PixelWidth, final.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        public static string Hash(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(data), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>내 사진 저장 (null 이면 삭제). 새 해시 반환</summary>
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

        public static ImageSource Get(string peerId)
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
            return id;
        }

        static ImageSource Load(string path)
        {
            try { return File.Exists(path) ? FromBytes(File.ReadAllBytes(path)) : null; }
            catch { return null; }
        }

        static ImageSource FromBytes(byte[] data)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(data);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
    }
}
