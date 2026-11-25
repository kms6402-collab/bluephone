using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BluePhone
{
    /// <summary>그룹 대화 입력칸에서 '@'를 치면 대화방 구성원 목록을 띄워 골라 부르기 (카카오톡의 @멘션)</summary>
    public partial class ChatWindow
    {
        Popup _mentionPop;
        ListBox _mentionList;
        int _mentionAt = -1;

        void InitMention()
        {
            if (Room == null) return;
            Input.TextChanged += (s, e) => UpdateMention();
            Input.SelectionChanged += (s, e) => { if (_mentionPop != null && _mentionPop.IsOpen) UpdateMention(); };
            Input.LostKeyboardFocus += (s, e) => CloseMention();
            LocationChanged += (s, e) => CloseMention();
            Deactivated += (s, e) => CloseMention();
        }

        /// <summary>점검용: 입력칸에 '@검색어'를 넣고 첫 후보를 Enter 로 고름</summary>
        internal string TestMention(string q)
        {
            Input.Focus();
            Input.Text = "확인 부탁 @" + q.TrimStart('!');
            Input.CaretIndex = Input.Text.Length;
            UpdateMention();
            bool open = MentionOpen;
            var names = open ? string.Join(",", _mentionList.Items.Cast<FrameworkElement>().Select(x => ((MentionPick)x.Tag).Name)) : "(목록 없음)";
            bool keep = q.StartsWith("!");
            if (open && !keep)
            {
                var src = PresentationSource.FromVisual(Input);
                MentionKey(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            return $"open={open} 후보=[{names}] → '{Input.Text}' popupAfter={_mentionPop?.IsOpen == true}";
        }

        bool MentionOpen => _mentionPop != null && _mentionPop.IsOpen && _mentionList.Items.Count > 0;

        void UpdateMention()
        {
            if (Room == null) return;
            int at = Mention.Find(Input.Text, Input.CaretIndex, out var q);
            var list = at < 0 ? null : _core.MentionCandidates(Room, q);
            if (list == null || list.Count == 0) { CloseMention(); return; }
            _mentionAt = at;
            EnsureMentionPopup();
            _mentionList.Items.Clear();
            foreach (var p in list.Take(30)) _mentionList.Items.Add(MentionRow(p));
            _mentionList.SelectedIndex = 0;
            var rect = Input.GetRectFromCharacterIndex(at);
            _mentionPop.PlacementRectangle = rect.IsEmpty ? new Rect(0, 0, 1, 1) : rect;
            _mentionPop.IsOpen = true;
            // 목록 위치를 글자 위치에 다시 맞춤
            _mentionPop.HorizontalOffset += 0.01; _mentionPop.HorizontalOffset -= 0.01;
        }

        void CloseMention()
        {
            if (_mentionPop != null) _mentionPop.IsOpen = false;
            _mentionAt = -1;
        }

        /// <summary>목록이 떠 있을 때 위 · 아래 · Enter · Tab · Esc</summary>
        bool MentionKey(KeyEventArgs e)
        {
            if (!MentionOpen) return false;
            int n = _mentionList.Items.Count;
            switch (e.Key)
            {
                case Key.Down: _mentionList.SelectedIndex = (_mentionList.SelectedIndex + 1) % n; _mentionList.ScrollIntoView(_mentionList.SelectedItem); return true;
                case Key.Up: _mentionList.SelectedIndex = (_mentionList.SelectedIndex - 1 + n) % n; _mentionList.ScrollIntoView(_mentionList.SelectedItem); return true;
                case Key.Enter:
                case Key.Tab:
                    if (Keyboard.Modifiers != ModifierKeys.None) return false;
                    PickMention((_mentionList.SelectedItem as FrameworkElement)?.Tag as MentionPick);
                    return true;
                case Key.Escape: CloseMention(); return true;
            }
            return false;
        }

        void PickMention(MentionPick p)
        {
            if (p == null || _mentionAt < 0 || _mentionAt > Input.Text.Length) { CloseMention(); return; }
            int caret = Math.Max(_mentionAt, Math.Min(Input.CaretIndex, Input.Text.Length));
            var text = Mention.Insert(Input.Text, _mentionAt, caret, p.Name, out var c);
            CloseMention();
            Input.Text = text;
            Input.CaretIndex = c;
            Input.Focus();
        }

        void EnsureMentionPopup()
        {
            if (_mentionPop != null) return;
            _mentionList = new ListBox
            {
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false,
                MaxHeight = 236, Padding = new Thickness(0),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_mentionList, ScrollBarVisibility.Disabled);
            // 누르면 고름 (입력칸의 글자 커서를 빼앗지 않도록 목록은 초점을 받지 않음)
            _mentionList.PreviewMouseLeftButtonDown += (s, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                    if (d is ListBoxItem item) { e.Handled = true; PickMention(item.Tag as MentionPick); return; }
            };
            var root = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
                Width = 240, Margin = new Thickness(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.16 },
            };
            TextElement.SetFontFamily(root, (FontFamily)Application.Current.FindResource("UI"));
            ChatLook.Attach(root, _look);
            root.SetResourceReference(Border.BackgroundProperty, "Surface");
            root.SetResourceReference(Border.BorderBrushProperty, "Line");
            root.SetResourceReference(TextElement.ForegroundProperty, "Ink");
            var stack = new StackPanel();
            var head = new TextBlock { Text = "대화 상대 부르기", FontSize = 11.5, Margin = new Thickness(8, 4, 8, 4) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(head);
            stack.Children.Add(_mentionList);
            root.Child = stack;
            _mentionPop = new Popup
            {
                Child = root, PlacementTarget = Input, Placement = PlacementMode.Top, StaysOpen = true,
                AllowsTransparency = true, Focusable = false,
            };
        }

        ListBoxItem MentionRow(MentionPick p)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var av = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
            av.Children.Add(new Ellipse { Fill = p.Id == Mention.All ? (Brush)FindResource("Accent") : Palette.Avatar(p.Id) });
            av.Children.Add(new TextBlock { Text = p.Initial, Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(av);
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(p.Sub))
            {
                var sub = new TextBlock { Text = p.Sub, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                txt.Children.Add(sub);
            }
            Grid.SetColumn(txt, 1);
            g.Children.Add(txt);
            return new ListBoxItem { Content = g, Tag = p, Focusable = false, Padding = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand };
        }
    }
}
