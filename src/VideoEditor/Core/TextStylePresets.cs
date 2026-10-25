using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 스타일 모음: 꾸미기 탭 맨 위에 썸네일로 보이는 미리 만든 스타일 8개. 이름은 한국어 일반 명사(상표·제품 이름은 쓰지 않음).
    /// 값은 모두 <see cref="TextStyle.Normalize"/>를 거쳐도 그대로다(색은 대문자 #RRGGBB, 길이·각도는 범위 안).
    /// </summary>
    public static class TextStylePresets
    {
        // 순서 = 갤러리 단추 순서. 길이 값은 프로젝트 픽셀(기본 글자 크기 72 기준으로 고름)
        private static readonly (string Name, TextStyle Style)[] Presets =
        {
            ("기본", new TextStyle()),
            ("노란 자막", new TextStyle { Bold = true, Fill = Solid("#FACC15"), Outline = Outline("#000000", 6) }),
            ("흰 글자 검은 테", new TextStyle { Fill = Solid("#FFFFFF"), Outline = Outline("#000000", 8), Shadow = Shadow(6, 6, 0.6) }),
            // 위가 밝고 아래가 짙은 금색(90° = 위→아래)
            ("금빛", new TextStyle { Bold = true, Fill = Linear(90, "#FFF3B0", "#D4AF37", "#8A6116"), Outline = Outline("#5C3B12", 3), Shadow = Shadow(5, 6, 0.6) }),
            ("하늘 그라데이션", new TextStyle { Bold = true, Fill = Linear(90, "#38BDF8", "#1D4ED8"), Outline = Outline("#FFFFFF", 4) }),
            ("빨간 강조", new TextStyle { Bold = true, Fill = Solid("#EF4444"), Outline = Outline("#FFFFFF", 5), Shadow = Shadow(6, 6, 0.6) }),
            ("진한 그림자 제목", new TextStyle { Bold = true, Fill = Solid("#FFFFFF"), Shadow = Shadow(12, 24, 0.9) }),
            // 채우기 없음 — 색 1은 [글자] 탭 글자 색과 맞출 값으로 남긴다
            ("속 빈 글자", new TextStyle { Bold = true, Fill = new TextFill { Kind = FillKind.None, Colors = new List<string> { "#FFFFFF" } }, Outline = Outline("#FFFFFF", 4) })
        };

        /// <summary>이름과 스타일. 부를 때마다 새 깊은 복사를 준다 — 받은 쪽이 고쳐도 모음은 그대로.</summary>
        public static IReadOnlyList<(string Name, TextStyle Style)> All
        {
            get { return Presets.Select(p => (p.Name, p.Style.Clone())).ToList(); }
        }

        private static TextFill Solid(string color)
        {
            return new TextFill { Kind = FillKind.Solid, Colors = new List<string> { color } };
        }

        private static TextFill Linear(double angle, params string[] colors)
        {
            return new TextFill { Kind = FillKind.Linear, Colors = colors.ToList(), Angle = angle };
        }

        private static TextOutline Outline(string color, double width)
        {
            return new TextOutline { Enabled = true, Color = color, Width = width, Join = LineJoinKind.Round };
        }

        /// <summary>검정 그림자(오른쪽 아래 45°).</summary>
        private static TextShadow Shadow(double distance, double blur, double opacity)
        {
            return new TextShadow { Enabled = true, Color = "#000000", Distance = distance, Angle = 45, Blur = blur, Opacity = opacity };
        }
    }
}
