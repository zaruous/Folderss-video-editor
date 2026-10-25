using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>
    /// 스타일 모음: 꾸미기 탭 맨 위에 썸네일로 보이는 미리 만든 스타일 16개 — 앞 8개는 채우기·윤곽선·그림자만, 뒤 8개는 광선·반사·휘기도 쓴다.
    /// 이름은 한국어 일반 명사(상표·제품 이름은 쓰지 않음). 값은 모두 <see cref="TextStyle.Normalize"/>를 거쳐도 그대로다(색은 대문자 #RRGGBB, 길이·각도는 범위 안).
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
            ("속 빈 글자", new TextStyle { Bold = true, Fill = new TextFill { Kind = FillKind.None, Colors = new List<string> { "#FFFFFF" } }, Outline = Outline("#FFFFFF", 4) }),
            // 여기부터 광선·반사·휘기. 가는 밝은 윤곽선 + 짙은 하늘색 광선(채우기 없음 — 광선이 글자 속에도 번져 관처럼 보임)
            ("네온", new TextStyle { Bold = true, Fill = new TextFill { Kind = FillKind.None, Colors = new List<string> { "#BAE6FD" } }, Outline = Outline("#BAE6FD", 2), Glow = Glow("#0EA5E9", 7, 1) }),
            ("유리 반사", new TextStyle { Bold = true, Fill = Linear(90, "#FFFFFF", "#A5F3FC"), Reflection = Reflection(60, 4, 0.45) }),
            ("아치 제목", new TextStyle { Bold = true, Fill = Linear(90, "#FDE68A", "#F59E0B"), Outline = Outline("#7C2D12", 4), Warp = Warp(WarpKind.ArchUp, 60) }),
            ("물결 글자", new TextStyle { Bold = true, Fill = Linear(0, "#34D399", "#22D3EE"), Outline = Outline("#064E3B", 3), Warp = Warp(WarpKind.Wave, 70) }),
            // 원은 짧은 글(두세 글자)도 알아볼 수 있게 30%(원의 108°)까지만 감는다 — 더 감으면 글자 아래쪽이 원 가운데로 몰려 뭉개진다
            ("원형 배지", new TextStyle { Bold = true, Fill = Solid("#FFFFFF"), Outline = Outline("#DC2626", 5), Warp = Warp(WarpKind.Circle, 30) }),
            ("금빛 반사", new TextStyle { Bold = true, Fill = Linear(90, "#FFF3B0", "#D4AF37", "#8A6116"), Outline = Outline("#5C3B12", 2), Reflection = Reflection(50, 2, 0.5) }),
            // 짙은 윤곽선 바깥에 흰 광선을 좁게 — 어두운 바탕에서도 무지개 글자가 떠 보이게
            ("무지개 그라데이션", new TextStyle { Bold = true, Fill = Linear(0, "#EF4444", "#FACC15", "#22C55E", "#3B82F6"), Outline = Outline("#1E1B4B", 3), Glow = Glow("#FFFFFF", 5, 0.9) }),
            // 음수 = 오른쪽으로 갈수록 올라가는 계단
            ("계단 글자", new TextStyle { Bold = true, Fill = Solid("#A3E635"), Outline = Outline("#1A2E05", 4), Warp = Warp(WarpKind.Stairs, -80) })
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

        private static TextGlow Glow(string color, double size, double opacity)
        {
            return new TextGlow { Enabled = true, Color = color, Size = size, Opacity = opacity };
        }

        /// <summary>반사. 크기는 글자 높이의 %.</summary>
        private static TextReflection Reflection(double size, double gap, double opacity)
        {
            return new TextReflection { Enabled = true, Size = size, Gap = gap, Opacity = opacity };
        }

        private static TextWarpSetting Warp(WarpKind kind, double amount)
        {
            return new TextWarpSetting { Kind = kind, Amount = amount };
        }
    }
}
