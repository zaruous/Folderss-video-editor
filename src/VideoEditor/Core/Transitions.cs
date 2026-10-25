using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    /// <summary>클립 앞 전환 효과(앞 클립 → 이 클립). 길이는 <see cref="Clip.TransitionIn"/>이고 타임라인 위치는 바꾸지 않는다.</summary>
    public enum TransitionKind
    {
        /// <summary>디졸브(교차 페이드) — 예전부터 있던 크로스페이드.</summary>
        Fade,
        FadeBlack,
        FadeWhite,
        Dissolve,
        WipeLeft,
        WipeRight,
        WipeUp,
        WipeDown,
        SlideLeft,
        SlideRight,
        SlideUp,
        SlideDown,
        SmoothLeft,
        SmoothRight,
        CircleOpen,
        CircleClose,
        HorizontalOpen,
        VerticalOpen,
        Radial,
        ZoomIn,
        Pixelize,
        Blur,
        Diagonal
    }

    /// <summary>
    /// 전환 효과의 이름과 FFmpeg xfade 이름. 디졸브는 예전처럼 뒤 클립의 알파 페이드로, 나머지는 xfade로 그린다.
    /// FFmpeg 5 이상에 다 있는 효과만 쓴다(최소 지원 버전).
    /// </summary>
    public static class Transitions
    {
        // 효과 탭에 보일 순서대로
        private static readonly (TransitionKind Kind, string Name, string Filter)[] Table =
        {
            (TransitionKind.Fade, "디졸브", "fade"),
            (TransitionKind.FadeBlack, "검정으로 페이드", "fadeblack"),
            (TransitionKind.FadeWhite, "흰색으로 페이드", "fadewhite"),
            (TransitionKind.Dissolve, "흩어지며 바뀌기", "dissolve"),
            (TransitionKind.WipeLeft, "닦아내기 ←", "wipeleft"),
            (TransitionKind.WipeRight, "닦아내기 →", "wiperight"),
            (TransitionKind.WipeUp, "닦아내기 ↑", "wipeup"),
            (TransitionKind.WipeDown, "닦아내기 ↓", "wipedown"),
            (TransitionKind.SlideLeft, "밀어내기 ←", "slideleft"),
            (TransitionKind.SlideRight, "밀어내기 →", "slideright"),
            (TransitionKind.SlideUp, "밀어내기 ↑", "slideup"),
            (TransitionKind.SlideDown, "밀어내기 ↓", "slidedown"),
            (TransitionKind.SmoothLeft, "부드럽게 닦기 ←", "smoothleft"),
            (TransitionKind.SmoothRight, "부드럽게 닦기 →", "smoothright"),
            (TransitionKind.CircleOpen, "원 열기", "circleopen"),
            (TransitionKind.CircleClose, "원 닫기", "circleclose"),
            (TransitionKind.HorizontalOpen, "가로로 열기", "horzopen"),
            (TransitionKind.VerticalOpen, "세로로 열기", "vertopen"),
            (TransitionKind.Radial, "시계처럼 쓸기", "radial"),
            (TransitionKind.ZoomIn, "확대하며 바뀌기", "zoomin"),
            (TransitionKind.Pixelize, "모자이크", "pixelize"),
            (TransitionKind.Blur, "흐리며 바뀌기", "hblur"),
            (TransitionKind.Diagonal, "대각선 닦기", "diagtl")
        };

        /// <summary>효과 탭에 보일 순서.</summary>
        public static IReadOnlyList<TransitionKind> All { get; } = Table.Select(t => t.Kind).ToList();

        /// <summary>새로 넣는 전환의 기본 길이(초).</summary>
        public const double DefaultSeconds = 0.5;

        public static string Name(TransitionKind kind)
        {
            return Table.FirstOrDefault(t => t.Kind == kind).Name ?? Table[0].Name;
        }

        /// <summary>xfade transition 값(디졸브는 xfade를 쓰지 않지만 이름은 fade).</summary>
        public static string FilterName(TransitionKind kind)
        {
            return Table.FirstOrDefault(t => t.Kind == kind).Filter ?? "fade";
        }

        /// <summary>xfade로 그리는 효과인지(디졸브는 예전처럼 뒤 클립의 알파 페이드).</summary>
        public static bool UsesXfade(TransitionKind kind)
        {
            return kind != TransitionKind.Fade && Table.Any(t => t.Kind == kind);
        }
    }
}
