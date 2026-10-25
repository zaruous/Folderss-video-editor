namespace VideoEditor.Core
{
    /// <summary>
    /// 인물 마스크 제공자: 원본 파일에서 사람만 따낸 흑백 마스크(흰색 = 사람). 렌더 그래프는 이것으로 효과를 사람 위에만 칠한다.
    /// 마스크는 원본과 같은 시간축(원본 처음 = 0)의 흑백 동영상, 이미지 원본이면 흑백 PNG 한 장이다. 크기는 원본보다 작아도 된다(그래프가 맞춘다).
    /// </summary>
    public interface IPersonMaskProvider
    {
        /// <summary>원본의 마스크가 준비돼 있으면 그 파일 경로, 없으면 null. 디스크 확인만 하고 빨리 돌아온다(만들지 않음).</summary>
        string Find(string sourcePath);
    }
}
