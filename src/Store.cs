using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace BluePhone
{
    /// <summary>설정 · 연락처 · 대화기록 영구 저장 (%APPDATA%\BluePhone)</summary>
    public static class Store
    {
        public static string Dir { get; private set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BluePhone");

        /// <summary>--data 로 별도 데이터 폴더를 쓰는 중인지 (포터블/테스트용 분리 실행)</summary>
        public static bool CustomDir { get; private set; }

        public static void UseDir(string dir)
        {
            Dir = Path.GetFullPath(dir);
            CustomDir = true;
            Directory.CreateDirectory(Dir);
        }

        static string SettingsPath => Path.Combine(Dir, "settings.json");
        static string ContactsPath => Path.Combine(Dir, "contacts.json");
        static string HistoryPath => Path.Combine(Dir, "history.jsonl");

        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly object HistoryLock = new object();

        static Store() { Directory.CreateDirectory(Dir); }

        public static bool HasSettings => File.Exists(SettingsPath);

        public static AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return Json.Deserialize<AppSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        public static void SaveSettings(AppSettings s) => WriteAtomic(SettingsPath, Json.Serialize(s));

        public static List<Peer> LoadContacts()
        {
            try
            {
                if (File.Exists(ContactsPath))
                    return Json.Deserialize<List<Peer>>(File.ReadAllText(ContactsPath, Encoding.UTF8)) ?? new List<Peer>();
            }
            catch { }
            return new List<Peer>();
        }

        public static void SaveContacts(IEnumerable<Peer> peers) =>
            WriteAtomic(ContactsPath, Json.Serialize(peers.Select(p => new
            {
                p.Id, p.Name, p.Alias, p.Dept, p.Ip, p.Port, p.Host, p.Note, p.AvatarHash, p.Favorite, p.LastSeenTs
            }).ToList()));

        public static List<ChatMessage> LoadHistory(int max = 3000)
        {
            var list = new List<ChatMessage>();
            try
            {
                if (!File.Exists(HistoryPath)) return list;
                var map = new Dictionary<string, ChatMessage>();
                foreach (var line in File.ReadLines(HistoryPath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var m = Json.Deserialize<ChatMessage>(line);
                        if (m?.Id == null) continue;
                        map[m.Id + (m.Outgoing ? ">" : "<")] = m; // 같은 Id 는 마지막 상태로 덮어씀
                    }
                    catch { }
                }
                list = map.Values.OrderBy(m => m.Ts).ToList();
                if (list.Count > max) list = list.Skip(list.Count - max).ToList();
            }
            catch { }
            return list;
        }

        public static void AppendHistory(ChatMessage m)
        {
            lock (HistoryLock)
            {
                try { File.AppendAllText(HistoryPath, Json.Serialize(m) + "\n", new UTF8Encoding(false)); } catch { }
            }
        }

        public static void RewriteHistory(IEnumerable<ChatMessage> all)
        {
            lock (HistoryLock)
            {
                var sb = new StringBuilder();
                foreach (var m in all) sb.Append(Json.Serialize(m)).Append('\n');
                WriteAtomic(HistoryPath, sb.ToString());
            }
        }

        static void WriteAtomic(string path, string content)
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch { }
        }

        public static string DownloadDir(AppSettings s)
        {
            var d = s.DownloadDir;
            if (string.IsNullOrWhiteSpace(d))
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "파란전화기");
            Directory.CreateDirectory(d);
            return d;
        }

        public static string UniquePath(string dir, string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) return p;
            var b = Path.GetFileNameWithoutExtension(name);
            var e = Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                p = Path.Combine(dir, $"{b} ({i}){e}");
                if (!File.Exists(p)) return p;
            }
        }
    }
}
