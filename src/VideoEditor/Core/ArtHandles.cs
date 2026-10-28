using System;
using System.Collections.Generic;

namespace VideoEditor.Core
{
    /// <summary>아트 요소의 모양 손잡이(미리 보기 틀에서 끄는 점).</summary>
    public enum ArtHandle
    {
        /// <summary>모서리 반경(왼쪽 위 모서리 원의 가운데).</summary>
        CornerRadius,
        /// <summary>선·화살표의 시작 끝(왼쪽).</summary>
        LineStart,
        /// <summary>선·화살표의 끝(오른쪽 — 화살표 머리).</summary>
        LineEnd,
        /// <summary>별의 안쪽 꼭짓점(안쪽 비율).</summary>
        InnerRatio
    }

    /// <summary>
    /// 아트 요소의 모양 손잡이 계산(순수 함수): 손잡이 자리와, 끈 만큼 바뀐 값.
    /// 상자 좌표(모양 상자 왼쪽 위 기준, 크기 배율 전 프로젝트 픽셀)와 화면 좌표(프로젝트 픽셀)는 렌더 그래프와 같은 규칙으로 잇는다 —
    /// 상자 가운데 = 화면 가운데 + X/Y, 크기 배율, 가운데 둘레 시계 방향 회전(<see cref="ClipLayout.TextBox"/>와 같은 자리).
    /// </summary>
    public static class ArtHandles
    {
        /// <summary>모양 종류마다 손잡이(없으면 빈 목록).</summary>
        public static IReadOnlyList<ArtHandle> For(ArtKind kind)
        {
            switch (kind)
            {
                case ArtKind.Rectangle:
                    return new[] { ArtHandle.CornerRadius };
                case ArtKind.Line:
                case ArtKind.Arrow:
                    return new[] { ArtHandle.LineStart, ArtHandle.LineEnd };
                case ArtKind.Star:
                    return new[] { ArtHandle.InnerRatio };
                default:
                    return Array.Empty<ArtHandle>();
            }
        }

        /// <summary>손잡이 이름(상태 줄·접근성).</summary>
        public static string Name(ArtHandle handle)
        {
            switch (handle)
            {
                case ArtHandle.LineStart: return "시작 끝";
                case ArtHandle.LineEnd: return "끝";
                case ArtHandle.InnerRatio: return "안쪽";
                default: return "모서리 반경";
            }
        }

        private static (double S, double Cos, double Sin, double CenterX, double CenterY) Frame(Project project, Clip clip)
        {
            var s = double.IsFinite(clip.Scale) ? Math.Max(0.01, clip.Scale) : 1;
            var radians = (double.IsFinite(clip.Rotation) ? clip.Rotation : 0) * Math.PI / 180;
            return (s, Math.Cos(radians), Math.Sin(radians), project.Width / 2.0 + clip.X, project.Height / 2.0 + clip.Y);
        }

        private static ArtShape Shape(Clip clip)
        {
            var shape = (clip.Art ?? new ArtShape()).Clone();
            shape.Normalize();
            return shape;
        }

        /// <summary>상자 좌표 → 화면(프로젝트 픽셀).</summary>
        public static (double X, double Y) ToProject(Project project, Clip clip, double localX, double localY)
        {
            var shape = Shape(clip);
            var (s, cos, sin, cx, cy) = Frame(project, clip);
            var dx = (localX - shape.Width / 2) * s;
            var dy = (localY - shape.Height / 2) * s;
            return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }

        /// <summary>화면(프로젝트 픽셀) → 상자 좌표.</summary>
        public static (double X, double Y) ToLocal(Project project, Clip clip, double x, double y)
        {
            var shape = Shape(clip);
            var (s, cos, sin, cx, cy) = Frame(project, clip);
            var dx = x - cx;
            var dy = y - cy;
            return (shape.Width / 2 + (dx * cos + dy * sin) / s, shape.Height / 2 + (-dx * sin + dy * cos) / s);
        }

        /// <summary>손잡이 자리(상자 좌표). 모서리 반경은 그리는 반경(짧은 변의 절반까지)의 원 가운데.</summary>
        public static (double X, double Y) Local(ArtShape art, ArtHandle handle)
        {
            var shape = (art ?? new ArtShape()).Clone();
            shape.Normalize();
            double w = shape.Width, h = shape.Height;
            switch (handle)
            {
                case ArtHandle.LineStart:
                    return (0, h / 2);
                case ArtHandle.LineEnd:
                    return (w, h / 2);
                case ArtHandle.InnerRatio:
                {
                    var a = InnerAngle(shape.Sides);
                    return (w / 2 + w / 2 * shape.InnerRatio * Math.Cos(a), h / 2 + h / 2 * shape.InnerRatio * Math.Sin(a));
                }
                default:
                {
                    var r = Math.Min(shape.CornerRadius, Math.Min(w, h) / 2);
                    return (r, r);
                }
            }
        }

        /// <summary>손잡이 자리(화면, 프로젝트 픽셀).</summary>
        public static (double X, double Y) Position(Project project, Clip clip, ArtHandle handle)
        {
            var local = Local(clip.Art, handle);
            return ToProject(project, clip, local.X, local.Y);
        }

        /// <summary>별의 첫 안쪽 꼭짓점 방향(라디안 — 위 가운데 꼭짓점에서 시계 방향으로 반 칸).</summary>
        private static double InnerAngle(int points)
        {
            return -Math.PI / 2 + Math.PI / Math.Max(3, points);
        }

        /// <summary>
        /// 손잡이를 from에서 to로(화면, 프로젝트 픽셀) 끌었을 때의 값 — clip은 끌기 시작 때의 값이고 고치지 않는다(새 모양은 사본).
        /// 모서리 반경: 끈 거리를 상자 대각선(안쪽)에 투영한 만큼(0 ~ 짧은 변의 절반). 선 끝: 그 끝만 옮기고 반대쪽 끝은 그대로 — 너비(길이)·회전·가운데가 함께 바뀐다(크기 배율은 그대로).
        /// 별 안쪽: 끈 점을 안쪽 꼭짓점 방향에 투영한 비율(0.1~0.95). 누른 자리와 손잡이가 조금 달라도 튀지 않게 끈 만큼만 옮긴다.
        /// </summary>
        public static (double X, double Y, double Rotation, ArtShape Art) Drag(Project project, Clip clip, ArtHandle handle,
            (double X, double Y) from, (double X, double Y) to)
        {
            var shape = Shape(clip);
            var rotation = double.IsFinite(clip.Rotation) ? clip.Rotation : 0;
            if (!Finite(from) || !Finite(to))
                return (clip.X, clip.Y, rotation, shape);
            var a = ToLocal(project, clip, from.X, from.Y);
            var b = ToLocal(project, clip, to.X, to.Y);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double w = shape.Width, h = shape.Height;
            switch (handle)
            {
                case ArtHandle.LineStart:
                case ArtHandle.LineEnd:
                {
                    var end = handle == ArtHandle.LineEnd;
                    var fixedEnd = Position(project, clip, end ? ArtHandle.LineStart : ArtHandle.LineEnd);
                    var moving = Position(project, clip, handle);
                    var point = (X: moving.X + to.X - from.X, Y: moving.Y + to.Y - from.Y);
                    // 시작 → 끝 방향
                    var vx = end ? point.X - fixedEnd.X : fixedEnd.X - point.X;
                    var vy = end ? point.Y - fixedEnd.Y : fixedEnd.Y - point.Y;
                    var length = Math.Sqrt(vx * vx + vy * vy);
                    var s = Math.Max(0.01, clip.Scale);
                    var newRotation = rotation;
                    if (length > 1e-9)
                        newRotation = Nearest(Math.Atan2(vy, vx) * 180 / Math.PI, rotation);
                    shape.Width = Math.Max(1, Math.Min(ClipLayout.MaxTextBox, length / s));
                    // 가운데 = 고정 끝에서 새 방향으로 길이의 절반(너비 하한·상한에 걸려도 고정 끝은 그대로)
                    var radians = newRotation * Math.PI / 180;
                    var half = shape.Width * s / 2 * (end ? 1 : -1);
                    var centerX = fixedEnd.X + Math.Cos(radians) * half;
                    var centerY = fixedEnd.Y + Math.Sin(radians) * half;
                    return (centerX - project.Width / 2.0, centerY - project.Height / 2.0, newRotation, shape);
                }
                case ArtHandle.InnerRatio:
                {
                    var start = Local(shape, ArtHandle.InnerRatio);
                    var angle = InnerAngle(shape.Sides);
                    var u = (start.X + dx - w / 2) / (w / 2);
                    var v = (start.Y + dy - h / 2) / (h / 2);
                    shape.InnerRatio = Math.Max(0.1, Math.Min(0.95, u * Math.Cos(angle) + v * Math.Sin(angle)));
                    return (clip.X, clip.Y, rotation, shape);
                }
                default:
                {
                    var limit = Math.Min(w, h) / 2;
                    var radius = Math.Min(shape.CornerRadius, limit);
                    shape.CornerRadius = Math.Max(0, Math.Min(limit, radius + (dx + dy) / 2));
                    return (clip.X, clip.Y, rotation, shape);
                }
            }
        }

        /// <summary>angle(도)에 360의 배수를 더해 reference에 가장 가깝게 — 끄는 동안 회전 값이 350° → −10°처럼 튀지 않게.</summary>
        private static double Nearest(double angle, double reference)
        {
            return angle + 360 * Math.Round((reference - angle) / 360);
        }

        private static bool Finite((double X, double Y) point)
        {
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }
    }
}
