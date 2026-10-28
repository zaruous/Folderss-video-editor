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
    /// 미리 보기 틀 — 여러 클립 고르기·함께 옮기기와 끄는 동안의 커서(#34).
    /// Ctrl+누르기는 그 자리에 보이는 맨 위 클립을 고르기에 더하거나 뺀다(타임라인의 Ctrl+누르기와 같음).
    /// 둘 이상 고르면 고른 클립마다 점선 틀만 그리고(손잡이 없음), 그 가운데 하나의 안쪽을 끌면 모두 같은 거리만큼 옮긴다 — 놓을 때 편집 한 번(실행 취소 한 단계).
    /// 화면 가운데 붙기는 누른 클립을 기준으로 하고, 화살표도 모두 함께 옮긴다.
    /// 끄는 동안 커서는 지금 하는 일을 따른다: 옮기기는 ✥, 옆(상자)은 클립의 회전한 축, 모서리(크기)는 그 모서리 커서에서 포인터가 틀 가운데를 도는 만큼.
    /// </summary>
    internal sealed partial class TransformOverlay
    {
        // 고른 클립이 둘 이상일 때 보이는 점선 틀(클립마다 하나 — 모자라면 더 만들고 남으면 숨김)
        private readonly List<Rectangle> _groupFrames = new List<Rectangle>();
        // 함께 옮길 클립(렌더 그래프의 쌓는 순서). 둘 이상 고르지 않았으면 비었다
        private List<Clip> _group = new List<Clip>();
        // 무리의 한 클립을 누른 채 끄는 중(_clip = 누른 클립, _drag = 1)
        private bool _groupMoving;

        /// <summary>함께 옮길 클립 수(시험용) — 둘 이상 골랐을 때만 0보다 크다.</summary>
        internal int GroupCount { get { return _group.Count; } }

        /// <summary>
        /// 무리 틀을 다시 정한다(<see cref="Refresh"/>가 끝에 부른다 — 끄는 중이 아닐 때). 틀 하나(_clip)를 보이는 중이면 무리는 없다.
        /// 고른 클립이 둘 이상이면(링크된 짝 하나는 틀 하나) 그 가운데 틀을 그릴 수 있는 것마다 점선 틀.
        /// </summary>
        private void RefreshGroup()
        {
            _groupMoving = false;
            var selected = _session.SelectedClips;
            _group = _clip == null && !_playing() && selected.Count > 1
                ? PreviewSelection.Movable(_session.Project, selected, _session.Playhead)
                : new List<Clip>();
            LayoutGroup();
        }

        /// <summary>무리 틀을 놓는다 — 끄는 중이면 누른 클립이 움직인 만큼 모두 옮겨 그린다.</summary>
        private void LayoutGroup()
        {
            while (_groupFrames.Count < _group.Count)
            {
                var frame = new Rectangle { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
                frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
                _groupFrames.Add(frame);
                Children.Add(frame);
            }
            var (dx, dy) = _groupMoving ? (_x - _startX, _y - _startY) : (0.0, 0.0);
            for (var i = 0; i < _groupFrames.Count; i++)
            {
                var shape = _groupFrames[i];
                if (i >= _group.Count || ActualWidth <= 0 || ActualHeight <= 0)
                {
                    shape.Visibility = Visibility.Collapsed;
                    continue;
                }
                var (center, width, height, rotation) = GroupFrame(_group[i], dx, dy);
                shape.Visibility = Visibility.Visible;
                shape.Width = Math.Max(1, width);
                shape.Height = Math.Max(1, height);
                SetLeft(shape, center.X - width / 2);
                SetTop(shape, center.Y - height / 2);
                shape.RenderTransform = new RotateTransform(rotation, width / 2, height / 2);
            }
        }

        /// <summary>클립의 보이는 영역(글자는 상자, 아트는 모양 상자)을 (dx, dy)만큼 옮긴 틀: 가운데·크기(화면 픽셀)와 회전 — <see cref="ClipLayout.HitTest"/>와 같은 규칙.</summary>
        private (Point Center, double Width, double Height, double Rotation) GroupFrame(Clip clip, double dx, double dy)
        {
            var project = _session.Project;
            var probe = Pose(clip).CloneWithNewId();
            probe.X += dx;
            probe.Y += dy;
            var content = ClipLayout.Content(project, probe);
            if (clip.Kind == ClipKind.Art && clip.Art != null)
            {
                content = ClipLayout.TextBox(project, probe, clip.Art.Width, clip.Art.Height);
            }
            else if (clip.Kind == ClipKind.Text)
            {
                var box = TextSize(clip, clip.TextBoxWidth, clip.TextBoxHeight);
                content = ClipLayout.TextBox(project, probe, box.Width, box.Height);
            }
            var (zoom, left, top) = Screen();
            return (new Point(left + content.CenterX * zoom, top + content.CenterY * zoom), content.Width * zoom, content.Height * zoom, content.Rotation);
        }

        /// <summary>
        /// 누른 자리가 무리의 한 클립이면(그 자리에 보이는 맨 위 클립) 그것을 앞세워 함께 옮기기를 시작할 준비를 한다 — 고르기는 그대로.
        /// 무리가 없거나 무리 밖이면 false(보통 누르기 — 그 클립 하나만 고른다).
        /// </summary>
        private bool GroupLead(Point point)
        {
            if (_group.Count == 0 || _clip != null)
                return false;
            var picked = Pick(point);
            if (picked == null || !_group.Any(c => c.Id == picked.Id))
                return false;
            _clip = picked;
            var pose = Pose(picked);
            _x = pose.X;
            _y = pose.Y;
            _scale = pose.Scale;
            _rotation = pose.Rotation;
            _boxWidth = picked.TextBoxWidth;
            _boxHeight = picked.TextBoxHeight;
            _groupMoving = true;
            return true;
        }

        /// <summary>
        /// 놓음(무리를 끌던 중이면): 움직인 거리(누른 클립 기준 — 가운데 붙기 포함)를 잡아 두고 무리 끌기를 끝낸다. 돌려준 동작이 모두를 한 번에 옮긴다(실행 취소 한 단계).
        /// 무리를 끌던 중이 아니면 null(틀 하나 놓기 그대로).
        /// </summary>
        private Action TakeGroupMove(bool moved)
        {
            if (!_groupMoving)
                return null;
            _groupMoving = false;
            var ids = _group.Select(c => c.Id).ToList();
            var (dx, dy) = (Math.Round(_x - _startX), Math.Round(_y - _startY));
            // 위치 키프레임이 있는 클립은 재생 헤드 시각의 키프레임을 고친다
            var playhead = _session.Playhead;
            if (!moved || dx == 0 && dy == 0)
                return () => { };
            return () =>
            {
                if (_commands.EditClips(ids, (p, clips) => PreviewSelection.Move(p, clips, dx, dy, playhead), null))
                    _commands.Status(ids.Count + "개 클립을 함께 옮겼습니다(가로 " + Signed(dx) + " · 세로 " + Signed(dy) + "px).");
            };
        }

        /// <summary>끄는 동안 상태 줄: 함께 옮기는 클립 수와 거리.</summary>
        private string GroupStatus()
        {
            return _group.Count + "개 클립 함께 옮기기: 가로 " + Signed(Math.Round(_x - _startX)) + " · 세로 " + Signed(Math.Round(_y - _startY)) + "px (놓으면 바뀝니다, Esc는 취소)";
        }

        private static string Signed(double value)
        {
            return (value > 0 ? "+" : value < 0 ? "−" : "") + Math.Abs(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Ctrl+누르기: 그 자리에 보이는 맨 위 클립을 고르기에 더하거나 뺀다(링크된 짝도 함께). 클립이 없거나 재생·끌기 중이면 false(아무 일도 없음).
        /// </summary>
        private bool PointerToggle(Point point)
        {
            if (_playing() || _drag != 0)
                return false;
            var picked = Pick(point);
            if (picked == null)
                return false;
            var adding = !_session.IsSelected(picked);
            _session.ToggleSelect(picked);
            Refresh();
            var count = _group.Count > 0 ? _group.Count : _clip != null ? 1 : 0;
            _commands.Status((adding ? "고르기에 더했습니다: " : "고르기에서 뺐습니다: ") + picked.Name + " — 미리 보기에서 고른 클립 " + count + "개"
                             + (_group.Count > 1 ? "(안쪽을 끌거나 화살표로 함께 옮김)" : ""));
            return true;
        }

        /// <summary>화살표로 무리를 함께 옮긴다(<see cref="Nudge"/>와 같은 규칙 — 누르고 있는 동안은 실행 취소 한 단계). 무리가 없으면 false.</summary>
        private bool GroupNudge(int dx, int dy, bool large, bool repeat)
        {
            if (_group.Count == 0)
                return false;
            var ids = _group.Select(c => c.Id).ToList();
            var step = large ? 10 : 1;
            if (!repeat || _nudgeKey == null)
                _nudgeKey = "nudge:group:" + ++_nudges;
            if (_commands.EditClips(ids, (p, clips) => PreviewSelection.Move(p, clips, dx * step, dy * step, _session.Playhead), _nudgeKey))
                _commands.Status(ids.Count + "개 클립을 함께 옮겼습니다(화살표 1px, Shift+화살표 10px).");
            return true;
        }

        /// <summary>끌지 않을 때 포인터가 무리 틀 안이면 옮기기 커서(아니면 null).</summary>
        private Cursor GroupHoverCursor(Point point)
        {
            if (_group.Count == 0)
                return null;
            var picked = Pick(point);
            return picked != null && _group.Any(c => c.Id == picked.Id) ? Cursors.SizeAll : null;
        }

        /// <summary>끌기를 시작했다(<see cref="PointerDown"/> 끝): 누른 손잡이를 기억하고 커서를 그 일에 맞춘다(눌러서 고른 클립을 끌 때도 ✥).</summary>
        private void StartDragCursor(int hit, Point point)
        {
            _dragHandle = hit;
            Cursor = DragCursor(point);
        }

        // 누른 손잡이(0~3 모서리, 4 안, 5~8 옆 — Hit의 번호)
        private int _dragHandle;

        /// <summary>
        /// 끄는 동안의 커서(45° 단위, 회전한 틀을 따름): 옮기기는 ✥, 옆(상자)은 클립의 회전한 축, 모서리(크기 — 틀 가운데에서 멀어지는 만큼 커짐)는
        /// 누를 때 그 모서리의 커서이고 포인터가 틀 가운데를 도는 만큼 함께 돈다. 모르는 끌기는 지금 커서 그대로.
        /// </summary>
        private Cursor DragCursor(Point point)
        {
            var rotation = _clip != null && double.IsFinite(_rotation) ? _rotation : 0;
            switch (_drag)
            {
                case 1:
                    return Cursors.SizeAll;
                case 2:
                    var center = Frame().Center;
                    var now = point - center;
                    var then = _dragFrom - center;
                    var turn = now.Length < 1 || then.Length < 1 ? 0 : (Math.Atan2(now.Y, now.X) - Math.Atan2(then.Y, then.X)) * 180 / Math.PI;
                    return CursorAt(HandleAngles[Math.Max(0, Math.Min(3, _dragHandle))] + rotation + turn);
                case 3:
                    return CursorAt(HandleAngles[FirstEdge + (int)_edge] + rotation);
                default:
                    return Cursor;
            }
        }

        /// <summary>방향(도, 0 = 오른쪽, 시계 방향)의 크기 커서: 45° 단위로 가로 ↔, 대각선 ⤡, 세로 ↕, 대각선 ⤢.</summary>
        private static Cursor CursorAt(double angle)
        {
            var step = (int)Math.Round(((angle % 180) + 180) % 180 / 45) % 4;
            return step == 0 ? Cursors.SizeWE : step == 1 ? Cursors.SizeNWSE : step == 2 ? Cursors.SizeNS : Cursors.SizeNESW;
        }
    }
}
