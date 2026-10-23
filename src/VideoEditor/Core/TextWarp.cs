using System;
using System.Collections.Generic;

namespace VideoEditor.Core
{
    /// <summary>
    /// 글자 휘기 계산(순수 함수). 그리개는 글자 윤곽 점을 배치 상자 기준 0~1로 정규화해 <see cref="Map"/>에 넣고 원래 좌표로 되돌린다.
    /// 계산은 폭 단위 좌표(x = u, y = v·ρ, ρ = 상자 높이/폭)에서 해서 가로세로 비율이 달라도 모양이 같게 휜다.
    /// </summary>
    public static class TextWarp
    {
        private const int StairCount = 5;

        /// <summary>
        /// (u, v): 글자 배치 상자 안의 정규화 좌표(0~1, v = 0이 위). aspect = 상자 높이/폭.
        /// 돌려주는 값도 같은 정규화 좌표(0~1을 넘을 수 있음). 계산이 NaN·무한대가 되면 입력을 그대로 돌려준다.
        /// </summary>
        public static (double U, double V) Map(WarpKind kind, double amount, double u, double v, double aspect)
        {
            var a = double.IsNaN(amount) ? 0 : Math.Max(-100, Math.Min(100, amount)) / 100;
            if (kind == WarpKind.None || a == 0)
                return (u, v);
            var rho = aspect;
            var x = u;
            var y = v * rho;
            double nx = x, ny = y;
            switch (kind)
            {
                case WarpKind.ArchUp:
                    ny = y - a * rho * 0.5 * Math.Sin(Math.PI * x);
                    break;
                case WarpKind.ArchDown:
                    ny = y + a * rho * 0.5 * Math.Sin(Math.PI * x);
                    break;
                case WarpKind.Wave:
                    ny = y + a * rho * 0.25 * Math.Sin(2 * Math.PI * x);
                    break;
                case WarpKind.Bulge:
                    ny = Swell(a, x, y, rho);
                    break;
                case WarpKind.Pinch:
                    ny = Swell(-a, x, y, rho);
                    break;
                case WarpKind.Slant:
                    nx = x + a * 0.5 * (rho / 2 - y);
                    break;
                case WarpKind.Stairs:
                    var step = Math.Floor(Math.Max(0, Math.Min(x, 0.9999)) * StairCount);
                    ny = y + a * rho * 0.5 * (step / (StairCount - 1) - 0.5);
                    break;
                case WarpKind.Circle:
                    (nx, ny) = Circle(a, x, y, rho);
                    break;
                default:
                    return (u, v);
            }
            var result = (U: nx, V: ny / rho);
            if (!double.IsFinite(result.U) || !double.IsFinite(result.V))
                return (u, v);
            return result;
        }

        /// <summary>가운데 열을 위아래로 벌린다(a &gt; 0) 또는 좁힌다(a &lt; 0).</summary>
        private static double Swell(double a, double x, double y, double rho)
        {
            var s = 1 + a * 0.5 * Math.Sin(Math.PI * x);
            return rho / 2 + (y - rho / 2) * s;
        }

        /// <summary>기준선 길이 1을 원호 |a|·2π에 감는다. a &gt; 0이면 원 위쪽 바깥, a &lt; 0이면 위아래를 뒤집어 아래쪽.</summary>
        private static (double X, double Y) Circle(double a, double x, double y, double rho)
        {
            if (Math.Abs(a) < 1e-6)
                return (x, y);
            var flip = a < 0;
            if (flip)
                y = rho - y;
            var span = Math.Abs(a) * 2 * Math.PI;
            var radius = 1 / span;
            var centerY = rho / 2 + radius;
            var phi = (x - 0.5) * span;
            var r = radius + (rho / 2 - y);
            var nx = 0.5 + r * Math.Sin(phi);
            var ny = centerY - r * Math.Cos(phi);
            return (nx, flip ? rho - ny : ny);
        }

        /// <summary>
        /// 닫힌 도형의 변(마지막 → 처음 점 포함)을 maxLength 이하로 쪼갠다 — 긴 직선도 휘게.
        /// 원래 꼭짓점은 모두 그대로 남고, 사이에 점을 더한다. maxLength가 양수가 아니면 그대로 복사한다.
        /// </summary>
        public static List<(double U, double V)> Subdivide(IReadOnlyList<(double U, double V)> polygon, double maxLength)
        {
            var result = new List<(double U, double V)>();
            if (polygon == null)
                return result;
            var n = polygon.Count;
            for (var i = 0; i < n; i++)
            {
                var p = polygon[i];
                var q = polygon[(i + 1) % n];
                result.Add(p);
                if (!(maxLength > 0) || double.IsInfinity(maxLength))
                    continue;
                var length = Math.Sqrt((q.U - p.U) * (q.U - p.U) + (q.V - p.V) * (q.V - p.V));
                if (!(length > maxLength) || double.IsInfinity(length))
                    continue;
                var pieces = (int)Math.Ceiling(length / maxLength);
                for (var k = 1; k < pieces; k++)
                {
                    var t = (double)k / pieces;
                    result.Add((p.U + (q.U - p.U) * t, p.V + (q.V - p.V) * t));
                }
            }
            return result;
        }

        /// <summary>점들의 경계 상자. 점이 없으면 모두 0.</summary>
        public static (double MinU, double MinV, double MaxU, double MaxV) Bounds(IEnumerable<(double U, double V)> points)
        {
            double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
            var any = false;
            foreach (var p in points ?? Array.Empty<(double, double)>())
            {
                any = true;
                minU = Math.Min(minU, p.U);
                minV = Math.Min(minV, p.V);
                maxU = Math.Max(maxU, p.U);
                maxV = Math.Max(maxV, p.V);
            }
            return any ? (minU, minV, maxU, maxV) : (0, 0, 0, 0);
        }
    }
}
