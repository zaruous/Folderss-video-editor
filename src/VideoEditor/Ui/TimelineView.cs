using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VideoEditor.Core;
using Track = VideoEditor.Core.Track;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 타임라인 영역: 도구 막대(실행 취소·분할·삭제·마그네틱·붙기·확대) + 트랙 머리글(출력·소리·잠금) + <see cref="TimelineCanvas"/> + 스크롤 막대.
    /// 명령은 <see cref="EditorCommands"/>로 실행한다(단축키·메뉴와 같은 동작).
    /// </summary>
    internal sealed class TimelineView : DockPanel
    {
        private const double HeaderWidth = 168;
        private readonly EditorSession _session;
        private readonly EditorCommands _commands;
        private readonly Canvas _headers = new Canvas { ClipToBounds = true };
        private readonly ScrollBar _horizontal = new ScrollBar { Orientation = Orientation.Horizontal, Height = 14 };
        private readonly ScrollBar _vertical = new ScrollBar { Orientation = Orientation.Vertical, Width = 14 };
        private readonly Slider _zoom = new Slider { Minimum = -6, Maximum = 5, Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        private readonly ToggleButton _magnetic;
        private readonly ToggleButton _snap;
        private bool _syncing;

        public TimelineView(EditorSession session, EditorServices services, EditorCommands commands)
        {
            _session = session;
            _commands = commands;
            Canvas = new TimelineCanvas(session, services);

            // 도구 막대
            var toolbar = new WrapPanel { Margin = new Thickness(4, 3, 4, 3) };
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphUndo, "실행 취소 (Ctrl+Z)", "UndoButton", commands.Undo));
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphRedo, "다시 실행 (Ctrl+Y)", "RedoButton", commands.Redo));
            toolbar.Children.Add(UiKit.ToolbarSeparator());
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphSplit, "분할 (Ctrl+B)", "SplitButton", commands.Split));
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphDelete, "삭제 (Delete) — Shift+Delete는 리플 삭제", "DeleteButton", () => commands.Delete(false)));
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphSpeed, "속도·길이 (Ctrl+R)", "SpeedButton", commands.ChangeSpeed));
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphAudio, "사운드 분리 (Ctrl+Alt+D)", "DetachAudioButton", commands.DetachAudio));
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphMarker, "마커 추가 (M)", "AddMarkerButton", commands.AddMarker));
            toolbar.Children.Add(UiKit.ToolbarSeparator());
            _magnetic = UiKit.IconToggle(UiKit.GlyphMagnet, "마그네틱 주 트랙 — 동영상 1에 빈틈 없이 붙임", "MagneticToggle", commands.SetMagnetic);
            _snap = UiKit.IconToggle(UiKit.GlyphSnap, "붙기 — 편집점·재생 헤드·마커에 붙음 (끄는 동안 Alt로 잠시 끔)", "SnapToggle", on => Canvas.Snapping = on);
            _snap.IsChecked = true;
            toolbar.Children.Add(_magnetic);
            toolbar.Children.Add(_snap);
            toolbar.Children.Add(UiKit.ToolbarSeparator());
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphZoomOut, "축소 (Ctrl+-)", "ZoomOutButton", () => Zoom(0.8)));
            AutomationProperties.SetAutomationId(_zoom, "ZoomSlider");
            AutomationProperties.SetName(_zoom, "확대/축소");
            _zoom.ValueChanged += (s, e) =>
            {
                if (!_syncing)
                    Canvas.ZoomBy(Math.Pow(2, _zoom.Value) / Canvas.PixelsPerFrame, Canvas.FrameToX(_session.Playhead));
            };
            toolbar.Children.Add(_zoom);
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphZoomIn, "확대 (Ctrl+=)", "ZoomInButton", () => Zoom(1.25)));
            toolbar.Children.Add(UiKit.IconButton(UiKit.GlyphFit, "타임라인에 맞춤 (Shift+Z)", "ZoomFitButton", Canvas.ZoomToFit));
            var bar = UiKit.Panel(toolbar, "SurfaceBackground");
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            SetDock(bar, Dock.Top);
            Children.Add(bar);

            // 머리글 | 캔버스 | 세로 스크롤 / 가로 스크롤
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headerBorder = UiKit.Panel(_headers, "SurfaceBackground");
            headerBorder.BorderThickness = new Thickness(0, 0, 1, 0);
            grid.Children.Add(headerBorder);
            Grid.SetColumn(Canvas, 1);
            grid.Children.Add(Canvas);
            Grid.SetColumn(_vertical, 2);
            grid.Children.Add(_vertical);
            Grid.SetColumn(_horizontal, 1);
            Grid.SetRow(_horizontal, 1);
            grid.Children.Add(_horizontal);
            AutomationProperties.SetAutomationId(_horizontal, "TimelineHScroll");
            Children.Add(grid);

            _horizontal.Scroll += (s, e) => Canvas.ScrollX = _horizontal.Value;
            _vertical.Scroll += (s, e) => Canvas.ScrollY = _vertical.Value;
            Canvas.ViewChanged += (s, e) => SyncView();
            session.Changed += (s, e) => SyncAll();
            Loaded += (s, e) => SyncAll();
            SyncAll();
        }

        public TimelineCanvas Canvas { get; }

        public void Zoom(double factor)
        {
            var x = Canvas.FrameToX(_session.Playhead);
            Canvas.ZoomBy(factor, x >= 0 && x <= Canvas.ActualWidth ? x : Canvas.ActualWidth / 2);
        }

        private void SyncAll()
        {
            _magnetic.IsChecked = _session.Project.Magnetic;
            BuildHeaders();
            SyncView();
        }

        private void SyncView()
        {
            _syncing = true;
            try
            {
                var width = Math.Max(1, Canvas.ActualWidth);
                _horizontal.Maximum = Math.Max(0, Canvas.ContentWidth - width + 40);
                _horizontal.ViewportSize = width;
                _horizontal.LargeChange = width * 0.8;
                _horizontal.SmallChange = 40;
                _horizontal.Value = Canvas.ScrollX;
                var height = Math.Max(1, Canvas.ActualHeight - TimelineCanvas.RulerHeight);
                _vertical.Maximum = Math.Max(0, Canvas.ContentHeight - height);
                _vertical.ViewportSize = height;
                _vertical.LargeChange = height * 0.8;
                _vertical.Value = Canvas.ScrollY;
                _vertical.Visibility = _vertical.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
                _zoom.Value = Math.Max(_zoom.Minimum, Math.Min(_zoom.Maximum, Math.Log(Canvas.PixelsPerFrame, 2)));
                // 머리글을 캔버스 줄 위치에 맞춘다
                var rows = Canvas.Rows();
                foreach (FrameworkElement header in _headers.Children)
                {
                    var row = rows.FirstOrDefault(r => r.Track.Id == (string)header.Tag);
                    if (row != null)
                        System.Windows.Controls.Canvas.SetTop(header, row.Top);
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>트랙 머리글(이름 + 출력/소리/잠금). 트랙 구성이 바뀔 때마다 다시 만든다.</summary>
        private void BuildHeaders()
        {
            _headers.Children.Clear();
            var add = UiKit.IconButton(UiKit.GlyphAdd, "트랙 추가", "AddTrackButton", () => { });
            add.Click += (s, e) => OpenTrackMenu(add, null);
            System.Windows.Controls.Canvas.SetLeft(add, 4);
            System.Windows.Controls.Canvas.SetTop(add, 2);
            _headers.Children.Add(add);
            var header = UiKit.Label("트랙", true);
            System.Windows.Controls.Canvas.SetLeft(header, 40);
            System.Windows.Controls.Canvas.SetTop(header, 6);
            _headers.Children.Add(header);
            foreach (var row in Canvas.Rows())
                _headers.Children.Add(TrackHeader(row));
        }

        private FrameworkElement TrackHeader(TimelineRow row)
        {
            var track = row.Track;
            var id = track.Id;
            var key = track.Name.Replace(" ", "");
            var panel = new DockPanel { Width = HeaderWidth - 1, Height = row.Height, Tag = id, LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (track.Kind == TrackKind.Video)
            {
                var eye = UiKit.IconToggle(UiKit.GlyphEye, "트랙 출력 켜기/끄기", "TrackOutput_" + key, on => _commands.SetTrackFlag(id, t => t.Hidden = !on));
                eye.IsChecked = !track.Hidden;
                buttons.Children.Add(eye);
            }
            // 자막 트랙에는 소리가 없다
            if (!track.Subtitles)
            {
                var sound = UiKit.IconToggle(track.Muted ? UiKit.GlyphMute : UiKit.GlyphVolume, "트랙 소리 켜기/끄기", "TrackSound_" + key, on => _commands.SetTrackFlag(id, t => t.Muted = !on));
                sound.IsChecked = !track.Muted;
                buttons.Children.Add(sound);
            }
            var lockButton = UiKit.IconToggle(UiKit.GlyphLock, "트랙 잠금", "TrackLock_" + key, on => _commands.SetTrackFlag(id, t => t.Locked = on));
            lockButton.IsChecked = track.Locked;
            buttons.Children.Add(lockButton);
            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);
            if (track.Subtitles)
            {
                // 자막 구역 표시(타임라인의 큐 색과 같은 띠)
                var stripe = new Border { Width = 3, Background = new SolidColorBrush(TimelineCanvas.SubtitleColor) };
                DockPanel.SetDock(stripe, Dock.Left);
                panel.Children.Add(stripe);
            }
            var name = UiKit.Label(track.Name + (track == _session.Project.MainTrack ? " ★" : ""));
            name.Margin = new Thickness(track.Subtitles ? 5 : 8, 0, 0, 0);
            name.ToolTip = track == _session.Project.MainTrack ? "주 트랙 — 마그네틱이 켜져 있으면 빈틈 없이 붙습니다"
                : track.Subtitles ? "자막 트랙 — 큐를 화면 아래 가운데에 입힙니다. SRT·VTT를 여기에 끌어 놓으면 이 트랙에 들어갑니다"
                : track.Name;
            panel.Children.Add(name);
            var border = new Border { Child = panel, Tag = id, BorderThickness = new Thickness(0, 0, 0, 1) };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            border.SetResourceReference(Border.BackgroundProperty, track.Kind == TrackKind.Video ? "SurfaceBackground" : "ControlBackground");
            border.ContextMenu = new ContextMenu();
            border.ContextMenuOpening += (s, e) => FillTrackMenu(border.ContextMenu, track);
            System.Windows.Controls.Canvas.SetTop(border, row.Top);
            return border;
        }

        private void OpenTrackMenu(FrameworkElement target, Track track)
        {
            var menu = new ContextMenu { PlacementTarget = target };
            FillTrackMenu(menu, track);
            menu.IsOpen = true;
        }

        private void FillTrackMenu(ContextMenu menu, Track track)
        {
            menu.Items.Clear();
            menu.Items.Add(MenuItem("동영상 트랙 추가", () => _commands.AddTrack(TrackKind.Video)));
            menu.Items.Add(MenuItem("오디오 트랙 추가", () => _commands.AddTrack(TrackKind.Audio)));
            menu.Items.Add(MenuItem("자막 트랙 추가", _commands.AddSubtitleTrack));
            if (track != null && track.Subtitles)
            {
                menu.Items.Add(new Separator());
                var highlight = MenuItem("말하는 단어 강조(음성 인식 자막)", () => _commands.SetWordHighlight(track.Id, !track.WordHighlight));
                highlight.IsChecked = track.WordHighlight;
                System.Windows.Automation.AutomationProperties.SetAutomationId(highlight, "WordHighlightMenu");
                menu.Items.Add(highlight);
            }
            if (track != null && track != _session.Project.MainTrack)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuItem("'" + track.Name + "' 트랙 삭제", () => _commands.RemoveTrack(track.Id)));
            }
        }

        private static MenuItem MenuItem(string header, Action click)
        {
            var item = new MenuItem { Header = new TextBlock { Text = header } };
            item.Click += (s, e) => click();
            return item;
        }
    }
}
