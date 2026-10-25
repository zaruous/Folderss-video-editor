using System;

namespace VideoEditor.Core
{
    /// <summary>꾸민 글자 PNG의 크기와, 그 안에서 글자 배치 상자의 왼쪽 위 위치(픽셀).</summary>
    public readonly struct TextImageLayout
    {
        public TextImageLayout(int width, int height, double originX, double originY)
        {
            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
        }

        public int Width { get; }
        public int Height { get; }
        public double OriginX { get; }
        public double OriginY { get; }
    }

    /// <summary>
    /// 꾸민 글자 PNG의 크기 계산(순수 함수). 효과(윤곽선·그림자·광선·반사)가 글자 배치 상자 밖으로 번지는 만큼 여백을 두는데,
    /// 좌우·위아래를 큰 쪽에 맞춰 대칭으로 둔다 — 그래서 PNG 가운데 = 배치 상자 가운데이고, 그래프는 PNG를 캔버스 가운데에 놓기만 하면 된다.
    /// 길이 값은 스타일(프로젝트 픽셀) × effectScale, 상자 크기는 출력 픽셀.
    /// </summary>
    public static class StyledTextLayout
    {
        /// <summary>각진 이음의 끝이 늘어나는 한도(펜 두께 절반의 배수 — 펜은 윤곽선 두께의 두 배). 그리개의 Pen.MiterLimit도 이 값을 쓴다.</summary>
        public const double MiterLimit = 2;
        /// <summary>안티에일리어싱 가장자리가 잘리지 않게 두는 여백(픽셀).</summary>
        public const double EdgeMargin = 1;
        /// <summary>그리개가 그리는 그림의 화소 상한(4096² 정도). 캔버스가 더 크면 렌더 그래프가 줄여 요청하고 키워 얹는다.</summary>
        public const double MaxPixels = 16000000;

        /// <summary>boxWidth×boxHeight 배치 상자를 가운데에 둔 PNG 크기(짝수, 캔버스 이하)와 상자 원점.</summary>
        public static TextImageLayout Fit(double boxWidth, double boxHeight, TextStyle style, double effectScale, int canvasWidth, int canvasHeight)
        {
            boxWidth = Length(boxWidth);
            boxHeight = Length(boxHeight);
            var spread = Spread(style, effectScale, boxHeight);
            var width = Size(boxWidth + 2 * (spread.Horizontal + EdgeMargin), canvasWidth);
            var height = Size(boxHeight + 2 * (spread.Vertical + EdgeMargin), canvasHeight);
            return new TextImageLayout(width, height, (width - boxWidth) / 2, (height - boxHeight) / 2);
        }

        /// <summary>효과가 배치 상자 밖으로 번지는 거리(픽셀): 좌우 중 큰 쪽, 위아래 중 큰 쪽.</summary>
        public static (double Horizontal, double Vertical) Spread(TextStyle style, double effectScale, double boxHeight)
        {
            if (style == null)
                return (0, 0);
            var scale = Length(effectScale);
            boxHeight = Length(boxHeight);
            var outline = 0.0;
            // 윤곽선은 글자 밖으로 두께만큼, 각진 이음은 모서리에서 한도만큼 더 나간다
            if (style.Outline != null && style.Outline.Enabled)
                outline = TextEffects.OutlineExtent(Length(style.Outline.Width) * scale, style.Outline.Join);
            double left = outline, right = outline, top = outline, bottom = outline;
            if (style.Shadow != null && style.Shadow.Enabled)
            {
                // 그림자 = 윤곽선까지 그린 글자를 옮겨 흐린 것 — 그리개와 같은 거리·흐림 반지름(상한 포함)
                var offset = ShadowOffset(style.Shadow, scale);
                var blur = TextEffects.BlurRadius(style.Shadow.Blur, scale);
                left = Math.Max(left, outline + Math.Max(0, -offset.X) + blur);
                right = Math.Max(right, outline + Math.Max(0, offset.X) + blur);
                top = Math.Max(top, outline + Math.Max(0, -offset.Y) + blur);
                bottom = Math.Max(bottom, outline + Math.Max(0, offset.Y) + blur);
            }
            if (style.Glow != null && style.Glow.Enabled)
            {
                // 광선 = 크기만큼 넓힌 윤곽 + 같은 반경의 흐림
                var glow = outline + 2 * Length(style.Glow.Size) * scale;
                left = Math.Max(left, glow);
                right = Math.Max(right, glow);
                top = Math.Max(top, glow);
                bottom = Math.Max(bottom, glow);
            }
            if (style.Reflection != null && style.Reflection.Enabled)
            {
                // 반사 = 글자(윤곽선 포함) 아래 간격을 두고 높이의 Size%만큼 — 그리개와 같은 계산(ReflectionRect).
                // 그리개가 넘기는 상자는 글자 모양을 다 담게 넓힌 것이고 윤곽선은 그 밖으로 outline까지 나가므로, 가장 크고 낮은 글자 = 상자를 위아래로 outline만큼 넓힌 것
                var reflection = ReflectionRect(style.Reflection, scale, (0, -outline, 0, boxHeight + 2 * outline));
                bottom = Math.Max(bottom, reflection.Top + reflection.Height - boxHeight);
            }
            return (Math.Max(left, right), Math.Max(top, bottom));
        }

        /// <summary>
        /// 반사가 놓이는 사각형(픽셀): 간격 × 효과 배율을 <see cref="TextEffects.ReflectionRect"/>로(글자 아래 간격, 높이 = 글자 높이 × 크기%).
        /// glyph는 글자(본문이 칠하는 모양) 경계 상자(픽셀).
        /// </summary>
        public static (double Left, double Top, double Width, double Height) ReflectionRect(TextReflection reflection, double effectScale, (double Left, double Top, double Width, double Height) glyph)
        {
            if (reflection == null)
                return TextEffects.ReflectionRect(glyph, 0, 0);
            return TextEffects.ReflectionRect(glyph, reflection.Size, Length(reflection.Gap) * Length(effectScale));
        }

        /// <summary>그림자가 글자에서 떨어진 거리(픽셀): 거리 × 효과 배율을 <see cref="TextEffects.ShadowOffset"/>로(0° = 오른쪽, 시계 방향).</summary>
        public static (double X, double Y) ShadowOffset(TextShadow shadow, double effectScale)
        {
            if (shadow == null)
                return (0, 0);
            return TextEffects.ShadowOffset(Length(shadow.Distance) * Length(effectScale), shadow.Angle);
        }

        /// <summary>필요한 길이 → 짝수로 올린 정수(2 이상), 캔버스보다 크면 캔버스.</summary>
        private static int Size(double needed, int canvas)
        {
            canvas = Math.Max(1, canvas);
            // NaN·무한대도 여기서 캔버스로
            if (!(needed < canvas))
                return canvas;
            var size = (int)Math.Ceiling(needed);
            size += size % 2;
            return Math.Min(canvas, Math.Max(2, size));
        }

        /// <summary>길이로 쓸 수 없는 값(음수·NaN·무한대)은 0.</summary>
        private static double Length(double value)
        {
            return double.IsFinite(value) && value > 0 ? value : 0;
        }
    }
}
