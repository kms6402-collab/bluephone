using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BluePhone
{
    public static class AppInfo
    {
        public const string Repo = "kms6402-collab/bluephone";
        public static readonly Version Version = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
        public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
        public static string RepoUrl => "https://github.com/" + Repo;

        public static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>CHANGELOG 텍스트에서 특정 버전 단락 추출</summary>
        public static string ExtractChanges(string text, string version)
        {
            var m = Regex.Match((text ?? "").Replace("\r\n", "\n"), @"(?ms)^##\s*\[?" + Regex.Escape(version) + @"\]?[^\n]*\n(.*?)(?=^##\s|\z)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>내장된 CHANGELOG.md 에서 특정 버전의 변경 사항 추출</summary>
        public static string ChangesFor(string version)
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
                if (s == null) return null;
                var text = new StreamReader(s, Encoding.UTF8).ReadToEnd();
                return ExtractChanges(text, version);
            }
            catch { return null; }
        }
    }

    public sealed class ReleaseInfo
    {
        public Version Version;
        public string Tag, Notes, AssetUrl, ShaUrl, PageUrl;
        public long Size;
        public string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
    }

    /// <summary>GitHub Releases 기반 자동 업데이트</summary>
    public static class Updater
    {
        const string AssetName = "BluePhone.exe";
        public static ReleaseInfo Ready { get; private set; }   // 내려받아 검증까지 끝난 새 버전
        public static string ReadyPath { get; private set; }

        static string OldPath => Native.ExePath + ".old";

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        static WebClient Client(bool api = false)
        {
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "BluePhone-Updater/" + AppInfo.VersionText;
            if (api) wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            UseSystemProxy(wc);
            return wc;
        }

        /// <summary>회사 프록시: 윈도우 로그인 계정으로 프록시 인증 (인증이 필요한 프록시에서 403/407 방지)</summary>
        static void UseSystemProxy(WebClient wc)
        {
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                proxy.Credentials = CredentialCache.DefaultCredentials;
                wc.Proxy = proxy;
            }
            catch { }
        }

        /// <summary>이전 업데이트에서 남은 파일 정리</summary>
        public static void Cleanup()
        {
            for (int i = 0; i < 5; i++)
            {
                try { if (File.Exists(OldPath)) File.Delete(OldPath); return; }
                catch { System.Threading.Thread.Sleep(400); }
            }
        }

        /// <summary>
        /// 최신 버전 확인.
        /// 1) github.com/…/releases/latest 의 리디렉션 주소에서 버전 확인 — GitHub API 를 쓰지 않으므로
        ///    회사처럼 여러 PC가 같은 공인 IP를 쓰는 곳에서도 'API 사용 한도 초과(403)'가 나지 않음
        /// 2) 실패하면 GitHub API 로 한 번 더 시도
        /// </summary>
        public static async Task<ReleaseInfo> CheckAsync()
        {
            Exception first = null;
            try { var r = await CheckViaRedirectAsync(); if (r != null) return r; }
            catch (Exception ex) { first = ex; }
            try { return await CheckViaApiAsync(); }
            catch (Exception ex) { throw new Exception(Explain(first ?? ex), ex); }
        }

        static async Task<ReleaseInfo> CheckViaRedirectAsync()
        {
            var req = (HttpWebRequest)WebRequest.Create($"https://github.com/{AppInfo.Repo}/releases/latest");
            req.Method = "HEAD";
            req.AllowAutoRedirect = false;
            req.UserAgent = "BluePhone-Updater/" + AppInfo.VersionText;
            req.Timeout = 15000;
            try { var proxy = WebRequest.GetSystemWebProxy(); proxy.Credentials = CredentialCache.DefaultCredentials; req.Proxy = proxy; } catch { }
            string location;
            using (var res = (HttpWebResponse)await req.GetResponseAsync())
                location = res.Headers[HttpResponseHeader.Location];
            // https://github.com/{repo}/releases/tag/v2.2.6
            var m = Regex.Match(location ?? "", @"/releases/tag/([^/?#]+)");
            if (!m.Success) return null;
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            var baseUrl = $"https://github.com/{AppInfo.Repo}/releases/download/{tag}/";
            var r = new ReleaseInfo
            {
                Version = AppInfo.Normalize(v),
                Tag = tag,
                AssetUrl = baseUrl + AssetName,
                ShaUrl = baseUrl + AssetName + ".sha256",
                PageUrl = $"https://github.com/{AppInfo.Repo}/releases/tag/{tag}",
            };
            // 변경 사항: 해당 버전의 CHANGELOG.md 에서 (raw 주소도 API 한도와 무관)
            try
            {
                using (var wc = Client())
                {
                    var md = await wc.DownloadStringTaskAsync($"https://raw.githubusercontent.com/{AppInfo.Repo}/{tag}/CHANGELOG.md");
                    var notes = AppInfo.ExtractChanges(md, r.VersionText);
                    if (notes != null) r.Notes = notes;
                }
            }
            catch { }
            r.Notes = r.Notes ?? "";
            return r;
        }

        static async Task<ReleaseInfo> CheckViaApiAsync()
        {
            string json;
            using (var wc = Client(api: true))
                json = await wc.DownloadStringTaskAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
            var d = Store.Json.Deserialize<Dictionary<string, object>>(json);
            var tag = NetService.Str(d, "tag_name") ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            var r = new ReleaseInfo
            {
                Version = AppInfo.Normalize(v),
                Tag = tag,
                Notes = (NetService.Str(d, "body") ?? "").Trim(),
                PageUrl = NetService.Str(d, "html_url"),
            };
            foreach (var a in NetService.ObjList(d, "assets"))
            {
                var name = NetService.Str(a, "name");
                if (name == AssetName) { r.AssetUrl = NetService.Str(a, "browser_download_url"); r.Size = NetService.Long(a, "size"); }
                else if (name == AssetName + ".sha256") r.ShaUrl = NetService.Str(a, "browser_download_url");
            }
            return r.AssetUrl == null ? null : r;
        }

        /// <summary>네트워크 오류를 사용자에게 알기 쉬운 말로</summary>
        public static string Explain(Exception ex)
        {
            if (ex is WebException we)
            {
                if (we.Response is HttpWebResponse res)
                {
                    int code = (int)res.StatusCode;
                    if (code == 407) return "회사 프록시 인증이 필요합니다 (407). 전산 담당자에게 github.com 접속 허용을 요청하세요.";
                    if (code == 403 || code == 429) return $"GitHub 접속이 거부되었습니다 ({code}). 회사 방화벽이 github.com 을 막고 있거나 잠시 사용량이 많은 경우입니다. 잠시 후 다시 시도하세요.";
                    return $"업데이트 서버 응답 오류 ({code})";
                }
                if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure)
                    return "인터넷 또는 github.com 에 연결할 수 없습니다.";
                if (we.Status == WebExceptionStatus.Timeout) return "업데이트 서버 응답이 없습니다 (시간 초과).";
                if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure)
                    return "보안 연결(TLS)에 실패했습니다. 회사 보안 프로그램이 HTTPS 를 검사하는 경우일 수 있습니다.";
            }
            return ex?.Message ?? "알 수 없는 오류";
        }

        public static bool IsNewer(ReleaseInfo r) => r != null && r.Version > AppInfo.Version;

        /// <summary>새 버전을 내려받고 SHA-256 을 검증한 뒤 실행 파일 옆에 대기시킴</summary>
        public static async Task DownloadAsync(ReleaseInfo r, IProgress<double> progress)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"BluePhone-{r.VersionText}.exe");
            using (var wc = Client())
            {
                wc.DownloadProgressChanged += (s, e) =>
                    progress?.Report(e.TotalBytesToReceive > 0 ? (double)e.BytesReceived / e.TotalBytesToReceive : r.Size > 0 ? (double)e.BytesReceived / r.Size : 0);
                await wc.DownloadFileTaskAsync(r.AssetUrl, tmp);
            }

            if (r.ShaUrl != null)
            {
                string expect;
                using (var wc = Client()) expect = (await wc.DownloadStringTaskAsync(r.ShaUrl)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(tmp))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (actual != expect)
                {
                    try { File.Delete(tmp); } catch { }
                    throw new InvalidDataException("내려받은 파일의 무결성 검사에 실패했습니다.");
                }
            }

            // 실행 파일과 같은 폴더(같은 드라이브)로 옮겨 두면 교체가 원자적으로 끝남
            var staged = Native.ExePath + ".new";
            try
            {
                if (File.Exists(staged)) File.Delete(staged);
                File.Move(tmp, staged);
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException("실행 파일이 있는 폴더에 쓸 권한이 없습니다. 바탕화면이나 문서 폴더처럼 쓰기 가능한 곳으로 옮겨 실행해 주세요.");
            }
            ReadyPath = staged;
            Ready = r;
        }

        /// <summary>
        /// 실행 중인 exe 는 덮어쓸 수 없지만 이름은 바꿀 수 있음 → 현재 파일을 .old 로 밀고 새 파일을 같은 이름으로.
        /// 사용자가 바꾼 파일명은 그대로 유지된다.
        /// </summary>
        public static bool Apply(bool restart)
        {
            if (ReadyPath == null || !File.Exists(ReadyPath)) return false;
            var cur = Native.ExePath;
            try
            {
                if (File.Exists(OldPath)) File.Delete(OldPath);
                File.Move(cur, OldPath);
                try { File.Move(ReadyPath, cur); }
                catch { File.Move(OldPath, cur); throw; }
                ReadyPath = null;
                var extra = Store.CustomDir ? $" --data \"{Store.Dir}\"" : "";
                if (restart) Process.Start(new ProcessStartInfo(cur, "--restart --updated" + extra) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        /// <summary>GitHub 릴리스 본문(마크다운)을 읽기 쉬운 일반 텍스트로</summary>
        public static string PlainNotes(string md)
        {
            if (string.IsNullOrWhiteSpace(md)) return "";
            var lines = md.Replace("\r", "").Split('\n').Select(l =>
            {
                var t = l.TrimEnd();
                t = Regex.Replace(t, @"^#{1,6}\s*", "");
                t = Regex.Replace(t, @"^\s*[-*+]\s+", "•  ");
                t = Regex.Replace(t, @"\*\*(.+?)\*\*", "$1");
                t = Regex.Replace(t, @"`(.+?)`", "$1");
                t = Regex.Replace(t, @"\[(.+?)\]\((.+?)\)", "$1");
                return t;
            });
            return Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
        }
    }
}
