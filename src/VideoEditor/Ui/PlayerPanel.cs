using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 플레이어 패널(가운데 위): 미리 보기 화면 + 재생 단추·타임코드(입력하면 그 위치로)·In/Out·소리 끄기·미리 보기 품질·스냅샷.
    /// </summary>
    internal sealed class PlayerPanel : DockPanel
    {
        private readonly EditorSession _session;
        private readonly EditorServices _services;
        private readonly EditorCommands _commands;
        private readonly PreviewPlayer _player;
        private readonly Image _image = new Image { Stretch = Stretch.Uniform };
        private readonly TextBox _timecode = new TextBox { Width = 96, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _duration = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        private readonly TextBlock _placeholder = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), Foreground = Brushes.Gray };
        private readonly Button _play;
        private readonly ToggleButton _mute;
        private readonly ComboBox _quality = new ComboBox { Width = 72, VerticalAlignment = VerticalAlignment.Center };
        // 원본 보기 표시 색(호박색 — 화면 위 검은 바탕에 얹으므로 테마와 관계없이 같은 색)
        private static readonly Brush SourceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xA9, 0x4A)));

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        public PlayerPanel(EditorSession session, EditorServices services, EditorCommands commands, PreviewPlayer player)
        {
            _session = session;
            _services = services;
            _commands = commands;
            _player = player;

            var bar = new DockPanel { Margin = new Thickness(4, 4, 4, 4), LastChildFill = false };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(UiKit.IconButton(UiKit.GlyphToStart, "처음으로 (Home)", "GoStartButton", () => commands.GoTo(0)));
            left.Children.Add(UiKit.IconButton(UiKit.GlyphStepBack, "이전 프레임 (←)", "PrevFrameButton", () => commands.Step(-1)));
            _play = UiKit.IconButton(UiKit.GlyphPlay, "재생/일시 정지 (Space)", "PlayButton", commands.TogglePlay);
            left.Children.Add(_play);
            left.Children.Add(UiKit.IconButton(UiKit.GlyphStepForward, "다음 프레임 (→)", "NextFrameButton", () => commands.Step(1)));
            left.Children.Add(UiKit.IconButton(UiKit.GlyphToEnd, "끝으로 (End)", "GoEndButton", () => commands.GoTo(_session.Project.Duration)));
            left.Children.Add(UiKit.ToolbarSeparator());
            AutomationProperties.SetAutomationId(_timecode, "TimecodeBox");
            AutomationProperties.SetName(_timecode, "재생 헤드 타임코드");
            _timecode.ToolTip = "타임코드(시:분:초:프레임)를 입력하고 Enter — 그 위치로 이동";
            _timecode.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    long frame;
                    if (_session.Project.Rate.TryParseTimecode(_timecode.Text, out frame))
                        commands.GoTo(frame);
                    else
                        commands.Status("타임코드를 읽지 못했습니다: " + _timecode.Text);
                    UpdateTime();
                    Keyboard.Focus(_image);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    UpdateTime();
                    e.Handled = true;
                }
            };
            _timecode.LostFocus += (s, e) => UpdateTime();
            left.Children.Add(_timecode);
            _duration.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            AutomationProperties.SetAutomationId(_duration, "DurationText");
            left.Children.Add(_duration);
            bar.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(UiKit.TextButton("In", "MarkInButton", commands.MarkIn));
            right.Children.Add(UiKit.TextButton("Out", "MarkOutButton", commands.MarkOut));
            ((Button)right.Children[0]).MinWidth = ((Button)right.Children[1]).MinWidth = 36;
            ((Button)right.Children[0]).ToolTip = "In 점 (I)";
            ((Button)right.Children[1]).ToolTip = "Out 점 (O)";
            right.Children.Add(UiKit.ToolbarSeparator());
            _mute = UiKit.IconToggle(UiKit.GlyphMute, "미리 보기 소리 끄기", "PlayerMuteToggle", on =>
            {
                _player.Muted = on;
                _services.SaveSetting(EditorServices.PlayerMutedKey, on ? "true" : "false");
            });
            _mute.IsChecked = _player.Muted;
            right.Children.Add(_mute);
            foreach (var scale in new[] { "전체", "1/2", "1/4" })
                _quality.Items.Add(scale);
            _quality.SelectedIndex = _player.Scale >= 0.99 ? 0 : _player.Scale >= 0.49 ? 1 : 2;
            _quality.ToolTip = "미리 보기 품질 — 낮을수록 재생이 가볍습니다(내보내기는 늘 전체 해상도)";
            AutomationProperties.SetAutomationId(_quality, "PreviewQualityBox");
            AutomationProperties.SetName(_quality, "미리 보기 품질");
            _quality.SelectionChanged += (s, e) =>
            {
                _player.Scale = new[] { 1.0, 0.5, 0.25 }[Math.Max(0, _quality.SelectedIndex)];
                _services.SaveSetting(EditorServices.PreviewScaleKey, UiKit.Number(_player.Scale));
                commands.RefreshPreview();
            };
            right.Children.Add(_quality);
            right.Children.Add(UiKit.IconButton(UiKit.GlyphCamera, "스냅샷 — 지금 화면을 PNG로 저장하고 미디어에 추가", "SnapshotButton", commands.Snapshot));
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var barBorder = UiKit.Panel(bar, "SurfaceBackground");
            barBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            SetDock(barBorder, Dock.Bottom);
            Children.Add(barBorder);

            var screen = new Grid { Background = Brushes.Black, ClipToBounds = true };
            AutomationProperties.SetAutomationId(_image, "PreviewImage");
            AutomationProperties.SetName(_image, "미리 보기 화면");
            screen.Children.Add(_image);
            // 미리 보기에서 클립을 눌러 고르고, 고른 동영상·사진·글자를 직접 옮기고 크기(글자는 상자 너비·높이도)를 바꾸는 틀
            var overlay = new TransformOverlay(session, commands, () => player.IsPlaying);
            screen.Children.Add(overlay);
            // 효과 칸 미리 보기(효과 탭 칸에 마우스를 올려 둔 동안): 화면을 효과를 얹은 그림으로 바꾸고 왼쪽 위에 이름 — 끝나면 미리 보기 그림으로 되돌린다
            var hover = commands.EffectHover;
            var hoverText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            var hoverLabel = new Border
            {
                Child = hoverText,
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0)),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(7, 3, 8, 3),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            hoverLabel.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(hoverLabel, "EffectHoverPreview");
            screen.Children.Add(hoverLabel);
            // 무거운 수식 전환을 배경에서 미리 그리는 동안: 왼쪽 아래에 전환 이름과 그린 장 수(다 그리면 사라진다 — 그 뒤 재생은 캐시를 읽는다)
            var prerenderText = new TextBlock { Foreground = Brushes.White, FontSize = 11 };
            var prerenderLabel = new Border
            {
                Child = prerenderText,
                Background = new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(7, 2, 8, 3),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(prerenderLabel, "PrerenderStatus");
            screen.Children.Add(prerenderLabel);
            player.PrerenderChanged += (s, e) =>
            {
                var status = player.Prerendering;
                prerenderText.Text = status == null ? "" : "전환 미리 그리는 중 · " + status.Name + " " + status.Done + "/" + status.Total + "장"
                                                           + (status.Waiting > 0 ? " · 다음 " + status.Waiting + "개" : "");
                AutomationProperties.SetName(prerenderLabel, prerenderText.Text);
                prerenderLabel.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
            };
            AutomationProperties.SetAutomationId(_placeholder, "PreviewPlaceholder");
            screen.Children.Add(_placeholder);
            // 원본 보기(미디어·클립을 눌렀을 때 — SourcePreview): 왼쪽 위 호박색 "원본 · 이름", 오른쪽 위 [× 돌아가기]
            var source = commands.Source;
            var sourceText = new TextBlock { Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            var sourceLabel = new Border
            {
                Child = sourceText,
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)),
                BorderBrush = SourceBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(7, 3, 8, 3),
                Margin = new Thickness(8, 8, 96, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(sourceLabel, "SourcePreviewLabel");
            screen.Children.Add(sourceLabel);
            var sourceClose = new Button
            {
                Content = "× 돌아가기",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)),
                BorderBrush = SourceBrush,
                ToolTip = "원본 보기를 닫고 재생 헤드 화면으로 (Esc)",
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(sourceClose, "SourcePreviewClose");
            AutomationProperties.SetName(sourceClose, "원본 보기 닫기");
            sourceClose.Click += (s, e) => source.Close();
            screen.Children.Add(sourceClose);
            var transport = SourceTransport(source, commands);
            screen.Children.Add(transport);
            Children.Add(screen);

            // 보일 그림: 효과 칸 미리 보기 > 원본 보기(그림이 오면) > 재생 헤드 화면
            void ShowPicture()
            {
                _image.Source = hover.Frame ?? (source.IsOpen ? source.Frame : null) ?? player.Bitmap;
                var showSource = source.IsOpen && hover.Frame == null;
                sourceText.Text = showSource ? source.Label + (source.Frame == null ? " · 불러오는 중…" : "") : "";
                AutomationProperties.SetName(sourceLabel, sourceText.Text);
                sourceLabel.Visibility = sourceClose.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
                transport.Visibility = showSource && source.IsVideo ? Visibility.Visible : Visibility.Collapsed;
                // 원본 보기 동안은 미리 보기 틀(옮기기·크기)을 숨긴다 — 보이는 그림이 재생 헤드 화면이 아니므로
                overlay.Visibility = source.IsOpen ? Visibility.Collapsed : Visibility.Visible;
                UpdatePlaceholder();
            }
            source.Changed += (s, e) => ShowPicture();

            hover.Changed += (s, e) =>
            {
                var showing = hover.Frame != null;
                ShowPicture();
                hoverText.Text = showing ? "미리 보기: " + hover.Effect.Name : "";
                AutomationProperties.SetName(hoverLabel, hoverText.Text);
                hoverLabel.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            };
            player.BitmapChanged += (s, e) =>
            {
                if (hover.Frame == null && source.Frame == null)
                    _image.Source = player.Bitmap;
            };
            player.StateChanged += (s, e) =>
            {
                ((TextBlock)_play.Content).Text = player.IsPlaying ? UiKit.GlyphPause : UiKit.GlyphPlay;
                UpdatePlaceholder();
                overlay.Refresh();
            };
            session.PlayheadChanged += (s, e) => UpdateTime();
            session.Changed += (s, e) =>
            {
                UpdateTime();
                UpdatePlaceholder();
            };
            EventHandler toolChanged = (s, e) => Dispatcher.BeginInvoke(new Action(UpdatePlaceholder));
            Loaded += (s, e) =>
            {
                services.ToolChanged -= toolChanged;
                services.ToolChanged += toolChanged;
                // 만든 뒤 화면에 붙기 전에 FFmpeg를 다시 찾아 바뀌었으면(예: 설정한 FFmpeg가 없어짐) 그 알림을 놓쳤으므로 지금 값으로 다시
                UpdatePlaceholder();
            };
            Unloaded += (s, e) => services.ToolChanged -= toolChanged;
            UpdateTime();
            UpdatePlaceholder();
        }

        /// <summary>
        /// 원본 보기(동영상 미디어)의 아래 막대: [재생] 원본 위치 슬라이더(In~Out 구간 표시) 원본 타임코드 [In][Out][타임라인에 넣기].
        /// 타임라인 재생 헤드와 따로 움직인다 — 슬라이더를 끌면 원본 플레이어가 잠깐 모아 마지막 위치만 그린다.
        /// </summary>
        private static FrameworkElement SourceTransport(SourcePreview source, EditorCommands commands)
        {
            Button Make(string text, string id, string tip, Action click)
            {
                var button = new Button
                {
                    Content = text,
                    MinWidth = 30,
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(2, 0, 2, 0),
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)),
                    BorderBrush = SourceBrush,
                    ToolTip = tip,
                    Focusable = false
                };
                AutomationProperties.SetAutomationId(button, id);
                AutomationProperties.SetName(button, tip);
                button.Click += (s, e) => click();
                return button;
            }
            var play = Make(UiKit.GlyphPlay, "SourcePlayButton", "원본 재생/일시 정지 (Space)", source.TogglePlay);
            var playGlyph = new TextBlock { Text = UiKit.GlyphPlay, FontFamily = UiKit.IconFont, FontSize = 12 };
            play.Content = playGlyph;
            var markIn = Make("In", "SourceMarkInButton", "원본 In 점 (I)", source.MarkIn);
            var markOut = Make("Out", "SourceMarkOutButton", "원본 Out 점 (O) — 이 프레임까지", source.MarkOut);
            var insert = Make("타임라인에 넣기", "SourceInsertButton", "원본 In~Out 구간만 타임라인 끝에 넣기(In/Out이 없으면 전체 — 미디어 두 번 누르기와 같음)", commands.InsertSource);
            var time = new TextBlock { Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
            AutomationProperties.SetAutomationId(time, "SourceTimecode");
            var slider = new Slider
            {
                Minimum = 0,
                SmallChange = 1,
                IsMoveToPointEnabled = true,
                IsSelectionRangeEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Focusable = false
            };
            AutomationProperties.SetAutomationId(slider, "SourceSlider");
            AutomationProperties.SetName(slider, "원본 위치");
            var updating = false;
            slider.ValueChanged += (s, e) =>
            {
                if (!updating)
                    source.Seek((long)Math.Round(e.NewValue));
            };

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(time);
            right.Children.Add(markIn);
            right.Children.Add(markOut);
            right.Children.Add(insert);
            var row = new DockPanel();
            DockPanel.SetDock(play, Dock.Left);
            row.Children.Add(play);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(slider);
            var bar = new Border
            {
                Child = row,
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)),
                BorderBrush = SourceBrush,
                BorderThickness = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(bar, "SourceTransport");

            void Update()
            {
                if (!source.IsVideo)
                    return;
                var rate = source.Rate;
                updating = true;
                slider.Maximum = Math.Max(0, source.Length - 1);
                slider.LargeChange = Math.Max(1, rate.NominalFps);
                slider.Value = source.Position;
                slider.SelectionStart = source.InPoint ?? 0;
                slider.SelectionEnd = source.OutPoint ?? slider.Maximum;
                slider.IsSelectionRangeEnabled = source.InPoint.HasValue || source.OutPoint.HasValue;
                updating = false;
                var text = rate.Timecode(source.Position) + " / " + rate.Timecode(source.Length);
                if (source.InPoint.HasValue || source.OutPoint.HasValue)
                    text += "  ·  In " + rate.Timecode(source.InPoint ?? 0) + " ~ Out " + rate.Timecode(source.OutPoint ?? source.Length - 1);
                time.Text = text;
                AutomationProperties.SetName(time, text);
                playGlyph.Text = source.IsPlaying ? UiKit.GlyphPause : UiKit.GlyphPlay;
            }
            source.TransportChanged += (s, e) => Update();
            source.Changed += (s, e) => Update();
            return bar;
        }

        private void UpdateTime()
        {
            var rate = _session.Project.Rate;
            if (!_timecode.IsKeyboardFocusWithin)
                _timecode.Text = rate.Timecode(_session.Playhead);
            _duration.Text = "/ " + rate.Timecode(_session.Project.Duration);
        }

        private void UpdatePlaceholder()
        {
            string text = null;
            if (_commands.Source.IsOpen)
                text = null;
            else if (_services.Tool == null)
                text = "FFmpeg가 없어 미리 보기를 할 수 없습니다.\n파일 › FFmpeg 설정에서 설치하세요.";
            else if (_session.Project.Duration == 0)
                text = "미디어를 가져와 타임라인에 놓으면 여기에 보입니다.";
            _placeholder.Text = text ?? "";
            _placeholder.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            _image.Visibility = text == null ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
