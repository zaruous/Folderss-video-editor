using System;
using System.Collections.Generic;
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
    /// 미리 보기 위의 틀: 고른 동영상·사진·글자 클립 하나가 재생 헤드에 보이면 그 영역(글자는 글자 상자)에 틀과 손잡이를 그린다.
    /// 안쪽을 끌면 옮기고(X·Y, 화면 가운데·가장자리와 다른 클립의 가장자리·가운데에 붙음 — Alt는 붙지 않음) 모서리를 끌면 가운데를 두고 크기를 바꾼다(크기 %).
    /// 글자는 옆 손잡이가 넷 더 있다: 왼쪽·오른쪽은 상자 너비, 위·아래는 상자 높이 — 반대쪽 옆은 그대로 두고, 두 번 누르면 자동(0)으로.
    /// 아트 요소는 모양 손잡이(노란 둥근 점 — <see cref="ArtHandles"/>)가 더 있다: 모서리 반경, 선·화살표 양 끝(반대쪽 끝은 그대로), 별 안쪽, 말풍선 꼬리 끝.
    /// 이동 경로가 있으면 경로를 점선과 점으로 보이고 점을 끌어 고친다(<see cref="LayoutPath"/> — TransformOverlay.Path.cs).
    /// 틀 위쪽의 둥근 점을 끌면 돌린다(15° 단위, Shift는 자유롭게 — <c>TransformOverlay.Rotate.cs</c>).
    /// 틀 밖에서 보이는 클립을 누르면 그 클립을 고른다(맨 위 것, 빈 곳이면 고르기는 그대로). Esc는 끌기 취소.
    /// 끄는 동안은 틀만 움직이고, 놓을 때 한 번 편집한다(실행 취소 한 번). 미리 보기를 누른 뒤에는 화살표로 1px(Shift 10px)씩 옮긴다.
    /// </summary>
    internal sealed partial class TransformOverlay : Canvas
    {
        private const double Handle = 10;
        private const double SnapPixels = 8;
        // 손잡이 번호: 0~3 모서리(왼쪽 위부터 시계 방향), 4 틀 안, 5~8 옆(위·오른쪽·아래·왼쪽 — 글자만, BoxEdge 순서), 9~ 모양 손잡이(아트만), -1 밖
        private const int Inside = 4;
        private const int FirstEdge = 5;
        private const int FirstShape = 9;
        // 모양 손잡이가 모서리 손잡이와 겹쳐 보이지 않게 띄우는 거리(화면 픽셀 — 모서리 반경 0일 때 등)
        private const double ShapeHandleGap = 14;
        // 손잡이가 틀 가운데에서 향하는 방향(회전 전, 도 — 0 = 오른쪽, 시계 방향): 커서 모양을 고를 때
        private static readonly double[] HandleAngles = { 225, 315, 45, 135, 0, 270, 0, 90, 180 };
        private static readonly Brush ShapeHandleFill = Frozen(Color.FromRgb(0xFF, 0xC1, 0x07));
        private readonly EditorSession _session;
        private readonly EditorCommands _commands;
        private readonly Func<bool> _playing;
        private readonly Rectangle _frame = new Rectangle { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
        // 모서리 넷 + 옆 넷(글자만)
        private readonly Rectangle[] _handles = new Rectangle[8];
        private readonly Line _guideX = new Line { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Line _guideY = new Line { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        // 모양 손잡이(아트만 — 종류마다 몇 개, _shapeKinds 순서)
        private readonly Ellipse[] _shapeHandles = new Ellipse[2];
        private IReadOnlyList<ArtHandle> _shapeKinds = Array.Empty<ArtHandle>();
        private Clip _clip;
        // 끄는 중: 0 = 아님, 1 = 옮기기, 2 = 크기, 3 = 상자 옆(_edge), 4 = 모양 손잡이(_shapeHandle), 5 = 이동 경로 점(PathDrag — TransformOverlay.Path.cs), 6 = 회전(RotateDrag — TransformOverlay.Rotate.cs)
        private int _drag;
        private ArtHandle _shapeHandle;
        // 아트의 지금 모양·회전(끄는 동안 바뀜)과 끌기 시작 때 값
        private ArtShape _art, _startArt;
        private double _rotation, _startRotation;
        private BoxEdge _edge;
        private Point _dragFrom;
        // 누른 뒤 포인터가 실제로 움직였는지
        private bool _pointerMoved;
        // 지금 값(끄는 동안 바뀜)과 끌기 시작 때 값. 상자 너비·높이 0 = 자동
        private double _x, _y, _scale, _boxWidth, _boxHeight;
        private double _startX, _startY, _startScale, _startDistance, _startBoxWidth, _startBoxHeight;
        // 옆 끌기를 시작할 때의 상자 크기(자동이면 잰 크기, 크기 배율 전)
        private double _startMeasuredWidth, _startMeasuredHeight;
        private Window _window;
        // 화살표로 옮기기: 키를 누르고 있는 동안(반복)은 실행 취소 한 단계
        private string _nudgeKey;
        private int _nudges;

        public TransformOverlay(EditorSession session, EditorCommands commands, Func<bool> playing)
        {
            _session = session;
            _commands = commands;
            _playing = playing;
            Background = Brushes.Transparent;
            ClipToBounds = true;
            // 눌러서 포커스를 받아 화살표로 옮긴다(다른 단축키는 편집 창이 그대로 받는다)
            Focusable = true;
            FocusVisualStyle = null;
            AutomationProperties.SetAutomationId(this, "TransformOverlay");
            _frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _guideX.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _guideY.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            Children.Add(_guideX);
            Children.Add(_guideY);
            Children.Add(_frame);
            for (var i = 0; i < _handles.Length; i++)
            {
                _handles[i] = new Rectangle { Width = Handle, Height = Handle, Fill = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
                _handles[i].SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                Children.Add(_handles[i]);
            }
            // 모양 손잡이는 노란 둥근 점(크기 손잡이와 다르게 보이게)
            for (var i = 0; i < _shapeHandles.Length; i++)
            {
                _shapeHandles[i] = new Ellipse { Width = Handle + 1, Height = Handle + 1, Fill = ShapeHandleFill, StrokeThickness = 1.5, IsHitTestVisible = false };
                _shapeHandles[i].SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                Children.Add(_shapeHandles[i]);
            }
            // 회전 손잡이(TransformOverlay.Rotate.cs)
            CreateRotationHandle();
            session.Changed += (s, e) => Refresh();
            session.SelectionChanged += (s, e) => Refresh();
            session.PlayheadChanged += (s, e) => Refresh();
            SizeChanged += (s, e) => Refresh();
            Refresh();
        }

        /// <summary>틀을 보일 클립(고른 동영상·사진·글자 하나, 재생 헤드에 보이고 트랙이 숨김·잠금·자막 트랙이 아님)과 그 값으로 다시 그린다.</summary>
        public void Refresh()
        {
            if (_drag != 0)
                return;
            // 하나, 또는 링크된 영상·사운드 짝이면 누른 쪽(사운드면 틀이 없다)
            var clip = _session.PrimaryClip;
            if (!Framable(clip) || _playing())
            {
                _clip = null;
                Show(false);
                // 여럿 골랐으면 고른 클립마다 점선 틀(TransformOverlay.Group.cs)
                RefreshGroup();
                return;
            }
            _clip = clip;
            // 키프레임이 있으면 재생 헤드 시각의 값(TransformOverlay.Keyframes.cs)
            var pose = Pose(clip);
            _x = pose.X;
            _y = pose.Y;
            _scale = pose.Scale;
            _boxWidth = clip.TextBoxWidth;
            _boxHeight = clip.TextBoxHeight;
            _rotation = pose.Rotation;
            _art = clip.Kind == ClipKind.Art ? (clip.Art ?? new ArtShape()).Clone() : null;
            _shapeKinds = _art != null ? ArtHandles.For(_art.Kind) : Array.Empty<ArtHandle>();
            Show(true);
            Layout();
            RefreshGroup();
        }

        /// <summary>틀을 그릴 수 있는 클립: 재생 헤드에 보이는 화면 클립이고 트랙이 동영상 트랙(숨김·잠금·자막 트랙 아님 — 자막 큐는 libass가 그린다).</summary>
        private bool Framable(Clip clip)
        {
            var track = clip == null ? null : _session.Project.TrackOf(clip);
            return clip != null && track != null && clip.IsVisual && track.Kind == TrackKind.Video && !track.Hidden && !track.Locked && !track.Subtitles
                && clip.Contains(_session.Playhead);
        }

        /// <summary>틀이 보이는지(시험용).</summary>
        public bool HasFrame { get { return _clip != null; } }

        private bool IsText { get { return _clip != null && _clip.Kind == ClipKind.Text; } }
        // 옆 손잡이(상자 너비·높이)는 상자를 쓰는 글자만 — 긴 글(drawtext 대체 경로)은 상자를 무시하므로 끌어도 크기가 바뀌지 않고 위치만 밀린다
        private bool HasBoxHandles { get { return IsText && (_clip.Text ?? "").Length <= RenderGraph.MaxStyledTextLength; } }

        private void Show(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            _frame.Visibility = state;
            for (var i = 0; i < _handles.Length; i++)
                _handles[i].Visibility = visible && (i < 4 || HasBoxHandles) ? Visibility.Visible : Visibility.Collapsed;
            for (var i = 0; i < _shapeHandles.Length; i++)
                _shapeHandles[i].Visibility = visible && i < _shapeKinds.Count ? Visibility.Visible : Visibility.Collapsed;
            ShowRotationHandle(visible);
            if (!visible)
                _guideX.Visibility = _guideY.Visibility = Visibility.Collapsed;
            Cursor = null;
            LayoutPath();
        }

        /// <summary>미리 보기 화면(비율 유지로 가운데)에서 프로젝트 1픽셀의 크기와 화면 왼쪽 위.</summary>
        private (double Zoom, double Left, double Top) Screen()
        {
            var project = _session.Project;
            var zoom = Math.Min(ActualWidth / project.Width, ActualHeight / project.Height);
            return (zoom, (ActualWidth - project.Width * zoom) / 2, (ActualHeight - project.Height * zoom) / 2);
        }

        /// <summary>글자 클립의 상자 크기(프로젝트 픽셀, 크기 배율 전) — 그리개와 같은 배치로 잰다. 상자 값은 끄는 동안의 값을 넣을 수 있다.</summary>
        private static (double Width, double Height) TextSize(Clip clip, double boxWidth, double boxHeight)
        {
            var size = StyledTextRasterizer.MeasureClip(clip, boxWidth, boxHeight);
            return (size.Width, size.Height);
        }

        /// <summary>지금 값(_x·_y·_scale, 글자는 상자 값도)의 틀: 가운데·크기(화면 픽셀)와 회전.</summary>
        private (Point Center, double Width, double Height, double Rotation) Frame()
        {
            var probe = Probe();
            var content = ClipLayout.Content(_session.Project, probe);
            if (IsText)
            {
                var box = TextSize(_clip, _boxWidth, _boxHeight);
                content = ClipLayout.TextBox(_session.Project, probe, box.Width, box.Height);
            }
            else if (_art != null)
            {
                content = ClipLayout.TextBox(_session.Project, probe, _art.Width, _art.Height);
            }
            var (zoom, left, top) = Screen();
            return (new Point(left + content.CenterX * zoom, top + content.CenterY * zoom), content.Width * zoom, content.Height * zoom, content.Rotation);
        }

        /// <summary>지금 값(끄는 동안의 위치·크기·회전·모양)을 넣은 클립 사본(프로젝트에 없음).</summary>
        private Clip Probe()
        {
            var probe = _clip.CloneWithNewId();
            probe.X = _x;
            probe.Y = _y;
            probe.Scale = _scale;
            probe.Rotation = _rotation;
            if (_art != null)
                probe.Art = _art.Clone();
            return probe;
        }

        /// <summary>
        /// 모양 손잡이 자리(화면 픽셀, _shapeKinds 순서). 모서리 반경 손잡이는 반경이 작아 모서리 손잡이와 겹치면 대각선 안쪽으로 조금 띄워 보인다
        /// (끌기는 끈 거리만큼 바꾸므로 띄운 만큼 튀지 않는다).
        /// </summary>
        private Point[] ShapeHandlePoints()
        {
            if (_art == null || _shapeKinds.Count == 0)
                return new Point[0];
            var probe = Probe();
            var project = _session.Project;
            var (zoom, left, top) = Screen();
            Point ToScreen((double X, double Y) at) => new Point(left + at.X * zoom, top + at.Y * zoom);
            var points = new Point[_shapeKinds.Count];
            for (var i = 0; i < points.Length; i++)
            {
                var point = ToScreen(ArtHandles.Position(project, probe, _shapeKinds[i]));
                if (_shapeKinds[i] == ArtHandle.CornerRadius)
                {
                    // 화면 테두리는 안쪽 모서리(테두리 두께만큼 안쪽)에서 띄운다
                    var inset = _art.Kind == ArtKind.ScreenBorder ? Math.Min(_art.Thickness, Math.Min(_art.Width, _art.Height) / 2) : 0;
                    var corner = ToScreen(ArtHandles.ToProject(project, probe, inset, inset));
                    var inward = ToScreen(ArtHandles.ToProject(project, probe, inset + 1, inset + 1)) - corner;
                    if (inward.Length > 1e-9 && (point - corner).Length < ShapeHandleGap)
                    {
                        inward.Normalize();
                        point = corner + inward * ShapeHandleGap;
                    }
                }
                points[i] = point;
            }
            return points;
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

        /// <summary>옆 손잡이 자리(위·오른쪽·아래·왼쪽 옆의 가운데).</summary>
        private static Point[] Edges(Point[] corners)
        {
            return Enumerable.Range(0, 4).Select(i => new Point((corners[i].X + corners[(i + 1) % 4].X) / 2, (corners[i].Y + corners[(i + 1) % 4].Y) / 2)).ToArray();
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
            var points = corners.Concat(Edges(corners)).ToArray();
            for (var i = 0; i < _handles.Length; i++)
            {
                SetLeft(_handles[i], points[i].X - Handle / 2);
                SetTop(_handles[i], points[i].Y - Handle / 2);
            }
            var shapePoints = ShapeHandlePoints();
            for (var i = 0; i < shapePoints.Length && i < _shapeHandles.Length; i++)
            {
                SetLeft(_shapeHandles[i], shapePoints[i].X - _shapeHandles[i].Width / 2);
                SetTop(_shapeHandles[i], shapePoints[i].Y - _shapeHandles[i].Height / 2);
            }
            LayoutRotationHandle();
            if (_groupMoving)
                LayoutGroup();
            // 이동 경로(TransformOverlay.Path.cs) — 클립을 끄는 동안 함께 움직인다
            LayoutPath();
        }

        /// <summary>점이 무엇 위에 있는지: 0~3 모서리 손잡이, 5~8 옆 손잡이(글자만), 9~ 모양 손잡이(아트만), 99~ 이동 경로(TransformOverlay.Path.cs), 4 틀 안, -1 밖.</summary>
        private int Hit(Point point)
        {
            if (_clip == null)
                return -1;
            // 이동 경로의 점·점선이 먼저(작은 점이라 틀 안·손잡이와 겹쳐도 잡히게)
            var pathHit = PathHit(point);
            if (pathHit >= 0)
                return pathHit;
            var frame = Frame();
            var corners = Corners(frame);
            // 모양 손잡이가 먼저(납작한 선의 끝점은 모서리 손잡이와 가깝다) — 둥근 점 크기만큼
            var shapePoints = ShapeHandlePoints();
            var shapeBest = -1;
            var shapeNearest = double.MaxValue;
            for (var i = 0; i < shapePoints.Length; i++)
            {
                var distance = (point - shapePoints[i]).Length;
                if (distance <= Handle / 2 + 2 && distance < shapeNearest)
                {
                    shapeBest = FirstShape + i;
                    shapeNearest = distance;
                }
            }
            if (shapeBest >= 0)
                return shapeBest;
            if (HitRotation(point))
                return RotationHit;
            // 가장 가까운 손잡이(같으면 모서리). 틀이 작으면 잡는 거리를 줄여 안쪽(옮기기)이 손잡이에 가리지 않게 한다
            var reach = Math.Min(Handle, Math.Max(3, Math.Min(frame.Width, frame.Height) / 3));
            var points = HasBoxHandles ? corners.Concat(Edges(corners)).ToArray() : corners;
            var best = -1;
            var nearest = double.MaxValue;
            for (var i = 0; i < points.Length; i++)
            {
                var distance = Math.Max(Math.Abs(point.X - points[i].X), Math.Abs(point.Y - points[i].Y));
                if (distance <= reach && distance < nearest)
                {
                    best = i < 4 ? i : FirstEdge + i - 4;
                    nearest = distance;
                }
            }
            if (best >= 0)
                return best;
            // 회전을 되돌려 틀 안인지 본다
            var local = new RotateTransform(-frame.Rotation, frame.Center.X, frame.Center.Y).Transform(point);
            return Math.Abs(local.X - frame.Center.X) <= frame.Width / 2 && Math.Abs(local.Y - frame.Center.Y) <= frame.Height / 2 ? Inside : -1;
        }

        /// <summary>손잡이의 커서: 회전을 더한 방향을 45° 단위로 — 가로 ↔, 세로 ↕, 대각선 ⤡·⤢. 틀 안은 옮기기.</summary>
        private Cursor CursorFor(int hit)
        {
            if (hit == Inside)
                return Cursors.SizeAll;
            if (hit < 0)
                return null;
            if (hit == RotationHit)
                return Cursors.Hand;
            if (hit >= FirstShape)
                return Cursors.Cross;
            var angle = HandleAngles[hit] + _rotation;
            var step = (int)Math.Round(((angle % 180) + 180) % 180 / 45) % 4;
            return step == 0 ? Cursors.SizeWE : step == 1 ? Cursors.SizeNWSE : step == 2 ? Cursors.SizeNS : Cursors.SizeNESW;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointerMove(e.GetPosition(this));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            // 미리 보기를 누르면 화살표가 고른 클립을 옮긴다(재생 헤드 이동 대신)
            Focus();
            // Ctrl+누르기: 고르기에 더하거나 뺀다(여럿 고르면 함께 옮김)
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (PointerToggle(e.GetPosition(this)))
                    e.Handled = true;
                return;
            }
            // Shift+누르기: 사용자 경로의 끝에 점을 더한다(TransformOverlay.Path.cs)
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && PathAppend(e.GetPosition(this)))
            {
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 2 && PointerDoubleClick(e.GetPosition(this)))
            {
                e.Handled = true;
                return;
            }
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

        /// <summary>
        /// 누름: 틀 안이면 옮기기, 모서리면 크기, 옆이면 상자 너비·높이 바꾸기를 시작한다. 틀 밖이면 그 자리에 보이는 맨 위 클립을 골라 옮기기를 시작한다.
        /// 아무것도 없거나 재생 중이면 false(고르기는 그대로).
        /// </summary>
        private bool PointerDown(Point point)
        {
            if (_playing())
                return false;
            // 여럿 골랐으면 누른 클립을 앞세워 함께 옮긴다
            var hit = GroupLead(point) ? Inside : Hit(point);
            // 사용자 경로의 점선은 두 번 눌러 점을 끼우는 자리 — 한 번 누름은 아무것도 하지 않는다(밑의 클립을 고르지 않게)
            if (hit == PathLine)
                return false;
            // 틀 안이라도 그 자리에 고른 클립보다 위에 보이는 클립이 있으면 그것을 고른다 — 화면 전체 배경을 고른 뒤에도 위의 글자를 누를 수 있게
            if (hit == Inside && Pick(point) is Clip above && above.Id != _clip.Id)
                hit = -1;
            if (hit < 0)
            {
                var picked = Pick(point);
                if (picked == null)
                    return false;
                _session.Select(new[] { picked });
                Refresh();
                if (_clip == null || _clip.Id != picked.Id)
                    return false;
                hit = Inside;
            }
            _drag = hit == RotationHit ? RotateDrag : hit == Inside ? 1 : hit < 4 ? 2 : hit >= FirstPath ? PathDrag : hit >= FirstShape ? 4 : 3;
            _dragFrom = point;
            _pointerMoved = false;
            _snapLines = null;
            _startX = _x;
            _startY = _y;
            _startScale = _scale;
            _startBoxWidth = _boxWidth;
            _startBoxHeight = _boxHeight;
            _startRotation = _rotation;
            _startArt = _art?.Clone();
            if (_drag == 4)
                _shapeHandle = _shapeKinds[hit - FirstShape];
            if (_drag == PathDrag)
                StartPathDrag(hit - FirstPath);
            if (_drag == RotateDrag)
                StartRotate(point);
            _startDistance = Math.Max(1, (point - Frame().Center).Length);
            if (_drag == 3)
            {
                // 자동인 쪽은 잰 크기에서 시작한다
                _edge = (BoxEdge)(hit - FirstEdge);
                var size = TextSize(_clip, _boxWidth, _boxHeight);
                _startMeasuredWidth = size.Width;
                _startMeasuredHeight = size.Height;
            }
            // 끄는 동안만 창에서 Esc를 받는다(편집 창의 Esc — 고르기 풀기보다 먼저)
            _window = Window.GetWindow(this);
            if (_window != null)
                _window.PreviewKeyDown += OnWindowKey;
            StartDragCursor(hit, point);
            return true;
        }

        /// <summary>점(화면 픽셀)에 보이는 맨 위 클립(<see cref="ClipLayout.HitTest"/>) — 글자는 상자로.</summary>
        private Clip Pick(Point point)
        {
            var (zoom, left, top) = Screen();
            if (zoom <= 0)
                return null;
            return ClipLayout.HitTest(_session.Project, _session.Playhead, (point.X - left) / zoom, (point.Y - top) / zoom,
                c => TextSize(c, c.TextBoxWidth, c.TextBoxHeight));
        }

        private void PointerMove(Point point)
        {
            if (_drag == 0)
            {
                Cursor = CursorFor(Hit(point)) ?? GroupHoverCursor(point);
                return;
            }
            if (point != _dragFrom)
                _pointerMoved = true;
            if (_drag == PathDrag)
            {
                PathMove(point);
                return;
            }
            var (zoom, _, _) = Screen();
            if (_drag == 1)
            {
                // 화면 가운데·가장자리, 다른 클립의 가장자리·가운데 가까이 가면 붙는다(가로·세로 따로, Alt는 붙지 않음 — TransformOverlay.Snap.cs)
                SnapMove(_startX + (point.X - _dragFrom.X) / zoom, _startY + (point.Y - _dragFrom.Y) / zoom);
            }
            else if (_drag == 2)
            {
                var center = Frame().Center;
                var distance = Math.Max(1, (point - center).Length);
                _scale = Math.Max(0.05, Math.Min(4, _startScale * distance / _startDistance));
            }
            else if (_drag == RotateDrag)
            {
                RotateMove(point);
            }
            else if (_drag == 4)
            {
                // 끌기 시작 때의 값에서 끈 만큼(화면 → 프로젝트 픽셀)
                var start = _clip.CloneWithNewId();
                start.X = _startX;
                start.Y = _startY;
                start.Scale = _startScale;
                start.Rotation = _startRotation;
                start.Art = _startArt.Clone();
                var (_, left, top) = Screen();
                var dragged = ArtHandles.Drag(_session.Project, start, _shapeHandle,
                    ((_dragFrom.X - left) / zoom, (_dragFrom.Y - top) / zoom), ((point.X - left) / zoom, (point.Y - top) / zoom));
                _x = dragged.X;
                _y = dragged.Y;
                _rotation = dragged.Rotation;
                _art = dragged.Art;
            }
            else
            {
                // 반대쪽 옆은 그대로: 끈 거리를 클립 축에 투영해 상자 크기와 가운데를 함께 바꾼다
                var min = ClipLayout.MinTextBox(_clip);
                var moved = ClipLayout.DragEdge(_edge, _startX, _startY, _startMeasuredWidth, _startMeasuredHeight, _scale, _rotation,
                    (point.X - _dragFrom.X) / zoom, (point.Y - _dragFrom.Y) / zoom, min.Width, min.Height);
                _x = moved.X;
                _y = moved.Y;
                if (_edge == BoxEdge.Left || _edge == BoxEdge.Right)
                    _boxWidth = moved.Width;
                else
                    _boxHeight = moved.Height;
            }
            Layout();
            // 끄는 동안 커서는 지금 하는 일을 따른다(옆은 회전한 틀의 축, 모서리는 포인터가 틀 가운데를 도는 만큼 — TransformOverlay.Group.cs)
            Cursor = DragCursor(point);
            _commands.Status(_groupMoving ? GroupStatus() : _drag == 1 ? "위치 X " + Math.Round(_x) + " · Y " + Math.Round(_y) + " (놓으면 바뀝니다, Esc는 취소)"
                : _drag == 2 ? "크기 " + Math.Round(_scale * 100) + "% (놓으면 바뀝니다, Esc는 취소)"
                : _drag == 4 ? ShapeStatus() + " (놓으면 바뀝니다, Esc는 취소)"
                : _drag == RotateDrag ? RotateStatus()
                : (_edge == BoxEdge.Left || _edge == BoxEdge.Right ? "상자 너비 " + Math.Round(_boxWidth) : "상자 높이 " + Math.Round(_boxHeight)) + "px (놓으면 바뀝니다, Esc는 취소)");
        }

        /// <summary>모양 손잡이를 끄는 동안 상태 줄 글.</summary>
        private string ShapeStatus()
        {
            switch (_shapeHandle)
            {
                case ArtHandle.LineStart:
                case ArtHandle.LineEnd:
                    return "길이 " + Math.Round(_art.Width) + "px · 방향 " + Math.Round(_rotation) + "°";
                case ArtHandle.InnerRatio:
                    return "안쪽 " + Math.Round(_art.InnerRatio * 100) + "%";
                case ArtHandle.Tail:
                    return "꼬리 끝 X " + Math.Round(_art.TailX * _art.Width) + " · Y " + Math.Round(_art.TailY * _art.Height) + "px(상자 가운데에서)";
                default:
                    return "모서리 반경 " + Math.Round(_art.CornerRadius) + "px";
            }
        }

        /// <summary>놓음: 바뀌었으면 한 번 편집한다(실행 취소 한 번 — 바뀐 값을 모두 함께). 끄는 중이 아니었으면 false.</summary>
        private bool PointerUp()
        {
            if (_drag == 0)
                return false;
            if (_drag == 4)
                return ShapeUp();
            if (_drag == PathDrag)
                return PathUp();
            if (_drag == RotateDrag)
                return RotateUp();
            // 누르고 움직이지 않았으면 편집하지 않는다(자동 크기가 상한보다 커서 옆 손잡이를 누르기만 해도 값이 바뀌는 경우 등)
            var moved = _pointerMoved && (_x != _startX || _y != _startY || _scale != _startScale || _boxWidth != _startBoxWidth || _boxHeight != _startBoxHeight);
            var horizontal = _drag == 3 && (_edge == BoxEdge.Left || _edge == BoxEdge.Right);
            var vertical = _drag == 3 && !horizontal;
            var (id, x, y, scale, boxWidth, boxHeight) = (_clip.Id, _x, _y, _scale, _boxWidth, _boxHeight);
            // 여럿을 함께 끌던 중이면 모두 같은 거리만큼(편집 한 번)
            var group = TakeGroupMove(moved);
            EndDrag();
            if (group != null)
            {
                group();
            }
            else if (moved)
            {
                var (startX, startY, startScale) = (_startX, _startY, _startScale);
                _commands.EditClip(id, (p, c) =>
                {
                    // 키프레임이 있는 값은 재생 헤드 시각의 키프레임을 고치거나 만든다 — 바꾼 값만(TransformOverlay.Keyframes.cs)
                    SetPosition(c, Math.Round(x), Math.Round(y), x != startX || y != startY);
                    SetValue(c, KeyframeProperty.Scale, Math.Round(scale, 3), scale != startScale);
                    // 끈 방향의 상자 크기만(끈 쪽은 적어도 1px이라 반올림해도 자동(0)이 되지 않는다)
                    if (horizontal)
                        c.TextBoxWidth = ClipLayout.TextBoxLength(Math.Round(boxWidth));
                    if (vertical)
                        c.TextBoxHeight = ClipLayout.TextBoxLength(Math.Round(boxHeight));
                }, null);
            }
            return true;
        }

        /// <summary>
        /// 모양 손잡이를 놓음: 바뀐 값(위치·회전·모양)을 한 번에 편집한다(실행 취소 한 번). 위치는 정수로 반올림하지 않는다 —
        /// 선 끝을 끌 때 반대쪽 끝이 반올림만큼 밀리지 않게(소수 둘째 자리까지).
        /// </summary>
        private bool ShapeUp()
        {
            var handle = _shapeHandle;
            var (id, x, y, rotation, art) = (_clip.Id, _x, _y, _rotation, _art?.Clone());
            var (startX, startY, startRotation) = (_startX, _startY, _startRotation);
            var moved = _pointerMoved && art != null && _startArt != null
                && (x != _startX || y != _startY || rotation != _startRotation || art.Width != _startArt.Width || art.CornerRadius != _startArt.CornerRadius
                    || art.InnerRatio != _startArt.InnerRatio || art.TailX != _startArt.TailX || art.TailY != _startArt.TailY);
            EndDrag();
            if (!moved)
                return true;
            _commands.EditClip(id, (p, c) =>
            {
                if (c.Art == null)
                    return;
                SetPosition(c, Math.Round(x, 2), Math.Round(y, 2), x != startX || y != startY);
                SetValue(c, KeyframeProperty.Rotation, Math.Round(rotation, 2), rotation != startRotation);
                switch (handle)
                {
                    case ArtHandle.LineStart:
                    case ArtHandle.LineEnd:
                        c.Art.Width = Math.Round(art.Width, 2);
                        break;
                    case ArtHandle.InnerRatio:
                        c.Art.InnerRatio = Math.Round(art.InnerRatio, 3);
                        break;
                    case ArtHandle.Tail:
                        c.Art.TailX = Math.Round(art.TailX, 4);
                        c.Art.TailY = Math.Round(art.TailY, 4);
                        break;
                    default:
                        c.Art.CornerRadius = Math.Round(art.CornerRadius);
                        break;
                }
            }, null);
            return true;
        }

        /// <summary>두 번 누름: 글자의 옆 손잡이면 그 방향 상자 크기를 자동(0)으로(실행 취소 한 번). 옆 손잡이가 아니면 false.</summary>
        private bool PointerDoubleClick(Point point)
        {
            // 사용자 경로의 점은 빼고 점선에는 점을 끼운다(TransformOverlay.Path.cs)
            if (PathDoubleClick(point))
                return true;
            if (!HasBoxHandles || _drag != 0 || _playing())
                return false;
            var hit = Hit(point);
            if (hit < FirstEdge)
                return false;
            var horizontal = (BoxEdge)(hit - FirstEdge) == BoxEdge.Left || (BoxEdge)(hit - FirstEdge) == BoxEdge.Right;
            var id = _clip.Id;
            if (horizontal ? _clip.TextBoxWidth > 0 : _clip.TextBoxHeight > 0)
            {
                _commands.EditClip(id, (p, c) =>
                {
                    if (horizontal)
                        c.TextBoxWidth = 0;
                    else
                        c.TextBoxHeight = 0;
                }, null);
            }
            _commands.Status(horizontal ? "상자 너비를 자동으로 했습니다(줄을 나누지 않음)." : "상자 높이를 자동으로 했습니다(글 높이).");
            return true;
        }

        /// <summary>
        /// 화살표로 옮기기(미리 보기에 포커스가 있을 때 편집 창이 부른다): 고른 클립을 (dx, dy) 방향으로 1 프로젝트 픽셀(large면 10).
        /// repeat(키를 누르고 있음)면 앞 편집과 합친다 — 누르고 있는 동안은 실행 취소 한 단계, 새로 누르면 새 단계. 틀이 없거나 끄는 중이면 false(화살표는 원래 동작).
        /// </summary>
        public bool Nudge(int dx, int dy, bool large, bool repeat)
        {
            // 여럿 골랐으면 모두 함께
            if (_clip == null && _drag == 0 && !_playing() && GroupNudge(dx, dy, large, repeat))
                return true;
            if (_clip == null || _drag != 0 || _playing())
                return false;
            var id = _clip.Id;
            var step = large ? 10 : 1;
            if (!repeat || _nudgeKey == null)
                _nudgeKey = "nudge:" + id + ":" + ++_nudges;
            _commands.EditClip(id, (p, c) =>
            {
                var at = Pose(c);
                SetPosition(c, Math.Round(at.X) + dx * step, Math.Round(at.Y) + dy * step, true);
            }, _nudgeKey);
            var clip = _session.Project.FindClip(id);
            if (clip != null)
                _commands.Status("위치 X " + Math.Round(Pose(clip).X) + " · Y " + Math.Round(Pose(clip).Y) + " (화살표 1px, Shift+화살표 10px)");
            return true;
        }

        /// <summary>모서리 손잡이 자리(화면 픽셀, 왼쪽 위부터 시계 방향) — 시험용.</summary>
        internal Point[] HandlePoints()
        {
            return _clip == null ? new Point[0] : Corners(Frame());
        }

        /// <summary>옆 손잡이 자리(화면 픽셀, 위·오른쪽·아래·왼쪽 — 글자만) — 시험용.</summary>
        internal Point[] EdgePoints()
        {
            return !HasBoxHandles ? new Point[0] : Edges(Corners(Frame()));
        }

        /// <summary>모양 손잡이 자리(화면 픽셀)와 종류 이름(<see cref="ArtHandle"/>) — 시험용.</summary>
        internal (Point[] Points, string[] Kinds) ShapePoints()
        {
            return _clip == null ? (new Point[0], new string[0]) : (ShapeHandlePoints(), _shapeKinds.Select(k => k.ToString()).ToArray());
        }

        /// <summary>프로젝트 픽셀 → 이 틀(화면) 픽셀 — 시험용.</summary>
        internal Point ToScreen(double x, double y)
        {
            var (zoom, left, top) = Screen();
            return new Point(left + x * zoom, top + y * zoom);
        }

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _drag != 0)
            {
                var drag = _drag;
                Cancel();
                _commands.Status(drag == 4 ? "모양 바꾸기를 취소했습니다." : drag == PathDrag ? "경로 점 옮기기를 취소했습니다." : drag == RotateDrag ? "회전을 취소했습니다." : "옮기기·크기 바꾸기를 취소했습니다.");
                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_drag != 0)
                Cancel();
        }

        /// <summary>끌기 취소: 시작 때 값으로 되돌리고 끝낸다(편집 없음).</summary>
        private void Cancel()
        {
            _x = _startX;
            _y = _startY;
            _scale = _startScale;
            _boxWidth = _startBoxWidth;
            _boxHeight = _startBoxHeight;
            _rotation = _startRotation;
            _art = _startArt?.Clone() ?? _art;
            EndDrag();
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

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
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
