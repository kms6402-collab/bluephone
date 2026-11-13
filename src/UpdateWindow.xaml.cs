using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace BluePhone
{
    public partial class UpdateWindow : Window
    {
        enum Mode { Available, Ready, WhatsNew }

        readonly Core _core;
        readonly ReleaseInfo _release;
        Mode _mode;
        bool _busy;

        /// <summary>새 버전 안내 (아직 받지 않았거나, 이미 받아 둔 상태)</summary>
        public UpdateWindow(Core core, ReleaseInfo r)
        {
            _core = core;
            _release = r;
            InitializeComponent();
            Chrome.Apply(this, caption: 40, resizable: false);
            Notes.Text = Updater.PlainNotes(r.Notes);
            if (string.IsNullOrWhiteSpace(Notes.Text)) Notes.Text = "변경 사항이 등록되지 않았습니다.";
            SetMode(Updater.Ready == r ? Mode.Ready : Mode.Available);
            KeyDown += (s, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
        }

        /// <summary>업데이트 직후 첫 실행 시 변경 사항 안내</summary>
        public UpdateWindow(Core core, string version, string notes)
        {
            _core = core;
            InitializeComponent();
            Chrome.Apply(this, caption: 40, resizable: false);
            Notes.Text = Updater.PlainNotes(notes);
            Heading.Text = $"{version} 버전으로 업데이트했습니다";
            SubHeading.Text = "이번 버전에서 바뀐 내용입니다.";
            SetMode(Mode.WhatsNew);
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        void SetMode(Mode m)
        {
            _mode = m;
            switch (m)
            {
                case Mode.Available:
                    Heading.Text = $"새 버전 {_release.VersionText} 이 나왔습니다";
                    SubHeading.Text = $"지금 버전 {AppInfo.VersionText} · 파일 크기 {FileItem.FormatSize(_release.Size)}";
                    PrimaryText.Text = "지금 업데이트";
                    LaterBtn.Content = "나중에";
                    SkipLink.Visibility = Visibility.Visible;
                    break;
                case Mode.Ready:
                    Heading.Text = $"{_release.VersionText} 업데이트 준비 완료";
                    SubHeading.Text = "다시 시작하면 바로 적용됩니다. 나중에를 누르면 프로그램을 종료할 때 적용됩니다.";
                    PrimaryText.Text = "지금 다시 시작";
                    LaterBtn.Content = "나중에";
                    SkipLink.Visibility = Visibility.Collapsed;
                    ProgressPane.Visibility = Visibility.Collapsed;
                    break;
                case Mode.WhatsNew:
                    PrimaryText.Text = "확인";
                    LaterBtn.Visibility = Visibility.Collapsed;
                    SkipLink.Text = "GitHub에서 보기";
                    break;
            }
        }

        async void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (_mode == Mode.WhatsNew) { Close(); return; }
            if (_mode == Mode.Ready) { _core.ApplyUpdateAndRestart(); return; }
            if (_busy) return;

            _busy = true;
            PrimaryBtn.IsEnabled = false;
            LaterBtn.IsEnabled = false;
            SkipLink.Visibility = Visibility.Collapsed;
            Error.Visibility = Visibility.Collapsed;
            ProgressPane.Visibility = Visibility.Visible;
            PrimaryText.Text = "받는 중…";
            var prog = new Progress<double>(v =>
            {
                Bar.Value = v;
                ProgressText.Text = $"{(int)(v * 100)}%  ·  {FileItem.FormatSize((long)(v * _release.Size))} / {FileItem.FormatSize(_release.Size)}";
            });
            try
            {
                await Updater.DownloadAsync(_release, prog);
                ProgressText.Text = "무결성 확인 완료";
                _busy = false;
                PrimaryBtn.IsEnabled = true;
                LaterBtn.IsEnabled = true;
                SetMode(Mode.Ready);
            }
            catch (Exception ex)
            {
                _busy = false;
                PrimaryBtn.IsEnabled = true;
                LaterBtn.IsEnabled = true;
                PrimaryText.Text = "다시 시도";
                ProgressPane.Visibility = Visibility.Collapsed;
                Error.Text = ex.Message;
                Error.Visibility = Visibility.Visible;
            }
        }

        void Later_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) Close();
        }

        void Skip_Click(object sender, MouseButtonEventArgs e)
        {
            if (_mode == Mode.WhatsNew)
            {
                try { Process.Start(AppInfo.RepoUrl + "/releases"); } catch { }
                return;
            }
            _core.Settings.SkippedVersion = _release.VersionText;
            Store.SaveSettings(_core.Settings);
            Close();
        }
    }
}
