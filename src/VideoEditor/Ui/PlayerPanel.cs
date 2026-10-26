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
            AutomationProperties.SetAutomationId(_placeholder, "PreviewPlaceholder");
            screen.Children.Add(_placeholder);
            Children.Add(screen);

            hover.Changed += (s, e) =>
            {
                var showing = hover.Frame != null;
                _image.Source = showing ? hover.Frame : player.Bitmap;
                hoverText.Text = showing ? "미리 보기: " + hover.Effect.Name : "";
                AutomationProperties.SetName(hoverLabel, hoverText.Text);
                hoverLabel.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            };
            player.BitmapChanged += (s, e) =>
            {
                if (hover.Frame == null)
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
            if (_services.Tool == null)
                text = "FFmpeg가 없어 미리 보기를 할 수 없습니다.\n파일 › FFmpeg 설정에서 설치하세요.";
            else if (_session.Project.Duration == 0)
                text = "미디어를 가져와 타임라인에 놓으면 여기에 보입니다.";
            _placeholder.Text = text ?? "";
            _placeholder.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            _image.Visibility = text == null ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
