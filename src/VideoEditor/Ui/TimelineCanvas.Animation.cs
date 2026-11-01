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
    /// 이동 경로(#42)는 그 위(◆ 줄 위)에 파란 띠 — 경로가 움직이는 구간 전체이고, 한 번이 끝나는 자리의 흰 손잡이를 끌면 한 번 길이를 바꾼다(높이 40px 이상인 클립만).
    /// 바로 나타나기·사라지기는 길이가 없어 그 장에 가는 띠만 그리고 손잡이가 없다.
    /// </summary>
    internal sealed partial class TimelineCanvas
    {
        private static readonly Brush AnimationInBrush = Frozen(Color.FromArgb(0xE0, 0x22, 0xC5, 0x5E));
        private static readonly Brush AnimationOutBrush = Frozen(Color.FromArgb(0xE0, 0xF9, 0x73, 0x16));
        private static readonly Brush AnimationPathBrush = Frozen(Color.FromArgb(0xE0, 0x3B, 0x82, 0xF6));
        // 경로 띠: 굵기, 클립 아랫변에서 띠 아랫변까지(◆ 줄 위), 그리는 클립 높이
        private const double PathBandHeight = 3;
        private const double PathBandLift = 18;
        private const double PathBandMinHeight = 40;
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

        /// <summary>길이가 없는 효과(바로 나타나기·사라지기)인지 — 손잡이가 없다.</summary>
        private static bool IsInstant(ClipAnimation animation)
        {
            return animation != null && ClipAnimations.Def(animation.Effect)?.Instant == true;
        }

        /// <summary>분류의 애니메이션(나타나기·사라지기·이동 경로).</summary>
        private static ClipAnimation BandAnimation(Clip clip, AnimationKind kind)
        {
            return clip == null ? null : kind == AnimationKind.In ? ClipAnimations.In(clip) : kind == AnimationKind.Out ? ClipAnimations.Out(clip) : ClipAnimations.PathOf(clip);
        }

        /// <summary>이동 경로 띠(클립 안 시작, 한 번 길이, 끝 — 없으면 null). 끄는 중이면 끄는 한 번 길이로.</summary>
        private (long Start, long Cycle, long End)? PathBand(Clip clip)
        {
            var path = ClipAnimations.PathOf(clip);
            if (path == null)
                return null;
            if (_drag == DragKind.Animation && _animationLength > 0 && clip.Id == _dragClipId && _animationKind == AnimationKind.Path)
            {
                path = path.Clone();
                path.Duration = _animationLength;
            }
            return AnimationMotion.EmphasisWindow(clip, path);
        }

        /// <summary>경로 띠의 윗변(화면 y).</summary>
        private static double PathBandTop(Rect rect)
        {
            return rect.Bottom - PathBandLift - PathBandHeight;
        }

        /// <summary>띠를 그린다(DrawClip 끝에서).</summary>
        private void DrawAnimationBands(DrawingContext dc, Clip clip, Rect rect)
        {
            if (clip.Animations == null || clip.Animations.Count == 0 || rect.Height < 14)
                return;
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            foreach (var (kind, start, length, animation) in AnimationBands(clip))
            {
                var left = FrameToX(clip.Start + start);
                var right = FrameToX(clip.Start + start + length);
                dc.DrawRectangle(kind == AnimationKind.In ? AnimationInBrush : AnimationOutBrush, null,
                    new Rect(IsInstant(animation) ? left - 1 : left, rect.Bottom - 1 - AnimationBandHeight, Math.Max(2, right - left), AnimationBandHeight));
                if (IsInstant(animation))
                    continue;
                var handle = kind == AnimationKind.In ? right : left;
                dc.DrawLine(AnimationHandlePen, new Point(handle, rect.Bottom - AnimationBandHeight - 5), new Point(handle, rect.Bottom - 1));
            }
            if (rect.Height >= PathBandMinHeight && PathBand(clip) is { } band)
            {
                var (pathStart, pathCycle, pathEnd) = band;
                var top = PathBandTop(rect);
                var left = FrameToX(clip.Start + pathStart);
                dc.DrawRectangle(AnimationPathBrush, null, new Rect(left, top, Math.Max(2, FrameToX(clip.Start + pathEnd) - left), PathBandHeight));
                var handle = FrameToX(clip.Start + Math.Min(pathEnd, pathStart + pathCycle));
                dc.DrawLine(AnimationHandlePen, new Point(handle, top - 3), new Point(handle, top + PathBandHeight + 3));
            }
            foreach (var emphasis in ClipAnimations.Emphases(clip))
            {
                var (start, _, end) = AnimationMotion.EmphasisWindow(clip, emphasis);
                var y = rect.Bottom - AnimationBandHeight - 3.5;
                dc.DrawLine(EmphasisPen, new Point(FrameToX(clip.Start + start), y), new Point(FrameToX(clip.Start + end), y));
            }
            dc.Pop();
        }

        /// <summary>
        /// 점이 띠 손잡이 위인지(클립 아래쪽 띠 높이 안, 손잡이에서 좌우 5px). 경로 띠는 그 띠 높이 위아래 4px 안, length는 한 번 길이.
        /// 바로 나타나기·사라지기는 손잡이가 없다.
        /// </summary>
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
                if (rect.Height < 14 || point.X < rect.Left - AnimationGrip || point.X > rect.Right + AnimationGrip)
                    continue;
                var top = PathBandTop(rect);
                if (rect.Height >= PathBandMinHeight && point.Y >= top - 4 && point.Y <= top + PathBandHeight + 4 && PathBand(clip) is { } path)
                {
                    var handle = FrameToX(clip.Start + Math.Min(path.End, path.Start + path.Cycle));
                    if (Math.Abs(point.X - handle) <= AnimationGrip)
                    {
                        kind = AnimationKind.Path;
                        length = path.Cycle;
                        return clip;
                    }
                }
                if (point.Y > rect.Bottom || point.Y < rect.Bottom - AnimationBandHeight - 7)
                    continue;
                foreach (var band in AnimationBands(clip))
                {
                    if (IsInstant(band.Animation))
                        continue;
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
            var animation = BandAnimation(clip, kind);
            var tip = kind == AnimationKind.Path
                ? ClipAnimations.Name(animation) + " 한 번 " + UiKit.Number(_session.Project.Rate.ToSeconds(length)) + "초 — 끌어 한 번 길이를 바꿉니다"
                : ClipAnimations.Name(animation) + " " + UiKit.Number(_session.Project.Rate.ToSeconds(length)) + "초 — 끌어 길이를 바꿉니다";
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

        /// <summary>끄는 동안: 손잡이 자리 = 나타나기 끝·사라지기 시작·경로 한 번의 끝(1프레임 ~ 클립에 들어가는 만큼).</summary>
        private void AnimationPointerMove(Point point)
        {
            if (!_dragStarted)
            {
                if (Math.Abs(point.X - _downPoint.X) < DragThreshold)
                    return;
                _dragStarted = true;
            }
            var clip = _session.Project.FindClip(_dragClipId);
            var animation = BandAnimation(clip, _animationKind);
            if (animation == null)
            {
                CancelDrag();
                return;
            }
            var frame = XToFrame(point.X) - clip.Start;
            if (_animationKind == AnimationKind.Path)
            {
                var (pathStart, _, _) = AnimationMotion.EmphasisWindow(clip, animation);
                _animationLength = Math.Min(ClipAnimations.MaxFrames, Math.Max(1, Math.Min(clip.Duration - pathStart, frame - pathStart)));
                InvalidateVisual();
                return;
            }
            var (start, length) = AnimationMotion.Window(clip, animation);
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
                var animation = BandAnimation(clip, kind);
                if (clip == null || animation == null)
                    return;
                TimelineEdits.EnsureEditable(p.TrackOf(clip));
                animation.Duration = length;
            });
            if (ok)
                StatusMessage?.Invoke(ClipAnimations.KindName(kind) + (kind == AnimationKind.Path ? " 한 번 길이 " : " 길이 ") + UiKit.Number(_session.Project.Rate.ToSeconds(length)) + "초");
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
                return " · " + ClipAnimations.KindName(a.Kind) + " " + def?.DisplayName(a.Kind) + (a.Kind == AnimationKind.Emphasis || def?.Instant == true ? "" : " " + UiKit.Number(rate.ToSeconds(a.Duration)) + "초");
            }));
        }
    }
}
