using System;

namespace VideoEditor.Core
{
    /// <summary>꾸민 글자 효과 계산(순수 함수 — 그리개와 시험이 함께 쓴다). 좌표는 글자 영역을 0~1로 정규화한 상자.</summary>
    public static class TextEffects
    {
        /// <summary>흐림 반지름 상한(출력 픽셀). 큰 그림(4K)에서 흐림이 오래 걸리지 않게 효과 배율을 곱한 뒤에 자른다.</summary>
        public const double MaxBlurRadius = 100;

        /// <summary>
        /// 선형 그라데이션의 시작·끝점. 0° = 왼쪽 → 오른쪽, 90° = 위 → 아래(시계 방향).
        /// 가운데를 지나 각도 방향으로, 상자를 그 방향에 투영한 길이만큼 늘린다 — 어느 각도든 첫 색·끝 색이 상자 모서리까지 닿는다(45°면 왼쪽 위 → 오른쪽 아래).
        /// </summary>
        public static (double StartX, double StartY, double EndX, double EndY) GradientPoints(double angleDeg)
        {
            var radians = (double.IsFinite(angleDeg) ? angleDeg : 0) * Math.PI / 180;
            var dx = Math.Round(Math.Cos(radians), 12);
            var dy = Math.Round(Math.Sin(radians), 12);
            var half = (Math.Abs(dx) + Math.Abs(dy)) / 2;
            return (0.5 - dx * half, 0.5 - dy * half, 0.5 + dx * half, 0.5 + dy * half);
        }

        /// <summary>
        /// 윤곽선이 글자 밖으로 번지는 거리(픽셀). 그리개는 두께 width × 2인 펜으로 채우기 전에 그려 바깥쪽 width만 보이게 하므로
        /// 둥글게·깎아서는 width, 각지게는 모서리 끝이 펜 두께 절반의 <see cref="StyledTextLayout.MiterLimit"/>배까지 나가 width × 2.
        /// </summary>
        public static double OutlineExtent(double width, LineJoinKind join)
        {
            var extent = double.IsFinite(width) && width > 0 ? width : 0;
            return join == LineJoinKind.Miter ? extent * StyledTextLayout.MiterLimit : extent;
        }

        /// <summary>
        /// 그림자가 글자에서 떨어진 거리(픽셀) → (dx, dy). 0° = 오른쪽, 90° = 아래(시계 방향 — 화면 좌표는 아래가 +y), dx = d·cos θ, dy = d·sin θ.
        /// 거리가 음수·NaN·무한대면 0, 각도가 NaN·무한대면 0°.
        /// </summary>
        public static (double X, double Y) ShadowOffset(double distance, double angleDeg)
        {
            var d = double.IsFinite(distance) && distance > 0 ? distance : 0;
            var radians = (double.IsFinite(angleDeg) ? angleDeg : 0) * Math.PI / 180;
            return (d * Math.Cos(radians), d * Math.Sin(radians));
        }

        /// <summary>그릴 흐림 반지름(픽셀): 흐림 × 효과 배율을 0~<see cref="MaxBlurRadius"/>로 자른다. 잘못된 값은 0.</summary>
        public static double BlurRadius(double blur, double effectScale)
        {
            var radius = blur * effectScale;
            return double.IsFinite(blur) && double.IsFinite(effectScale) && blur > 0 && effectScale > 0 ? Math.Min(MaxBlurRadius, radius) : 0;
        }

        /// <summary>정지점 위치: 0~1을 고르게 나눈다(색이 1개면 [0]).</summary>
        public static double[] GradientStops(int count)
        {
            if (count <= 1)
                return new[] { 0.0 };
            var stops = new double[count];
            for (var i = 0; i < count; i++)
                stops[i] = (double)i / (count - 1);
            return stops;
        }
    }
}
