using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VideoEditor.Ui
{
    /// <summary>화면 조각을 만드는 도우미. 색은 Folderss 테마 리소스 키에 연결한다(테마를 바꾸면 따라 바뀜).</summary>
    internal static class UiKit
    {
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        // Segoe MDL2 Assets 글리프
        public const string GlyphImport = "";
        public const string GlyphExport = "";
        public const string GlyphUndo = "";
        public const string GlyphRedo = "";
        public const string GlyphSplit = "";
        public const string GlyphDelete = "";
        public const string GlyphPlay = "";
        public const string GlyphPause = "";
        public const string GlyphStepBack = "";
        public const string GlyphStepForward = "";
        public const string GlyphToStart = "";
        public const string GlyphToEnd = "";
        public const string GlyphAdd = "";
        public const string GlyphVolume = "";
        public const string GlyphMute = "";
        public const string GlyphEye = "";
        public const string GlyphLock = "";
        public const string GlyphZoomIn = "";
        public const string GlyphZoomOut = "";
        public const string GlyphFit = "";
        public const string GlyphMarker = "";
        public const string GlyphMagnet = "";
        public const string GlyphSnap = "";
        public const string GlyphSpeed = "";
        public const string GlyphAudio = "";
        public const string GlyphText = "";
        public const string GlyphCamera = "";
        public const string GlyphLink = "";
        public const string GlyphFolder = "";

        public static Button IconButton(string glyph, string tooltip, string automationId, Action click)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14 },
                ToolTip = tooltip,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(1, 0, 1, 0),
                MinWidth = 28
            };
            AutomationProperties.SetName(button, tooltip);
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += (s, e) => click();
            return button;
        }

        public static ToggleButton IconToggle(string glyph, string tooltip, string automationId, Action<bool> changed)
        {
            var toggle = new ToggleButton
            {
                Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14 },
                ToolTip = tooltip,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(1, 0, 1, 0),
                MinWidth = 28
            };
            AutomationProperties.SetName(toggle, tooltip);
            AutomationProperties.SetAutomationId(toggle, automationId);
            toggle.Click += (s, e) => changed(toggle.IsChecked == true);
            return toggle;
        }

        public static Button TextButton(string text, string automationId, Action click)
        {
            var button = new Button { Content = new TextBlock { Text = text }, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), MinWidth = 70 };
            AutomationProperties.SetAutomationId(button, automationId);
            AutomationProperties.SetName(button, text);
            button.Click += (s, e) => click();
            return button;
        }

        public static TextBlock Label(string text, bool secondary = false)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (secondary)
                block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            return block;
        }

        public static Separator ToolbarSeparator()
        {
            return new Separator { Width = 1, Margin = new Thickness(6, 2, 6, 2) };
        }

        public static void Themed(Control control, string background = "PanelBackground")
        {
            control.SetResourceReference(Control.BackgroundProperty, background);
            control.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        }

        public static Border Panel(UIElement child, string background = "PanelBackground")
        {
            var border = new Border { Child = child, BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, background);
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return border;
        }

        /// <summary>
        /// 탭을 테마에 맞춘다. Folderss 테마(Controls.xaml)에는 TabControl 스타일이 없어 기본(밝은) 모양이 나오므로
        /// 탭 머리글을 직접 그린다: 바탕 SurfaceBackground, 고른 탭은 ControlBackground + 아래 AccentBrush 줄.
        /// </summary>
        public static void StyleTabs(TabControl tabs)
        {
            tabs.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            tabs.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            tabs.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            tabs.Padding = new Thickness(0);
            var template = new ControlTemplate(typeof(TabItem));
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 4));
            border.SetValue(Border.MarginProperty, new Thickness(0, 0, 2, 0));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBackground");
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlBackground"), "Bd"));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "Bd"));
            template.Triggers.Add(selected);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlHoverBrush"), "Bd"));
            template.Triggers.Add(hover);
            var style = new Style(typeof(TabItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryText")));
            tabs.ItemContainerStyle = style;
        }

        /// <summary>요소가 붙은 창(없으면 앱의 활성 창).</summary>
        public static Window OwnerOf(DependencyObject element)
        {
            var window = element == null ? null : Window.GetWindow(element);
            return window ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        }

        /// <summary>탐색기로 폴더를 연다(없으면 만든다). 성공하면 null, 실패하면 알릴 글.</summary>
        public static string OpenFolder(string folder)
        {
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
                return null;
            }
            catch (Exception ex)
            {
                return "폴더를 열지 못했습니다: " + ex.Message;
            }
        }

        /// <summary>탐색기에서 파일을 고른 채로 연다. 성공하면 null, 실패하면 알릴 글.</summary>
        public static string RevealFile(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
                return null;
            }
            catch (Exception ex)
            {
                return "탐색기를 열지 못했습니다: " + ex.Message;
            }
        }

        /// <summary>
        /// 짧은 확인 창(WPF — 테마를 따르고 화면 시험이 단추로 찾을 수 있다). 누른 단추 번호, 닫으면 -1.
        /// </summary>
        public static int Ask(DependencyObject owner, string title, string message, params string[] buttons)
        {
            var result = -1;
            var window = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                MinWidth = 320,
                MaxWidth = 560
            };
            var owning = OwnerOf(owner);
            if (owning != null && owning.IsLoaded)
                window.Owner = owning;
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            window.SetResourceReference(Window.ForegroundProperty, "PrimaryText");
            window.SetResourceReference(Window.FontFamilyProperty, "AppFontFamily");
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (var i = 0; i < buttons.Length; i++)
            {
                var index = i;
                var button = TextButton(buttons[i], "AskButton" + i, () =>
                {
                    result = index;
                    window.Close();
                });
                if (i == 0)
                    button.IsDefault = true;
                if (i == buttons.Length - 1)
                    button.IsCancel = true;
                row.Children.Add(button);
            }
            panel.Children.Add(row);
            window.Content = panel;
            window.ShowDialog();
            return result;
        }

        public static void Inform(DependencyObject owner, string title, string message)
        {
            Ask(owner, title, message, "확인");
        }

        public static string Number(double value, string format = "0.##")
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        public static bool TryNumber(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
