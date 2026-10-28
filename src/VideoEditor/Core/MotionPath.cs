using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VideoEditor.Core
{
    /// <summary>이동 경로의 점 하나(프로젝트 픽셀, 클립 자리 기준 — 첫 점이 (0, 0)).</summary>
    public sealed class PathPoint
    {
        public PathPoint()
        {
        }

        public PathPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }
        public double Y { get; set; }

        public PathPoint Clone()
        {
            return new PathPoint(X, Y);
        }
    }

    /// <summary>
    /// 이동 경로(#4 단계 2): 직선·호·원·물결·사용자 경로. 점은 클립 자리(가운데) 기준 상대 위치(프로젝트 픽셀)라 클립을 옮기면 경로도 함께 움직인다.
    /// 경로 위 자리는 매개변수 s(0 = 시작, 1 = 끝 — 원은 한 바퀴 돌아 처음 자리)의 함수이고, 시간식(τ → s)은 강조처럼 지연·한 번 길이·반복을 따른다.
    /// 그래프와 화면이 같은 식을 쓴다: 변위 식(<see cref="Offset"/>)을 C#에서 계산하면 손잡이·점선이, FFmpeg 식 글로 쓰면 overlay 위치가 된다.
    /// </summary>
    public static class MotionPaths
    {
        /// <summary>사용자 경로의 점 수 상한(시작점 포함).</summary>
        public const int MaxPoints = 32;
        /// <summary>점 좌표 상한(프로젝트 픽셀, 절댓값).</summary>
        public const double MaxOffset = 100000;
        /// <summary>물결 수(경로 하나에 오르내림 두 번).</summary>
        public const int WaveCount = 2;
        // 끝 자리를 정확히(1/1024 픽셀로 맞춤 — 삼각 함수 오차로 overlay가 버림한 위치가 한 픽셀 어긋나지 않게)
        private const double SnapSteps = 1024;

        /// <summary>경로 효과인지.</summary>
        public static bool IsPath(AnimationEffect effect)
        {
            return effect == AnimationEffect.PathLine || effect == AnimationEffect.PathArc || effect == AnimationEffect.PathCircle
                || effect == AnimationEffect.PathWave || effect == AnimationEffect.PathCustom;
        }

        /// <summary>점 수가 정해진 경로의 점 수(직선·물결·원 2, 호 3). 사용자 경로는 0(2~<see cref="MaxPoints"/>).</summary>
        public static int FixedCount(AnimationEffect effect)
        {
            switch (effect)
            {
                case AnimationEffect.PathArc:
                    return 3;
                case AnimationEffect.PathCustom:
                    return 0;
                default:
                    return 2;
            }
        }

        /// <summary>점을 더하고 뺄 수 있는 경로(사용자 경로).</summary>
        public static bool CanAddPoints(AnimationEffect effect)
        {
            return effect == AnimationEffect.PathCustom;
        }

        /// <summary>
        /// 기본 점(높이 1080 화면 기준 × scale): 직선은 오른쪽 400, 호는 위로 부푼 반원꼴, 원은 지름 300(시작이 원 위쪽, 가운데가 아래), 물결은 오른쪽 600, 사용자 경로는 지그재그.
        /// </summary>
        public static List<PathPoint> DefaultPoints(AnimationEffect effect, double scale = 1)
        {
            double[] values;
            switch (effect)
            {
                case AnimationEffect.PathArc:
                    values = new double[] { 0, 0, 200, -150, 400, 0 };
                    break;
                case AnimationEffect.PathCircle:
                    values = new double[] { 0, 0, 0, 300 };
                    break;
                case AnimationEffect.PathWave:
                    values = new double[] { 0, 0, 600, 0 };
                    break;
                case AnimationEffect.PathCustom:
                    values = new double[] { 0, 0, 150, -150, 300, 0, 450, -150 };
                    break;
                default:
                    values = new double[] { 0, 0, 400, 0 };
                    break;
            }
            if (!double.IsFinite(scale) || scale <= 0)
                scale = 1;
            var points = new List<PathPoint>();
            for (var i = 0; i < values.Length; i += 2)
                points.Add(new PathPoint(Math.Round(values[i] * scale), Math.Round(values[i + 1] * scale)));
            return points;
        }

        /// <summary>
        /// 읽은 점(남이 만든 파일일 수 있음)을 규칙에 맞게: 첫 점을 (0, 0)으로 옮기고(경로는 시작점 기준), NaN·무한대·null 점은 빼고, 좌표는 상한 안으로.
        /// 점 수가 정해진 경로는 모자라면 기본 점, 넘치면 앞에서부터. 사용자 경로는 2~<see cref="MaxPoints"/>개(모자라면 기본 점).
        /// </summary>
        public static List<PathPoint> Clean(AnimationEffect effect, IEnumerable<PathPoint> points)
        {
            var list = (points ?? Enumerable.Empty<PathPoint>()).Where(p => p != null && double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
            var fixedCount = FixedCount(effect);
            var need = fixedCount > 0 ? fixedCount : 2;
            if (list.Count < need)
                return DefaultPoints(effect);
            list = list.Take(fixedCount > 0 ? fixedCount : MaxPoints).ToList();
            double x0 = list[0].X, y0 = list[0].Y;
            return list.Select(p => new PathPoint(Clamp(p.X - x0), Clamp(p.Y - y0))).ToList();
        }

        private static double Clamp(double value)
        {
            return Math.Max(-MaxOffset, Math.Min(MaxOffset, value));
        }

        // ---------- 경로 위 자리 ----------

        /// <summary>
        /// 경로 위 자리 s(시간식 — 0 = 시작, 1 = 끝)의 시작점 기준 변위(프로젝트 픽셀, 시간식). s = 0에서 (0, 0), s = 1에서 끝점(원은 (0, 0))에 1/1024 픽셀까지 정확히 닿는다.
        /// s가 0~1 밖이면(탄성 이징) 경로를 이어 늘인 자리.
        /// </summary>
        public static (MotionExpr X, MotionExpr Y) Offset(ClipAnimation animation, MotionExpr s)
        {
            var points = animation?.Points;
            if (points == null || points.Count < 2)
                return (MotionExpr.Num(0), MotionExpr.Num(0));
            var p0 = points[0];
            switch (animation.Effect)
            {
                case AnimationEffect.PathArc when points.Count >= 3:
                    return Snap(Arc(p0, points[1], points[2], s));
                case AnimationEffect.PathCircle:
                    return Snap(Circle(p0, points[1], animation.Direction != AnimationDirection.Left, s));
                case AnimationEffect.PathWave:
                    return Snap(Wave(p0, points[1], animation.Amount, s));
                case AnimationEffect.PathCustom:
                    return Snap(Polyline(points, s));
                default:
                    return Snap(Line(p0, points[^1], s));
            }
        }

        /// <summary><see cref="Offset"/>를 C#에서 계산한 값(손잡이·점선·시험).</summary>
        public static (double X, double Y) OffsetAt(ClipAnimation animation, double s)
        {
            var (x, y) = Offset(animation, MotionExpr.Num(s));
            return (x.Eval(0), y.Eval(0));
        }

        /// <summary>점선으로 그릴 경로 위 자리(시작점 기준 변위) count + 1개 — s = 0부터 1까지 고르게.</summary>
        public static List<(double X, double Y)> Samples(ClipAnimation animation, int count)
        {
            count = Math.Max(1, count);
            // 직선·사용자 경로는 꺾이는 점만 있으면 된다(고르게 나누면 꼭짓점이 잘린다)
            if (animation?.Points != null && (animation.Effect == AnimationEffect.PathLine || animation.Effect == AnimationEffect.PathCustom))
            {
                var p0 = animation.Points[0];
                return animation.Points.Select(p => (p.X - p0.X, p.Y - p0.Y)).ToList();
            }
            return Enumerable.Range(0, count + 1).Select(i => OffsetAt(animation, (double)i / count)).ToList();
        }

        private static (MotionExpr X, MotionExpr Y) Snap((MotionExpr X, MotionExpr Y) offset)
        {
            MotionExpr Round(MotionExpr v) => MotionExpr.Call("round", a => Math.Round(a[0], MidpointRounding.AwayFromZero), v * SnapSteps) / SnapSteps;
            return (Round(offset.X), Round(offset.Y));
        }

        private static (MotionExpr X, MotionExpr Y) Line(PathPoint p0, PathPoint p1, MotionExpr s)
        {
            return (Exact(p1.X - p0.X) * s, Exact(p1.Y - p0.Y) * s);
        }

        /// <summary>호: 세 점(시작·가운데 지나는 점·끝)을 지나는 원의 한 조각. 세 점이 한 줄이면 직선.</summary>
        private static (MotionExpr X, MotionExpr Y) Arc(PathPoint p0, PathPoint pm, PathPoint p1, MotionExpr s)
        {
            if (!Circumcircle(p0, pm, p1, out var cx, out var cy, out var radius))
                return Line(p0, p1, s);
            var a0 = Math.Atan2(p0.Y - cy, p0.X - cx);
            var am = Math.Atan2(pm.Y - cy, pm.X - cx);
            var a1 = Math.Atan2(p1.Y - cy, p1.X - cx);
            // 시작에서 끝까지 가운데 점을 지나는 쪽으로 돈다
            var toEnd = Turn(a1 - a0);
            var toMiddle = Turn(am - a0);
            var sweep = toMiddle < toEnd ? toEnd : toEnd - 2 * Math.PI;
            return Around(cx - p0.X, cy - p0.Y, radius, a0, sweep, s);
        }

        /// <summary>원: 시작점과 맞은편 점이 지름. 시계 방향(화면 기준)이면 각도가 커지는 쪽으로 한 바퀴.</summary>
        private static (MotionExpr X, MotionExpr Y) Circle(PathPoint p0, PathPoint opposite, bool clockwise, MotionExpr s)
        {
            var cx = (p0.X + opposite.X) / 2;
            var cy = (p0.Y + opposite.Y) / 2;
            var radius = Math.Sqrt((opposite.X - p0.X) * (opposite.X - p0.X) + (opposite.Y - p0.Y) * (opposite.Y - p0.Y)) / 2;
            if (radius < 1e-9)
                return (MotionExpr.Num(0), MotionExpr.Num(0));
            var a0 = Math.Atan2(p0.Y - cy, p0.X - cx);
            return Around(cx - p0.X, cy - p0.Y, radius, a0, (clockwise ? 1 : -1) * 2 * Math.PI, s);
        }

        /// <summary>가운데(시작점 기준 cx, cy)·반지름·시작 각에서 sweep만큼 도는 자리 − 시작점.</summary>
        private static (MotionExpr X, MotionExpr Y) Around(double cx, double cy, double radius, double a0, double sweep, MotionExpr s)
        {
            var angle = Exact(a0) + Exact(sweep) * s;
            var x = Exact(cx) + Exact(radius) * MotionExpr.Call("cos", v => Math.Cos(v[0]), angle);
            var y = Exact(cy) + Exact(radius) * MotionExpr.Sin(angle);
            return (x, y);
        }

        /// <summary>물결: 시작에서 끝까지 곧게 가며 옆으로(진행 방향 왼쪽이 먼저 — 오른쪽으로 가면 위로) 높이만큼 <see cref="WaveCount"/>번 오르내린다.</summary>
        private static (MotionExpr X, MotionExpr Y) Wave(PathPoint p0, PathPoint p1, double height, MotionExpr s)
        {
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9)
                return (MotionExpr.Num(0), MotionExpr.Num(0));
            // 진행 방향의 왼쪽(화면은 y가 아래라 (dy, −dx))
            double nx = dy / length, ny = -dx / length;
            var swing = MotionExpr.Sin(Exact(2 * Math.PI * WaveCount) * s);
            return (Exact(dx) * s + Exact(nx * height) * swing, Exact(dy) * s + Exact(ny * height) * swing);
        }

        /// <summary>사용자 경로: 점을 곧은 선으로 잇고 길이에 맞춰 일정한 빠르기로(길이 0인 마디는 건너뜀). s가 0~1 밖이면 첫·마지막 마디를 이어 늘인다.</summary>
        private static (MotionExpr X, MotionExpr Y) Polyline(IReadOnlyList<PathPoint> points, MotionExpr s)
        {
            var p0 = points[0];
            var corners = new List<PathPoint> { p0 };
            foreach (var p in points.Skip(1))
            {
                var last = corners[^1];
                if (Math.Abs(p.X - last.X) > 1e-9 || Math.Abs(p.Y - last.Y) > 1e-9)
                    corners.Add(p);
            }
            if (corners.Count < 2)
                return (MotionExpr.Num(0), MotionExpr.Num(0));
            var lengths = new double[corners.Count];
            for (var i = 1; i < corners.Count; i++)
            {
                double dx = corners[i].X - corners[i - 1].X, dy = corners[i].Y - corners[i - 1].Y;
                lengths[i] = lengths[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            var total = lengths[^1];
            var u = Exact(total) * s;
            (MotionExpr X, MotionExpr Y) Segment(int i)
            {
                var a = corners[i];
                var b = corners[i + 1];
                var t = (u - Exact(lengths[i])) / Exact(lengths[i + 1] - lengths[i]);
                return (Exact(a.X - p0.X) + Exact(b.X - a.X) * t, Exact(a.Y - p0.Y) + Exact(b.Y - a.Y) * t);
            }
            // 마지막 마디부터 앞으로 감싼다: u가 i번째 마디 끝보다 작으면 그 마디
            var (x, y) = Segment(corners.Count - 2);
            for (var i = corners.Count - 3; i >= 0; i--)
            {
                var (sx, sy) = Segment(i);
                var inside = MotionExpr.Lt(u, Exact(lengths[i + 1]));
                x = MotionExpr.If(inside, sx, x);
                y = MotionExpr.If(inside, sy, y);
            }
            return (x, y);
        }

        /// <summary>세 점을 지나는 원(한 줄이면 false).</summary>
        private static bool Circumcircle(PathPoint a, PathPoint b, PathPoint c, out double cx, out double cy, out double radius)
        {
            var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            var span = Math.Max(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)), Math.Max(Math.Abs(c.X - a.X), Math.Abs(c.Y - a.Y)));
            if (Math.Abs(d) <= 1e-6 * Math.Max(1, span * span))
            {
                cx = cy = radius = 0;
                return false;
            }
            double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
            cx = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
            cy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
            radius = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));
            return true;
        }

        /// <summary>각도를 [0, 2π)로.</summary>
        private static double Turn(double angle)
        {
            var turn = angle % (2 * Math.PI);
            return turn < 0 ? turn + 2 * Math.PI : turn;
        }

        // ---------- 시간 ----------

        /// <summary>
        /// τ(클립 안 장 번호) → 경로 위 자리 s(시간식). 강조처럼 지연 뒤부터 한 번 길이마다 되풀이하고(반복 0 = 클립 끝까지), 다 하면 끝 자리에 남는다.
        /// 되돌아오기면 한 번 안에서 갔다가(앞 절반) 돌아온다(뒤 절반 — 간 길을 거꾸로 돌린 움직임이라 시작 자리에 남는다). 이징은 한 번마다.
        /// </summary>
        public static MotionExpr Progress(Clip clip, ClipAnimation animation)
        {
            var (start, cycle, end) = AnimationMotion.EmphasisWindow(clip, animation);
            var count = Math.Max(1, (end - start + cycle - 1) / cycle);
            var u = (MotionExpr.Time - start) / cycle;
            MotionExpr q;
            if (count == 1)
            {
                q = MotionExpr.Clip01(u);
            }
            else
            {
                var turns = MotionExpr.Clip01(u / count) * count;
                var done = MotionExpr.Min(MotionExpr.Floor(turns), count);
                q = MotionExpr.If(MotionExpr.Lt(turns, count), turns - done, 1);
            }
            if (animation.RoundTrip)
                q = 1 - MotionExpr.Call("abs", v => Math.Abs(v[0]), 2 * q - 1);
            return AnimationMotion.Ease(animation.Ease, q);
        }

        /// <summary>τ의 시작점 기준 변위(프로젝트 픽셀, 시간식) — 경로 시작 전(지연)은 0.</summary>
        public static (MotionExpr X, MotionExpr Y) Displacement(Clip clip, ClipAnimation animation)
        {
            return Offset(animation, Progress(clip, animation));
        }

        // ---------- 점 고치기 ----------

        /// <summary>
        /// 경로 선에서 화면 점에 가장 가까운 마디(사용자 경로 — 점 i와 i+1 사이면 i)와 그 위의 자리. 점은 시작점 기준(프로젝트 픽셀). 마디가 없으면 -1.
        /// </summary>
        public static (int Segment, double X, double Y, double Distance) Nearest(IReadOnlyList<PathPoint> points, double x, double y)
        {
            var best = (-1, 0.0, 0.0, double.MaxValue);
            if (points == null)
                return best;
            for (var i = 0; i + 1 < points.Count; i++)
            {
                double ax = points[i].X, ay = points[i].Y, bx = points[i + 1].X, by = points[i + 1].Y;
                double dx = bx - ax, dy = by - ay;
                var length2 = dx * dx + dy * dy;
                var t = length2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / length2));
                double px = ax + dx * t, py = ay + dy * t;
                var distance = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                if (distance < best.Item4)
                    best = (i, px, py, distance);
            }
            return best;
        }

        // ---------- 식 글 ----------

        /// <summary>
        /// 정밀한 상수(식 글에 17자리까지): 경로의 반지름·각도는 6자리로 줄이면 끝 자리가 1/1024 픽셀보다 더 어긋난다.
        /// 상수로 접지 않는다(접으면 6자리 상수가 된다) — 값은 <see cref="MotionExpr.Eval"/>로 같다.
        /// </summary>
        private static MotionExpr Exact(double value)
        {
            return value == 0 ? MotionExpr.Num(0) : value == 1 ? MotionExpr.Num(1) : new ExactExpr(value);
        }

        private sealed class ExactExpr : MotionExpr
        {
            private readonly double _value;

            public ExactExpr(double value)
            {
                _value = value;
            }

            public override double Eval(double time)
            {
                return _value;
            }

            internal override void Write(StringBuilder text, string time)
            {
                // F17은 지수 없이 모든 자리(.NET Core 3.0부터 정확한 자리) — 끝의 0은 뺀다
                var number = _value.ToString("F17", CultureInfo.InvariantCulture);
                if (number.Contains('.'))
                    number = number.TrimEnd('0').TrimEnd('.');
                if (number.StartsWith("-", StringComparison.Ordinal))
                    text.Append('(').Append(number).Append(')');
                else
                    text.Append(number);
            }
        }
    }
}
