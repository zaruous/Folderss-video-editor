using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VideoEditor.Core
{
    /// <summary>
    /// 꾸민 글자를 투명 배경 PNG로 그리는 것. Core에는 인터페이스만 두고 WPF 구현은 Ui에 둔다(Core·Media 시험은 가짜 그리개로).
    /// </summary>
    public interface ITextRasterizer
    {
        /// <summary>그리는 방식이 바뀌면 올린다 — 캐시 키에 들어가므로 예전 PNG를 다시 쓰지 않는다.</summary>
        int Version { get; }

        /// <summary>
        /// request를 pngPath에 투명 배경 PNG로 그린다. PNG 크기는 캔버스 이하, 가운데 = 글자 배치 상자 가운데(<see cref="StyledTextLayout"/>) —
        /// 글자 상자(WrapWidth·BoxHeight)를 정했으면 그 상자, 아니면 글을 감싼 상자.
        /// </summary>
        void Render(TextImageRequest request, string pngPath, CancellationToken cancellation);
    }

    /// <summary>꾸민 글자 PNG 한 장을 그릴 값. 같은 키면 같은 픽셀이다.</summary>
    public sealed class TextImageRequest
    {
        public string Text { get; set; }
        /// <summary>정규화한 깊은 복사(클립의 스타일을 그대로 쥐지 않는다).</summary>
        public TextStyle Style { get; set; }
        /// <summary>출력 픽셀 글자 크기 = Clip.FontSize × 출력 폭 / 프로젝트 폭.</summary>
        public double FontPixelSize { get; set; }
        /// <summary>출력 폭 / 프로젝트 폭. 스타일의 길이 값(그림자 거리·윤곽 두께 등 프로젝트 픽셀)에 곱한다.</summary>
        public double EffectScale { get; set; }
        /// <summary>출력 크기. PNG가 이보다 크면 그래프에서 잘린다.</summary>
        public int CanvasWidth { get; set; }
        public int CanvasHeight { get; set; }
        /// <summary>글자 상자 너비(출력 픽셀 = Clip.TextBoxWidth × 출력 배율). 0이면 줄을 나누지 않고 상자 폭 = 가장 긴 줄.</summary>
        public double WrapWidth { get; set; }
        /// <summary>글자 상자 높이(출력 픽셀 = Clip.TextBoxHeight × 출력 배율). 0이면 상자 높이 = 글 높이.</summary>
        public double BoxHeight { get; set; }
        /// <summary>상자가 글보다 높을 때 글을 놓는 자리.</summary>
        public TextVerticalAlign VerticalAlign { get; set; }
        /// <summary>
        /// 아트 요소의 모양(정규화한 사본). 있으면 글 대신 이 모양(<see cref="ArtGeometry"/>, 길이 × <see cref="EffectScale"/>)을 같은 꾸미기로 그린다 —
        /// PNG 가운데 = 모양 상자 가운데. 글자 요청은 null(키가 예전과 같아 이미 그린 글자 PNG를 그대로 쓴다).
        /// </summary>
        public ArtShape Art { get; set; }
        /// <summary>
        /// 단어·글자별 애니메이션의 한 장(<see cref="TextAnimationFrame"/>). 있으면 그 장의 단위 모습으로 그리고, 한 클립의 모든 장을 같은 크기로 그린다.
        /// 정지 그림은 null(키가 예전과 같다).
        /// </summary>
        public TextAnimationFrame Animation { get; set; }

        /// <summary>애니메이션 한 장의 요청(나머지 값은 같이 쓴다).</summary>
        public TextImageRequest WithAnimation(TextAnimationFrame animation)
        {
            var copy = (TextImageRequest)MemberwiseClone();
            copy.Animation = animation;
            return copy;
        }

        /// <summary>애니메이션 묶음의 키: 장 번호만 빼고 <see cref="Key"/>와 같다 — 한 클립의 장들이 같은 이름 앞부분을 쓴다(그림 묶음 입력).</summary>
        public string JobKey(int rasterizerVersion)
        {
            return Key(rasterizerVersion, false);
        }

        /// <summary>
        /// 캐시 키: 픽셀에 영향을 주는 값을 정해진 순서의 JSON으로 적고 SHA-256(소문자 16진 64자)으로.
        /// 실수는 InvariantCulture "R"(문화권과 무관), 글은 UTF-16 단위를 그대로 \u로 적는다(짝 없는 대리 문자도 구별).
        /// </summary>
        public string Key(int rasterizerVersion)
        {
            return Key(rasterizerVersion, true);
        }

        private string Key(int rasterizerVersion, bool frame)
        {
            var style = Style?.Clone() ?? new TextStyle();
            style.Normalize();
            var json = new StringBuilder();
            json.Append('{');
            Number(json, "version", rasterizerVersion);
            Quoted(json, "text", Text ?? "");
            Number(json, "fontPixelSize", FontPixelSize);
            Number(json, "effectScale", EffectScale);
            Number(json, "canvasWidth", CanvasWidth);
            Number(json, "canvasHeight", CanvasHeight);
            Number(json, "wrapWidth", WrapWidth);
            Number(json, "boxHeight", BoxHeight);
            Quoted(json, "verticalAlign", VerticalAlign.ToString());
            Quoted(json, "fontFamily", style.FontFamily);
            Flag(json, "bold", style.Bold);
            Flag(json, "italic", style.Italic);
            Number(json, "letterSpacing", style.LetterSpacing);
            Number(json, "lineSpacing", style.LineSpacing);
            Quoted(json, "align", style.Align.ToString());
            Quoted(json, "fill.kind", style.Fill.Kind.ToString());
            Quoted(json, "fill.colors", string.Join(",", style.Fill.Colors));
            Number(json, "fill.angle", style.Fill.Angle);
            Flag(json, "outline.enabled", style.Outline.Enabled);
            Quoted(json, "outline.color", style.Outline.Color);
            Number(json, "outline.width", style.Outline.Width);
            Quoted(json, "outline.join", style.Outline.Join.ToString());
            Flag(json, "shadow.enabled", style.Shadow.Enabled);
            Quoted(json, "shadow.color", style.Shadow.Color);
            Number(json, "shadow.distance", style.Shadow.Distance);
            Number(json, "shadow.angle", style.Shadow.Angle);
            Number(json, "shadow.blur", style.Shadow.Blur);
            Number(json, "shadow.opacity", style.Shadow.Opacity);
            Flag(json, "glow.enabled", style.Glow.Enabled);
            Quoted(json, "glow.color", style.Glow.Color);
            Number(json, "glow.size", style.Glow.Size);
            Number(json, "glow.opacity", style.Glow.Opacity);
            Flag(json, "reflection.enabled", style.Reflection.Enabled);
            Number(json, "reflection.size", style.Reflection.Size);
            Number(json, "reflection.gap", style.Reflection.Gap);
            Number(json, "reflection.opacity", style.Reflection.Opacity);
            Quoted(json, "warp.kind", style.Warp.Kind.ToString());
            Number(json, "warp.amount", style.Warp.Amount);
            if (Art != null)
            {
                var art = Art.Clone();
                art.Normalize();
                Quoted(json, "art.kind", art.Kind.ToString());
                Number(json, "art.width", art.Width);
                Number(json, "art.height", art.Height);
                Number(json, "art.cornerRadius", art.CornerRadius);
                Number(json, "art.sides", art.Sides);
                Number(json, "art.innerRatio", art.InnerRatio);
                Number(json, "art.thickness", art.Thickness);
                Flag(json, "art.dashed", art.Dashed);
                Number(json, "art.headSize", art.HeadSize);
            }
            Animation?.AppendKey(json, frame);
            json.Length--;
            json.Append('}');
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void Name(StringBuilder json, string name)
        {
            json.Append('"').Append(name).Append("\":");
        }

        internal static void Number(StringBuilder json, string name, double value)
        {
            Name(json, name);
            json.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        }

        internal static void Flag(StringBuilder json, string name, bool value)
        {
            Name(json, name);
            json.Append(value ? "true" : "false").Append(',');
        }

        internal static void Quoted(StringBuilder json, string name, string value)
        {
            Name(json, name);
            json.Append('"');
            foreach (var ch in value)
            {
                if (ch >= 0x20 && ch < 0x7F && ch != '"' && ch != '\\')
                    json.Append(ch);
                else
                    json.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            }
            json.Append("\",");
        }
    }
}
