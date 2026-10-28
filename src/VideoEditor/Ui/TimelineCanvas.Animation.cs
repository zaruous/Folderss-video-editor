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
    /// 타임라인의 애니메이션 띠(#4): 클립 아래 가장자리 안에 나타나기(초록, 클립 앞쪽)·사라지기(주황, 클립 뒤쪽) 구간을 띠로 그리고(위쪽은 클립 이름),
    /// 띠 안쪽 끝의 흰 손잡이를 끌면 길이를 바꾼다(놓을 때 실행 취소 한 단계). 강조가 있으면 띠 위에 점선.
    /// </summary>
    internal sealed partial class TimelineCanvas
    {
        private static readonly Brush AnimationInBrush = Frozen(Color.FromArgb(0xE0, 0x22, 0xC5, 0x5E));
        private static readonly Brush AnimationOutBrush = Frozen(Color.FromArgb(0xE0, 0xF9, 0x73, 0x16));
        private static readonly Pen AnimationHandlePen = FrozenPen(Brushes.White, 2);
        private static readonly Pen EmphasisPen = new Func<Pen>(() =>
        {
            var pen = new Pen(Frozen(Color.FromArgb(0xC0, 0xFA, 0xCC, 0x15)), 2) { DashStyle = new DashStyle(new[] { 1.5, 1.5 }, 0) };
            pen.Freeze();
            return pen;
        })();
        private const double AnimationBandHeight = 5;
        private const double AnimationGrip = 5;

        // 끄는 띠의 분류와 끄는 동안의 길이(프레임, -1 = 끄지 않음)
        private AnimationKind _animationKind;
        private long _animationLength = -1;

        /// <summary>클립의 나타나기·사라지기 띠(분류, 클립 안 시작, 길이). 끄는 띠는 끄는 길이로.</summary>
        private IEnumerable<(AnimationKind Kind, long Start, long Length, ClipAnimation Animation)> AnimationBands(Clip clip)
        {
            foreach (var kind in new[] { AnimationKind.In, AnimationKind.Out })
            {
                var animation = kind == AnimationKind.In ? ClipAnimations.In(clip) : ClipAnimations.Out(clip);
                if (animation == null)
                    continue;
                var (start, length) = AnimationMotion.Window(clip, animation);
                if (_drag == DragKind.Animation && _animationLength > 0 && clip.Id == _dragClipId && kind == _animationKind)
                {
                    if (kind == AnimationKind.In)
                    {
                        length = _animationLength;
                    }
                    else
                    {
                        var end = start + length;
                        length = _animationLength;
                        start = end - length;
                    }
                }
                yield return (kind, start, length, animation);
            }
        }

        /// <summary>띠를 그린다(DrawClip 끝에서).</summary>
        private void DrawAnimationBands(DrawingContext dc, Clip clip, Rect rect)
        {
            if (clip.Animations == null || clip.Animations.Count == 0 || rect.Height < 14)
                return;
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            foreach (var (kind, start, length, _) in AnimationBands(clip))
            {
                var left = FrameToX(clip.Start + start);
                var right = FrameToX(clip.Start + start + length);
                dc.DrawRectangle(kind == AnimationKind.In ? AnimationInBrush : AnimationOutBrush, null,
                    new Rect(left, rect.Bottom - 1 - AnimationBandHeight, Math.Max(2, right - left), AnimationBandHeight));
                var handle = kind == AnimationKind.In ? right : left;
                dc.DrawLine(AnimationHandlePen, new Point(handle, rect.Bottom - AnimationBandHeight - 5), new Point(handle, rect.Bottom - 1));
            }
            foreach (var emphasis in ClipAnimations.Emphases(clip))
            {
                var (start, _, end) = AnimationMotion.EmphasisWindow(clip, emphasis);
                var y = rect.Bottom - AnimationBandHeight - 3.5;
                dc.DrawLine(EmphasisPen, new Point(FrameToX(clip.Start + start), y), new Point(FrameToX(clip.Start + end), y));
            }
            dc.Pop();
        }

        /// <summary>점이 띠 손잡이 위인지(클립 아래쪽 띠 높이 안, 손잡이에서 좌우 5px).</summary>
        private Clip HitAnimationHandle(Point point, out TimelineRow row, out AnimationKind kind, out long length)
        {
            kind = AnimationKind.In;
            length = 0;
            row = RowAt(point.Y);
            if (row == null)
                return null;
            foreach (var clip in row.Track.Clips.Where(c => c.Animations != null && c.Animations.Count > 0))
            {
                var rect = ClipRect(row, clip.Start, clip.End);
                if (rect.Height < 14 || point.Y > rect.Bottom || point.Y < rect.Bottom - AnimationBandHeight - 7 || point.X < rect.Left - AnimationGrip || point.X > rect.Right + AnimationGrip)
                    continue;
                foreach (var band in AnimationBands(clip))
                {
                    var handle = FrameToX(clip.Start + band.Start + (band.Kind == AnimationKind.In ? band.Length : 0));
                    if (Math.Abs(point.X - handle) <= AnimationGrip)
                    {
                        kind = band.Kind;
                        length = band.Length;
                        return clip;
                    }
                }
            }
            return null;
        }

        /// <summary>마우스가 손잡이 위면 커서·도움말을 바꾸고 true.</summary>
        private bool AnimationHover(Point point)
        {
            var clip = HitAnimationHandle(point, out var row, out var kind, out var length);
            if (clip == null)
                return false;
            Cursor = row.Track.Locked ? Cursors.No : Cursors.SizeWE;
            var animation = kind == AnimationKind.In ? ClipAnimations.In(clip) : ClipAnimations.Out(clip);
            var tip = ClipAnimations.Name(animation) + " " + UiKit.Number(_session.Project.Rate.ToSeconds(length)) + "초 — 끌어 길이를 바꿉니다";
            if (!Equals(ToolTip, tip))
                ToolTip = tip;
            return true;
        }

        /// <summary>손잡이를 눌렀으면 그 클립을 고르고 길이 끌기를 시작해 true.</summary>
        private bool AnimationPointerDown(Point point)
        {
            var clip = HitAnimationHandle(point, out var row, out var kind, out var length);
            if (clip == null)
                return false;
            if (!_session.IsSelected(clip))
                _session.Select(new[] { clip });
            if (row.Track.Locked)
            {
                StatusMessage?.Invoke("'" + row.Track.Name + "' 트랙이 잠겨 있습니다.");
                return true;
            }
            _drag = DragKind.Animation;
            _dragStarted = false;
            _dragClipId = clip.Id;
            _animationKind = kind;
            _animationLength = length;
            return true;
        }

        /// <summary>끄는 동안: 손잡이 자리 = 나타나기 끝·사라지기 시작(1프레임 ~ 클립에 들어가는 만큼).</summary>
        private void AnimationPointerMove(Point point)
        {
            if (!_dragStarted)
            {
                if (Math.Abs(point.X - _downPoint.X) < DragThreshold)
                    return;
                _dragStarted = true;
            }
            var clip = _session.Project.FindClip(_dragClipId);
            var animation = clip == null ? null : _animationKind == AnimationKind.In ? ClipAnimations.In(clip) : ClipAnimations.Out(clip);
            if (animation == null)
            {
                CancelDrag();
                return;
            }
            var (start, length) = AnimationMotion.Window(clip, animation);
            var frame = XToFrame(point.X) - clip.Start;
            _animationLength = _animationKind == AnimationKind.In
                ? Math.Max(1, Math.Min(clip.Duration - start, frame - start))
                : Math.Max(1, Math.Min(start + length, start + length - frame));
            _animationLength = Math.Min(ClipAnimations.MaxFrames, _animationLength);
            InvalidateVisual();
        }

        /// <summary>놓으면 길이를 바꾼다(실행 취소 한 단계).</summary>
        private void AnimationPointerUp()
        {
            var clipId = _dragClipId;
            var kind = _animationKind;
            var length = _animationLength;
            var started = _dragStarted;
            CancelDrag();
            if (!started || length <= 0)
                return;
            var ok = _session.Execute(p =>
            {
                var clip = p.FindClip(clipId);
                var animation = kind == AnimationKind.In ? ClipAnimations.In(clip) : ClipAnimations.Out(clip);
                if (clip == null || animation == null)
                    return;
                TimelineEdits.EnsureEditable(p.TrackOf(clip));
                animation.Duration = length;
            });
            if (ok)
                StatusMessage?.Invoke(ClipAnimations.KindName(kind) + " 길이 " + UiKit.Number(_session.Project.Rate.ToSeconds(length)) + "초");
            else if (_session.LastError != null)
                StatusMessage?.Invoke(_session.LastError);
        }

        /// <summary>화면 읽기·시험용 이름 덧붙임: " · 나타나기 서서히 0.5초 · 강조 맥박 · 사라지기 …".</summary>
        internal static string AnimationLabel(Clip clip, FrameRate rate)
        {
            if (clip.Animations == null || clip.Animations.Count == 0)
                return "";
            return string.Concat(clip.Animations.Select(a =>
            {
                var def = ClipAnimations.Def(a.Effect);
                return " · " + ClipAnimations.KindName(a.Kind) + " " + def?.DisplayName(a.Kind) + (a.Kind == AnimationKind.Emphasis ? "" : " " + UiKit.Number(rate.ToSeconds(a.Duration)) + "초");
            }));
        }
    }
}
