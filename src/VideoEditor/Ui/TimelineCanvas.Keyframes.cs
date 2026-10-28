using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Core;

namespace VideoEditor.Ui
{
    /// <summary>
    /// 타임라인의 사용자 키프레임 표시(#4): 고른 클립 위에 키프레임 시각마다 ◆(값을 가리지 않고 시각 하나에 하나, 클립 안의 것만).
    /// ◆를 끌면 그 시각의 키프레임을 모두 옮긴다(놓을 때 실행 취소 한 단계, 클립 안으로). 마우스를 올리면 그 시각과 값 이름을 알린다.
    /// </summary>
    internal sealed partial class TimelineCanvas
    {
        private static readonly Brush KeyframeFill = Frozen(Color.FromRgb(0xFA, 0xCC, 0x15));
        private static readonly Pen KeyframePen = FrozenPen(Frozen(Color.FromRgb(0x1F, 0x29, 0x37)), 1);
        private const double KeyframeSize = 5;

        // 끄는 ◆의 원래 시각과 지금 시각(클립 안 프레임)
        private long _keyframeFrom;
        private long _keyframeTo;

        /// <summary>◆의 세로 자리: 클립 아래쪽 애니메이션 띠 바로 위(낮은 트랙은 가운데).</summary>
        private static double KeyframeY(Rect rect)
        {
            return rect.Height >= 30 ? rect.Bottom - AnimationBandHeight - 8 : rect.Top + rect.Height / 2;
        }

        /// <summary>그릴 ◆의 시각(클립 안, 끄는 중이면 옮겨 그림).</summary>
        private List<long> KeyframeMarks(Clip clip)
        {
            var frames = ClipKeyframes.Frames(clip).Where(f => f >= 0 && f < clip.Duration).ToList();
            if (_drag == DragKind.Keyframe && clip.Id == _dragClipId && _dragStarted)
            {
                frames.Remove(_keyframeFrom);
                if (!frames.Contains(_keyframeTo))
                    frames.Add(_keyframeTo);
            }
            return frames;
        }

        /// <summary>고른 클립에 ◆를 그린다(DrawClip 끝에서).</summary>
        private void DrawKeyframeMarks(DrawingContext dc, Clip clip, Rect rect)
        {
            if (clip.Keyframes == null || !_session.IsSelected(clip) || rect.Height < 12)
                return;
            var y = KeyframeY(rect);
            dc.PushClip(new RectangleGeometry(new Rect(rect.Left - KeyframeSize, rect.Top, rect.Width + KeyframeSize * 2, rect.Height)));
            foreach (var frame in KeyframeMarks(clip))
            {
                var x = FrameToX(clip.Start + frame);
                var diamond = new StreamGeometry();
                using (var context = diamond.Open())
                {
                    context.BeginFigure(new Point(x, y - KeyframeSize), true, true);
                    context.LineTo(new Point(x + KeyframeSize, y), true, false);
                    context.LineTo(new Point(x, y + KeyframeSize), true, false);
                    context.LineTo(new Point(x - KeyframeSize, y), true, false);
                }
                diamond.Freeze();
                dc.DrawGeometry(KeyframeFill, KeyframePen, diamond);
            }
            dc.Pop();
        }

        /// <summary>점이 고른 클립의 ◆ 위인지(가까운 것, 좌우·위아래 ◆ 크기 + 2px). 그 클립과 시각.</summary>
        private Clip HitKeyframe(Point point, out TimelineRow row, out long frame)
        {
            frame = 0;
            row = RowAt(point.Y);
            if (row == null)
                return null;
            Clip best = null;
            var nearest = double.MaxValue;
            foreach (var clip in row.Track.Clips.Where(c => c.Keyframes != null && _session.IsSelected(c)))
            {
                var rect = ClipRect(row, clip.Start, clip.End);
                if (rect.Height < 12 || Math.Abs(point.Y - KeyframeY(rect)) > KeyframeSize + 2)
                    continue;
                foreach (var at in KeyframeMarks(clip))
                {
                    var distance = Math.Abs(point.X - FrameToX(clip.Start + at));
                    if (distance <= KeyframeSize + 2 && distance < nearest)
                    {
                        best = clip;
                        frame = at;
                        nearest = distance;
                    }
                }
            }
            return best;
        }

        /// <summary>◆ 위면 커서·도움말을 바꾸고 true.</summary>
        private bool KeyframeHover(Point point)
        {
            var clip = HitKeyframe(point, out var row, out var frame);
            if (clip == null)
                return false;
            Cursor = row.Track.Locked ? Cursors.No : Cursors.SizeWE;
            var names = string.Join("·", clip.Keyframes.Where(k => k.Frame == frame).Select(k => ClipKeyframes.Name(k.Property)).Distinct());
            var tip = "키프레임 " + _session.Project.Rate.Timecode(clip.Start + frame) + " · " + names + " — 끌어 옮깁니다";
            if (!Equals(ToolTip, tip))
                ToolTip = tip;
            return true;
        }

        /// <summary>◆를 눌렀으면 재생 헤드를 그 시각으로 옮기고 끌기를 시작해 true.</summary>
        private bool KeyframePointerDown(Point point)
        {
            var clip = HitKeyframe(point, out var row, out var frame);
            if (clip == null)
                return false;
            _session.Playhead = clip.Start + frame;
            if (row.Track.Locked)
            {
                StatusMessage?.Invoke("'" + row.Track.Name + "' 트랙이 잠겨 있습니다.");
                return true;
            }
            _drag = DragKind.Keyframe;
            _dragStarted = false;
            _dragClipId = clip.Id;
            _keyframeFrom = _keyframeTo = frame;
            return true;
        }

        /// <summary>끄는 동안: ◆ 자리 = 포인터의 클립 안 시각(클립 안으로).</summary>
        private void KeyframePointerMove(Point point)
        {
            if (!_dragStarted)
            {
                if (Math.Abs(point.X - _downPoint.X) < DragThreshold)
                    return;
                _dragStarted = true;
            }
            var clip = _session.Project.FindClip(_dragClipId);
            if (clip == null)
            {
                CancelDrag();
                return;
            }
            _keyframeTo = Math.Max(0, Math.Min(clip.Duration - 1, XToFrame(point.X) - clip.Start));
            StatusMessage?.Invoke("키프레임 " + _session.Project.Rate.Timecode(clip.Start + _keyframeFrom) + " → " + _session.Project.Rate.Timecode(clip.Start + _keyframeTo));
            InvalidateVisual();
        }

        /// <summary>놓으면 그 시각의 키프레임들을 옮기고(실행 취소 한 단계) 재생 헤드도 따라간다.</summary>
        private void KeyframePointerUp()
        {
            var (clipId, from, to, started) = (_dragClipId, _keyframeFrom, _keyframeTo, _dragStarted);
            CancelDrag();
            if (!started || from == to)
                return;
            var ok = _session.Execute(p =>
            {
                var clip = p.FindClip(clipId);
                if (clip == null)
                    return;
                TimelineEdits.EnsureEditable(p.TrackOf(clip));
                ClipKeyframes.MoveFrame(clip, from, to);
            });
            var moved = _session.Project.FindClip(clipId);
            if (ok && moved != null)
            {
                _session.Playhead = moved.Start + to;
                StatusMessage?.Invoke("키프레임을 옮겼습니다: " + _session.Project.Rate.Timecode(moved.Start + from) + " → " + _session.Project.Rate.Timecode(moved.Start + to));
            }
            else if (_session.LastError != null)
            {
                StatusMessage?.Invoke(_session.LastError);
            }
        }

        /// <summary>고른 클립의 ◆ 자리(타임라인 픽셀, 클립 안 시각 차례) — 시험용.</summary>
        internal Point[] KeyframePoints(string clipId)
        {
            var clip = _session.Project.FindClip(clipId);
            var row = clip == null ? null : Rows().FirstOrDefault(r => r.Track.Clips.Contains(clip));
            if (row == null || clip.Keyframes == null)
                return new Point[0];
            var rect = ClipRect(row, clip.Start, clip.End);
            return KeyframeMarks(clip).OrderBy(f => f).Select(f => new Point(FrameToX(clip.Start + f), KeyframeY(rect))).ToArray();
        }

        /// <summary>화면 읽기·시험용 이름 덧붙임: " · 키프레임 3"(클립 안 시각 수).</summary>
        internal static string KeyframeLabel(Clip clip)
        {
            var count = ClipKeyframes.Frames(clip).Count(f => f >= 0 && f < clip.Duration);
            return count == 0 ? "" : " · 키프레임 " + count;
        }
    }
}
