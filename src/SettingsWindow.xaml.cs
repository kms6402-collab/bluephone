using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;

namespace BluePhone
{
    public partial class SettingsWindow : Window
    {
        readonly Core _core;
        byte[] _photo;          // 새로 고른 사진 (저장 전)
        bool _photoChanged;

        public SettingsWindow(Core core)
        {
            _core = core;
            InitializeComponent();
            Chrome.Apply(this, caption: 44);
            if (Owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var s = core.Settings;
            NameBox.Text = s.Name;
            DeptBox.Text = s.Dept;
            NoteBox.Text = s.Note;
            SoundChk.IsChecked = s.Sound;
            NoFocusChk.IsChecked = s.PopupNoFocus;
            foreach (System.Windows.Controls.RadioButton rb in ChatClosePanel.Children)
                rb.IsChecked = (string)rb.Tag == s.ChatAlertAutoClose.ToString();
            if (!ChatClosePanel.Children.OfType<System.Windows.Controls.RadioButton>().Any(r => r.IsChecked == true))
                ((System.Windows.Controls.RadioButton)ChatClosePanel.Children[1]).IsChecked = true;
            foreach (System.Windows.Controls.RadioButton rb in PrivacyPanel.Children)
                rb.IsChecked = (string)rb.Tag == s.AlertPrivacy.ToString();
            foreach (System.Windows.Controls.RadioButton rb in AutoClosePanel.Children)
                rb.IsChecked = (string)rb.Tag == s.PopupAutoClose.ToString();
            if (!AutoClosePanel.Children.OfType<System.Windows.Controls.RadioButton>().Any(r => r.IsChecked == true))
                ((System.Windows.Controls.RadioButton)AutoClosePanel.Children[0]).IsChecked = true;
            ReceiptChk.IsChecked = s.ReadReceipts;
            TypingChk.IsChecked = s.ShareTyping;
            EnterChk.IsChecked = s.EnterToSend;
            HotkeyChk.IsChecked = s.CaptureHotkey;
            if (s.CaptureHotkey && core.HotkeyFailed) HotkeyHelp.Text = "다른 프로그램이 이미 Ctrl+Alt+S 를 쓰고 있어 등록하지 못했습니다";
            AwayBox.Text = s.AutoAwayMinutes.ToString();
            AutoReplyBox.Text = s.AutoReply;
            AutoStartChk.IsChecked = s.AutoStart;
            HiddenChk.IsChecked = s.StartHidden;
            DirBox.Text = Store.DownloadDir(s);
            PortBox.Text = s.Port.ToString();
            HostsBox.Text = string.Join(Environment.NewLine, s.ManualHosts ?? new System.Collections.Generic.List<string>());

            AutoUpdateChk.IsChecked = s.AutoUpdate;
            PhotoBg.Fill = Palette.Avatar(s.Id);
            PhotoInitial.Text = string.IsNullOrEmpty(s.Name) ? "?" : s.Name.Substring(0, 1).ToUpperInvariant();
            PhotoBrush.ImageSource = AvatarStore.Mine;
            RemovePhotoBtn.Visibility = AvatarStore.Mine != null ? Visibility.Visible : Visibility.Collapsed;
            NameBox.TextChanged += (o, e) => PhotoInitial.Text = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim().Substring(0, 1).ToUpperInvariant() : "?";

            AutoStartHelp.Text = $"현재 실행 파일({Path.GetFileName(Native.ExePath)})을 등록합니다. 파일 이름이나 위치를 바꾼 뒤에는 한 번 실행하면 자동으로 갱신됩니다.";
            var ver = typeof(SettingsWindow).Assembly.GetName().Version;
            VersionText.Text = "버전 " + AppInfo.VersionText;
            AppNameText.Text = AppInfo.Name;
            ExeText.Text = $"프로세스 이름  {Process.GetCurrentProcess().ProcessName}\n실행 파일  {Native.ExePath}";
            MyIps.Text = "이 PC 주소  " + string.Join(", ", LocalIps().Select(ip => $"{ip}:{s.Port}")) + "  — 동료가 이 주소로 나를 추가할 수 있습니다";

            TgChk.IsChecked = s.TelegramEnabled;
            TgTokenBox.Text = s.TelegramToken ?? "";
            TgNotesChk.IsChecked = s.TelegramNotes;
            TgChatsChk.IsChecked = s.TelegramChats;
            TgTextChk.IsChecked = s.TelegramShowText;
            TgAwayChk.IsChecked = s.TelegramOnlyAway;
            TgFilesChk.IsChecked = s.TelegramFiles;
            foreach (System.Windows.Controls.RadioButton rb in TgNetPanel.Children)
            {
                rb.IsChecked = (string)rb.Tag == (string.IsNullOrEmpty(s.TelegramNet) ? "auto" : s.TelegramNet);
                rb.Checked += (o, e) => UpdateProxyPanel();
            }
            TgProxyBox.Text = s.TelegramProxy ?? "";
            TgProxyUserBox.Text = s.TelegramProxyUser ?? "";
            TgProxyPassBox.Password = Secret.Unprotect(s.TelegramProxyPass);
            TgApiBox.Text = s.TelegramApi ?? "";
            TgNetExp.IsExpanded = (s.TelegramNet ?? "auto") != "auto" || !string.IsNullOrWhiteSpace(s.TelegramApi);
            UpdateProxyPanel();
            TgChk.Checked += (o, e) => TgPanel.Visibility = Visibility.Visible;
            TgChk.Unchecked += (o, e) => TgPanel.Visibility = Visibility.Collapsed;
            TgPanel.Visibility = s.TelegramEnabled ? Visibility.Visible : Visibility.Collapsed;
            RefreshTg();
            if (core.Telegram != null) core.Telegram.StateChanged += RefreshTg;
            Closed += (o, e) =>
            {
                if (core.Telegram != null) core.Telegram.StateChanged -= RefreshTg;
                if (_pairing != null) { _pairing.Cancel(); _pairing = null; core.Telegram?.Apply(); }
            };

            Loaded += (o, e) => NameBox.Focus();
        }

        // ───────────── telegram ─────────────

        string NetMode => TgNetPanel.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "auto";

        void UpdateProxyPanel()
        {
            var m = NetMode;
            TgProxyPanel.Visibility = m == "http" || m == "socks5" ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>화면에 입력한 연결 방법 (저장 전에도 '연결' · '진단'에 바로 씀)</summary>
        TgNetConfig NetFromUi() => new TgNetConfig
        {
            Mode = NetMode,
            Proxy = TgProxyBox.Text.Trim(),
            User = TgProxyUserBox.Text.Trim(),
            Pass = TgProxyPassBox.Password,
            Api = TgApiBox.Text.Trim(),
        };

        void SaveNet(AppSettings s)
        {
            var c = NetFromUi();
            s.TelegramNet = c.Mode;
            s.TelegramProxy = c.Proxy;
            s.TelegramProxyUser = c.User;
            s.TelegramProxyPass = Secret.Protect(c.Pass);
            s.TelegramApi = c.Api;
            TgNet.Config = c;
        }

        async void TgDiag_Click(object sender, RoutedEventArgs e)
        {
            var c = NetFromUi();
            if ((c.Mode == "http" || c.Mode == "socks5") && c.Proxy.Length == 0)
            {
                TgDiagText.Visibility = Visibility.Visible;
                TgDiagText.Text = "프록시 주소를 입력해 주세요. 예) 203.0.113.10:1080";
                TgProxyBox.Focus();
                return;
            }
            TgDiagBtn.IsEnabled = false;
            TgDiagText.Visibility = Visibility.Visible;
            TgDiagText.Text = "시험하는 중…";
            try { TgDiagText.Text = await TgNet.Diagnose(c, System.Threading.CancellationToken.None); }
            catch (Exception ex) { TgDiagText.Text = "진단하지 못했습니다: " + TgNet.Short(ex); }
            TgDiagBtn.IsEnabled = true;
        }

        System.Threading.CancellationTokenSource _pairing;
        /// <summary>마지막 연결 실패 이유 (다시 시도하기 전까지 계속 표시)</summary>
        string _tgError;

        /// <summary>붙여 넣은 글에서 봇 토큰만 골라냄 (BotFather 안내문을 통째로 붙여 넣어도 됨)</summary>
        static string ExtractToken(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d{5,}:[A-Za-z0-9_-]{30,}");
            return m.Success ? m.Value : (text ?? "").Trim();
        }

        static string Explain(Exception ex)
        {
            if (ex is TelegramException te)
            {
                if (te.Code == 401 || te.Code == 404) return "봇 토큰이 올바르지 않습니다. @BotFather 에서 받은 토큰을 다시 복사해 붙여 넣어 주세요.";
                if (te.Code == 409) return "이 봇을 다른 PC(또는 웹훅)에서 쓰고 있습니다. 다른 PC의 연결을 해제한 뒤 다시 시도해 주세요.";
                return $"텔레그램 오류 ({te.Code}): {te.Message}";
            }
            if (ex is TimeoutException) return ex.Message.Contains("코드") ? ex.Message + " 다시 '연결'을 눌러 주세요." : "텔레그램 서버의 응답이 없습니다. 인터넷 · 회사 방화벽(api.telegram.org)을 확인해 주세요.";
            var w = ex as System.Net.WebException ?? ex.InnerException as System.Net.WebException;
            if (w != null)
            {
                if (w.Status == System.Net.WebExceptionStatus.NameResolutionFailure || w.Status == System.Net.WebExceptionStatus.ConnectFailure)
                    return "텔레그램 서버에 연결할 수 없습니다. 회사 방화벽이 막고 있을 수 있습니다 — 아래 '연결 방법 · 프록시'에서 '연결 진단'을 누르고, 안 되면 외부 프록시를 지정해 보세요. (" + w.Status + ")";
                if (w.Status == System.Net.WebExceptionStatus.TrustFailure || w.Status == System.Net.WebExceptionStatus.SecureChannelFailure)
                    return "보안 연결(HTTPS)을 만들지 못했습니다. 회사 보안 프로그램이 텔레그램을 막고 있을 수 있습니다. (" + w.Status + ")";
                return "텔레그램에 연결하지 못했습니다: " + w.Message + " (" + w.Status + ")";
            }
            if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException || ex.InnerException is System.Net.Sockets.SocketException)
                return "텔레그램 서버에 연결할 수 없습니다: " + TgNet.Short(ex) + "\n아래 '연결 방법 · 프록시'에서 '연결 진단'을 눌러 보세요.";
            return "연결하지 못했습니다: " + TgNet.Short(ex) + "\n아래 '연결 방법 · 프록시'에서 '연결 진단'을 눌러 보세요.";
        }

        void RefreshTg()
        {
            if (_pairing != null) return;
            if (_tgError != null) { TgStatus.Text = _tgError; return; }
            var s = _core.Settings;
            bool linked = !string.IsNullOrWhiteSpace(s.TelegramToken) && s.TelegramChatId != 0;
            TgTestBtn.Visibility = TgUnlinkBtn.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
            TgConnectBtn.Content = linked ? "다시 연결" : "연결";
            if (!linked) { TgStatus.Text = "연결되지 않음 — 봇 토큰을 넣고 '연결'을 누르세요"; return; }
            var who = string.IsNullOrEmpty(s.TelegramWho) ? "" : s.TelegramWho;
            var bot = string.IsNullOrEmpty(s.TelegramBot) ? "" : " · 봇 @" + s.TelegramBot;
            var st = _core.Telegram?.State ?? "";
            var via = string.IsNullOrEmpty(TgNet.LastRoute) ? "" : " · " + TgNet.RouteLabel(TgNet.LastRoute);
            TgStatus.Text = "연결됨: " + who + bot + via + (st.Length > 0 && st != "연결됨" && s.TelegramEnabled ? "\n" + st : "");
        }

        async void TgConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_pairing != null) { _pairing.Cancel(); return; }
            _tgError = null;
            var token = ExtractToken(TgTokenBox.Text);
            TgTokenBox.Text = token;
            if (token.Length == 0 || !token.Contains(":"))
            {
                TgStatus.Text = "봇 토큰 형식이 아닙니다. 예) 123456789:ABCdef…";
                Dialog.Alert(this, "봇 토큰을 확인해 주세요", "텔레그램 @BotFather 에서 /newbot 으로 봇을 만들면 '123456789:ABC…' 형식의 토큰을 받을 수 있습니다.");
                TgTokenBox.Focus();
                return;
            }
            var prevNet = TgNet.Config;
            TgNet.Config = NetFromUi();
            var cts = _pairing = new System.Threading.CancellationTokenSource();
            MessageWindow notice = null;
            TgConnectBtn.Content = "취소";
            TgTestBtn.Visibility = TgUnlinkBtn.Visibility = Visibility.Collapsed;
            try
            {
                TgStatus.Text = "봇 확인 중…";
                var bot = await TelegramBridge.CheckToken(token, cts.Token);
                var code = new Random().Next(100000, 1000000).ToString();
                TgStatus.Text = $"텔레그램에서 @{bot} 을 열어 [시작]을 누르거나 '/start {code}' 를 보내 주세요. (3분 안에)";
                // 브라우저를 바로 여는 대신 프로그램 쪽지 창으로 안내 (링크 · 코드는 눌러서 열거나 복사)
                var link = $"https://t.me/{bot}?start={code}";
                notice = _core.ShowNotice("텔레그램 연결 안내",
                    $"내 봇 @{bot} 에게 아래 코드를 보내면 연결됩니다. (3분 안에)\n\n" +
                    $"**연결 코드: {code}**\n\n" +
                    $"① 휴대폰 텔레그램에서 @{bot} 을 찾아 대화를 열고\n" +
                    $"② /start {code} 를 보내세요.\n\n" +
                    $"PC 텔레그램이 있으면 아래 '텔레그램 열기'를 누르면 바로 열립니다.\n{link}",
                    "텔레그램 열기", () => Markdown.OpenUrl(link));
                var (chatId, who) = await _core.Telegram.Pair(token, code, cts.Token);
                try { notice?.Close(); } catch { }
                notice = null;

                var s = _core.Settings;
                s.TelegramToken = token;
                s.TelegramChatId = chatId;
                s.TelegramWho = who;
                s.TelegramBot = bot;
                s.TelegramEnabled = true;
                SaveNet(s);   // 연결에 성공한 방법을 저장
                Store.SaveSettings(s);
                TgChk.IsChecked = true;
                _pairing = null;
                try { await TelegramBridge.Welcome(token, chatId, s.Name, System.Threading.CancellationToken.None); } catch { }
                _core.Telegram.Apply();
                RefreshTg();
            }
            catch (Exception ex)
            {
                _pairing = null;
                try { notice?.Close(); } catch { }
                // 실패 이유는 다시 시도할 때까지 남겨 둠 (상태 갱신이 덮어쓰지 않도록)
                _tgError = cts.IsCancellationRequested ? null : "⚠️ " + Explain(ex);
                TgNet.Config = prevNet;
                _core.Telegram?.Apply();
                RefreshTg();
            }
            finally { if (_pairing == null) TgConnectBtn.Content = _core.Telegram?.Connected == true ? "다시 연결" : "연결"; }
        }

        async void TgTest_Click(object sender, RoutedEventArgs e)
        {
            TgTestBtn.IsEnabled = false;
            try { await _core.Telegram.SendTest(); TgStatus.Text = "테스트 알림을 보냈습니다. 텔레그램을 확인해 보세요."; }
            catch (Exception ex) { TgStatus.Text = "보내지 못했습니다: " + ex.Message; }
            TgTestBtn.IsEnabled = true;
        }

        void TgUnlink_Click(object sender, RoutedEventArgs e)
        {
            var s = _core.Settings;
            s.TelegramChatId = 0;
            s.TelegramWho = "";
            s.TelegramEnabled = false;
            Store.SaveSettings(s);
            TgChk.IsChecked = false;
            _core.Telegram?.Apply();
            RefreshTg();
        }

        internal static string[] LocalIps()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).Distinct().ToArray();
            }
            catch { return new string[0]; }
        }

        // ───────────── profile photo ─────────────

        void PickPhoto_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "프로필 사진 선택",
                Filter = "이미지 (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|모든 파일 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == true) LoadPhoto(dlg.FileName);
        }

        void Photo_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) LoadPhoto(f[0]);
        }

        void LoadPhoto(string path)
        {
            try
            {
                _photo = AvatarStore.Prepare(path);
                _photoChanged = true;
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(_photo);
                bi.EndInit();
                PhotoBrush.ImageSource = bi;
                RemovePhotoBtn.Visibility = Visibility.Visible;
            }
            catch
            {
                Dialog.Alert(this, "사진을 열 수 없습니다", "이미지 파일인지 확인해 주세요.");
            }
        }

        void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            _photo = null;
            _photoChanged = true;
            PhotoBrush.ImageSource = null;
            RemovePhotoBtn.Visibility = Visibility.Collapsed;
        }

        // ───────────── update ─────────────

        async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckBtn.IsEnabled = false;
            UpdateStatus.Text = "확인 중…";
            var msg = await _core.CheckForUpdates(manual: true);
            UpdateStatus.Text = msg ?? "";
            CheckBtn.IsEnabled = true;
        }

        void Changelog_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(AppInfo.RepoUrl + "/blob/main/CHANGELOG.md"); } catch { }
        }

        void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

        void PickDir_Click(object sender, RoutedEventArgs e)
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = DirBox.Text, Description = "받은 파일을 저장할 폴더" };
            if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) DirBox.Text = d.SelectedPath;
        }

        void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(DirBox.Text); Process.Start("explorer.exe", $"\"{DirBox.Text}\""); } catch { }
        }

        void DataDir_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", $"\"{Store.Dir}\""); } catch { }
        }

        void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_core.History.Count == 0) return;
            if (Dialog.Confirm(this, "쪽지함 비우기", $"주고받은 쪽지 {_core.History.Count}개를 모두 삭제합니다. 되돌릴 수 없습니다.", "모두 삭제", danger: true))
                _core.DeleteHistory(_core.History.ToList());
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var s = _core.Settings;
            var name = NameBox.Text.Trim();
            if (name.Length == 0) { NameBox.Focus(); return; }

            int.TryParse(PortBox.Text, out int port);
            if (port < 1024 || port > 65535)
            {
                Dialog.Alert(this, "포트 번호를 확인해 주세요", "1024 ~ 65535 사이의 숫자를 입력해야 합니다.");
                PortBox.Focus();
                return;
            }
            bool portChanged = port != s.Port;

            s.Name = name;
            s.Dept = DeptBox.Text.Trim();
            s.Note = NoteBox.Text.Trim();
            s.Sound = SoundChk.IsChecked == true;
            s.PopupNoFocus = NoFocusChk.IsChecked == true;
            var cc = ChatClosePanel.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            s.ChatAlertAutoClose = cc != null && int.TryParse((string)cc.Tag, out int csec) ? csec : 5;
            var pv = PrivacyPanel.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            s.AlertPrivacy = pv != null && int.TryParse((string)pv.Tag, out int pl) ? pl : 1;
            var ac = AutoClosePanel.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            s.PopupAutoClose = ac != null && int.TryParse((string)ac.Tag, out int sec) ? sec : 0;
            s.ReadReceipts = ReceiptChk.IsChecked == true;
            s.ShareTyping = TypingChk.IsChecked == true;
            s.EnterToSend = EnterChk.IsChecked == true;
            bool hk = HotkeyChk.IsChecked == true;
            if (hk != s.CaptureHotkey) { s.CaptureHotkey = hk; _core.ApplyHotkey(); }
            s.AutoAwayMinutes = int.TryParse(AwayBox.Text, out int m) ? Math.Max(0, m) : 10;
            s.AutoReply = AutoReplyBox.Text.Trim();
            s.AutoStart = AutoStartChk.IsChecked == true;
            s.StartHidden = HiddenChk.IsChecked == true;
            s.DownloadDir = DirBox.Text.Trim();
            s.Port = port;
            s.AutoUpdate = AutoUpdateChk.IsChecked == true;
            var tgToken = ExtractToken(TgTokenBox.Text);
            if (tgToken != (s.TelegramToken ?? "").Trim()) { s.TelegramToken = tgToken; s.TelegramChatId = 0; s.TelegramWho = ""; }
            s.TelegramEnabled = TgChk.IsChecked == true;
            s.TelegramNotes = TgNotesChk.IsChecked == true;
            s.TelegramChats = TgChatsChk.IsChecked == true;
            s.TelegramShowText = TgTextChk.IsChecked == true;
            s.TelegramOnlyAway = TgAwayChk.IsChecked == true;
            s.TelegramFiles = TgFilesChk.IsChecked == true;
            SaveNet(s);
            if (_photoChanged) s.AvatarHash = AvatarStore.SetMine(_photo);
            s.ManualHosts = HostsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

            if (!Store.CustomDir) Native.SetAutoStart(s.AutoStart);
            _core.ProfileChanged();
            Close();
            _core.Telegram?.Apply();

            if (portChanged)
            {
                if (Dialog.Confirm(null, "포트를 바꿨습니다", "새 포트는 프로그램을 다시 시작해야 적용됩니다. 지금 다시 시작할까요?", "다시 시작"))
                {
                    Process.Start(new ProcessStartInfo(Native.ExePath, "--restart" + (Store.CustomDir ? $" --data \"{Store.Dir}\"" : "")) { UseShellExecute = true });
                    _core.Quit();
                }
            }
            else _core.Rescan();
        }
    }
}
