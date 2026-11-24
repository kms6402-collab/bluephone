using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace BluePhone
{
    /// <summary>새 버전 안내 · 받기 · 설치 / 업데이트 직후 변경 사항</summary>
    public sealed class UpdateWindow : Window
    {
        enum Mode { Available, Ready, WhatsNew }

        readonly Core _core;
        readonly ReleaseInfo _release;
        Mode _mode;
        bool _busy;

        TextBlock _heading, _sub, _notes, _progressText, _error, _primaryText, _skip;
        ProgressBar _bar;
        StackPanel _progressPane;
        Button _primary, _later;

        public UpdateWindow(Core core, ReleaseInfo r)
        {
            _core = core;
            _release = r;
            Init();
            _notes.Text = Updater.PlainNotes(r.Notes);
            if (string.IsNullOrWhiteSpace(_notes.Text)) _notes.Text = "변경 사항이 등록되지 않았습니다.";
            SetMode(Updater.Ready == r ? Mode.Ready : Mode.Available);
        }

        public UpdateWindow(Core core, string version, string notes)
        {
            _core = core;
            Init();
            _notes.Text = Updater.PlainNotes(notes);
            _heading.Text = $"{version} 버전으로 업데이트했습니다";
            _sub.Text = "이번 버전에서 바뀐 내용입니다.";
            SetMode(Mode.WhatsNew);
        }

        void Init()
        {
            Title = "업데이트";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Cls("app");
            Icons.Apply(this);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
            p.Children.Add(Ui.Text(AppInfo.Name, 13, FontWeight.SemiBold, "Muted"));
            _heading = Ui.Text("", 18, FontWeight.SemiBold, wrap: true);
            _heading.Margin = new Thickness(0, 8, 0, 0);
            _sub = Ui.Text("", 12, fg: "Muted", wrap: true);
            _sub.Margin = new Thickness(0, 4, 0, 0);
            var lbl = Ui.Text("변경 사항", 12, fg: "Ink2");
            lbl.Margin = new Thickness(0, 18, 0, 8);
            _notes = Ui.Text("", 13, fg: "Ink2", wrap: true);
            _notes.LineHeight = 21;
            var nb = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12), MaxHeight = 300, Child = new ScrollViewer { Content = _notes } };
            nb.Res(Border.BackgroundProperty, "Canvas");
            _bar = new ProgressBar { Height = 4, MinHeight = 4, Maximum = 1 };
            _bar.Res(ProgressBar.ForegroundProperty, "Accent");
            _progressText = Ui.Text("", 12, fg: "Muted");
            _progressText.Margin = new Thickness(0, 6, 0, 0);
            _progressPane = new StackPanel { Margin = new Thickness(0, 14, 0, 0), IsVisible = false, Children = { _bar, _progressText } };
            _error = Ui.Text("", 13, fg: "Danger", wrap: true);
            _error.Margin = new Thickness(0, 12, 0, 0); _error.IsVisible = false;
            foreach (var c in new Control[] { _heading, _sub, lbl, nb, _progressPane, _error }) p.Children.Add(c);

            _skip = Ui.Text("이 버전 건너뛰기", 12, fg: "Muted").Dock(Dock.Left);
            _skip.VerticalAlignment = VerticalAlignment.Center;
            _skip.TextDecorations = TextDecorations.Underline;
            _skip.Cursor = new Cursor(StandardCursorType.Hand);
            _skip.PointerReleased += (s, e) => Skip();
            _primaryText = new TextBlock();
            _primary = new Button { Content = _primaryText, MinWidth = 110, Height = 34 }.Cls("primary").Dock(Dock.Right);
            _primary.Click += (s, e) => Primary();
            _later = new Button { MinWidth = 76, Height = 34, Margin = new Thickness(0, 0, 8, 0) }.Cls("outline").Dock(Dock.Right);
            _later.Click += (s, e) => { if (!_busy) Close(); };
            var bar = new DockPanel { Margin = new Thickness(24, 20, 24, 20), LastChildFill = false, Children = { _skip, _primary, _later } };

            var root = new StackPanel { Children = { p, bar } };
            root.Res(Panel.BackgroundProperty, "Surface");
            Content = root;
            KeyDown += (s, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        void SetMode(Mode m)
        {
            _mode = m;
            switch (m)
            {
                case Mode.Available:
                    _heading.Text = $"새 버전 {_release.VersionText} 이 나왔습니다";
                    _sub.Text = $"지금 버전 {AppInfo.VersionText} · 파일 크기 {FileItem.FormatSize(_release.Size)}";
                    _primaryText.Text = "지금 업데이트";
                    _later.Content = "나중에";
                    _skip.IsVisible = true;
                    break;
                case Mode.Ready:
                    _heading.Text = $"{_release.VersionText} 업데이트 준비 완료";
                    _sub.Text = "설치를 누르면 설치 프로그램이 열리고, 파란전화기는 잠시 종료됩니다. 설치가 끝나면 다시 실행됩니다.";
                    _primaryText.Text = "지금 설치";
                    _later.Content = "나중에";
                    _skip.IsVisible = false;
                    _progressPane.IsVisible = false;
                    break;
                case Mode.WhatsNew:
                    _primaryText.Text = "확인";
                    _later.IsVisible = false;
                    _skip.Text = "GitHub에서 보기";
                    break;
            }
        }

        async void Primary()
        {
            if (_mode == Mode.WhatsNew) { Close(); return; }
            if (_mode == Mode.Ready) { _core.ApplyUpdateAndRestart(); return; }
            if (_busy) return;
            _busy = true;
            _primary.IsEnabled = _later.IsEnabled = false;
            _skip.IsVisible = false;
            _error.IsVisible = false;
            _progressPane.IsVisible = true;
            _primaryText.Text = "받는 중…";
            var prog = new Progress<double>(v =>
            {
                _bar.Value = v;
                _progressText.Text = $"{(int)(v * 100)}%  ·  {FileItem.FormatSize((long)(v * _release.Size))} / {FileItem.FormatSize(_release.Size)}";
            });
            try
            {
                await Updater.DownloadAsync(_release, prog);
                _busy = false;
                _primary.IsEnabled = _later.IsEnabled = true;
                SetMode(Mode.Ready);
            }
            catch (Exception ex)
            {
                _busy = false;
                _primary.IsEnabled = _later.IsEnabled = true;
                _primaryText.Text = "다시 시도";
                _progressPane.IsVisible = false;
                _error.Text = Updater.Explain(ex);
                _error.IsVisible = true;
            }
        }

        void Skip()
        {
            if (_mode == Mode.WhatsNew) { Native.Open(AppInfo.RepoUrl + "/releases"); return; }
            _core.Settings.SkippedVersion = _release.VersionText;
            Store.SaveSettings(_core.Settings);
            Close();
        }
    }
}
