using System;
using System.Collections.Generic;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class TextWarpTests
    {
        private const double Aspect = 0.25;

        public static IEnumerable<object[]> AllKinds()
        {
            return Enum.GetValues<WarpKind>().Select(k => new object[] { k });
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void ZeroAmount_IsIdentity(WarpKind kind)
        {
            foreach (var u in new[] { 0, 0.3, 1 })
            {
                foreach (var v in new[] { 0, 0.5, 1 })
                    Assert.Equal((u, v), TextWarp.Map(kind, 0, u, v, Aspect));
            }
        }

        [Fact]
        public void None_IsIdentity()
        {
            Assert.Equal((0.3, 0.7), TextWarp.Map(WarpKind.None, 80, 0.3, 0.7, Aspect));
        }

        [Fact]
        public void ArchUp_RaisesMiddle_ArchDown_LowersIt()
        {
            var middle = TextWarp.Map(WarpKind.ArchUp, 50, 0.5, 0.5, Aspect).V;
            Assert.True(middle < TextWarp.Map(WarpKind.ArchUp, 50, 0, 0.5, Aspect).V);
            Assert.True(middle < TextWarp.Map(WarpKind.ArchUp, 50, 1, 0.5, Aspect).V);

            var down = TextWarp.Map(WarpKind.ArchDown, 50, 0.5, 0.5, Aspect).V;
            Assert.True(down > TextWarp.Map(WarpKind.ArchDown, 50, 0, 0.5, Aspect).V);
            Assert.True(down > TextWarp.Map(WarpKind.ArchDown, 50, 1, 0.5, Aspect).V);
        }

        [Fact]
        public void Wave_QuartersMoveOpposite()
        {
            var first = TextWarp.Map(WarpKind.Wave, 60, 0.25, 0.5, Aspect).V - 0.5;
            var third = TextWarp.Map(WarpKind.Wave, 60, 0.75, 0.5, Aspect).V - 0.5;
            Assert.True(Math.Abs(first) > 0.01);
            Assert.True(first * third < 0);
        }

        private static double ColumnHeight(WarpKind kind, double u)
        {
            return TextWarp.Map(kind, 60, u, 1, Aspect).V - TextWarp.Map(kind, 60, u, 0, Aspect).V;
        }

        [Fact]
        public void Bulge_WidensMiddle_PinchNarrowsIt()
        {
            Assert.True(ColumnHeight(WarpKind.Bulge, 0.5) > 1.1);
            Assert.Equal(1, ColumnHeight(WarpKind.Bulge, 0), 6);
            Assert.True(ColumnHeight(WarpKind.Pinch, 0.5) < 0.9);
            // 가운데(v = 0.5)는 움직이지 않는다
            Assert.Equal(0.5, TextWarp.Map(WarpKind.Bulge, 60, 0.5, 0.5, Aspect).V, 9);
        }

        [Fact]
        public void Slant_TopGoesRight_BottomGoesLeft()
        {
            Assert.True(TextWarp.Map(WarpKind.Slant, 50, 0.5, 0, Aspect).U > 0.5);
            Assert.True(TextWarp.Map(WarpKind.Slant, 50, 0.5, 1, Aspect).U < 0.5);
            Assert.Equal(0.5, TextWarp.Map(WarpKind.Slant, 50, 0.5, 0.5, Aspect).U, 9);
        }

        [Fact]
        public void Stairs_HasFiveLevels()
        {
            var levels = Enumerable.Range(0, 101)
                .Select(i => Math.Round(TextWarp.Map(WarpKind.Stairs, 80, i / 100.0, 0.5, Aspect).V, 9))
                .Distinct()
                .ToList();
            Assert.Equal(5, levels.Count);
            // 왼쪽에서 오른쪽으로 내려간다(a > 0)
            Assert.Equal(levels.OrderBy(v => v), levels);
        }

        [Fact]
        public void Circle_FullTurn_BaselineEndsMeet_AtSameRadius()
        {
            var left = TextWarp.Map(WarpKind.Circle, 100, 0, 1, Aspect);
            var right = TextWarp.Map(WarpKind.Circle, 100, 1, 1, Aspect);
            // 폭 단위 좌표로 비교한다
            var dx = left.U - right.U;
            var dy = (left.V - right.V) * Aspect;
            Assert.True(Math.Sqrt(dx * dx + dy * dy) < 1e-6);

            var radius = 1 / (2 * Math.PI);
            var center = (X: 0.5, Y: Aspect / 2 + radius);
            double Distance((double U, double V) p) => Math.Sqrt(Math.Pow(p.U - center.X, 2) + Math.Pow(p.V * Aspect - center.Y, 2));
            var middle = TextWarp.Map(WarpKind.Circle, 100, 0.5, 1, Aspect);
            Assert.Equal(Distance(middle), Distance(left), 9);
            Assert.Equal(Distance(middle), Distance(right), 9);
        }

        [Fact]
        public void Circle_Negative_IsUpsideDown()
        {
            // 위쪽(양수)은 가운데가 양 끝보다 위, 아래쪽(음수)은 가운데가 양 끝보다 아래
            Assert.True(TextWarp.Map(WarpKind.Circle, 50, 0.5, 0.5, Aspect).V < TextWarp.Map(WarpKind.Circle, 50, 0.1, 0.5, Aspect).V);
            Assert.True(TextWarp.Map(WarpKind.Circle, -50, 0.5, 0.5, Aspect).V > TextWarp.Map(WarpKind.Circle, -50, 0.1, 0.5, Aspect).V);
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void IncreasingU_NeverFolds(WarpKind kind)
        {
            foreach (var amount in new[] { -100.0, -40, 40, 100 })
            {
                foreach (var v in new[] { 0, 0.5, 1 })
                {
                    var previous = double.NegativeInfinity;
                    for (var i = 0; i <= 100; i++)
                    {
                        var p = TextWarp.Map(kind, amount, i / 100.0, v, Aspect);
                        // 원은 가로 위치 대신 중심에서 본 각도가 늘어야 한다
                        var value = kind == WarpKind.Circle ? CircleAngle(amount, p, i / 100.0) : p.U;
                        Assert.True(value >= previous - 1e-9, kind + " " + amount + " v=" + v + " u=" + i / 100.0);
                        previous = value;
                    }
                }
            }
        }

        /// <summary>원 중심에서 본 점의 각도(풀린 값). 중심은 Map의 식과 같다.</summary>
        private static double CircleAngle(double amount, (double U, double V) p, double u)
        {
            var span = Math.Abs(amount) / 100 * 2 * Math.PI;
            var radius = 1 / span;
            var y = p.V * Aspect;
            var centerY = amount > 0 ? Aspect / 2 + radius : Aspect / 2 - radius;
            var angle = amount > 0 ? Math.Atan2(p.U - 0.5, centerY - y) : Math.Atan2(p.U - 0.5, y - centerY);
            // atan2는 ±π에서 돌아가므로, 기대 각도(φ = (u - 0.5)·span)에 가장 가까운 값으로 푼다
            var expected = (u - 0.5) * span;
            while (angle - expected > Math.PI)
                angle -= 2 * Math.PI;
            while (expected - angle > Math.PI)
                angle += 2 * Math.PI;
            return angle;
        }

        [Theory]
        [MemberData(nameof(AllKinds))]
        public void HostileInput_StaysFinite(WarpKind kind)
        {
            foreach (var amount in new[] { 1e9, -1e9, double.NaN, double.PositiveInfinity, 50 })
            {
                foreach (var aspect in new[] { 0, -1, double.NaN, double.PositiveInfinity, 1e-300, 1e300 })
                {
                    foreach (var (u, v) in new[] { (0.0, 0.0), (0.5, 0.5), (1.0, 1.0) })
                    {
                        var p = TextWarp.Map(kind, amount, u, v, aspect);
                        Assert.True(double.IsFinite(p.U) && double.IsFinite(p.V), kind + " " + amount + " " + aspect);
                    }
                }
            }
        }

        [Fact]
        public void Subdivide_ShortensEveryEdge_AndKeepsVertices()
        {
            var square = new List<(double U, double V)> { (0, 0), (1, 0), (1, 1), (0, 1) };
            var result = TextWarp.Subdivide(square, 0.07);
            for (var i = 0; i < result.Count; i++)
            {
                var p = result[i];
                var q = result[(i + 1) % result.Count];
                Assert.True(Math.Sqrt(Math.Pow(q.U - p.U, 2) + Math.Pow(q.V - p.V, 2)) <= 0.07 + 1e-12);
            }
            // 원래 꼭짓점이 순서대로 들어 있다
            var indexes = square.Select(v => result.IndexOf(v)).ToList();
            Assert.DoesNotContain(-1, indexes);
            Assert.Equal(indexes.OrderBy(i => i), indexes);
            Assert.Equal(0, indexes[0]);
        }

        [Fact]
        public void Subdivide_BadLength_CopiesPoints()
        {
            var points = new List<(double U, double V)> { (0, 0), (1, 0) };
            Assert.Equal(points, TextWarp.Subdivide(points, 0));
            Assert.Equal(points, TextWarp.Subdivide(points, double.NaN));
            Assert.Empty(TextWarp.Subdivide(new List<(double U, double V)>(), 0.1));
        }

        [Fact]
        public void Bounds_OfPoints()
        {
            Assert.Equal((-1.0, 0.0, 2.0, 3.0), TextWarp.Bounds(new[] { (0.0, 0.0), (2.0, 3.0), (-1.0, 1.0) }));
            Assert.Equal((0.0, 0.0, 0.0, 0.0), TextWarp.Bounds(Array.Empty<(double, double)>()));
        }
    }
}
