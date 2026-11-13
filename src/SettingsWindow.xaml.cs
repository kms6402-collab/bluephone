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
            ReceiptChk.IsChecked = s.ReadReceipts;
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
            ExeText.Text = $"프로세스 이름  {Process.GetCurrentProcess().ProcessName}\n실행 파일  {Native.ExePath}";
            MyIps.Text = "이 PC 주소  " + string.Join(", ", LocalIps());

            Loaded += (o, e) => NameBox.Focus();
        }

        static string[] LocalIps()
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
            s.ReadReceipts = ReceiptChk.IsChecked == true;
            s.AutoAwayMinutes = int.TryParse(AwayBox.Text, out int m) ? Math.Max(0, m) : 10;
            s.AutoReply = AutoReplyBox.Text.Trim();
            s.AutoStart = AutoStartChk.IsChecked == true;
            s.StartHidden = HiddenChk.IsChecked == true;
            s.DownloadDir = DirBox.Text.Trim();
            s.Port = port;
            s.AutoUpdate = AutoUpdateChk.IsChecked == true;
            if (_photoChanged) s.AvatarHash = AvatarStore.SetMine(_photo);
            s.ManualHosts = HostsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

            if (!Store.CustomDir) Native.SetAutoStart(s.AutoStart);
            _core.ProfileChanged();
            Close();

            if (portChanged)
            {
                if (Dialog.Confirm(null, "포트를 바꿨습니다", "새 포트는 프로그램을 다시 시작해야 적용됩니다. 지금 다시 시작할까요?", "다시 시작"))
                {
                    Process.Start(Native.ExePath, "--restart" + (Store.CustomDir ? $" --data \"{Store.Dir}\"" : ""));
                    _core.Quit();
                }
            }
            else _core.Rescan();
        }
    }
}
