using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VideoEditor.Core
{
    /// <summary>
    /// 아트 요소의 모양 종류. 저장은 이름(글)이라 순서를 바꿔도 되지만, 숫자로 읽는 일도 있어 새 종류는 끝에 더한다.
    /// 예전 판(1단계)은 모르는 종류를 사각형으로 읽는다(<see cref="LenientEnumConverter{T}"/>) — 프로젝트 형식은 그대로 3.
    /// </summary>
    public enum ArtKind
    {
        Rectangle,
        Ellipse,
        Triangle,
        Polygon,
        Star,
        Heart,
        Line,
        Arrow,
        // 2단계(#39): 말풍선
        Bubble,
        BubbleSquare,
        BubbleThought,
        // 강조 표시
        CircleMark,
        Underline,
        Highlighter,
        BoxMark,
        Sparkle,
        FocusLines,
        // 프레임·배경
        LowerThird,
        ScreenBorder,
        Plate
    }

    /// <summary>
    /// 아트 클립의 모양 값(길이는 프로젝트 픽셀). 꾸미기(채우기·윤곽선·그림자·광선·반사)는 클립의 <see cref="TextStyle"/>를 그대로 쓴다(글꼴·휘기는 쓰지 않음).
    /// 모양은 상자(Width×Height) 안에 그리고, 선·화살표는 상자 가운데 높이에서 왼쪽 끝 → 오른쪽 끝으로 긋는다.
    /// 말풍선·아래 자막 띠·배경 판은 클립의 글(<see cref="Clip.Text"/>·글자 크기·글자 색)을 같은 그림 안 글 상자(<see cref="ArtGeometry.TextArea"/>)에 그린다 —
    /// 글꼴은 스타일의 글꼴 값을 쓴다. 말풍선 꼬리는 상자 밖으로 나갈 수 있다(그림은 꼬리까지 넓어진다).
    /// </summary>
    public sealed class ArtShape
    {
        public const double MaxThickness = 500;
        /// <summary>말풍선 꼬리 끝이 상자 가운데에서 갈 수 있는 거리(상자 너비·높이의 배수).</summary>
        public const double MaxTail = 4;

        [JsonConverter(typeof(LenientEnumConverter<ArtKind>))]
        public ArtKind Kind { get; set; }
        public double Width { get; set; } = 400;
        public double Height { get; set; } = 300;
        /// <summary>사각형·각진 말풍선·테두리 상자·배경 판의 모서리 반경, 화면 테두리는 안쪽 모서리 반경(프로젝트 픽셀, 짧은 변의 절반까지).</summary>
        public double CornerRadius { get; set; }
        /// <summary>다각형의 변 수·별의 뿔 수(3~12).</summary>
        public int Sides { get; set; } = 5;
        /// <summary>별의 안쪽 꼭짓점 반지름 / 바깥 반지름, 집중선의 비운 가운데(상자에 내접한 타원의 비율)(0.1~0.95).</summary>
        public double InnerRatio { get; set; } = 0.5;
        /// <summary>선·화살표·동그라미 치기·밑줄·테두리 두께(프로젝트 픽셀).</summary>
        public double Thickness { get; set; } = 12;
        /// <summary>선·화살표를 점선으로(대시 = 두께 × 3, 사이 = 두께 × 2).</summary>
        public bool Dashed { get; set; }
        /// <summary>화살표 머리 크기(두께의 배수 — 머리 길이, 머리 너비는 그 1.2배).</summary>
        public double HeadSize { get; set; } = 3;
        /// <summary>말풍선 꼬리 끝: 상자 가운데에서 상자 너비의 몇 배(오른쪽 +) — 상자를 키우면 꼬리도 따라 커진다. 0이면 저장하지 않는다.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double TailX { get; set; }
        /// <summary>말풍선 꼬리 끝: 상자 가운데에서 상자 높이의 몇 배(아래 +).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double TailY { get; set; }

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
                case ArtKind.Bubble: return "둥근 말풍선";
                case ArtKind.BubbleSquare: return "각진 말풍선";
                case ArtKind.BubbleThought: return "생각 풍선";
                case ArtKind.CircleMark: return "동그라미 치기";
                case ArtKind.Underline: return "밑줄";
                case ArtKind.Highlighter: return "형광펜";
                case ArtKind.BoxMark: return "테두리 상자";
                case ArtKind.Sparkle: return "반짝임";
                case ArtKind.FocusLines: return "집중선";
                case ArtKind.LowerThird: return "아래 자막 띠";
                case ArtKind.ScreenBorder: return "화면 테두리";
                case ArtKind.Plate: return "배경 판";
                default: return "사각형";
            }
        }

        /// <summary>요소 탭의 묶음 이름(도형·말풍선·강조·프레임·배경).</summary>
        public static string Group(ArtKind kind)
        {
            if (IsBubble(kind))
                return "말풍선";
            switch (kind)
            {
                case ArtKind.CircleMark:
                case ArtKind.Underline:
                case ArtKind.Highlighter:
                case ArtKind.BoxMark:
                case ArtKind.Sparkle:
                case ArtKind.FocusLines:
                    return "강조";
                case ArtKind.LowerThird:
                case ArtKind.ScreenBorder:
                case ArtKind.Plate:
                    return "프레임·배경";
                default:
                    return "도형";
            }
        }

        /// <summary>꼬리가 있는 말풍선인지.</summary>
        public static bool IsBubble(ArtKind kind)
        {
            return kind == ArtKind.Bubble || kind == ArtKind.BubbleSquare || kind == ArtKind.BubbleThought;
        }

        /// <summary>안에 글을 그리는 모양인지(말풍선·아래 자막 띠·배경 판).</summary>
        public static bool HoldsText(ArtKind kind)
        {
            return IsBubble(kind) || kind == ArtKind.LowerThird || kind == ArtKind.Plate;
        }

        /// <summary>두께 값을 쓰는 모양인지(선·화살표·동그라미 치기·밑줄·테두리 상자·화면 테두리).</summary>
        public static bool UsesThickness(ArtKind kind)
        {
            switch (kind)
            {
                case ArtKind.Line:
                case ArtKind.Arrow:
                case ArtKind.CircleMark:
                case ArtKind.Underline:
                case ArtKind.BoxMark:
                case ArtKind.ScreenBorder:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>모서리 반경 값을 쓰는 모양인지.</summary>
        public static bool UsesCornerRadius(ArtKind kind)
        {
            switch (kind)
            {
                case ArtKind.Rectangle:
                case ArtKind.BubbleSquare:
                case ArtKind.BoxMark:
                case ArtKind.ScreenBorder:
                case ArtKind.Plate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 새 요소의 처음 모양: 선·화살표는 납작한 상자(400×60), 원·다각형·별·하트는 정사각형, 말풍선은 꼬리가 왼쪽 아래로,
        /// 집중선·화면 테두리는 1920×1080(넣을 때 화면 크기로 — <see cref="TimelineEdits.AddArt(Project, ArtKind, long, long, Track)"/>), 나머지는 400×300.
        /// </summary>
        public static ArtShape Default(ArtKind kind)
        {
            var shape = new ArtShape { Kind = kind };
            switch (kind)
            {
                case ArtKind.Line:
                case ArtKind.Arrow:
                    shape.Height = 60;
                    break;
                case ArtKind.Rectangle:
                case ArtKind.Triangle:
                    break;
                case ArtKind.Bubble:
                case ArtKind.BubbleSquare:
                    shape.Width = 480;
                    shape.Height = 300;
                    shape.CornerRadius = kind == ArtKind.BubbleSquare ? 24 : 0;
                    shape.TailX = -0.22;
                    shape.TailY = 0.85;
                    break;
                case ArtKind.BubbleThought:
                    shape.Width = 480;
                    shape.Height = 320;
                    shape.TailX = -0.3;
                    shape.TailY = 0.95;
                    break;
                case ArtKind.CircleMark:
                    shape.Width = 420;
                    shape.Height = 240;
                    shape.Thickness = 10;
                    break;
                case ArtKind.Underline:
                    shape.Width = 480;
                    shape.Height = 60;
                    shape.Thickness = 14;
                    break;
                case ArtKind.Highlighter:
                    shape.Width = 480;
                    shape.Height = 70;
                    break;
                case ArtKind.BoxMark:
                    shape.Width = 480;
                    shape.Height = 280;
                    shape.Thickness = 8;
                    shape.CornerRadius = 12;
                    break;
                case ArtKind.Sparkle:
                    shape.Width = shape.Height = 240;
                    break;
                case ArtKind.FocusLines:
                    shape.Width = 1920;
                    shape.Height = 1080;
                    shape.InnerRatio = 0.55;
                    break;
                case ArtKind.LowerThird:
                    shape.Width = 1000;
                    shape.Height = 140;
                    break;
                case ArtKind.ScreenBorder:
                    shape.Width = 1920;
                    shape.Height = 1080;
                    shape.Thickness = 24;
                    shape.CornerRadius = 40;
                    break;
                case ArtKind.Plate:
                    shape.Width = 800;
                    shape.Height = 450;
                    shape.CornerRadius = 24;
                    break;
                default:
                    shape.Height = shape.Width = 300;
                    break;
            }
            return shape;
        }

        /// <summary>글을 넣는 모양의 처음 글(배경 판은 비움 — 판만 깔 때가 많다). 다른 모양은 null.</summary>
        public static string DefaultText(ArtKind kind)
        {
            switch (kind)
            {
                case ArtKind.Bubble:
                case ArtKind.BubbleSquare:
                    return "안녕하세요!";
                case ArtKind.BubbleThought:
                    return "음…";
                case ArtKind.LowerThird:
                    return "이름 · 소개";
                case ArtKind.Plate:
                    return "";
                default:
                    return null;
            }
        }

        /// <summary>글을 넣는 모양의 처음 글자 색: 흰 말풍선에는 짙은 회색, 어두운 띠·판에는 흰색.</summary>
        public static string DefaultTextColor(ArtKind kind)
        {
            return IsBubble(kind) ? "#111827" : "#FFFFFF";
        }

        public ArtShape Clone()
        {
            return (ArtShape)MemberwiseClone();
        }

        /// <summary>
        /// 아트 요소 그림에 쓰는 꾸미기만 남긴 정규화한 사본: 채우기·윤곽선·그림자·광선·반사(글꼴·휘기는 기본값) —
        /// 화면에서 숨긴 값이 달라도 같은 그림이 다른 캐시 파일이 되지 않게. text면(글을 넣는 모양에 글이 있을 때) 글꼴 값(글꼴·굵게·기울임·자간·행간·정렬)도 남긴다.
        /// </summary>
        public static TextStyle UsedStyle(TextStyle style, bool text = false)
        {
            var source = style?.Clone() ?? new TextStyle();
            source.Normalize();
            var used = new TextStyle { Fill = source.Fill, Outline = source.Outline, Shadow = source.Shadow, Glow = source.Glow, Reflection = source.Reflection };
            if (text)
            {
                used.FontFamily = source.FontFamily;
                used.Bold = source.Bold;
                used.Italic = source.Italic;
                used.LetterSpacing = source.LetterSpacing;
                used.LineSpacing = source.LineSpacing;
                used.Align = source.Align;
            }
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
            TailX = Range(TailX, -MaxTail, MaxTail, 0);
            TailY = Range(TailY, -MaxTail, MaxTail, 0);
        }

        private static double Range(double value, double min, double max, double fallback)
        {
            return double.IsFinite(value) ? Math.Max(min, Math.Min(max, value)) : fallback;
        }
    }

    /// <summary>
    /// 아트 모양의 윤곽(순수 함수): 닫힌 다각형들(점은 상자 왼쪽 위 기준, 프로젝트 픽셀 × scale). 그리개는 이것을 채우고 윤곽선을 그린다.
    /// 곡선(둥근 모서리·원·하트)은 출력 픽셀 기준 약 2픽셀마다 점을 찍어 크게 그려도 각지지 않게 한다(점 수 상한 있음).
    /// 그리개는 조각을 모두 한 기하에 넣어 0이 아닌 감김 규칙으로 칠한다 — 조각이 여럿인 모양은 채울 조각을 화면에서 시계 방향(넓이 부호 +),
    /// 구멍을 반대 방향으로 둔다(말풍선 몸통과 꼬리는 겹쳐 합쳐지고, 테두리 상자의 안쪽은 빈다).
    /// </summary>
    public static class ArtGeometry
    {
        private const double Step = 2;
        private const int MaxCurvePoints = 720;
        private const int MaxDashes = 1000;
        /// <summary>집중선 줄 수.</summary>
        public const int FocusLineCount = 72;

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
                case ArtKind.Bubble:
                    result.Add(Ellipse(w, h));
                    Tail(result, w, h, s.TailX, s.TailY);
                    break;
                case ArtKind.BubbleSquare:
                    result.Add(Rectangle(w, h, s.CornerRadius * k));
                    Tail(result, w, h, s.TailX, s.TailY);
                    break;
                case ArtKind.BubbleThought:
                    Thought(result, w, h, s.TailX, s.TailY);
                    break;
                case ArtKind.CircleMark:
                    result.Add(CircleMark(w, h, s.Thickness * k));
                    break;
                case ArtKind.Underline:
                    result.Add(Underline(w, h, s.Thickness * k));
                    break;
                case ArtKind.Highlighter:
                {
                    // 비스듬히 깎은 펜 끝
                    var slant = Math.Min(w * 0.1, h * 0.35);
                    result.Add(new List<(double, double)> { (slant, 0), (w, 0), (w - slant, h), (0, h) });
                    break;
                }
                case ArtKind.BoxMark:
                    result.AddRange(Ring(w, h, s.Thickness * k, s.CornerRadius * k, true));
                    break;
                case ArtKind.ScreenBorder:
                    result.AddRange(Ring(w, h, s.Thickness * k, s.CornerRadius * k, false));
                    break;
                case ArtKind.Sparkle:
                    // 큰 반짝임 하나와 작은 것 둘(오른쪽 위·아래) — 모두 상자 안
                    result.Add(Sparkle(w * 0.4, h * 0.56, w * 0.4, h * 0.4));
                    result.Add(Sparkle(w * 0.82, h * 0.2, w * 0.16, h * 0.16));
                    result.Add(Sparkle(w * 0.86, h * 0.8, w * 0.11, h * 0.11));
                    break;
                case ArtKind.FocusLines:
                    result.AddRange(FocusLines(w, h, s.InnerRatio));
                    break;
                case ArtKind.LowerThird:
                {
                    // 왼쪽 강조 막대 + 틈 + 오른쪽 끝을 비스듬히 자른 띠
                    var (accent, gap, slant) = LowerThirdParts(w, h);
                    result.Add(new List<(double, double)> { (0, 0), (accent, 0), (accent, h), (0, h) });
                    result.Add(new List<(double, double)> { (accent + gap, 0), (w, 0), (Math.Max(accent + gap, w - slant), h), (accent + gap, h) });
                    break;
                }
                case ArtKind.Plate:
                    result.Add(Rectangle(w, h, s.CornerRadius * k));
                    break;
                default:
                    result.Add(Rectangle(w, h, s.CornerRadius * k));
                    break;
            }
            return result;
        }

        /// <summary>
        /// 글 상자(상자 좌표 × scale — 왼쪽 위·크기): 말풍선 몸통 안쪽, 아래 자막 띠의 띠 안쪽, 배경 판 안쪽. 글을 넣지 않는 모양은 null.
        /// 그리개는 이 너비에서 줄을 나누고 이 높이 가운데에 놓는다(글이 더 길면 넘친다 — 글자 상자와 같음).
        /// </summary>
        public static (double X, double Y, double Width, double Height)? TextArea(ArtShape shape, double scale)
        {
            var s = shape?.Clone() ?? new ArtShape();
            s.Normalize();
            if (!ArtShape.HoldsText(s.Kind))
                return null;
            var k = double.IsFinite(scale) && scale > 0 ? scale : 1;
            double w = s.Width * k, h = s.Height * k;
            var shorter = Math.Min(w, h);
            switch (s.Kind)
            {
                case ArtKind.Bubble:
                    // 타원에 내접하는 사각형(지름 / √2)보다 조금 안쪽
                    return Inset(w, h, w * 0.16, h * 0.16);
                case ArtKind.BubbleThought:
                    return Inset(w, h, w * 0.2, h * 0.2);
                case ArtKind.LowerThird:
                {
                    var (accent, gap, slant) = LowerThirdParts(w, h);
                    var pad = h * 0.12;
                    var left = accent + gap + pad;
                    return (left, pad, Math.Max(1e-3, w - slant - pad - left), Math.Max(1e-3, h - pad * 2));
                }
                default:
                {
                    // 각진 말풍선·배경 판: 둥근 모서리만큼 조금 더 안쪽
                    var pad = Math.Max(shorter * 0.08, Math.Min(s.CornerRadius * k, shorter / 2) * 0.3);
                    return Inset(w, h, pad, pad);
                }
            }
        }

        private static (double X, double Y, double Width, double Height) Inset(double w, double h, double padX, double padY)
        {
            return (padX, padY, Math.Max(1e-3, w - padX * 2), Math.Max(1e-3, h - padY * 2));
        }

        /// <summary>아래 자막 띠의 강조 막대 폭·틈·띠 오른쪽 끝을 비스듬히 자른 폭.</summary>
        private static (double Accent, double Gap, double Slant) LowerThirdParts(double w, double h)
        {
            return (Math.Min(w * 0.2, h * 0.12), Math.Min(w * 0.1, h * 0.08), Math.Min(w * 0.2, h * 0.35));
        }

        private static int Segments(double length, int min)
        {
            return Math.Max(min, Math.Min(MaxCurvePoints, (int)Math.Ceiling(length / Step)));
        }

        /// <summary>다각형의 넓이(신발끈 공식, 부호 있음) — 화면 좌표(y 아래)에서 시계 방향이면 +.</summary>
        public static double SignedArea(IReadOnlyList<(double X, double Y)> polygon)
        {
            double twice = 0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var (x1, y1) = polygon[i];
                var (x2, y2) = polygon[(i + 1) % polygon.Count];
                twice += x1 * y2 - x2 * y1;
            }
            return twice / 2;
        }

        /// <summary>채울 조각(넓이 부호 +)이 되게 — 반대면 점 순서를 뒤집는다.</summary>
        private static List<(double X, double Y)> Solid(List<(double X, double Y)> polygon)
        {
            if (SignedArea(polygon) < 0)
                polygon.Reverse();
            return polygon;
        }

        /// <summary>구멍 조각(넓이 부호 −)이 되게.</summary>
        private static List<(double X, double Y)> Hole(List<(double X, double Y)> polygon)
        {
            if (SignedArea(polygon) > 0)
                polygon.Reverse();
            return polygon;
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
            return Ellipse(w / 2, h / 2, w / 2, h / 2, 24);
        }

        /// <summary>가운데 (cx, cy), 반지름 a·b인 타원(시계 방향).</summary>
        private static List<(double X, double Y)> Ellipse(double cx, double cy, double a, double b, int min)
        {
            // 둘레(라마누잔 근사)
            var perimeter = Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b)));
            var n = Segments(perimeter, min);
            var points = new List<(double X, double Y)>(n);
            for (var i = 0; i < n; i++)
            {
                var t = 2 * Math.PI * i / n;
                points.Add((cx + a * Math.Cos(t), cy + b * Math.Sin(t)));
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

        /// <summary>말풍선 꼬리 끝(상자 좌표 × scale).</summary>
        public static (double X, double Y) TailTip(ArtShape shape, double scale)
        {
            var s = shape?.Clone() ?? new ArtShape();
            s.Normalize();
            var k = double.IsFinite(scale) && scale > 0 ? scale : 1;
            double w = s.Width * k, h = s.Height * k;
            return (w / 2 + s.TailX * w, h / 2 + s.TailY * h);
        }

        /// <summary>
        /// 말풍선 꼬리: 몸통 가운데의 밑변(몸통 안이라 보이지 않음)에서 꼬리 끝으로 가늘어지며 한쪽으로 살짝 휜 조각. 끝이 가운데면 없다.
        /// </summary>
        private static void Tail(List<List<(double X, double Y)>> result, double w, double h, double tailX, double tailY)
        {
            double cx = w / 2, cy = h / 2;
            var tip = (X: cx + tailX * w, Y: cy + tailY * h);
            double dx = tip.X - cx, dy = tip.Y - cy;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6)
                return;
            double ux = dx / length, uy = dy / length;
            // 밑변 반폭과 휜 정도(길이의 12% — 꼬리 방향의 오른쪽으로)
            var half = Math.Min(w, h) * 0.14;
            double nx = -uy, ny = ux;
            var bend = length * 0.12;
            var n = Math.Max(8, Math.Min(64, (int)Math.Ceiling(length / 8)));
            var points = new List<(double X, double Y)>(n * 2 + 1);
            void Side((double X, double Y) from, (double X, double Y) to, bool forward)
            {
                var control = ((from.X + to.X) / 2 - nx * bend, (from.Y + to.Y) / 2 - ny * bend);
                for (var i = forward ? 0 : 1; forward ? i < n : i <= n; i++)
                {
                    var t = (double)i / n;
                    var a = (1 - t) * (1 - t);
                    var b = 2 * (1 - t) * t;
                    var c = t * t;
                    points.Add((a * from.X + b * control.Item1 + c * to.X, a * from.Y + b * control.Item2 + c * to.Y));
                }
            }
            var left = (cx + nx * half, cy + ny * half);
            var right = (cx - nx * half, cy - ny * half);
            Side(left, tip, true);
            points.Add(tip);
            Side(tip, right, false);
            result.Add(Solid(points));
        }

        /// <summary>
        /// 생각 풍선: 몸통은 타원 둘레에 부푼 혹 10개(구름), 꼬리는 몸통에서 꼬리 끝으로 작아지는 동그라미 셋(마지막이 꼬리 끝).
        /// 혹 끝이 상자에 닿게 타원을 줄인다. 꼬리 끝이 몸통 안이면 동그라미가 없다.
        /// </summary>
        private static void Thought(List<List<(double X, double Y)>> result, double w, double h, double tailX, double tailY)
        {
            const double bump = 0.1;
            double cx = w / 2, cy = h / 2, a = w / 2 / (1 + bump), b = h / 2 / (1 + bump);
            var perimeter = Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b)));
            var n = Segments(perimeter * 1.3, 120);
            var cloud = new List<(double X, double Y)>(n);
            for (var i = 0; i < n; i++)
            {
                var t = 2 * Math.PI * i / n;
                var r = 1 + bump * Math.Abs(Math.Sin(5 * t));
                cloud.Add((cx + a * r * Math.Cos(t), cy + b * r * Math.Sin(t)));
            }
            result.Add(cloud);
            var tip = (X: cx + tailX * w, Y: cy + tailY * h);
            double dx = tip.X - cx, dy = tip.Y - cy;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-6)
                return;
            double ux = dx / length, uy = dy / length;
            // 그 방향의 몸통 가장자리(혹 바깥 끝)
            var edge = (1 + bump) / Math.Sqrt(ux * ux / (a * a) + uy * uy / (b * b));
            if (length <= edge)
                return;
            var shorter = Math.Min(w, h);
            foreach (var (along, radius) in new[] { (0.3, 0.09), (0.64, 0.065), (1.0, 0.045) })
            {
                var d = edge + (length - edge) * along;
                result.Add(Ellipse(cx + ux * d, cy + uy * d, shorter * radius, shorter * radius, 16));
            }
        }

        /// <summary>
        /// 동그라미 치기: 손으로 그린 듯 1.12바퀴를 도는 굵은 선(돌수록 반지름이 조금 줄어 겹친 끝이 어긋나고, 양 끝은 가늘다). 바깥 가장자리가 상자에 닿는다.
        /// 선 조각 하나라 스스로 겹치는 곳도 0이 아닌 감김 규칙으로 칠해진다.
        /// </summary>
        private static List<(double X, double Y)> CircleMark(double w, double h, double thickness)
        {
            var t = Math.Min(thickness, Math.Min(w, h) / 4);
            double a = w / 2 - t / 2, b = h / 2 - t / 2, cx = w / 2, cy = h / 2;
            const double turns = 1.12;
            const double shrink = 0.07;
            var start = 200 * Math.PI / 180;
            var sweep = 2 * Math.PI * turns;
            var n = Segments(Math.PI * (a + b) * turns, 64);
            return Stroke(n, u =>
            {
                var angle = start + sweep * u;
                var s = 1 - shrink * u;
                return (cx + a * s * Math.Cos(angle), cy + b * s * Math.Sin(angle));
            }, u => t / 2 * (0.3 + 0.7 * Math.Sqrt(Math.Sin(Math.PI * u))));
        }

        /// <summary>밑줄: 상자 가운데 높이를 지나는 완만한 붓 선(가운데가 조금 처지고 양 끝이 가늘다).</summary>
        private static List<(double X, double Y)> Underline(double w, double h, double thickness)
        {
            var t = Math.Min(thickness, h / 2);
            var amplitude = Math.Max(0, h / 2 - t / 2) * 0.5;
            var n = Segments(w, 32);
            return Stroke(n, u => (t / 2 + u * Math.Max(0, w - t), h / 2 + amplitude * (Math.Sin(Math.PI * u) - 0.5)),
                u => t / 2 * (0.35 + 0.65 * Math.Pow(Math.Sin(Math.PI * u), 0.35)));
        }

        /// <summary>
        /// 굵기가 바뀌는 선(중심선 path(u), u = 0..1, 반폭 half(u))의 윤곽: 왼쪽 가장자리를 앞으로, 오른쪽 가장자리를 뒤로 이은 조각 하나.
        /// 방향은 중심선의 이웃 점 차이로 구한다(점이 겹치면 앞 방향).
        /// </summary>
        private static List<(double X, double Y)> Stroke(int n, Func<double, (double X, double Y)> path, Func<double, double> half)
        {
            var centers = new (double X, double Y)[n + 1];
            for (var i = 0; i <= n; i++)
                centers[i] = path((double)i / n);
            var left = new List<(double X, double Y)>(n + 1);
            var right = new List<(double X, double Y)>(n + 1);
            double nx = 0, ny = -1;
            for (var i = 0; i <= n; i++)
            {
                var from = centers[Math.Max(0, i - 1)];
                var to = centers[Math.Min(n, i + 1)];
                double dx = to.X - from.X, dy = to.Y - from.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 1e-12)
                {
                    nx = -dy / length;
                    ny = dx / length;
                }
                var r = Math.Max(0, half((double)i / n));
                left.Add((centers[i].X + nx * r, centers[i].Y + ny * r));
                right.Add((centers[i].X - nx * r, centers[i].Y - ny * r));
            }
            right.Reverse();
            left.AddRange(right);
            return left;
        }

        /// <summary>
        /// 테두리(바깥 사각형 + 안쪽 구멍, 두께 t): rounded면 바깥 모서리가 radius이고 안쪽은 radius − t(테두리 상자),
        /// 아니면 바깥은 각지고 안쪽 모서리가 radius(화면 테두리 — 화면 가장자리는 꽉 채우고 안쪽만 둥글게). 두께가 짧은 변의 절반 이상이면 구멍이 없다.
        /// </summary>
        private static List<List<(double X, double Y)>> Ring(double w, double h, double thickness, double radius, bool rounded)
        {
            var t = Math.Min(thickness, Math.Min(w, h) / 2);
            var result = new List<List<(double X, double Y)>> { Solid(Rectangle(w, h, rounded ? radius : 0)) };
            var innerWidth = w - t * 2;
            var innerHeight = h - t * 2;
            if (innerWidth <= 1e-6 || innerHeight <= 1e-6)
                return result;
            var inner = Rectangle(innerWidth, innerHeight, rounded ? Math.Max(0, radius - t) : radius);
            for (var i = 0; i < inner.Count; i++)
                inner[i] = (inner[i].X + t, inner[i].Y + t);
            result.Add(Hole(inner));
            return result;
        }

        /// <summary>네 뿔 반짝임(오목한 변 — x = cos·|cos|^1.4, y = sin·|sin|^1.4), 가운데 (cx, cy), 반지름 rx·ry.</summary>
        private static List<(double X, double Y)> Sparkle(double cx, double cy, double rx, double ry)
        {
            var n = Segments(4 * (rx + ry), 64);
            var points = new List<(double X, double Y)>(n);
            for (var i = 0; i < n; i++)
            {
                var t = 2 * Math.PI * i / n;
                double c = Math.Cos(t), s = Math.Sin(t);
                points.Add((cx + rx * Math.Sign(c) * Math.Pow(Math.Abs(c), 2.4), cy + ry * Math.Sign(s) * Math.Pow(Math.Abs(s), 2.4)));
            }
            return points;
        }

        /// <summary>
        /// 집중선: 상자 가장자리에서 가운데로 뻗는 가는 쐐기 <see cref="FocusLineCount"/>개. 가운데의 타원(상자에 내접한 타원 × inner) 근처는 비우고,
        /// 쐐기마다 각도·굵기·길이를 조금씩 다르게(정해진 해시 — 늘 같은 모양).
        /// </summary>
        private static IEnumerable<List<(double X, double Y)>> FocusLines(double w, double h, double inner)
        {
            double cx = w / 2, cy = h / 2;
            (double X, double Y) Edge(double angle)
            {
                double c = Math.Cos(angle), s = Math.Sin(angle);
                var t = Math.Min(Math.Abs(c) > 1e-12 ? cx / Math.Abs(c) : double.MaxValue, Math.Abs(s) > 1e-12 ? cy / Math.Abs(s) : double.MaxValue);
                return (cx + c * t, cy + s * t);
            }
            for (var i = 0; i < FocusLineCount; i++)
            {
                var angle = 2 * Math.PI * (i + (Hash(i, 1) - 0.5) * 0.6) / FocusLineCount;
                var spread = Math.PI / FocusLineCount * (0.2 + 0.5 * Hash(i, 2));
                var reach = Math.Min(0.98, inner * (1 + 0.3 * Hash(i, 3)));
                var tip = (cx + cx * reach * Math.Cos(angle), cy + cy * reach * Math.Sin(angle));
                yield return Solid(new List<(double, double)> { Edge(angle - spread), Edge(angle + spread), tip });
            }
        }

        /// <summary>정수 해시로 만든 0~1 값(플랫폼과 상관없이 늘 같다).</summary>
        private static double Hash(int index, int salt)
        {
            unchecked
            {
                var x = (uint)index * 2654435761u ^ (uint)salt * 40503u;
                x ^= x >> 15;
                x *= 2246822519u;
                x ^= x >> 13;
                x *= 3266489917u;
                x ^= x >> 16;
                return x / 4294967296.0;
            }
        }
    }
}
