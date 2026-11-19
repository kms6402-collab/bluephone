using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace BluePhone
{
    /// <summary>
    /// 마우스로 일부분을 골라 복사할 수 있는 TextBlock.
    /// 글자 배치 · 이모지 · 링크는 TextBlock 그대로 두고, WPF 내부 편집기(TextEditor)를 읽기 전용으로 붙여 선택만 가능하게 함.
    /// 복사할 때 이모지 그림은 원래 글자로 되돌림
    /// </summary>
    public class SelectableTextBlock : TextBlock
    {
        static readonly Type EditorType = Type.GetType("System.Windows.Documents.TextEditor, PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
        static readonly Type ContainerType = Type.GetType("System.Windows.Documents.ITextContainer, PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
        static readonly PropertyInfo IsReadOnlyProp = EditorType?.GetProperty("IsReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo TextViewProp = EditorType?.GetProperty("TextView", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo SelectionProp = EditorType?.GetProperty("Selection", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly PropertyInfo ContainerTextViewProp = ContainerType?.GetProperty("TextView");
        static readonly PropertyInfo TextContainerProp = typeof(TextBlock).GetProperty("TextContainer", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly bool Supported;

        static SelectableTextBlock()
        {
            try
            {
                var register = EditorType.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic, null,
                                                    new[] { typeof(Type), typeof(bool), typeof(bool), typeof(bool) }, null);
                FocusableProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(true));
                FocusVisualStyleProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(null));
                // 복사는 직접 처리 (이모지 그림 → 글자). 편집기보다 먼저 등록해야 우선함
                CommandManager.RegisterClassCommandBinding(typeof(SelectableTextBlock),
                    new CommandBinding(ApplicationCommands.Copy, (s, e) => { ((SelectableTextBlock)s).CopySelection(); e.Handled = true; },
                                       (s, e) => { e.CanExecute = ((SelectableTextBlock)s).HasSelection; e.Handled = true; }));
                // 오른쪽 클릭: 편집 메뉴(잘라내기 · 붙여넣기) 대신 말풍선 원래 메뉴, 없으면 복사 · 모두 선택
                EventManager.RegisterClassHandler(typeof(SelectableTextBlock), ContextMenuOpeningEvent, new ContextMenuEventHandler(OnMenuOpening));
                register.Invoke(null, new object[] { typeof(SelectableTextBlock), true, true, true });
                Supported = IsReadOnlyProp != null && TextViewProp != null && ContainerTextViewProp != null && TextContainerProp != null;
            }
            catch { Supported = false; }
        }

        object _editor;
        ContextMenu _own;
        static WeakReference _menuSource;

        static void OnMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var tb = (SelectableTextBlock)sender;
            _menuSource = new WeakReference(tb);
            ContextMenu owner = null;
            for (DependencyObject d = System.Windows.Media.VisualTreeHelper.GetParent(tb); d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement fe && fe.ContextMenu != null) { owner = fe.ContextMenu; break; }
            tb.ContextMenu = owner ?? tb.OwnMenu();
        }

        ContextMenu OwnMenu()
        {
            if (_own != null) return _own;
            _own = new ContextMenu();
            var copy = new MenuItem { Header = "복사", InputGestureText = "Ctrl+C" };
            copy.Click += (s, e) => { if (HasSelection) CopySelection(); else { SelectAll(); CopySelection(); } };
            var all = new MenuItem { Header = "모두 선택", InputGestureText = "Ctrl+A" };
            all.Click += (s, e) => SelectAll();
            _own.Items.Add(copy);
            _own.Items.Add(all);
            return _own;
        }

        public void SelectAll()
        {
            Focus();
            try { ApplicationCommands.SelectAll.Execute(null, this); } catch { }
        }

        public SelectableTextBlock()
        {
            if (!Supported) return;
            try
            {
                var container = TextContainerProp.GetValue(this);
                _editor = Activator.CreateInstance(EditorType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.CreateInstance,
                                                   null, new[] { container, this, false }, null);
                IsReadOnlyProp.SetValue(_editor, true);
                TextViewProp.SetValue(_editor, ContainerTextViewProp.GetValue(container));
                Cursor = Cursors.IBeam;
            }
            catch { _editor = null; }
        }

        TextRange Selection
        {
            get { try { return _editor == null ? null : SelectionProp?.GetValue(_editor) as TextRange; } catch { return null; } }
        }

        public bool HasSelection => Selection is TextRange r && !r.IsEmpty;

        /// <summary>선택한 부분의 글자 (이모지는 원래 글자로)</summary>
        public string SelectedText
        {
            get
            {
                var r = Selection;
                if (r == null || r.IsEmpty) return "";
                var sb = new StringBuilder();
                var p = r.Start;
                while (p != null && p.CompareTo(r.End) < 0)
                {
                    switch (p.GetPointerContext(LogicalDirection.Forward))
                    {
                        case TextPointerContext.Text:
                        {
                            var run = p.GetTextInRun(LogicalDirection.Forward);
                            int max = p.GetOffsetToPosition(r.End);
                            sb.Append(run.Length > max ? run.Substring(0, max) : run);
                            break;
                        }
                        case TextPointerContext.EmbeddedElement:
                            if (p.GetAdjacentElement(LogicalDirection.Forward) is FrameworkElement fe && fe.Tag is string t) sb.Append(t);
                            break;
                        case TextPointerContext.ElementStart:
                            if (p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) sb.Append(Environment.NewLine);
                            else if (p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer iu && iu.Child is FrameworkElement ie && ie.Tag is string it) sb.Append(it);
                            break;
                    }
                    p = p.GetNextContextPosition(LogicalDirection.Forward);
                }
                return sb.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            }
        }

        public void CopySelection()
        {
            var t = SelectedText;
            if (t.Length > 0) try { Clipboard.SetText(t); } catch { }
        }

        /// <summary>창 안에서 선택된 글자가 있으면 그것 (대화 창 '복사' 메뉴에서 씀)</summary>
        public static string SelectionIn(DependencyObject root)
        {
            if (Keyboard.FocusedElement is SelectableTextBlock s && s.HasSelection && (root == null || IsInside(s, root))) return s.SelectedText;
            // 오른쪽 클릭 메뉴가 열리면 포커스가 메뉴로 가므로, 메뉴를 연 글자 영역의 선택을 씀
            if (_menuSource?.Target is SelectableTextBlock m && m.HasSelection && (root == null || IsInside(m, root))) return m.SelectedText;
            return null;
        }

        static bool IsInside(DependencyObject child, DependencyObject root)
        {
            for (var d = child; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                if (d == root) return true;
            return false;
        }

        // ── 링크: 선택 편집기가 마우스를 잡아도 클릭하면 열리도록 ──
        Point _down;

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _down = e.GetPosition(this);
            base.OnPreviewMouseLeftButtonDown(e);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            var pt = e.GetPosition(this);
            if (Math.Abs(pt.X - _down.X) > 3 || Math.Abs(pt.Y - _down.Y) > 3) return;   // 끌어서 선택한 경우
            try
            {
                var pos = GetPositionFromPoint(pt, false);
                for (var el = pos?.Parent as TextElement; el != null; el = el.Parent as TextElement)
                    if (el is Hyperlink h && h.Tag is string url) { Markdown.OpenUrl(url); e.Handled = true; return; }
            }
            catch { }
        }
    }
}
