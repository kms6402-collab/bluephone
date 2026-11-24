using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        public static string Name => "파란전화기";
        public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
        public static string RepoUrl => "https://github.com/" + Repo;

        public static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        public static string ExtractChanges(string text, string version)
        {
            var m = Regex.Match((text ?? "").Replace("\r\n", "\n"), @"(?ms)^##\s*\[?" + Regex.Escape(version) + @"\]?[^\n]*\n(.*?)(?=^##\s|\z)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public static string ChangesFor(string version)
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
                if (s == null) return null;
                return ExtractChanges(new StreamReader(s, Encoding.UTF8).ReadToEnd(), version);
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

    /// <summary>
    /// GitHub Releases 기반 업데이트 (macOS).
    /// 새 버전의 설치 파일(BluePhone-mac.pkg)을 받아 SHA-256 을 확인한 뒤, 설치 프로그램을 열고 종료한다.
    /// 설치가 끝나면 설치 스크립트가 파란전화기를 다시 실행한다.
    /// </summary>
    public static class Updater
    {
        public const string AssetName = "BluePhone-mac.pkg";
        public static ReleaseInfo Ready { get; private set; }
        public static string ReadyPath { get; private set; }

        static readonly HttpClient Http = CreateClient(true);
        static readonly HttpClient NoRedirect = CreateClient(false);

        static HttpClient CreateClient(bool redirect)
        {
            var h = new HttpClientHandler { AllowAutoRedirect = redirect, UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
            var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BluePhone-Updater/" + AppInfo.VersionText);
            return c;
        }

        public static void Cleanup()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "BluePhone-*.pkg"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f);
            }
            catch { }
        }

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
            using var req = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{AppInfo.Repo}/releases/latest");
            using var res = await NoRedirect.SendAsync(req);
            var location = res.Headers.Location?.ToString();
            var m = Regex.Match(location ?? "", @"/releases/tag/([^/?#]+)");
            if (!m.Success) return null;
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            var baseUrl = $"https://github.com/{AppInfo.Repo}/releases/download/{tag}/";
            var r = new ReleaseInfo
            {
                Version = AppInfo.Normalize(v), Tag = tag,
                AssetUrl = baseUrl + AssetName, ShaUrl = baseUrl + AssetName + ".sha256",
                PageUrl = $"https://github.com/{AppInfo.Repo}/releases/tag/{tag}",
            };
            if (IsNewer(r))
            {
                // Mac 설치 파일은 Windows 판보다 몇 분 늦게 올라올 수 있음 → 아직 없으면 다음에 다시 확인
                using var head = new HttpRequestMessage(HttpMethod.Head, r.AssetUrl);
                using var hr = await Http.SendAsync(head);
                if (!hr.IsSuccessStatusCode) return new ReleaseInfo { Version = AppInfo.Version, Tag = "v" + AppInfo.VersionText };
                r.Size = hr.Content.Headers.ContentLength ?? 0;
            }
            try
            {
                var md = await Http.GetStringAsync($"https://raw.githubusercontent.com/{AppInfo.Repo}/{tag}/CHANGELOG.md");
                r.Notes = AppInfo.ExtractChanges(md, r.VersionText);
            }
            catch { }
            r.Notes ??= "";
            return r;
        }

        static async Task<ReleaseInfo> CheckViaApiAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var d = Store.Json.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
            var tag = NetService.Str(d, "tag_name") ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            var r = new ReleaseInfo
            {
                Version = AppInfo.Normalize(v), Tag = tag,
                Notes = (NetService.Str(d, "body") ?? "").Trim(), PageUrl = NetService.Str(d, "html_url"),
            };
            foreach (var a in NetService.ObjList(d, "assets"))
            {
                var name = NetService.Str(a, "name");
                if (name == AssetName) { r.AssetUrl = NetService.Str(a, "browser_download_url"); r.Size = NetService.Long(a, "size"); }
                else if (name == AssetName + ".sha256") r.ShaUrl = NetService.Str(a, "browser_download_url");
            }
            return r.AssetUrl == null ? null : r;
        }

        public static string Explain(Exception ex)
        {
            if (ex is HttpRequestException he)
            {
                if (he.StatusCode is HttpStatusCode code)
                {
                    int c = (int)code;
                    if (c == 407) return "회사 프록시 인증이 필요합니다 (407). 전산 담당자에게 github.com 접속 허용을 요청하세요.";
                    if (c == 403 || c == 429) return $"GitHub 접속이 거부되었습니다 ({c}). 잠시 후 다시 시도하세요.";
                    return $"업데이트 서버 응답 오류 ({c})";
                }
                return "인터넷 또는 github.com 에 연결할 수 없습니다.";
            }
            if (ex is TaskCanceledException) return "업데이트 서버 응답이 없습니다 (시간 초과).";
            return ex?.Message ?? "알 수 없는 오류";
        }

        public static bool IsNewer(ReleaseInfo r) => r != null && r.Version > AppInfo.Version;

        public static async Task DownloadAsync(ReleaseInfo r, IProgress<double> progress)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"BluePhone-{r.VersionText}.pkg");
            using (var res = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? r.Size;
                if (total > 0) r.Size = total;
                using var src = await res.Content.ReadAsStreamAsync();
                using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long got = 0; int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    got += n;
                    if (total > 0) progress?.Report((double)got / total);
                }
            }
            if (r.ShaUrl != null)
            {
                var expect = (await Http.GetStringAsync(r.ShaUrl)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(tmp))
                    actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                if (actual != expect)
                {
                    try { File.Delete(tmp); } catch { }
                    throw new InvalidDataException("내려받은 파일의 무결성 검사에 실패했습니다.");
                }
            }
            ReadyPath = tmp;
            Ready = r;
        }

        /// <summary>설치 프로그램을 연다. restart 가 true 면 호출한 쪽이 곧바로 종료해야 함</summary>
        public static bool Apply(bool restart)
        {
            if (!restart || ReadyPath == null || !File.Exists(ReadyPath)) return false;
            try
            {
                Process.Start(new ProcessStartInfo("open", $"\"{ReadyPath}\"") { UseShellExecute = false });
                ReadyPath = null;
                return true;
            }
            catch { return false; }
        }

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
