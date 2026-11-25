using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace BluePhone
{
    /// <summary>설정 창 (프로필 · 알림 · 텔레그램 · 자리 비움 · 시작 · 받은 파일 · 네트워크 · 정보)</summary>
    public sealed class SettingsWindow : Window
    {
        readonly Core _core;
        byte[] _photo;
        bool _photoChanged;
        CancellationTokenSource _pairing;
        string _tgError;

        TextBox _name, _dept, _note, _away, _autoReply, _dir, _port, _hosts, _tgToken, _tgProxy, _tgUser, _tgPass, _tgApi;
        CheckBox _sound, _noFocus, _receipt, _typing, _enter, _hotkey, _autoStart, _hidden, _autoUpdate, _tg, _tgNotes, _tgChats, _tgText, _tgFiles, _tgAway;
        Chips _autoClose, _chatClose, _privacy, _tgNet;
        Avatar _photoView;
        Button _removePhoto, _tgConnect, _tgTest, _tgUnlink, _tgDiag, _checkBtn;
        TextBlock _tgStatus, _tgDiagText, _updateStatus;
        StackPanel _tgPanel, _proxyPanel;
        Expander _tgNetExp;

        public static string[] LocalIps() => Core.LocalIps();

        public SettingsWindow(Core core)
        {
            _core = core;
            Title = "설정";
            Width = 480; Height = 680; MinWidth = 400; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Cls("app");
            Icons.Apply(this);
            Content = Build();
            Load();
            if (core.Telegram != null) core.Telegram.StateChanged += RefreshTgSafe;
            Closed += (o, e) =>
            {
                if (core.Telegram != null) core.Telegram.StateChanged -= RefreshTgSafe;
                if (_pairing != null) { _pairing.Cancel(); _pairing = null; core.Telegram?.Apply(); }
            };
            Opened += (o, e) => _name.Focus();
            AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Escape || (e.Key == Key.W && Keys.Primary(e.KeyModifiers))) { Close(); e.Handled = true; }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        void RefreshTgSafe() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshTg);

        // ───────────── 화면 구성 도우미 ─────────────

        sealed class Chips
        {
            public readonly WrapPanel Panel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            readonly List<(ToggleButton b, string v)> _items = new List<(ToggleButton, string)>();
            public event Action Changed;
            public Chips(params (string label, string value)[] items)
            {
                foreach (var (label, value) in items)
                {
                    var b = new ToggleButton { Content = Ui.Text(label, 12), Margin = new Thickness(0, 0, 6, 6) }.Cls("filter");
                    b.Click += (s, e) => { Value = value; Changed?.Invoke(); };
                    _items.Add((b, value));
                    Panel.Children.Add(b);
                }
            }
            public string Value
            {
                get => _items.FirstOrDefault(i => i.b.IsChecked == true).v;
                set { foreach (var i in _items) i.b.IsChecked = i.v == value; if (!_items.Any(i => i.v == value) && _items.Count > 0) _items[0].b.IsChecked = true; }
            }
        }

        static TextBlock Section(string t, bool first = false)
        {
            var x = Ui.Text(t, 13.5, FontWeight.SemiBold);
            x.Margin = first ? new Thickness(0, 8, 0, 12) : new Thickness(0, 26, 0, 12);
            return x;
        }

        static TextBlock Help(string t, double top = 2)
        {
            var x = Ui.Text(t, 12, fg: "Muted", wrap: true);
            x.Margin = new Thickness(0, top, 0, 0);
            return x;
        }

        static TextBlock Label(string t, double top = 0)
        {
            var x = Ui.Text(t, 12, fg: "Ink2");
            x.Margin = new Thickness(0, top, 0, 6);
            return x;
        }

        static Border RowBox(Control c, bool top = false)
        {
            var b = new Border { BorderThickness = new Thickness(0, top ? 1 : 0, 0, 1), Padding = new Thickness(0, 11), Child = c };
            return b.Res(Border.BorderBrushProperty, "Line");
        }

        static CheckBox Switch(string title, string help = null)
        {
            var sp = new StackPanel { Children = { new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap } } };
            if (help != null) sp.Children.Add(Help(help));
            return new CheckBox { Content = sp, HorizontalAlignment = HorizontalAlignment.Stretch }.Cls("switch");
        }

        static TextBox Field(string watermark = null, int max = 0)
        {
            var t = new TextBox { Watermark = watermark }.Cls("field");
            if (max > 0) t.MaxLength = max;
            return t;
        }

        static Button Btn(string text, string cls, Action click, double h = 28)
        {
            var b = new Button { Content = text, Height = h, Padding = new Thickness(12, 0) }.Cls(cls);
            b.Click += (s, e) => click();
            return b;
        }

        static void DigitsOnly(TextBox t) => t.AddHandler(TextInputEvent, (s, e) => { if (e.Text != null && !e.Text.All(char.IsDigit)) e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Control Build()
        {
            var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            root.Res(Panel.BackgroundProperty, "Surface");
            var p = new StackPanel();
            var mod = Native.IsMac ? "⌘" : "Ctrl+";

            // 프로필
            p.Children.Add(Section("프로필", true));
            _photoView = new Avatar { Size = 68, Cursor = new Cursor(StandardCursorType.Hand) }.Tip("사진 선택");
            _photoView.PointerReleased += (s, e) => PickPhoto();
            _removePhoto = Btn("삭제", "ghost", RemovePhoto, 30);
            _removePhoto.Res(Button.ForegroundProperty, "Danger");
            var prof = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 16), Background = Brushes.Transparent };
            prof.Children.Add(_photoView);
            prof.Children.Add(new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Btn("사진 선택", "outline", PickPhoto, 30), _removePhoto } },
                Help("JPG · PNG · GIF · HEIC. 가운데를 정사각형으로 맞춰 동료들에게 보여집니다.", 8),
            } }.Col(1));
            DragDrop.SetAllowDrop(prof, true);
            prof.AddHandler(DragDrop.DropEvent, (s, e) =>
            {
                var f = e.Data.GetFiles()?.Select(x => x.TryGetLocalPath()).FirstOrDefault(x => x != null);
                if (f != null) LoadPhoto(f);
            });
            p.Children.Add(prof);
            _name = Field(max: 30); _dept = Field("예: 영업2팀", 30); _note = Field("동료 목록에 이름 아래로 표시됩니다", 60);
            var nd = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
            nd.Children.Add(new StackPanel { Children = { Label("이름"), _name } });
            nd.Children.Add(new StackPanel { Children = { Label("부서"), _dept } }.Col(2));
            p.Children.Add(nd);
            p.Children.Add(Label("상태 메시지", 14));
            p.Children.Add(_note);
            _name.TextChanged += (o, e) => { var n = (_name.Text ?? "").Trim(); _photoView.Initial = n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?"; };

            // 알림
            p.Children.Add(Section("알림"));
            p.Children.Add(RowBox(_sound = Switch("알림음", "긴급 쪽지는 다른 소리로 알립니다"), true));
            p.Children.Add(RowBox(_noFocus = Switch("작업 중에는 쪽지 창이 입력을 가로채지 않음", "쪽지 창은 화면 오른쪽 아래에 뜨고, 클릭해야 활성화됩니다")));
            _autoClose = new Chips(("닫지 않음", "0"), ("5초", "5"), ("10초", "10"), ("30초", "30"), ("1분", "60"));
            p.Children.Add(RowBox(new StackPanel { Children = { new TextBlock { Text = "받은 쪽지 창 자동으로 닫기" },
                Help("정한 시간이 지나면 창이 사라지고 쪽지는 쪽지함에 '안 읽음'으로 남습니다. 마우스를 올리거나 답장 중이면 기다리고, 긴급 쪽지는 닫지 않습니다."), _autoClose.Panel } }));
            _chatClose = new Chips(("닫지 않음", "0"), ("5초", "5"), ("10초", "10"), ("30초", "30"), ("1분", "60"));
            p.Children.Add(RowBox(new StackPanel { Children = { new TextBlock { Text = "1:1 대화 알림 창 자동으로 닫기" },
                Help("대화 창에서 보낸 메시지가 올 때 뜨는 알림 창입니다. 닫혀도 메시지는 대화 창에 '안 읽음'으로 남습니다."), _chatClose.Panel } }));
            _privacy = new Chips(("보낸 사람, 메시지 내용 모두 표시", "0"), ("보낸 사람만 표시", "1"), ("보낸 사람, 메시지 내용 모두 숨기기", "2"));
            p.Children.Add(RowBox(new StackPanel { Children = { new TextBlock { Text = "알림 개인정보 보호" },
                Help("쪽지·대화 알림이 뜰 때 화면에 보여 줄 범위입니다. 가려진 알림은 누르거나 '내용 보기'를 눌러야 보이고, 그 전에는 읽음으로 처리하지 않습니다."), _privacy.Panel } }));
            p.Children.Add(RowBox(_receipt = Switch("읽음 확인 보내기", "쪽지를 열면 보낸 사람에게 '읽음'으로 표시됩니다")));
            p.Children.Add(RowBox(_typing = Switch("입력 중 표시 보내기", "대화 창에서 글자를 입력하는 동안 상대 대화 창에 '입력 중…'으로 표시됩니다")));
            p.Children.Add(RowBox(_enter = Switch("대화 창에서 Enter 로 보내기", $"끄면 {mod}Enter 로 보내고, Enter 는 줄바꿈이 됩니다")));
            p.Children.Add(RowBox(_hotkey = Switch(Native.IsMac ? "화면 캡처 단축키 ⌃⌥S" : "화면 캡처 단축키 Ctrl+Alt+S", "어디서든 영역을 드래그해 바로 쪽지에 첨부합니다")));

            // 텔레그램
            p.Children.Add(Section("텔레그램"));
            p.Children.Add(RowBox(_tg = Switch("텔레그램으로 알림 받기", "받은 쪽지 · 대화를 내 텔레그램으로 보내고, 텔레그램의 버튼 메뉴로 동료를 골라 보낼 수 있습니다"), true));
            _tgPanel = new StackPanel();
            _tgToken = Field("123456789:ABCdef…  (@BotFather 에서 받은 토큰)").Cls("code");
            _tgConnect = new Button { Content = "연결", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) }.Cls("primary").Dock(Dock.Right);
            _tgConnect.Click += (s, e) => TgConnect();
            _tgPanel.Children.Add(Label("봇 토큰", 14));
            _tgPanel.Children.Add(new DockPanel { Children = { _tgConnect, _tgToken } });
            _tgPanel.Children.Add(Help("① 텔레그램에서 @BotFather 에게 /newbot 을 보내 내 봇을 만들고 토큰을 붙여 넣습니다. ② '연결'을 누르고 안내에 나온 코드를 내 봇에게 보내면 끝입니다. 봇 하나는 PC 한 대에서만 쓰세요.", 6));
            _tgTest = Btn("테스트", "outline", TgTest);
            _tgUnlink = Btn("연결 해제", "ghost", TgUnlink);
            _tgUnlink.Res(Button.ForegroundProperty, "Danger");
            _tgStatus = Ui.Text("", 13, fg: "Ink2", wrap: true);
            _tgStatus.VerticalAlignment = VerticalAlignment.Center;
            var stBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Margin = new Thickness(0, 10, 0, 0),
                Child = new DockPanel { Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _tgTest, _tgUnlink } }.Dock(Dock.Right), _tgStatus } } };
            stBox.Res(Border.BackgroundProperty, "Canvas").Res(Border.BorderBrushProperty, "Line");
            _tgPanel.Children.Add(stBox);

            // 연결 방법
            _tgNet = new Chips(("자동 (권장)", "auto"), (Native.IsMac ? "macOS 기본" : "Windows 기본", "system"), ("직접 연결", "direct"), ("HTTP 프록시", "http"), ("SOCKS5 프록시", "socks5"));
            _tgNet.Changed += UpdateProxyPanel;
            _tgProxy = Field("예) 203.0.113.10:1080  ·  proxy.example.com:8080");
            _tgUser = Field("사용자 (없으면 비워 두기)");
            _tgPass = new TextBox { PasswordChar = '•', Watermark = "비밀번호" }.Cls("field");
            var up = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Margin = new Thickness(0, 8, 0, 0) };
            up.Children.Add(_tgUser); up.Children.Add(_tgPass.Col(2));
            _proxyPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { Label("프록시 주소"), _tgProxy, up } };
            _tgApi = Field("비워 두면 https://api.telegram.org");
            _tgDiag = Btn("연결 진단", "outline", TgDiag);
            _tgDiagText = Help("", 8);
            _tgDiagText.IsVisible = false;
            _tgDiagText.Res(TextBlock.FontFamilyProperty, "MonoFont");
            var net = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children =
            {
                _tgNet.Panel,
                Help("자동: 시스템 기본 방식(시스템 프록시)과 직접 연결을 차례로 시도하고, 되는 방법을 기억합니다."),
                _proxyPanel,
                Label("API 서버 주소 (선택)", 10), _tgApi,
                Help("텔레그램 주소 자체가 막혀 있으면, 텔레그램 Bot API 를 그대로 전달하는 중계 서버(예: 내 Cloudflare Worker) 주소를 넣을 수 있습니다.", 4),
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { _tgDiag, WithLeft(Ui.Text("각 방법으로 텔레그램 서버에 닿는지 시험합니다", 12, fg: "Muted"), 10) } },
                _tgDiagText,
            } };
            _tgPanel.Children.Add(_tgNetExp = new Expander { Header = Ui.Text("연결 방법 · 프록시 (연결이 안 될 때)", 13, FontWeight.SemiBold), Content = net, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch });
            var notes = RowBox(_tgNotes = Switch("받은 쪽지 보내기"));
            notes.Margin = new Thickness(0, 8, 0, 0);
            _tgPanel.Children.Add(notes);
            _tgPanel.Children.Add(RowBox(_tgChats = Switch("받은 1:1 대화 보내기")));
            _tgPanel.Children.Add(RowBox(_tgText = Switch("메시지 내용 포함", "끄면 보낸 사람만 알리고 내용은 PC에서 확인합니다")));
            _tgPanel.Children.Add(RowBox(_tgFiles = Switch("첨부파일도 보내기", "받은 파일 · 사진을 보낸 PC에서 받아 텔레그램에 올립니다 (50MB 까지). '메시지 내용 포함'을 끄면 보내지 않습니다")));
            _tgPanel.Children.Add(RowBox(_tgAway = Switch("자리를 비웠을 때만 보내기", "상태가 '대화 가능'이 아니거나 3분 넘게 컴퓨터를 쓰지 않을 때만 보냅니다")));
            _tgPanel.Children.Add(Help("텔레그램에서: 아래쪽 📋 메뉴 · ✉️ 쪽지 · 💬 대화 버튼을 누르고 동료를 골라 내용(사진 · 파일 포함, 20MB 까지)을 보내면 됩니다. 받은 알림 아래 버튼으로 바로 답장할 수 있습니다. 이 컴퓨터에서 파란전화기가 실행 중일 때만 동작합니다.", 8));
            p.Children.Add(_tgPanel);
            _tg.IsCheckedChanged += (s, e) => _tgPanel.IsVisible = _tg.IsChecked == true;

            // 자리 비움
            p.Children.Add(Section("자리 비움"));
            _away = Field(max: 3); _away.Width = 64; _away.HorizontalContentAlignment = HorizontalAlignment.Center; DigitsOnly(_away);
            p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _away, WithLeft(Ui.Text("분 동안 입력이 없으면 자리 비움으로 전환", 13, fg: "Ink2"), 10) } });
            p.Children.Add(Help("0 으로 두면 자동 전환하지 않습니다", 6));
            p.Children.Add(Label("자동응답", 16));
            _autoReply = new TextBox { Watermark = "비워 두면 사용 안 함", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxLength = 300, VerticalContentAlignment = VerticalAlignment.Top }.Cls("field");
            p.Children.Add(_autoReply);
            p.Children.Add(Help("'대화 가능'이 아닐 때 받은 쪽지에 이 내용으로 자동 답장합니다", 6));

            // 시작
            p.Children.Add(Section("시작"));
            p.Children.Add(RowBox(_autoStart = Switch(Native.IsMac ? "로그인할 때 실행" : "Windows 시작 시 실행", Native.IsMac ? "macOS 에 로그인하면 메뉴 막대에서 조용히 시작합니다" : null), true));
            p.Children.Add(RowBox(_hidden = Switch(Native.IsMac ? "실행할 때 창 없이 메뉴 막대에서 시작" : "실행할 때 창 없이 트레이에서 시작")));

            // 받은 파일
            p.Children.Add(Section("받은 파일"));
            _dir = Field(); _dir.IsReadOnly = true;
            p.Children.Add(new DockPanel { Children =
            {
                Btn("변경", "outline", PickDir, 32).Also(b => b.Margin = new Thickness(8, 0, 0, 0)).Dock(Dock.Right),
                Btn("열기", "outline", () => { try { Directory.CreateDirectory(_dir.Text); Native.Open(_dir.Text); } catch { } }, 32).Also(b => b.Margin = new Thickness(8, 0, 0, 0)).Dock(Dock.Right),
                _dir,
            } });
            p.Children.Add(Help("Shift 를 누른 채 '받기'를 누르면 위치를 직접 고를 수 있습니다", 6));

            // 네트워크
            p.Children.Add(Section("네트워크"));
            _port = Field(max: 5); _port.Width = 80; _port.HorizontalContentAlignment = HorizontalAlignment.Center; DigitsOnly(_port);
            var portRow = new DockPanel();
            portRow.Children.Add(Ui.Text("포트", 13, fg: "Ink2").Also(t => { t.Width = 40; t.VerticalAlignment = VerticalAlignment.Center; }).Dock(Dock.Left));
            portRow.Children.Add(_port.Dock(Dock.Left));
            portRow.Children.Add(Help("자동 찾기는 같은 포트끼리만 됩니다. 포트가 다른 컴퓨터는 아래에 '주소:포트'로 추가하세요", 0).Also(t => { t.Margin = new Thickness(10, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
            p.Children.Add(portRow);
            p.Children.Add(Label("직접 추가할 컴퓨터", 16));
            _hosts = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 74, VerticalContentAlignment = VerticalAlignment.Top, Watermark = "한 줄에 하나씩 · 192.168.10.25:9082 (포트 생략 시 내 포트)" }.Cls("field", "code");
            p.Children.Add(_hosts);
            p.Children.Add(Help("다른 대역 · 포트가 다른 컴퓨터 · UDP 가 막힌 컴퓨터처럼 자동으로 찾지 못하는 곳을 등록합니다. 메인 창 ⋯ 메뉴 › IP 주소로 동료 추가로도 넣을 수 있습니다", 6));
            p.Children.Add(Help("이 컴퓨터 주소  " + string.Join(", ", LocalIps().Select(ip => $"{ip}:{_core.Settings.Port}")) + "  — 동료가 이 주소로 나를 추가할 수 있습니다", 10));

            // 정보
            p.Children.Add(Section("정보"));
            _autoUpdate = Switch("자동 업데이트", "GitHub에 새 버전이 올라오면 받아 두고 변경 사항과 함께 알려 줍니다");
            _autoUpdate.Margin = new Thickness(0, 14, 0, 0);
            _checkBtn = Btn("업데이트 확인", "primary", CheckUpdate);
            _updateStatus = Ui.Text("", 12, fg: "Muted", wrap: true);
            _updateStatus.VerticalAlignment = VerticalAlignment.Center; _updateStatus.Margin = new Thickness(10, 0, 0, 0);
            var about = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12), Child = new StackPanel { Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.Text(AppInfo.Name, 13, FontWeight.SemiBold), WithLeft(Ui.Text("버전 " + AppInfo.VersionText, 12, fg: "Muted"), 6) } },
                Help($"실행 파일  {(Native.IsMac ? Native.BundlePath ?? Native.ExePath : Native.ExePath)}\n데이터 폴더  {Store.Dir}", 10),
                _autoUpdate,
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Spacing = 8, Children =
                {
                    _checkBtn,
                    Btn("변경 이력", "outline", () => Native.Open(AppInfo.RepoUrl + "/blob/main/CHANGELOG.md")),
                    _updateStatus,
                } },
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Spacing = 8, Children =
                {
                    Btn("데이터 폴더", "outline", () => Native.Open(Store.Dir)),
                    Btn("쪽지함 비우기", "outline", ClearHistory),
                } },
            } } };
            about.Res(Border.BackgroundProperty, "Canvas");
            p.Children.Add(about);

            p.Margin = new Thickness(24, 0, 20, 24);
            var sv = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(sv);
            var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12), Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children =
            {
                Btn("취소", "outline", Close, 32).Also(b => b.MinWidth = 76),
                Btn("저장", "primary", Save, 32).Also(b => b.MinWidth = 76),
            } } }.Row(1);
            footer.Res(Border.BorderBrushProperty, "Line");
            root.Children.Add(footer);
            return root;
        }

        static T WithLeft<T>(T c, double l) where T : Control { c.Margin = new Thickness(l, 0, 0, 0); return c; }

        void Load()
        {
            var s = _core.Settings;
            _name.Text = s.Name; _dept.Text = s.Dept; _note.Text = s.Note;
            _sound.IsChecked = s.Sound;
            _noFocus.IsChecked = s.PopupNoFocus;
            _autoClose.Value = s.PopupAutoClose.ToString();
            _chatClose.Value = s.ChatAlertAutoClose.ToString();
            if (_chatClose.Value == null) _chatClose.Value = "5";
            _privacy.Value = s.AlertPrivacy.ToString();
            _receipt.IsChecked = s.ReadReceipts;
            _typing.IsChecked = s.ShareTyping;
            _enter.IsChecked = s.EnterToSend;
            _hotkey.IsChecked = s.CaptureHotkey;
            _away.Text = s.AutoAwayMinutes.ToString();
            _autoReply.Text = s.AutoReply;
            _autoStart.IsChecked = s.AutoStart;
            _hidden.IsChecked = s.StartHidden;
            _dir.Text = Store.DownloadDir(s);
            _port.Text = s.Port.ToString();
            _hosts.Text = string.Join("\n", s.ManualHosts ?? new List<string>());
            _autoUpdate.IsChecked = s.AutoUpdate;
            _photoView.Fill = Palette.Avatar(s.Id);
            _photoView.Initial = string.IsNullOrEmpty(s.Name) ? "?" : s.Name.Substring(0, 1).ToUpperInvariant();
            _photoView.Image = AvatarStore.Mine;
            _removePhoto.IsVisible = AvatarStore.Mine != null;

            _tg.IsChecked = s.TelegramEnabled;
            _tgPanel.IsVisible = s.TelegramEnabled;
            _tgToken.Text = s.TelegramToken ?? "";
            _tgNotes.IsChecked = s.TelegramNotes;
            _tgChats.IsChecked = s.TelegramChats;
            _tgText.IsChecked = s.TelegramShowText;
            _tgAway.IsChecked = s.TelegramOnlyAway;
            _tgFiles.IsChecked = s.TelegramFiles;
            _tgNet.Value = string.IsNullOrEmpty(s.TelegramNet) ? "auto" : s.TelegramNet;
            _tgProxy.Text = s.TelegramProxy ?? "";
            _tgUser.Text = s.TelegramProxyUser ?? "";
            _tgPass.Text = Secret.Unprotect(s.TelegramProxyPass);
            _tgApi.Text = s.TelegramApi ?? "";
            _tgNetExp.IsExpanded = (s.TelegramNet ?? "auto") != "auto" || !string.IsNullOrWhiteSpace(s.TelegramApi);
            UpdateProxyPanel();
            RefreshTg();
        }

        // ───────────── 텔레그램 ─────────────

        void UpdateProxyPanel() { var m = _tgNet.Value; _proxyPanel.IsVisible = m == "http" || m == "socks5"; }

        TgNetConfig NetFromUi() => new TgNetConfig
        {
            Mode = _tgNet.Value ?? "auto",
            Proxy = (_tgProxy.Text ?? "").Trim(),
            User = (_tgUser.Text ?? "").Trim(),
            Pass = _tgPass.Text ?? "",
            Api = (_tgApi.Text ?? "").Trim(),
        };

        void SaveNet(AppSettings s)
        {
            var c = NetFromUi();
            s.TelegramNet = c.Mode; s.TelegramProxy = c.Proxy; s.TelegramProxyUser = c.User;
            s.TelegramProxyPass = Secret.Protect(c.Pass); s.TelegramApi = c.Api;
            TgNet.Config = c;
        }

        async void TgDiag()
        {
            var c = NetFromUi();
            _tgDiagText.IsVisible = true;
            if ((c.Mode == "http" || c.Mode == "socks5") && c.Proxy.Length == 0)
            {
                _tgDiagText.Text = "프록시 주소를 입력해 주세요. 예) 203.0.113.10:1080";
                _tgProxy.Focus();
                return;
            }
            _tgDiag.IsEnabled = false;
            _tgDiagText.Text = "시험하는 중…";
            try { _tgDiagText.Text = await TgNet.Diagnose(c, CancellationToken.None); }
            catch (Exception ex) { _tgDiagText.Text = "진단하지 못했습니다: " + TgNet.Short(ex); }
            _tgDiag.IsEnabled = true;
        }

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
                if (te.Code == 409) return "이 봇을 다른 컴퓨터(또는 웹훅)에서 쓰고 있습니다. 다른 컴퓨터의 연결을 해제한 뒤 다시 시도해 주세요.";
                return $"텔레그램 오류 ({te.Code}): {te.Message}";
            }
            if (ex is TimeoutException) return ex.Message.Contains("코드") ? ex.Message + " 다시 '연결'을 눌러 주세요." : "텔레그램 서버의 응답이 없습니다. 인터넷 · 회사 방화벽(api.telegram.org)을 확인해 주세요.";
            if (ex is System.Net.Http.HttpRequestException || ex is IOException || ex is System.Net.Sockets.SocketException || ex.InnerException is System.Net.Sockets.SocketException)
                return "텔레그램 서버에 연결할 수 없습니다: " + TgNet.Short(ex) + "\n아래 '연결 방법 · 프록시'에서 '연결 진단'을 눌러 보세요.";
            return "연결하지 못했습니다: " + TgNet.Short(ex) + "\n아래 '연결 방법 · 프록시'에서 '연결 진단'을 눌러 보세요.";
        }

        void RefreshTg()
        {
            if (_pairing != null) return;
            if (_tgError != null) { _tgStatus.Text = _tgError; return; }
            var s = _core.Settings;
            bool linked = !string.IsNullOrWhiteSpace(s.TelegramToken) && s.TelegramChatId != 0;
            _tgTest.IsVisible = _tgUnlink.IsVisible = linked;
            _tgConnect.Content = linked ? "다시 연결" : "연결";
            if (!linked) { _tgStatus.Text = "연결되지 않음 — 봇 토큰을 넣고 '연결'을 누르세요"; return; }
            var bot = string.IsNullOrEmpty(s.TelegramBot) ? "" : " · 봇 @" + s.TelegramBot;
            var st = _core.Telegram?.State ?? "";
            var via = string.IsNullOrEmpty(TgNet.LastRoute) ? "" : " · " + TgNet.RouteLabel(TgNet.LastRoute);
            _tgStatus.Text = "연결됨: " + (s.TelegramWho ?? "") + bot + via + (st.Length > 0 && st != "연결됨" && s.TelegramEnabled ? "\n" + st : "");
        }

        async void TgConnect()
        {
            if (_pairing != null) { _pairing.Cancel(); return; }
            _tgError = null;
            var token = ExtractToken(_tgToken.Text);
            _tgToken.Text = token;
            if (token.Length == 0 || !token.Contains(":"))
            {
                _tgStatus.Text = "봇 토큰 형식이 아닙니다. 예) 123456789:ABCdef…";
                await Dialog.Alert(this, "봇 토큰을 확인해 주세요", "텔레그램 @BotFather 에서 /newbot 으로 봇을 만들면 '123456789:ABC…' 형식의 토큰을 받을 수 있습니다.");
                _tgToken.Focus();
                return;
            }
            var prevNet = TgNet.Config;
            TgNet.Config = NetFromUi();
            var cts = _pairing = new CancellationTokenSource();
            MessageWindow notice = null;
            _tgConnect.Content = "취소";
            _tgTest.IsVisible = _tgUnlink.IsVisible = false;
            try
            {
                _tgStatus.Text = "봇 확인 중…";
                var bot = await TelegramBridge.CheckToken(token, cts.Token);
                var code = new Random().Next(100000, 1000000).ToString();
                _tgStatus.Text = $"텔레그램에서 @{bot} 을 열어 [시작]을 누르거나 '/start {code}' 를 보내 주세요. (3분 안에)";
                var link = $"https://t.me/{bot}?start={code}";
                notice = _core.ShowNotice("텔레그램 연결 안내",
                    $"내 봇 @{bot} 에게 아래 코드를 보내면 연결됩니다. (3분 안에)\n\n**연결 코드: {code}**\n\n" +
                    $"① 휴대폰 텔레그램에서 @{bot} 을 찾아 대화를 열고\n② /start {code} 를 보내세요.\n\n" +
                    $"이 컴퓨터에 텔레그램이 있으면 아래 '텔레그램 열기'를 누르면 바로 열립니다.\n{link}",
                    "텔레그램 열기", () => Markdown.OpenUrl(link));
                var (chatId, who) = await _core.Telegram.Pair(token, code, cts.Token);
                try { notice?.Close(); } catch { }
                notice = null;
                var s = _core.Settings;
                s.TelegramToken = token; s.TelegramChatId = chatId; s.TelegramWho = who; s.TelegramBot = bot; s.TelegramEnabled = true;
                SaveNet(s);
                Store.SaveSettings(s);
                _tg.IsChecked = true;
                _pairing = null;
                try { await TelegramBridge.Welcome(token, chatId, s.Name, CancellationToken.None); } catch { }
                _core.Telegram.Apply();
                RefreshTg();
            }
            catch (Exception ex)
            {
                _pairing = null;
                try { notice?.Close(); } catch { }
                _tgError = cts.IsCancellationRequested ? null : "⚠️ " + Explain(ex);
                TgNet.Config = prevNet;
                _core.Telegram?.Apply();
                RefreshTg();
            }
            finally { if (_pairing == null) _tgConnect.Content = _core.Telegram?.Connected == true ? "다시 연결" : "연결"; }
        }

        async void TgTest()
        {
            _tgTest.IsEnabled = false;
            try { await _core.Telegram.SendTest(); _tgStatus.Text = "테스트 알림을 보냈습니다. 텔레그램을 확인해 보세요."; }
            catch (Exception ex) { _tgStatus.Text = "보내지 못했습니다: " + ex.Message; }
            _tgTest.IsEnabled = true;
        }

        void TgUnlink()
        {
            var s = _core.Settings;
            s.TelegramChatId = 0; s.TelegramWho = ""; s.TelegramEnabled = false;
            Store.SaveSettings(s);
            _tg.IsChecked = false;
            _core.Telegram?.Apply();
            _tgError = null;
            RefreshTg();
        }

        // ───────────── 프로필 사진 ─────────────

        async void PickPhoto()
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "프로필 사진 선택",
                    FileTypeFilter = new[] { new FilePickerFileType("이미지") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp", "*.heic" } }, FilePickerFileTypes.All },
                });
                var p = files.FirstOrDefault()?.TryGetLocalPath();
                if (p != null) LoadPhoto(p);
            }
            catch { }
        }

        async void LoadPhoto(string path)
        {
            try
            {
                _photo = AvatarStore.Prepare(path);
                _photoChanged = true;
                _photoView.Image = new Bitmap(new MemoryStream(_photo));
                _removePhoto.IsVisible = true;
            }
            catch { await Dialog.Alert(this, "사진을 열 수 없습니다", "이미지 파일인지 확인해 주세요."); }
        }

        void RemovePhoto()
        {
            _photo = null; _photoChanged = true;
            _photoView.Image = null;
            _removePhoto.IsVisible = false;
        }

        // ───────────── 기타 ─────────────

        async void CheckUpdate()
        {
            _checkBtn.IsEnabled = false;
            _updateStatus.Text = "확인 중…";
            var msg = await _core.CheckForUpdates(manual: true);
            _updateStatus.Text = msg ?? "";
            _checkBtn.IsEnabled = true;
        }

        async void PickDir()
        {
            try
            {
                var start = await StorageProvider.TryGetFolderFromPathAsync(_dir.Text);
                var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "받은 파일을 저장할 폴더", SuggestedStartLocation = start });
                var p = r.FirstOrDefault()?.TryGetLocalPath();
                if (p != null) _dir.Text = p;
            }
            catch { }
        }

        async void ClearHistory()
        {
            if (_core.History.Count == 0) return;
            if (await Dialog.Confirm(this, "쪽지함 비우기", $"주고받은 쪽지 {_core.History.Count}개를 모두 삭제합니다. 되돌릴 수 없습니다.", "모두 삭제", danger: true))
                _core.DeleteHistory(_core.History.ToList());
        }

        async void Save()
        {
            var s = _core.Settings;
            var name = (_name.Text ?? "").Trim();
            if (name.Length == 0) { _name.Focus(); return; }
            int.TryParse(_port.Text, out int port);
            if (port < 1024 || port > 65535)
            {
                await Dialog.Alert(this, "포트 번호를 확인해 주세요", "1024 ~ 65535 사이의 숫자를 입력해야 합니다.");
                _port.Focus();
                return;
            }
            bool portChanged = port != s.Port;

            s.Name = name;
            s.Dept = (_dept.Text ?? "").Trim();
            s.Note = (_note.Text ?? "").Trim();
            s.Sound = _sound.IsChecked == true;
            s.PopupNoFocus = _noFocus.IsChecked == true;
            s.ChatAlertAutoClose = int.TryParse(_chatClose.Value, out int csec) ? csec : 5;
            s.AlertPrivacy = int.TryParse(_privacy.Value, out int pl) ? pl : 1;
            s.PopupAutoClose = int.TryParse(_autoClose.Value, out int sec) ? sec : 0;
            s.ReadReceipts = _receipt.IsChecked == true;
            s.ShareTyping = _typing.IsChecked == true;
            s.EnterToSend = _enter.IsChecked == true;
            bool hk = _hotkey.IsChecked == true;
            if (hk != s.CaptureHotkey) { s.CaptureHotkey = hk; _core.ApplyHotkey(); }
            s.AutoAwayMinutes = int.TryParse(_away.Text, out int m) ? Math.Max(0, m) : 10;
            s.AutoReply = (_autoReply.Text ?? "").Trim();
            s.AutoStart = _autoStart.IsChecked == true;
            s.StartHidden = _hidden.IsChecked == true;
            s.DownloadDir = (_dir.Text ?? "").Trim();
            s.Port = port;
            s.AutoUpdate = _autoUpdate.IsChecked == true;
            var tgToken = ExtractToken(_tgToken.Text);
            if (tgToken != (s.TelegramToken ?? "").Trim()) { s.TelegramToken = tgToken; s.TelegramChatId = 0; s.TelegramWho = ""; }
            s.TelegramEnabled = _tg.IsChecked == true;
            s.TelegramNotes = _tgNotes.IsChecked == true;
            s.TelegramChats = _tgChats.IsChecked == true;
            s.TelegramShowText = _tgText.IsChecked == true;
            s.TelegramOnlyAway = _tgAway.IsChecked == true;
            s.TelegramFiles = _tgFiles.IsChecked == true;
            SaveNet(s);
            if (_photoChanged) s.AvatarHash = AvatarStore.SetMine(_photo);
            s.ManualHosts = (_hosts.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

            if (!Store.CustomDir) Native.SetAutoStart(s.AutoStart);
            _core.ProfileChanged();
            Close();
            _core.Telegram?.Apply();

            if (portChanged)
            {
                if (await Dialog.Confirm(null, "포트를 바꿨습니다", "새 포트는 프로그램을 다시 시작해야 적용됩니다. 지금 다시 시작할까요?", "다시 시작"))
                {
                    try
                    {
                        var args = "--restart" + (Store.CustomDir ? $" --data \"{Store.Dir}\"" : "");
                        Process.Start(new ProcessStartInfo(Native.ExePath, args) { UseShellExecute = false });
                    }
                    catch { }
                    _core.Quit();
                }
            }
            else _core.Rescan();
        }
    }
}
