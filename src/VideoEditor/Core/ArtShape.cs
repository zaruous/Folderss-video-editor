using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>아트 요소(도형·선·화살표)의 모양 종류.</summary>
    public enum ArtKind
    {
        Rectangle,
        Ellipse,
        Triangle,
        Polygon,
        Star,
        Heart,
        Line,
        Arrow
    }

    /// <summary>
    /// 아트 클립의 모양 값(길이는 프로젝트 픽셀). 꾸미기(채우기·윤곽선·그림자·광선·반사)는 클립의 <see cref="TextStyle"/>를 그대로 쓴다(글꼴·휘기는 쓰지 않음).
    /// 모양은 상자(Width×Height) 안에 그리고, 선·화살표는 상자 가운데 높이에서 왼쪽 끝 → 오른쪽 끝으로 긋는다.
    /// </summary>
    public sealed class ArtShape
    {
        public const double MaxThickness = 500;

        [JsonConverter(typeof(LenientEnumConverter<ArtKind>))]
        public ArtKind Kind { get; set; }
        public double Width { get; set; } = 400;
        public double Height { get; set; } = 300;
        /// <summary>사각형의 모서리 반경(프로젝트 픽셀, 짧은 변의 절반까지).</summary>
        public double CornerRadius { get; set; }
        /// <summary>다각형의 변 수·별의 뿔 수(3~12).</summary>
        public int Sides { get; set; } = 5;
        /// <summary>별의 안쪽 꼭짓점 반지름 / 바깥 반지름(0.1~0.95).</summary>
        public double InnerRatio { get; set; } = 0.5;
        /// <summary>선·화살표 두께(프로젝트 픽셀).</summary>
        public double Thickness { get; set; } = 12;
        /// <summary>선·화살표를 점선으로(대시 = 두께 × 3, 사이 = 두께 × 2).</summary>
        public bool Dashed { get; set; }
        /// <summary>화살표 머리 크기(두께의 배수 — 머리 길이, 머리 너비는 그 1.2배).</summary>
        public double HeadSize { get; set; } = 3;

        /// <summary>요소 탭·타임라인에 보이는 이름.</summary>
        public static string DisplayName(ArtKind kind)
        {
            switch (kind)
            {
                case ArtKind.Ellipse: return "원";
                case ArtKind.Triangle: return "삼각형";
                case ArtKind.Polygon: return "다각형";
                case ArtKind.Star: return "별";
                case ArtKind.Heart: return "하트";
                case ArtKind.Line: return "선";
                case ArtKind.Arrow: return "화살표";
                default: return "사각형";
            }
        }

        /// <summary>새 요소의 처음 모양: 선·화살표는 납작한 상자(400×60), 원·다각형·별·하트는 정사각형, 나머지는 400×300.</summary>
        public static ArtShape Default(ArtKind kind)
        {
            var shape = new ArtShape { Kind = kind };
            if (kind == ArtKind.Line || kind == ArtKind.Arrow)
                shape.Height = 60;
            else if (kind != ArtKind.Rectangle && kind != ArtKind.Triangle)
                shape.Height = shape.Width = 300;
            return shape;
        }

        public ArtShape Clone()
        {
            return (ArtShape)MemberwiseClone();
        }

        /// <summary>
        /// 아트 요소 그림에 쓰는 꾸미기만 남긴 정규화한 사본: 채우기·윤곽선·그림자·광선·반사(글꼴·휘기는 기본값) —
        /// 화면에서 숨긴 값이 달라도 같은 그림이 다른 캐시 파일이 되지 않게.
        /// </summary>
        public static TextStyle UsedStyle(TextStyle style)
        {
            var source = style?.Clone() ?? new TextStyle();
            source.Normalize();
            var used = new TextStyle { Fill = source.Fill, Outline = source.Outline, Shadow = source.Shadow, Glow = source.Glow, Reflection = source.Reflection };
            used.Normalize();
            return used;
        }

        /// <summary>속성 패널 [꾸미기] 스타일 모음·내 스타일의 도형 견본(둥근 사각형) — 스타일만 바꿔 그린다.</summary>
        public static ArtShape StyleSample()
        {
            return new ArtShape { Kind = ArtKind.Rectangle, Width = 150, Height = 100, CornerRadius = 24 };
        }

        /// <summary>남이 만든 파일의 값도 그릴 수 있는 범위로(NaN·무한대는 기본값).</summary>
        public void Normalize()
        {
            if (!Enum.IsDefined(Kind))
                Kind = ArtKind.Rectangle;
            Width = Range(Width, 1, ClipLayout.MaxTextBox, 400);
            Height = Range(Height, 1, ClipLayout.MaxTextBox, 300);
            CornerRadius = Range(CornerRadius, 0, ClipLayout.MaxTextBox / 2, 0);
            Sides = Math.Max(3, Math.Min(12, Sides));
            InnerRatio = Range(InnerRatio, 0.1, 0.95, 0.5);
            Thickness = Range(Thickness, 1, MaxThickness, 12);
            HeadSize = Range(HeadSize, 1.5, 10, 3);
        }

        private static double Range(double value, double min, double max, double fallback)
        {
            return double.IsFinite(value) ? Math.Max(min, Math.Min(max, value)) : fallback;
        }
    }

    /// <summary>
    /// 아트 모양의 윤곽(순수 함수): 닫힌 다각형들(점은 상자 왼쪽 위 기준, 프로젝트 픽셀 × scale). 그리개는 이것을 채우고 윤곽선을 그린다.
    /// 곡선(둥근 모서리·원·하트)은 출력 픽셀 기준 약 2픽셀마다 점을 찍어 크게 그려도 각지지 않게 한다(점 수 상한 있음).
    /// </summary>
    public static class ArtGeometry
    {
        private const double Step = 2;
        private const int MaxCurvePoints = 720;
        private const int MaxDashes = 1000;

        public static List<List<(double X, double Y)>> Outline(ArtShape shape, double scale)
        {
            var s = shape?.Clone() ?? new ArtShape();
            s.Normalize();
            var k = double.IsFinite(scale) && scale > 0 ? scale : 1;
            double w = s.Width * k, h = s.Height * k;
            var result = new List<List<(double X, double Y)>>();
            switch (s.Kind)
            {
                case ArtKind.Ellipse:
                    result.Add(Ellipse(w, h));
                    break;
                case ArtKind.Triangle:
                    result.Add(new List<(double, double)> { (w / 2, 0), (w, h), (0, h) });
                    break;
                case ArtKind.Polygon:
                    result.Add(Star(w, h, s.Sides, 1));
                    break;
                case ArtKind.Star:
                    result.Add(Star(w, h, s.Sides, s.InnerRatio));
                    break;
                case ArtKind.Heart:
                    result.Add(Heart(w, h));
                    break;
                case ArtKind.Line:
                case ArtKind.Arrow:
                    result.AddRange(Line(w, h, s.Thickness * k, s.Dashed, s.Kind == ArtKind.Arrow ? s.HeadSize : 0));
                    break;
                default:
                    result.Add(Rectangle(w, h, s.CornerRadius * k));
                    break;
            }
            return result;
        }

        private static int Segments(double length, int min)
        {
            return Math.Max(min, Math.Min(MaxCurvePoints, (int)Math.Ceiling(length / Step)));
        }

        private static List<(double X, double Y)> Rectangle(double w, double h, double radius)
        {
            var r = Math.Min(radius, Math.Min(w, h) / 2);
            if (r <= 0)
                return new List<(double, double)> { (0, 0), (w, 0), (w, h), (0, h) };
            var n = Segments(r * Math.PI / 2, 2);
            var points = new List<(double X, double Y)>();
            // 모서리 가운데: 오른쪽 위 → 오른쪽 아래 → 왼쪽 아래 → 왼쪽 위(시계 방향, y는 아래로)
            var corners = new[] { (w - r, r, -90.0), (w - r, h - r, 0.0), (r, h - r, 90.0), (r, r, 180.0) };
            foreach (var (cx, cy, start) in corners)
            {
                for (var i = 0; i <= n; i++)
                {
                    var a = (start + 90.0 * i / n) * Math.PI / 180;
                    points.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
                }
            }
            return points;
        }

        private static List<(double X, double Y)> Ellipse(double w, double h)
        {
            double a = w / 2, b = h / 2;
            // 둘레(라마누잔 근사)
            var perimeter = Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b)));
            var n = Segments(perimeter, 24);
            var points = new List<(double X, double Y)>(n);
            for (var i = 0; i < n; i++)
            {
                var t = 2 * Math.PI * i / n;
                points.Add((a + a * Math.Cos(t), b + b * Math.Sin(t)));
            }
            return points;
        }

        /// <summary>상자에 내접하는 타원 위 정다각형(inner = 1) 또는 별(바깥·안쪽 꼭짓점 번갈아). 첫 꼭짓점은 위 가운데.</summary>
        private static List<(double X, double Y)> Star(double w, double h, int points, double inner)
        {
            var count = inner >= 1 ? points : points * 2;
            var result = new List<(double X, double Y)>(count);
            for (var i = 0; i < count; i++)
            {
                var r = inner >= 1 || i % 2 == 0 ? 1 : inner;
                var a = -Math.PI / 2 + 2 * Math.PI * i / count;
                result.Add((w / 2 + w / 2 * r * Math.Cos(a), h / 2 + h / 2 * r * Math.Sin(a)));
            }
            return result;
        }

        /// <summary>하트 곡선(x = 16 sin³t, y = 13 cos t − 5 cos 2t − 2 cos 3t − cos 4t)을 상자에 꽉 차게.</summary>
        private static List<(double X, double Y)> Heart(double w, double h)
        {
            var n = Segments(Math.PI * (w + h) / 2 * 1.2, 48);
            var raw = new List<(double X, double Y)>(n);
            for (var i = 0; i < n; i++)
            {
                var t = 2 * Math.PI * i / n;
                var sin = Math.Sin(t);
                raw.Add((16 * sin * sin * sin, -(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t))));
            }
            var (minX, minY, maxX, maxY) = TextWarp.Bounds(raw);
            var result = new List<(double X, double Y)>(n);
            foreach (var (x, y) in raw)
                result.Add(((x - minX) / (maxX - minX) * w, (y - minY) / (maxY - minY) * h));
            return result;
        }

        /// <summary>
        /// 선(head = 0) 또는 화살표(head = 머리 크기, 두께의 배수): 상자 가운데 높이에서 0 → w. 점선이면 대시마다 사각형, 화살표 머리는 따로 한 삼각형.
        /// 점선은 대시가 <see cref="MaxDashes"/>개를 넘지 않게 무늬를 늘린다.
        /// </summary>
        private static List<List<(double X, double Y)>> Line(double w, double h, double thickness, bool dashed, double head)
        {
            var t = Math.Min(thickness, h);
            var y = h / 2;
            var result = new List<List<(double X, double Y)>>();
            var headLength = head > 0 ? Math.Min(w, head * t) : 0;
            var headHalf = Math.Min(h / 2, head * t * 0.6);
            var shaftEnd = w - headLength;
            if (!dashed && head > 0)
            {
                result.Add(new List<(double, double)>
                {
                    (0, y - t / 2), (shaftEnd, y - t / 2), (shaftEnd, y - headHalf), (w, y),
                    (shaftEnd, y + headHalf), (shaftEnd, y + t / 2), (0, y + t / 2)
                });
                return result;
            }
            if (!dashed)
            {
                result.Add(Bar(0, shaftEnd, y, t));
                return result;
            }
            var unit = Math.Max(t, shaftEnd / (MaxDashes * 5.0));
            for (var x = 0.0; x < shaftEnd; x += unit * 5)
                result.Add(Bar(x, Math.Min(shaftEnd, x + unit * 3), y, t));
            if (head > 0)
                result.Add(new List<(double, double)> { (shaftEnd, y - headHalf), (w, y), (shaftEnd, y + headHalf) });
            return result;
        }

        private static List<(double X, double Y)> Bar(double from, double to, double y, double t)
        {
            return new List<(double, double)> { (from, y - t / 2), (to, y - t / 2), (to, y + t / 2), (from, y + t / 2) };
        }
    }
}
