using System;

namespace VideoEditor.Core
{
    /// <summary>꾸민 글자 효과 계산(순수 함수 — 그리개와 시험이 함께 쓴다). 좌표는 글자 영역을 0~1로 정규화한 상자.</summary>
    public static class TextEffects
    {
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
