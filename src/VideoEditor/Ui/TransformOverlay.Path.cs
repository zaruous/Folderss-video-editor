using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 미리 보기 틀 — 이동 경로 고치기(#4). 고른 클립에 이동 경로가 있으면 경로를 점선으로, 점을 둥근 점으로 그린다(첫 점은 클립 자리 — 속이 빈 점, 끌지 않음).
    /// 점을 끌면 그 점을 옮기고(놓을 때 편집 한 번 — 실행 취소 한 단계, Esc는 취소), 사용자 경로는 Shift+누르기로 끝에 점을 더하고,
    /// 점선을 두 번 누르면 그 자리에 점을 끼우고, 점을 두 번 누르면 뺀다. 경로는 클립 자리 기준이라 클립을 끌어 옮기는 동안 함께 움직인다.
    /// </summary>
    internal sealed partial class TransformOverlay
    {
        // Hit 번호: 경로 점 = FirstPath + 점 번호(1~), 사용자 경로의 점선 위 = PathLine
        private const int FirstPath = 100;
        private const int PathLine = FirstPath - 1;
        // 끄는 중(_drag)의 경로 점 끌기
        private const int PathDrag = 5;
        // 점선을 잡는 거리(화면 픽셀)
        private const double PathLineReach = 6;
        // 점선에 쓰는 표본 수(곡선 경로)
        private const int PathSamples = 96;
        private static readonly Brush PathHandleFill = Frozen(Color.FromRgb(0xE0, 0xF2, 0xFE));
        private Polyline _pathLine;
        private Ellipse _pathStart;
        private readonly List<Ellipse> _pathHandles = new List<Ellipse>();
        // 보이는 경로(고른 클립의 경로 사본 — 끄는 동안 바뀜)와 끌기 시작 때 점, 끄는 점 번호
        private ClipAnimation _path;
        private List<PathPoint> _startPathPoints;
        private int _pathIndex;

        /// <summary>경로를 보이는지(시험용).</summary>
        internal bool HasPath { get { return _path != null && _pathLine != null && _pathLine.Visibility == Visibility.Visible; } }

        /// <summary>경로 점 자리(화면 픽셀, 0번은 클립 자리) — 시험용. 경로가 없으면 빈 배열.</summary>
        internal Point[] PathPoints()
        {
            return HasPath ? _path.Points.Select(PathToScreen).ToArray() : new Point[0];
        }

        /// <summary>경로 시작(클립 자리 = 클립 가운데)의 화면 자리 — 끄는 동안의 위치(_x·_y)를 따른다.</summary>
        private Point PathOrigin()
        {
            var project = _session.Project;
            return ToScreen(project.Width / 2.0 + _x, project.Height / 2.0 + _y);
        }

        private Point PathToScreen(PathPoint point)
        {
            var (zoom, _, _) = Screen();
            var origin = PathOrigin();
            var start = _path.Points[0];
            return new Point(origin.X + (point.X - start.X) * zoom, origin.Y + (point.Y - start.Y) * zoom);
        }

        /// <summary>화면 점 → 경로 점(시작점 기준, 프로젝트 픽셀).</summary>
        private (double X, double Y) ScreenToPath(Point point)
        {
            var (zoom, _, _) = Screen();
            var origin = PathOrigin();
            return ((point.X - origin.X) / Math.Max(1e-9, zoom), (point.Y - origin.Y) / Math.Max(1e-9, zoom));
        }

        /// <summary>경로를 다시 그린다(<see cref="Layout"/>·<see cref="Show"/>가 부른다). 끄는 중이 아니면 고른 클립의 경로를 다시 읽는다.</summary>
        private void LayoutPath()
        {
            if (_drag != PathDrag)
                _path = _clip == null || _playing() ? null : ClipAnimations.PathOf(_clip)?.Clone();
            if (_path == null || _path.Points == null || _path.Points.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0)
            {
                if (_pathLine != null)
                    _pathLine.Visibility = _pathStart.Visibility = Visibility.Collapsed;
                foreach (var handle in _pathHandles)
                    handle.Visibility = Visibility.Collapsed;
                return;
            }
            if (_pathLine == null)
            {
                // 처음 쓸 때 만든다(맨 위 — 경로 점은 크기 손잡이보다 먼저 잡힌다)
                _pathLine = new Polyline { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 3 }, IsHitTestVisible = false };
                _pathLine.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                _pathStart = new Ellipse { Width = Handle - 2, Height = Handle - 2, StrokeThickness = 1.5, Fill = Brushes.Transparent, IsHitTestVisible = false };
                _pathStart.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                Children.Add(_pathLine);
                Children.Add(_pathStart);
            }
            var (zoom, _, _) = Screen();
            var origin = PathOrigin();
            var line = new PointCollection();
            foreach (var (x, y) in MotionPaths.Samples(_path, PathSamples))
                line.Add(new Point(origin.X + x * zoom, origin.Y + y * zoom));
            _pathLine.Points = line;
            _pathLine.Visibility = Visibility.Visible;
            _pathStart.Visibility = Visibility.Visible;
            SetLeft(_pathStart, origin.X - _pathStart.Width / 2);
            SetTop(_pathStart, origin.Y - _pathStart.Height / 2);
            while (_pathHandles.Count < _path.Points.Count - 1)
            {
                var handle = new Ellipse { Width = Handle, Height = Handle, Fill = PathHandleFill, StrokeThickness = 1.5, IsHitTestVisible = false };
                handle.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                _pathHandles.Add(handle);
                Children.Add(handle);
            }
            for (var i = 0; i < _pathHandles.Count; i++)
            {
                var handle = _pathHandles[i];
                if (i + 1 >= _path.Points.Count)
                {
                    handle.Visibility = Visibility.Collapsed;
                    continue;
                }
                var at = PathToScreen(_path.Points[i + 1]);
                handle.Visibility = Visibility.Visible;
                SetLeft(handle, at.X - Handle / 2);
                SetTop(handle, at.Y - Handle / 2);
            }
        }

        /// <summary>
        /// 점이 경로 위인지: 끌 수 있는 점(1번부터)이면 <see cref="FirstPath"/> + 번호, 사용자 경로의 점선 위면 <see cref="PathLine"/>, 아니면 -1.
        /// </summary>
        private int PathHit(Point point)
        {
            if (!HasPath)
                return -1;
            var best = -1;
            var nearest = double.MaxValue;
            for (var i = 1; i < _path.Points.Count; i++)
            {
                var distance = (point - PathToScreen(_path.Points[i])).Length;
                if (distance <= Handle / 2 + 2 && distance < nearest)
                {
                    best = FirstPath + i;
                    nearest = distance;
                }
            }
            if (best >= 0)
                return best;
            if (MotionPaths.CanAddPoints(_path.Effect))
            {
                var (zoom, _, _) = Screen();
                var (x, y) = ScreenToPath(point);
                if (MotionPaths.Nearest(Relative(_path.Points), x, y).Distance * zoom <= PathLineReach)
                    return PathLine;
            }
            return -1;
        }

        /// <summary>점들을 첫 점 기준으로(첫 점이 (0,0)이 아니어도 화면과 맞게).</summary>
        private static List<PathPoint> Relative(IReadOnlyList<PathPoint> points)
        {
            var start = points[0];
            return points.Select(p => new PathPoint(p.X - start.X, p.Y - start.Y)).ToList();
        }

        /// <summary>점 끌기를 시작했다(<see cref="PointerDown"/>).</summary>
        private void StartPathDrag(int index)
        {
            _pathIndex = index;
            _startPathPoints = _path.Points.Select(p => p.Clone()).ToList();
        }

        /// <summary>끄는 동안: 끌기 시작 때 점에서 끈 만큼(정수 픽셀). 틀은 그대로이고 경로만 다시 그린다.</summary>
        private void PathMove(Point point)
        {
            var (zoom, _, _) = Screen();
            var start = _startPathPoints[_pathIndex];
            var moved = new PathPoint(
                Math.Max(-MotionPaths.MaxOffset, Math.Min(MotionPaths.MaxOffset, Math.Round(start.X + (point.X - _dragFrom.X) / zoom))),
                Math.Max(-MotionPaths.MaxOffset, Math.Min(MotionPaths.MaxOffset, Math.Round(start.Y + (point.Y - _dragFrom.Y) / zoom))));
            _path.Points = _startPathPoints.Select(p => p.Clone()).ToList();
            _path.Points[_pathIndex] = moved;
            LayoutPath();
            Cursor = Cursors.Cross;
            var origin = _path.Points[0];
            _commands.Status("경로 점 " + _pathIndex + ": 가로 " + Signed(moved.X - origin.X) + " · 세로 " + Signed(moved.Y - origin.Y) + "px(클립 자리에서) (놓으면 바뀝니다, Esc는 취소)");
        }

        /// <summary>점 끌기를 놓음: 바뀌었으면 경로 점을 한 번에 편집한다(실행 취소 한 단계).</summary>
        private bool PathUp()
        {
            var points = _path?.Points.Select(p => p.Clone()).ToList();
            var moved = _pointerMoved && points != null && _startPathPoints != null
                && points.Zip(_startPathPoints, (a, b) => a.X != b.X || a.Y != b.Y).Any(changed => changed);
            var effect = _path?.Effect;
            var id = _clip.Id;
            EndDrag();
            if (moved)
                EditPath(id, effect.Value, points);
            return true;
        }

        /// <summary>경로 점을 바꾸는 편집(클립의 경로가 그 효과일 때만 — 실행 취소 한 단계).</summary>
        private void EditPath(string id, AnimationEffect effect, List<PathPoint> points)
        {
            _commands.EditClip(id, (p, c) =>
            {
                var path = ClipAnimations.PathOf(c);
                if (path != null && path.Effect == effect)
                    path.Points = MotionPaths.Clean(effect, points);
            }, null);
        }

        /// <summary>
        /// Shift+누르기: 사용자 경로면 누른 자리를 끝 점으로 더한다(실행 취소 한 단계). 사용자 경로가 아니거나 재생·끌기 중이면 false(보통 누르기).
        /// </summary>
        private bool PathAppend(Point point)
        {
            if (!HasPath || _drag != 0 || _playing() || !MotionPaths.CanAddPoints(_path.Effect))
                return false;
            if (_path.Points.Count >= MotionPaths.MaxPoints)
            {
                _commands.Status("사용자 경로의 점은 " + MotionPaths.MaxPoints + "개까지입니다.");
                return true;
            }
            var (x, y) = ScreenToPath(point);
            var points = Relative(_path.Points);
            points.Add(new PathPoint(Math.Round(x), Math.Round(y)));
            EditPath(_clip.Id, _path.Effect, points);
            _commands.Status("경로 끝에 점을 더했습니다(" + points.Count + "개). 점을 두 번 누르면 뺍니다.");
            return true;
        }

        /// <summary>
        /// 두 번 누름(사용자 경로): 점이면 빼고(시작점·두 점 남았을 때는 빼지 않음), 점선 위면 그 자리에 점을 끼운다(실행 취소 한 단계). 경로 위가 아니면 false.
        /// </summary>
        private bool PathDoubleClick(Point point)
        {
            if (!HasPath || _drag != 0 || _playing() || !MotionPaths.CanAddPoints(_path.Effect))
                return false;
            var hit = PathHit(point);
            if (hit < 0)
                return false;
            var points = Relative(_path.Points);
            if (hit >= FirstPath)
            {
                if (points.Count <= 2)
                {
                    _commands.Status("경로에는 점이 둘 이상 있어야 합니다.");
                    return true;
                }
                points.RemoveAt(hit - FirstPath);
                EditPath(_clip.Id, _path.Effect, points);
                _commands.Status("경로 점을 뺐습니다(" + points.Count + "개).");
                return true;
            }
            if (points.Count >= MotionPaths.MaxPoints)
            {
                _commands.Status("사용자 경로의 점은 " + MotionPaths.MaxPoints + "개까지입니다.");
                return true;
            }
            var (x, y) = ScreenToPath(point);
            var (segment, footX, footY, _) = MotionPaths.Nearest(points, x, y);
            points.Insert(segment + 1, new PathPoint(Math.Round(footX), Math.Round(footY)));
            EditPath(_clip.Id, _path.Effect, points);
            _commands.Status("경로에 점을 끼웠습니다(" + points.Count + "개).");
            return true;
        }
    }
}
