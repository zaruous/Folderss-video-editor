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

        private static readonly (string Label, int Width, int Height)[] SizePresets =
        {
            ("1920×1080 (16:9 FHD)", 1920, 1080),
            ("1280×720 (16:9 HD)", 1280, 720),
            ("3840×2160 (16:9 4K)", 3840, 2160),
            ("1080×1920 (9:16 세로 — 쇼츠·릴스)", 1080, 1920),
            ("1080×1080 (1:1 정사각형)", 1080, 1080),
            ("1440×1080 (4:3)", 1440, 1080)
        };

        private static readonly string[] RatePresets = { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60" };

        /// <summary>프로젝트 설정(해상도·프레임 레이트). 확인하면 (너비, 높이, 레이트), 취소하면 null.</summary>
        public static (int Width, int Height, FrameRate Rate)? ProjectSettings(DependencyObject owner, Project project)
        {
            var window = NewWindow(owner, "프로젝트 설정", 420);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(UiKit.Label("해상도"));
            var size = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
            AutomationProperties.SetAutomationId(size, "ProjectSizeBox");
            foreach (var preset in SizePresets)
                size.Items.Add(preset.Label);
            size.Items.Add("직접 입력");
            var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            var width = new TextBox { Width = 70, Text = project.Width.ToString() };
            var height = new TextBox { Width = 70, Text = project.Height.ToString() };
            AutomationProperties.SetAutomationId(width, "ProjectWidthBox");
            AutomationProperties.SetAutomationId(height, "ProjectHeightBox");
            custom.Children.Add(width);
            custom.Children.Add(UiKit.Label(" × "));
            custom.Children.Add(height);
            var index = Array.FindIndex(SizePresets, p => p.Width == project.Width && p.Height == project.Height);
            size.SelectedIndex = index >= 0 ? index : SizePresets.Length;
            size.SelectionChanged += (s, e) =>
            {
                if (size.SelectedIndex >= 0 && size.SelectedIndex < SizePresets.Length)
                {
                    width.Text = SizePresets[size.SelectedIndex].Width.ToString();
                    height.Text = SizePresets[size.SelectedIndex].Height.ToString();
                }
            };
            panel.Children.Add(size);
            panel.Children.Add(custom);
            panel.Children.Add(UiKit.Label("프레임 레이트"));
            var rate = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
            AutomationProperties.SetAutomationId(rate, "ProjectRateBox");
            foreach (var preset in RatePresets)
                rate.Items.Add(preset + " fps");
            var current = RatePresets.Select(p => { FrameRate.TryParse(p, out var r); return r; }).ToList().FindIndex(r => r.Equals(project.Rate));
            if (current < 0)
            {
                rate.Items.Add(project.Rate + " fps (지금)");
                current = rate.Items.Count - 1;
            }
            rate.SelectedIndex = current;
            panel.Children.Add(rate);
            var note = UiKit.Label("프레임 레이트를 바꾸면 클립 위치는 초 기준으로 다시 맞춥니다.", true);
            note.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(note);
            var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            (int, int, FrameRate)? result = null;
            panel.Children.Add(Buttons(window, () =>
            {
                int w, h;
                if (!int.TryParse(width.Text, out w) || !int.TryParse(height.Text, out h) || w < 16 || h < 16 || w > 8192 || h > 8192)
                {
                    error.Text = "해상도는 16~8192 사이의 숫자여야 합니다.";
                    return false;
                }
                var chosen = project.Rate;
                if (rate.SelectedIndex < RatePresets.Length)
                    FrameRate.TryParse(RatePresets[rate.SelectedIndex], out chosen);
                result = (w, h, chosen);
                return true;
            }));
            window.Content = panel;
            return window.ShowDialog() == true ? result : null;
        }
    }
}
