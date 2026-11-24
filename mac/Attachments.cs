using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace BluePhone
{
    public sealed class Attachment
    {
        public string Path { get; set; }
        public string Source { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string SizeText { get; set; }
        public string Kind { get; set; }
        public string Thumb => Media.IsImage(Path) ? Path : null;
    }

    /// <summary>보낼 첨부 파일 목록 (폴더는 ZIP 으로 묶음) + 그 칩 목록 화면</summary>
    public sealed class Attachments
    {
        public ObservableCollection<Attachment> Items { get; } = new ObservableCollection<Attachment>();
        public int Zipping { get; private set; }
        public event Action Changed;
        readonly Window _owner;

        public Attachments(Window owner)
        {
            _owner = owner;
            Items.CollectionChanged += (s, e) => Changed?.Invoke();
        }

        public async void Add(IEnumerable<string> paths)
        {
            foreach (var p in paths.ToList())
            {
                if (Items.Any(f => string.Equals(f.Source ?? f.Path, p, StringComparison.Ordinal))) continue;
                if (Directory.Exists(p) && !p.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(p))
                {
                    var item = new Attachment { Path = p, Source = p, SizeText = "압축 중…", Kind = "폴더" };
                    Items.Add(item);
                    Zipping++; Changed?.Invoke();
                    try
                    {
                        var zip = await Task.Run(() => ZipFolder(p));
                        var idx = Items.IndexOf(item);
                        if (idx >= 0) Items[idx] = new Attachment { Path = zip, Source = p, SizeText = FileItem.FormatSize(new FileInfo(zip).Length), Kind = "폴더 · ZIP" };
                    }
                    catch (Exception ex)
                    {
                        Items.Remove(item);
                        _ = Dialog.Alert(_owner, "폴더를 압축하지 못했습니다", ex.Message);
                    }
                    finally { Zipping--; Changed?.Invoke(); }
                    continue;
                }
                if (!File.Exists(p)) continue;
                long size;
                try { size = new FileInfo(p).Length; } catch { continue; }
                Items.Add(new Attachment { Path = p, SizeText = FileItem.FormatSize(size) });
            }
        }

        public static string ZipFolder(string dir)
        {
            var name = new DirectoryInfo(dir.TrimEnd('/', '\\')).Name;
            var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BluePhone", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            var zip = System.IO.Path.Combine(tmpDir, name + ".zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(dir, zip, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true,
                entryNameEncoding: System.Text.Encoding.UTF8);
            return zip;
        }

        /// <summary>파일 선택 창 (여러 개)</summary>
        public async Task Pick()
        {
            try
            {
                var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "첨부할 파일 선택", AllowMultiple = true });
                Add(files.Select(f => f.TryGetLocalPath()).Where(p => p != null));
            }
            catch { }
        }

        /// <summary>첨부 칩 목록 (이미지는 작은 미리 보기)</summary>
        public Control BuildList(string lineKey = "Line")
        {
            var ic = new ItemsControl { ItemsSource = Items, Focusable = false };
            ic.ItemsPanel = new FuncTemplate<Panel>(() => new WrapPanel());
            ic.ItemTemplate = new FuncDataTemplate<Attachment>((a, _) =>
            {
                if (a == null) return new Panel();
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (a.Thumb != null)
                {
                    var img = new AsyncImage { MaxHeight = 72, MaxWidth = 128, MaxSide = 256, Margin = new Thickness(0, 4, 8, 4), Cursor = new Cursor(StandardCursorType.Hand), Path = a.Thumb };
                    ToolTip.SetTip(img, "누르면 크게 봅니다");
                    img.PointerReleased += (s, e) => Media.OpenPreview(a.Thumb, _owner);
                    row.Children.Add(img);
                }
                else row.Children.Add(Ui.Text("📄", 12));
                var name = Ui.Text(a.Name, 13, trim: true);
                name.MaxWidth = 180; name.Margin = new Thickness(6, 0, 6, 0);
                row.Children.Add(name);
                row.Children.Add(Ui.Text(a.SizeText, 12, fg: "Muted"));
                if (!string.IsNullOrEmpty(a.Kind)) { var k = Ui.Text(a.Kind, 12, fg: "Muted"); k.Margin = new Thickness(6, 0, 0, 0); row.Children.Add(k); }
                var x = new Button { Content = "✕", FontSize = 9, Width = 22, Height = 22, Margin = new Thickness(4, 0, 0, 0) }.Cls("icon").Tip("빼기");
                x.Click += (s, e) => Items.Remove(a);
                row.Children.Add(x);
                var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), MinHeight = 30, Padding = new Thickness(8, 0, 2, 0), Margin = new Thickness(0, 0, 6, 6), Child = row };
                b.Res(Border.BorderBrushProperty, lineKey);
                ToolTip.SetTip(b, a.Source ?? a.Path);
                return b;
            });
            return ic;
        }

        /// <summary>끌어다 놓기로 첨부 (overlay 는 놓을 때 보여 줄 안내)</summary>
        public void EnableDrop(Control target, Control overlay)
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragEnterEvent, (s, e) => { if (e.Data.Contains(DataFormats.Files)) overlay.IsVisible = true; });
            target.AddHandler(DragDrop.DragLeaveEvent, (s, e) => overlay.IsVisible = false);
            target.AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None; }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            target.AddHandler(DragDrop.DropEvent, (s, e) =>
            {
                overlay.IsVisible = false;
                var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
                if (files != null && files.Count > 0) { Add(files); e.Handled = true; }
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }

        /// <summary>붙여넣기(⌘V): 클립보드에 파일 · 이미지가 있으면 첨부, 아니면 글자 붙여넣기</summary>
        public void EnablePaste(TextBox box)
        {
            box.AddHandler(InputElement.KeyDownEvent, async (s, e) =>
            {
                if (e.Key != Key.V || !Keys.Primary(e.KeyModifiers)) return;
                e.Handled = true;
                var files = await ClipKit.FilesAsync(box);
                if (files != null && files.Count > 0) Add(files);
                else box.Paste();
            }, RoutingStrategies.Tunnel);
        }

        public static Border DropOverlay(string bgKey = "Ct.Overlay")
        {
            var t = Ui.Text("여기에 놓으면 첨부됩니다", 13, FontWeight.SemiBold, "Accent");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { BorderThickness = new Thickness(1.5), Margin = new Thickness(8), CornerRadius = new CornerRadius(8), IsVisible = false, IsHitTestVisible = false, Child = t };
            b.Res(Border.BackgroundProperty, bgKey).Res(Border.BorderBrushProperty, "Accent");
            return b;
        }
    }

    /// <summary>
    /// 메시지 안의 첨부 파일 한 줄: 이미지 바로 보기 · 이름 · 크기 · 상태 · [미리 보기] [받기/열기/다시] [Finder]
    /// </summary>
    public sealed class FileView : Border
    {
        readonly FileItem _f;
        readonly Func<FileItem, Task> _download;
        readonly Window _owner;
        readonly bool _outgoing;
        readonly AsyncImage _img;
        readonly TextBlock _state;
        readonly ProgressBar _bar;
        readonly Button _preview, _act, _folder;

        public FileView(FileItem f, bool outgoing, Window owner, Func<FileItem, Task> download, string bgKey = "Ct.FileBg")
        {
            _f = f; _outgoing = outgoing; _owner = owner; _download = download;
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(10, 7, 6, 7);
            Margin = new Thickness(0, 6, 0, 0);
            MinWidth = 200;
            if (outgoing) Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            else this.Res(BackgroundProperty, bgKey);

            _img = new AsyncImage { MaxWidth = 260, MaxHeight = 260, MaxSide = 640, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, -1, 0, 7), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(_img, "누르면 크게 봅니다");
            _img.PointerReleased += (s, e) => { e.Handled = true; Media.OpenPreview(_f.LocalPath, _owner); };

            var name = new TextBlock { Text = f.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
            ToolTip.SetTip(name, f.Name);
            var size = new TextBlock { Text = f.SizeText, FontSize = 11.5 };
            _state = new TextBlock { FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0) };
            _bar = new ProgressBar { Maximum = 1, Height = 3, MinHeight = 3, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
            _bar.Res(ProgressBar.ForegroundProperty, "Accent");
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.75, Children = { size, _state } }, _bar } };

            _preview = SmallBtn("👁", "미리 보기");
            _preview.Click += (s, e) => Media.OpenPreview(_f.LocalPath, _owner);
            _act = new Button { Height = 26, Padding = new Thickness(9, 0), Content = "받기" }.Cls("outline");
            _act.Click += async (s, e) => await Act(e);
            _folder = SmallBtn("📂", Native.IsMac ? "Finder 에서 보기" : "폴더에서 보기");
            _folder.Click += (s, e) => Native.Reveal(_f.LocalPath);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Spacing = 4, Children = { _preview, _act, _folder } };

            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(info);
            g.Children.Add(btns.Col(1));
            Child = new StackPanel { Children = { _img, g } };

            if (outgoing && string.IsNullOrEmpty(f.State)) f.State = "보낸 파일";
            else if (!outgoing && string.IsNullOrEmpty(f.State) && Media.Exists(f.LocalPath)) f.State = "완료";
            Update();
            AttachedToVisualTree += (s, e) => _f.PropertyChanged += Changed;
            DetachedFromVisualTree += (s, e) => _f.PropertyChanged -= Changed;
        }

        static Button SmallBtn(string glyph, string tip)
        {
            var b = new Button { Height = 26, Width = 30, Padding = new Thickness(0), Content = new TextBlock { Text = glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } }.Cls("outline");
            ToolTip.SetTip(b, tip);
            return b;
        }

        void Changed(object sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Update);

        void Update()
        {
            _img.Path = _f.PreviewPath;
            _state.Text = _f.State == "보낸 파일" ? "" : _f.State;
            _bar.Value = _f.Progress;
            var st = _f.State ?? "";
            _bar.IsVisible = st == "받는 중";
            _act.IsEnabled = st != "받는 중";
            _act.Content = st == "완료" ? "열기" : st == "실패" ? "다시" : "받기";
            _preview.IsVisible = _f.CanPreviewNow;
            bool sent = st == "보낸 파일";
            _act.IsVisible = !sent && !_f.HasPreview;
            _folder.IsVisible = sent && Media.Exists(_f.LocalPath) || st == "완료" || _f.HasPreview;
        }

        async Task Act(RoutedEventArgs e)
        {
            if (_f.State == "완료" && Media.Exists(_f.LocalPath)) { Native.Open(_f.LocalPath); return; }
            try { await _download(_f); }
            catch (Exception ex) { await Dialog.Alert(_owner, "파일을 받지 못했습니다", ex.Message); }
        }
    }
}
