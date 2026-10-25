using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미리 보기 위의 틀: 고른 동영상·사진 클립 하나가 재생 헤드에 보이면 그 영역에 틀과 모서리 손잡이를 그린다.
    /// 안쪽을 끌면 옮기고(X·Y, 화면 가운데 가까이 가면 붙음) 모서리를 끌면 가운데를 두고 크기를 바꾼다(크기 %). Esc는 끌기 취소.
    /// 끄는 동안은 틀만 움직이고, 놓을 때 한 번 편집한다(실행 취소 한 번).
    /// </summary>
    internal sealed class TransformOverlay : Canvas
    {
        private const double Handle = 10;
        private const double SnapPixels = 8;
        private readonly EditorSession _session;
        private readonly EditorCommands _commands;
        private readonly Func<bool> _playing;
        private readonly Rectangle _frame = new Rectangle { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
        private readonly Rectangle[] _handles = new Rectangle[4];
        private readonly Line _guideX = new Line { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Line _guideY = new Line { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private Clip _clip;
        // 끄는 중: 0 = 아님, 1 = 옮기기, 2 = 크기
        private int _drag;
        private Point _dragFrom;
        private double _x, _y, _scale, _startX, _startY, _startScale, _startDistance;
        private Window _window;

        public TransformOverlay(EditorSession session, EditorCommands commands, Func<bool> playing)
        {
            _session = session;
            _commands = commands;
            _playing = playing;
            Background = Brushes.Transparent;
            ClipToBounds = true;
            AutomationProperties.SetAutomationId(this, "TransformOverlay");
            _frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _guideX.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _guideY.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            Children.Add(_guideX);
            Children.Add(_guideY);
            Children.Add(_frame);
            for (var i = 0; i < 4; i++)
            {
                _handles[i] = new Rectangle { Width = Handle, Height = Handle, Fill = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
                _handles[i].SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                Children.Add(_handles[i]);
            }
            session.Changed += (s, e) => Refresh();
            session.SelectionChanged += (s, e) => Refresh();
            session.PlayheadChanged += (s, e) => Refresh();
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }

        /// <summary>틀을 보일 클립(고른 동영상·사진 하나, 재생 헤드에 보이고 트랙이 숨김·잠금이 아님)과 그 값으로 다시 그린다.</summary>
        public void Refresh()
        {
            if (_drag != 0)
                return;
            // 하나, 또는 링크된 영상·사운드 짝이면 누른 쪽(사운드면 틀이 없다)
            var clip = _session.PrimaryClip;
            var track = clip == null ? null : _session.Project.TrackOf(clip);
            if (clip == null || track == null || clip.Kind != ClipKind.Media || !clip.IsVisual || track.Kind != TrackKind.Video || track.Hidden || track.Locked
                || !clip.Contains(_session.Playhead) || _playing())
            {
                _clip = null;
                Show(false);
                return;
            }
            _clip = clip;
            _x = clip.X;
            _y = clip.Y;
            _scale = clip.Scale;
            Show(true);
            Layout();
        }

        /// <summary>틀이 보이는지(시험용).</summary>
        public bool HasFrame { get { return _clip != null; } }

        private void Show(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            _frame.Visibility = state;
            foreach (var handle in _handles)
                handle.Visibility = state;
            if (!visible)
                _guideX.Visibility = _guideY.Visibility = Visibility.Collapsed;
            Cursor = null;
        }

        /// <summary>미리 보기 화면(비율 유지로 가운데)에서 프로젝트 1픽셀의 크기와 화면 왼쪽 위.</summary>
        private (double Zoom, double Left, double Top) Screen()
        {
            var project = _session.Project;
            var zoom = Math.Min(ActualWidth / project.Width, ActualHeight / project.Height);
            return (zoom, (ActualWidth - project.Width * zoom) / 2, (ActualHeight - project.Height * zoom) / 2);
        }

        /// <summary>지금 값(_x·_y·_scale)의 틀: 가운데·크기(화면 픽셀)와 회전.</summary>
        private (Point Center, double Width, double Height, double Rotation) Frame()
        {
            var probe = _clip.CloneWithNewId();
            probe.X = _x;
            probe.Y = _y;
            probe.Scale = _scale;
            var content = ClipLayout.Content(_session.Project, probe);
            var (zoom, left, top) = Screen();
            return (new Point(left + content.CenterX * zoom, top + content.CenterY * zoom), content.Width * zoom, content.Height * zoom, content.Rotation);
        }

        private Point[] Corners((Point Center, double Width, double Height, double Rotation) frame)
        {
            var rotate = new RotateTransform(frame.Rotation, frame.Center.X, frame.Center.Y);
            var halfW = frame.Width / 2;
            var halfH = frame.Height / 2;
            return new[]
            {
                new Point(frame.Center.X - halfW, frame.Center.Y - halfH), new Point(frame.Center.X + halfW, frame.Center.Y - halfH),
                new Point(frame.Center.X + halfW, frame.Center.Y + halfH), new Point(frame.Center.X - halfW, frame.Center.Y + halfH)
            }.Select(rotate.Transform).ToArray();
        }

        private void Layout()
        {
            if (_clip == null || ActualWidth <= 0 || ActualHeight <= 0)
                return;
            var frame = Frame();
            _frame.Width = Math.Max(1, frame.Width);
            _frame.Height = Math.Max(1, frame.Height);
            SetLeft(_frame, frame.Center.X - frame.Width / 2);
            SetTop(_frame, frame.Center.Y - frame.Height / 2);
            _frame.RenderTransform = new RotateTransform(frame.Rotation, frame.Width / 2, frame.Height / 2);
            var corners = Corners(frame);
            for (var i = 0; i < 4; i++)
            {
                SetLeft(_handles[i], corners[i].X - Handle / 2);
                SetTop(_handles[i], corners[i].Y - Handle / 2);
            }
        }

        /// <summary>점이 무엇 위에 있는지: 0~3 모서리 손잡이, 4 틀 안, -1 밖.</summary>
        private int Hit(Point point)
        {
            if (_clip == null)
                return -1;
            var frame = Frame();
            var corners = Corners(frame);
            for (var i = 0; i < 4; i++)
            {
                if (Math.Abs(point.X - corners[i].X) <= Handle && Math.Abs(point.Y - corners[i].Y) <= Handle)
                    return i;
            }
            // 회전을 되돌려 틀 안인지 본다
            var local = new RotateTransform(-frame.Rotation, frame.Center.X, frame.Center.Y).Transform(point);
            return Math.Abs(local.X - frame.Center.X) <= frame.Width / 2 && Math.Abs(local.Y - frame.Center.Y) <= frame.Height / 2 ? 4 : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointerMove(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!PointerDown(e.GetPosition(this)))
                return;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (PointerUp())
                e.Handled = true;
        }

        /// <summary>누름: 틀 안이면 옮기기, 모서리면 크기 바꾸기를 시작한다(밖이면 false).</summary>
        private bool PointerDown(Point point)
        {
            var hit = Hit(point);
            if (hit < 0)
                return false;
            _drag = hit == 4 ? 1 : 2;
            _dragFrom = point;
            _startX = _x;
            _startY = _y;
            _startScale = _scale;
            _startDistance = Math.Max(1, (point - Frame().Center).Length);
            // 키보드 포커스는 그대로 두고(타임라인 단축키) 끄는 동안만 창에서 Esc를 받는다
            _window = Window.GetWindow(this);
            if (_window != null)
                _window.PreviewKeyDown += OnWindowKey;
            return true;
        }

        private void PointerMove(Point point)
        {
            if (_drag == 0)
            {
                var hit = Hit(point);
                Cursor = hit == 4 ? Cursors.SizeAll : hit == 0 || hit == 2 ? Cursors.SizeNWSE : hit == 1 || hit == 3 ? Cursors.SizeNESW : null;
                return;
            }
            var (zoom, _, _) = Screen();
            if (_drag == 1)
            {
                // 화면 가운데 가까이 가면 붙는다(가로·세로 따로)
                _x = ClipLayout.Snap(_startX + (point.X - _dragFrom.X) / zoom, SnapPixels / zoom);
                _y = ClipLayout.Snap(_startY + (point.Y - _dragFrom.Y) / zoom, SnapPixels / zoom);
                ShowGuides(_x == 0, _y == 0);
            }
            else
            {
                var center = Frame().Center;
                var distance = Math.Max(1, (point - center).Length);
                _scale = Math.Max(0.05, Math.Min(4, _startScale * distance / _startDistance));
            }
            Layout();
            _commands.Status(_drag == 1 ? "위치 X " + Math.Round(_x) + " · Y " + Math.Round(_y) + " (놓으면 바뀝니다, Esc는 취소)" : "크기 " + Math.Round(_scale * 100) + "% (놓으면 바뀝니다, Esc는 취소)");
        }

        /// <summary>놓음: 바뀌었으면 한 번 편집한다(실행 취소 한 번). 끄는 중이 아니었으면 false.</summary>
        private bool PointerUp()
        {
            if (_drag == 0)
                return false;
            var moved = _x != _startX || _y != _startY || _scale != _startScale;
            var (id, x, y, scale) = (_clip.Id, _x, _y, _scale);
            EndDrag();
            if (moved)
            {
                _commands.EditClip(id, (p, c) =>
                {
                    c.X = Math.Round(x);
                    c.Y = Math.Round(y);
                    c.Scale = Math.Round(scale, 3);
                }, null);
            }
            return true;
        }

        /// <summary>모서리 손잡이 자리(화면 픽셀, 왼쪽 위부터 시계 방향) — 시험용.</summary>
        internal Point[] HandlePoints()
        {
            return _clip == null ? new Point[0] : Corners(Frame());
        }

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _drag != 0)
            {
                _x = _startX;
                _y = _startY;
                _scale = _startScale;
                EndDrag();
                _commands.Status("옮기기·크기 바꾸기를 취소했습니다.");
                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_drag != 0)
            {
                _x = _startX;
                _y = _startY;
                _scale = _startScale;
                EndDrag();
            }
        }

        private void EndDrag()
        {
            _drag = 0;
            if (_window != null)
                _window.PreviewKeyDown -= OnWindowKey;
            _window = null;
            ShowGuides(false, false);
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            Refresh();
        }

        /// <summary>가운데에 붙었을 때 세로(가로 가운데)·가로(세로 가운데) 안내선.</summary>
        private void ShowGuides(bool vertical, bool horizontal)
        {
            var project = _session.Project;
            var (zoom, left, top) = Screen();
            _guideX.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
            _guideY.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
            _guideX.X1 = _guideX.X2 = left + project.Width * zoom / 2;
            _guideX.Y1 = top;
            _guideX.Y2 = top + project.Height * zoom;
            _guideY.Y1 = _guideY.Y2 = top + project.Height * zoom / 2;
            _guideY.X1 = left;
            _guideY.X2 = left + project.Width * zoom;
        }
    }
}
