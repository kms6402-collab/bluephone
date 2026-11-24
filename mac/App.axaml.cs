using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace BluePhone
{
    public partial class App : Application
    {
        public static Core Core { get; private set; }
        FileSystemWatcher _signal;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.ShutdownRequested += (s, e) => Core?.Stop();
                Dispatcher.UIThread.UnhandledException += (s, e) => { Program.Log(e.Exception.ToString()); e.Handled = true; };

                Updater.Cleanup();
                Core = new Core();
                BuildAppMenu();
                bool tray = Program.Has("--tray");
                Core.Start(startHidden: tray || Core.Settings.StartHidden);

                // Dock 아이콘을 누르면 메인 창 열기
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime act)
                    act.Activated += (s, e) => { if (e.Kind == ActivationKind.Reopen) Core.ShowMain(); };

                // 두 번째 실행 → 기존 창을 앞으로
                try
                {
                    _signal = new FileSystemWatcher(Store.Dir, "show.signal") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
                    FileSystemEventHandler h = (s, e) => Dispatcher.UIThread.Post(() => Core.ShowMain());
                    _signal.Changed += h; _signal.Created += h;
                    _signal.EnableRaisingEvents = true;
                }
                catch { }
            }
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>macOS 앱 메뉴 (파란전화기 ▸ 정보 · 설정 · 업데이트 확인)</summary>
        void BuildAppMenu()
        {
            var menu = new NativeMenu();
            var about = new NativeMenuItem($"{AppInfo.Name} 정보");
            about.Click += (s, e) => _ = Dialog.Alert(Core.Main, $"{AppInfo.Name} {AppInfo.VersionText}", "서버 없이 같은 네트워크의 동료와 쪽지 · 대화 · 파일을 주고받는 메신저입니다.\nWindows 판과 서로 주고받을 수 있습니다.\n\n" + AppInfo.RepoUrl);
            var settings = new NativeMenuItem("설정…") { Gesture = Avalonia.Input.KeyGesture.Parse("Cmd+OemComma") };
            settings.Click += (s, e) => Core.ShowSettings(Core.Main);
            var update = new NativeMenuItem("업데이트 확인…");
            update.Click += async (s, e) =>
            {
                var r = await Core.CheckForUpdates(manual: true);
                if (r != null && !r.StartsWith("새 버전") && !r.StartsWith("업데이트가 준비")) _ = Dialog.Alert(Core.Main, "업데이트", r);
            };
            var show = new NativeMenuItem("메인 창 보기") { Gesture = Avalonia.Input.KeyGesture.Parse("Cmd+0") };
            show.Click += (s, e) => Core.ShowMain();
            menu.Items.Add(about);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(settings);
            menu.Items.Add(update);
            menu.Items.Add(show);
            NativeMenu.SetMenu(this, menu);
        }
    }
}
