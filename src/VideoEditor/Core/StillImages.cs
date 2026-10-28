namespace VideoEditor.Core
{
    /// <summary>
    /// 효과의 한 장 그림(수식으로 한 번 그려 tpad로 늘이는 정지 그림 — 빛 띠·거리 지도·방향 지도·모양 표·잡음 판) 캐시.
    /// 같은 그림(그리는 글이 같으면 같은 그림)은 파일로 두고 다음 그래프부터 그 파일을 읽어(<see cref="EffectRender.Still"/>) 멈춘 화면 한 장 준비를 줄인다.
    /// 파일은 무손실(PAM)이라 읽은 값이 수식으로 그린 값과 같다.
    /// </summary>
    public interface IStillImageCache
    {
        /// <summary>그림의 캐시 파일 경로(있든 없든) — 그리는 글(generator)과 끝 형식(format)이 같으면 같은 경로.</summary>
        string PathOf(string generator, string format);

        /// <summary>캐시 파일이 있는지(있으면 사용 시각을 적는다 — 청소가 지우지 않게).</summary>
        bool Has(string path);
    }

    /// <summary>만들 한 장 그림: 그리는 글(color 소스부터 — 한 장)과 끝 픽셀 형식(gray·gray16·gbrp·gbrap).</summary>
    public sealed class StillImageRequest
    {
        public StillImageRequest(string generator, string format)
        {
            Generator = generator;
            Format = format;
        }

        public string Generator { get; }
        public string Format { get; }
    }
}
