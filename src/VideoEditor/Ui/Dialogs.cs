using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>작은 입력 창들(테마를 따르는 WPF 창 — 화면 시험이 AutomationId로 찾는다).</summary>
    internal static class Dialogs
    {
        private static Window NewWindow(DependencyObject owner, string title, double width)
        {
            var window = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var owning = UiKit.OwnerOf(owner);
            if (owning != null && owning.IsLoaded)
                window.Owner = owning;
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            return window;
        }

        private static StackPanel Buttons(Window window, Func<bool> ok, string okText = "확인")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var okButton = UiKit.TextButton(okText, "DialogOkButton", () =>
            {
                if (ok())
                    window.DialogResult = true;
            });
            okButton.IsDefault = true;
            var cancel = UiKit.TextButton("취소", "DialogCancelButton", () => window.DialogResult = false);
            cancel.IsCancel = true;
            row.Children.Add(okButton);
            row.Children.Add(cancel);
            return row;
        }

        /// <summary>글 한 줄을 묻는다. 취소하면 null.</summary>
        public static string Input(DependencyObject owner, string title, string label, string initial)
        {
            var window = NewWindow(owner, title, 380);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            var box = new TextBox { Text = initial ?? "" };
            AutomationProperties.SetAutomationId(box, "DialogInputBox");
            panel.Children.Add(box);
            panel.Children.Add(Buttons(window, () => true));
            window.Content = panel;
            window.Loaded += (s, e) =>
            {
                box.Focus();
                box.SelectAll();
            };
            return window.ShowDialog() == true ? box.Text : null;
        }

        /// <summary>마커 편집(이름·색·메모). 삭제를 누르면 Deleted=true.</summary>
        public static bool EditMarker(DependencyObject owner, Marker marker, FrameRate rate, out bool deleted)
        {
            var removed = false;
            var window = NewWindow(owner, "마커 — " + rate.Timecode(marker.Frame), 400);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(UiKit.Label("이름"));
            var name = new TextBox { Text = marker.Name, Margin = new Thickness(0, 2, 0, 8) };
            AutomationProperties.SetAutomationId(name, "MarkerNameBox");
            panel.Children.Add(name);
            panel.Children.Add(UiKit.Label("색"));
            var colors = new[] { ("파랑", "#3B82F6"), ("빨강", "#EF4444"), ("초록", "#22C55E"), ("노랑", "#FACC15"), ("보라", "#A855F7") };
            var color = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
            foreach (var c in colors)
                color.Items.Add(c.Item1);
            color.SelectedIndex = Math.Max(0, Array.FindIndex(colors, c => string.Equals(c.Item2, marker.Color, StringComparison.OrdinalIgnoreCase)));
            AutomationProperties.SetAutomationId(color, "MarkerColorBox");
            panel.Children.Add(color);
            panel.Children.Add(UiKit.Label("메모"));
            var comment = new TextBox { Text = marker.Comment, AcceptsReturn = true, MinHeight = 50, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            AutomationProperties.SetAutomationId(comment, "MarkerCommentBox");
            panel.Children.Add(comment);
            var buttons = Buttons(window, () =>
            {
                marker.Name = name.Text;
                marker.Color = colors[Math.Max(0, color.SelectedIndex)].Item2;
                marker.Comment = comment.Text;
                return true;
            });
            buttons.Children.Insert(0, UiKit.TextButton("삭제", "MarkerDeleteButton", () =>
            {
                removed = true;
                window.DialogResult = true;
            }));
            panel.Children.Add(buttons);
            window.Content = panel;
            var ok = window.ShowDialog() == true;
            deleted = removed;
            return ok;
        }
    }
}
