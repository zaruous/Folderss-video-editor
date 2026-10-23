using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoEditor.Core
{
    public enum TextAlign
    {
        Left,
        Center,
        Right
    }

    public enum FillKind
    {
        None,
        Solid,
        Linear,
        Radial
    }

    public enum LineJoinKind
    {
        Round,
        Miter,
        Bevel
    }

    public enum WarpKind
    {
        None,
        ArchUp,
        ArchDown,
        Circle,
        Wave,
        Bulge,
        Pinch,
        Slant,
        Stairs
    }

    /// <summary>채우기. 색은 1~4개(그라데이션 정지점), 각도는 선형 그라데이션 방향(0° = 왼쪽→오른쪽, 시계 방향).</summary>
    public sealed class TextFill
    {
        public FillKind Kind { get; set; } = FillKind.Solid;
        public List<string> Colors { get; set; } = new List<string> { "#FFFFFF" };
        public double Angle { get; set; }
    }

    public sealed class TextOutline
    {
        public bool Enabled { get; set; }
        public string Color { get; set; } = "#000000";
        public double Width { get; set; } = 4;
        public LineJoinKind Join { get; set; } = LineJoinKind.Round;
    }

    /// <summary>그림자. 각도 0° = 오른쪽, 시계 방향.</summary>
    public sealed class TextShadow
    {
        public bool Enabled { get; set; }
        public string Color { get; set; } = "#000000";
        public double Distance { get; set; } = 6;
        public double Angle { get; set; } = 45;
        public double Blur { get; set; } = 6;
        public double Opacity { get; set; } = 0.6;
    }

    public sealed class TextGlow
    {
        public bool Enabled { get; set; }
        public string Color { get; set; } = "#FFD54F";
        public double Size { get; set; } = 12;
        public double Opacity { get; set; } = 0.8;
    }

    /// <summary>반사. 크기는 글자 높이의 %.</summary>
    public sealed class TextReflection
    {
        public bool Enabled { get; set; }
        public double Size { get; set; } = 50;
        public double Gap { get; set; } = 4;
        public double Opacity { get; set; } = 0.4;
    }

    /// <summary>휘기. 정도는 -100~100%.</summary>
    public sealed class TextWarpSetting
    {
        public WarpKind Kind { get; set; } = WarpKind.None;
        public double Amount { get; set; } = 50;
    }

    /// <summary>
    /// 글자 클립의 꾸밈(글꼴·채우기·윤곽선·그림자·광선·반사·휘기). 길이 값은 프로젝트 픽셀 기준.
    /// 프로젝트 파일은 남이 만든 것일 수 있으므로 열 때 <see cref="Normalize"/>로 값을 잡는다.
    /// </summary>
    public sealed class TextStyle
    {
        public const string DefaultFont = "Malgun Gothic";
        public const int MaxFontNameLength = 100;
        public const int MaxFillColors = 4;

        public string FontFamily { get; set; } = DefaultFont;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        /// <summary>글자 간격(1/1000 em, -200~1000).</summary>
        public double LetterSpacing { get; set; }
        /// <summary>줄 간격(배, 0.5~3.0).</summary>
        public double LineSpacing { get; set; } = 1.2;
        public TextAlign Align { get; set; } = TextAlign.Center;
        public TextFill Fill { get; set; } = new TextFill();
        public TextOutline Outline { get; set; } = new TextOutline();
        public TextShadow Shadow { get; set; } = new TextShadow();
        public TextGlow Glow { get; set; } = new TextGlow();
        public TextReflection Reflection { get; set; } = new TextReflection();
        public TextWarpSetting Warp { get; set; } = new TextWarpSetting();

        /// <summary>깊은 복사(분할·붙여넣기한 클립끼리 스타일을 같이 쓰지 않게).</summary>
        public TextStyle Clone()
        {
            var copy = (TextStyle)MemberwiseClone();
            copy.Fill = Fill == null ? null : new TextFill { Kind = Fill.Kind, Colors = Fill.Colors?.ToList(), Angle = Fill.Angle };
            copy.Outline = Outline == null ? null : new TextOutline { Enabled = Outline.Enabled, Color = Outline.Color, Width = Outline.Width, Join = Outline.Join };
            copy.Shadow = Shadow == null ? null : new TextShadow { Enabled = Shadow.Enabled, Color = Shadow.Color, Distance = Shadow.Distance, Angle = Shadow.Angle, Blur = Shadow.Blur, Opacity = Shadow.Opacity };
            copy.Glow = Glow == null ? null : new TextGlow { Enabled = Glow.Enabled, Color = Glow.Color, Size = Glow.Size, Opacity = Glow.Opacity };
            copy.Reflection = Reflection == null ? null : new TextReflection { Enabled = Reflection.Enabled, Size = Reflection.Size, Gap = Reflection.Gap, Opacity = Reflection.Opacity };
            copy.Warp = Warp == null ? null : new TextWarpSetting { Kind = Warp.Kind, Amount = Warp.Amount };
            return copy;
        }

        /// <summary>스타일이 없는 글자 클립에서 처음 만들 때: 지금 글자 색을 단색 채우기로 이어받는다.</summary>
        public static TextStyle FromLegacy(Clip clip)
        {
            var style = new TextStyle();
            style.Fill.Colors = new List<string> { NormalizeColor(clip?.TextColor, "#FFFFFF") };
            return style;
        }

        /// <summary>범위를 벗어난 값은 경계값으로, NaN·무한대·정의되지 않은 enum·잘못된 색은 기본값으로 바꾼다.</summary>
        public void Normalize()
        {
            if (!IsSafeFontName(FontFamily))
                FontFamily = DefaultFont;
            LetterSpacing = Range(LetterSpacing, -200, 1000, 0);
            LineSpacing = Range(LineSpacing, 0.5, 3.0, 1.2);
            Align = Defined(Align, TextAlign.Center);

            Fill ??= new TextFill();
            Fill.Kind = Defined(Fill.Kind, FillKind.Solid);
            Fill.Colors = (Fill.Colors ?? new List<string>()).Take(MaxFillColors).Select(c => NormalizeColor(c, "#FFFFFF")).ToList();
            if (Fill.Colors.Count == 0)
                Fill.Colors.Add("#FFFFFF");
            Fill.Angle = Range(Fill.Angle, 0, 360, 0);

            Outline ??= new TextOutline();
            Outline.Color = NormalizeColor(Outline.Color, "#000000");
            Outline.Width = Range(Outline.Width, 0, 50, 4);
            Outline.Join = Defined(Outline.Join, LineJoinKind.Round);

            Shadow ??= new TextShadow();
            Shadow.Color = NormalizeColor(Shadow.Color, "#000000");
            Shadow.Distance = Range(Shadow.Distance, 0, 200, 6);
            Shadow.Angle = Range(Shadow.Angle, 0, 360, 45);
            Shadow.Blur = Range(Shadow.Blur, 0, 100, 6);
            Shadow.Opacity = Range(Shadow.Opacity, 0, 1, 0.6);

            Glow ??= new TextGlow();
            Glow.Color = NormalizeColor(Glow.Color, "#FFD54F");
            Glow.Size = Range(Glow.Size, 0, 100, 12);
            Glow.Opacity = Range(Glow.Opacity, 0, 1, 0.8);

            Reflection ??= new TextReflection();
            Reflection.Size = Range(Reflection.Size, 10, 100, 50);
            Reflection.Gap = Range(Reflection.Gap, 0, 100, 4);
            Reflection.Opacity = Range(Reflection.Opacity, 0, 1, 0.4);

            Warp ??= new TextWarpSetting();
            Warp.Kind = Defined(Warp.Kind, WarpKind.None);
            Warp.Amount = Range(Warp.Amount, -100, 100, 50);
        }

        /// <summary>
        /// 시스템 글꼴 이름으로만 쓸 수 있는지. WPF FontFamily는 \\host\share\#이름 같은 경로·URI도 받아
        /// 남이 만든 프로젝트가 네트워크 글꼴을 읽게 할 수 있으므로(자격 증명 유출) 경로·URI 문자와 제어 문자를 막는다.
        /// </summary>
        public static bool IsSafeFontName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFontNameLength)
                return false;
            foreach (var ch in name)
            {
                if (char.IsControl(ch) || ch == '\\' || ch == '/' || ch == ':' || ch == '#' || ch == '%')
                    return false;
            }
            return true;
        }

        /// <summary>#RRGGBB 또는 #AARRGGBB만 받아 대문자로 돌려준다. 그 밖에는 fallback.</summary>
        public static string NormalizeColor(string value, string fallback)
        {
            if (value == null || value.Length != 7 && value.Length != 9 || value[0] != '#')
                return fallback;
            for (var i = 1; i < value.Length; i++)
            {
                if (!Uri.IsHexDigit(value[i]))
                    return fallback;
            }
            return value.ToUpperInvariant();
        }

        private static double Range(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private static T Defined<T>(T value, T fallback) where T : struct, Enum
        {
            return Enum.IsDefined(value) ? value : fallback;
        }
    }
}
