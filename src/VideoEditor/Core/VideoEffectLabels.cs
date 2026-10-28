using System.Globalization;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 타임라인 클립에 동영상 효과를 알리는 글(#34): 클립 오른쪽 위 배지와 화면 낭독 이름. 효과는 그리는 차례(목록 차례 — 뒤 효과가 위)로,
    /// 모르는 효과(새 판에서 만든 것 — 남겨 두고 그리지 않음)도 센다(속성 패널 [효과] 탭에 "알 수 없는 효과"로 보이는 것과 같게).
    /// </summary>
    public static class VideoEffectLabels
    {
        /// <summary>배지 글: 효과 하나면 "✦", 둘 이상이면 "✦" + 수. 효과를 넣을 수 없는 클립이거나 효과가 없으면 null.</summary>
        public static string Badge(Clip clip)
        {
            var count = Count(clip);
            return count == 0 ? null : count == 1 ? "✦" : "✦" + count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>화면 낭독 이름에 붙일 글: "효과 스캔 라인, RGB 분할"(그리는 차례). 효과가 없으면 null.</summary>
        public static string Spoken(Clip clip)
        {
            if (Count(clip) == 0)
                return null;
            return "효과 " + string.Join(", ", clip.Effects.Where(e => e != null).Select(VideoEffects.Name));
        }

        private static int Count(Clip clip)
        {
            return VideoEffects.CanHave(clip) && clip.Effects != null ? clip.Effects.Count(e => e != null) : 0;
        }
    }
}
