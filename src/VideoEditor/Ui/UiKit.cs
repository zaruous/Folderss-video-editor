using System;
using System.Collections.Generic;
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
        /// padding은 머리글 글 좌우 여백(탭이 많은 좁은 패널은 줄여 한 줄에 들어가게 — 넘치면 탭 줄이 두 줄이 되고 고를 때마다 줄이 자리를 바꾼다).
        /// </summary>
        public static void StyleTabs(TabControl tabs, double padding = 12)
        {
            tabs.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
            tabs.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            tabs.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
            tabs.Padding = new Thickness(0);
            var template = new ControlTemplate(typeof(TabItem));
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.PaddingProperty, new Thickness(padding, 5, padding, 4));
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

        /// <summary>
        /// 색 칸 한 줄: 이름 · 팔레트(색 견본) · 16진 칸(#RRGGBB). get으로 보이고 set으로 바꾼다.
        /// 값이 바뀌었을 때 <see cref="ColorRow.Refresh"/>를 불러 다시 보인다(보이는 것만으로는 set을 부르지 않음).
        /// </summary>
        public static ColorRow ColorRow(string label, string automationId, Func<string> get, Action<string> set)
        {
            return new ColorRow(label, automationId, get, set);
        }
    }

    /// <summary>
    /// 색 칸: 팔레트(흰·검정·빨강·주황·노랑·초록·하늘·파랑·보라·금색·회색)에서 고르거나 16진(#RRGGBB, # 없어도 됨)으로 적는다.
    /// 잘못 적은 값은 지금 색으로 되돌린다. 칸은 마지막으로 보인 값을 기억해, 적은 글이 그 값과 다를 때만 바꾼다 —
    /// 칸에 포커스를 둔 채 메뉴로 실행 취소한 뒤 칸을 떠나도 옛 값을 다시 적용하지 않게.
    /// </summary>
    internal sealed class ColorRow : DockPanel
    {
        private static readonly IReadOnlyList<PaletteColor> Palette = new[]
        {
            new PaletteColor("흰색", "#FFFFFF"), new PaletteColor("검정", "#000000"), new PaletteColor("빨강", "#EF4444"), new PaletteColor("주황", "#F97316"),
            new PaletteColor("노랑", "#FACC15"), new PaletteColor("초록", "#22C55E"), new PaletteColor("하늘", "#38BDF8"), new PaletteColor("파랑", "#3B82F6"),
            new PaletteColor("보라", "#8B5CF6"), new PaletteColor("금색", "#D4AF37"), new PaletteColor("회색", "#9CA3AF")
        };

        private readonly Func<string> _get;
        private readonly Action<string> _set;
        private readonly ComboBox _palette = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _hex = new TextBox { Width = 66, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        // 지금 색 견본 — 팔레트에 없는 색(16진으로 적은 색)도, 좁은 패널에서 팔레트가 눌려도 보인다
        private readonly Border _swatch = new Border { Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
        private bool _showing;
        // 칸에 마지막으로 보인 값(#RRGGBB)
        private string _shown;

        public ColorRow(string label, string automationId, Func<string> get, Action<string> set)
        {
            _get = get;
            _set = set;
            Margin = new Thickness(0, 3, 0, 3);
            var name = UiKit.Label(label);
            name.Width = 84;
            SetDock(name, Dock.Left);
            Children.Add(name);
            SetDock(_swatch, Dock.Left);
            Children.Add(_swatch);
            AutomationProperties.SetAutomationId(_hex, automationId);
            AutomationProperties.SetName(_hex, label + " (16진)");
            SetDock(_hex, Dock.Right);
            Children.Add(_hex);
            AutomationProperties.SetAutomationId(_palette, automationId + "Palette");
            AutomationProperties.SetName(_palette, label);
            // 데이터 항목 + 틀: 고른 칸에도 견본·이름이 보이고(요소를 항목으로 두면 목록을 열기 전까지 빈칸), 글자로 찾기·화면 낭독은 이름으로
            var swatch = new FrameworkElementFactory(typeof(Border));
            swatch.SetValue(WidthProperty, 14.0);
            swatch.SetValue(HeightProperty, 14.0);
            swatch.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
            swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            swatch.SetValue(Border.BorderBrushProperty, Brushes.Gray);
            swatch.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(PaletteColor.Brush)));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaletteColor.Name)));
            var item = new FrameworkElementFactory(typeof(StackPanel));
            item.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            item.AppendChild(swatch);
            item.AppendChild(text);
            _palette.ItemTemplate = new DataTemplate { VisualTree = item };
            TextSearch.SetTextPath(_palette, nameof(PaletteColor.Name));
            _palette.ItemsSource = Palette;
            Children.Add(_palette);
            _palette.SelectionChanged += (s, e) =>
            {
                if (!_showing && _palette.SelectedItem is PaletteColor chosen)
                    Apply(chosen.Hex);
            };
            _hex.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    Commit();
                    e.Handled = true;
                }
            };
            _hex.LostFocus += (s, e) => Commit();
            Refresh();
        }

        /// <summary>지금 색을 다시 보인다(팔레트에 있으면 그 색, 없으면 비움). 칸에 사용자가 적던 글이 있으면 그 글은 그대로 둔다.</summary>
        public void Refresh()
        {
            var hex = Current();
            _showing = true;
            try
            {
                _palette.SelectedItem = Palette.FirstOrDefault(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase));
                _swatch.Background = new SolidColorBrush(UiKit.ParseColor(hex, Colors.White));
                // 포커스가 있어도 사용자가 고치지 않은 글(마지막으로 보인 값)이면 새 값으로 — 실행 취소가 칸에 보이게
                if (!_hex.IsKeyboardFocusWithin || _hex.Text == _shown)
                    _hex.Text = hex;
                _shown = hex;
            }
            finally
            {
                _showing = false;
            }
        }

        private string Current()
        {
            return Core.TextStyle.NormalizeColor(_get(), "#FFFFFF");
        }

        private void Commit()
        {
            // 숨은 칸(접힌 색 칸)·꺼진 칸(실행 취소로 윤곽선이 꺼지며 포커스를 잃을 때)과 고치지 않은 글은 적용하지 않는다
            var text = (_hex.Text ?? "").Trim();
            if (!IsVisible || !IsEnabled || text == _shown)
                return;
            if (text.Length == 6 || text.Length == 8)
                text = "#" + text;
            var hex = Core.TextStyle.NormalizeColor(text, null);
            if (hex == null || string.Equals(hex, Current(), StringComparison.OrdinalIgnoreCase))
            {
                // 잘못 적었거나 지금 색 그대로면 되돌린다(편집하지 않음)
                _hex.Text = Current();
                Refresh();
                return;
            }
            Apply(hex);
        }

        private void Apply(string hex)
        {
            _set(hex);
            // 적용한 글은 정리한 값으로 보인다(포커스가 남아 있어도)
            _hex.Text = Current();
            Refresh();
        }

        /// <summary>팔레트 색 하나(이름이 화면 낭독·글자로 찾기에 쓰인다).</summary>
        private sealed class PaletteColor
        {
            public PaletteColor(string name, string hex)
            {
                Name = name;
                Hex = hex;
                var brush = new SolidColorBrush(UiKit.ParseColor(hex, Colors.White));
                brush.Freeze();
                Brush = brush;
            }

            public string Name { get; }
            public string Hex { get; }
            public Brush Brush { get; }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
