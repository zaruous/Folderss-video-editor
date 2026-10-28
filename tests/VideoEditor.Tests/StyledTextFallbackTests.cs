using System.Collections.Generic;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>긴 글(2,000자 초과)의 drawtext 대체 경로가 그리지 못하는 꾸밈 안내(#34): 쓰는 것만, 차례대로, 짧은 글·꾸미지 않은 글은 없음.</summary>
    public class StyledTextFallbackTests
    {
        private static Clip Text(int length, TextStyle style)
        {
            var clip = TimelineEdits.CreateText(new string('가', length), 0, 30);
            clip.Style = style;
            return clip;
        }

        private static TextStyle Plain()
        {
            return TextStyle.FromLegacy(new Clip { TextColor = "#FFFFFF" });
        }

        [Fact]
        public void ShortOrUnstyledText_IsNotLong_AndHasNoNotice()
        {
            var full = new TextStyle { Bold = true };
            full.Glow.Enabled = true;
            // 2,000자까지는 꾸민 경로
            var limit = Text(RenderGraph.MaxStyledTextLength, full);
            Assert.False(StyledTextFallback.IsLong(limit));
            Assert.Empty(StyledTextFallback.Lost(limit));
            Assert.Null(StyledTextFallback.Notice(limit));
            // 꾸미지도 상자를 정하지도 않은 글자는 원래 drawtext — 빠지는 것이 없다
            var legacy = Text(RenderGraph.MaxStyledTextLength + 1, null);
            Assert.False(StyledTextFallback.IsLong(legacy));
            Assert.Null(StyledTextFallback.Notice(legacy));
            // 글자가 아닌 클립·null
            Assert.False(StyledTextFallback.IsLong(null));
            Assert.Empty(StyledTextFallback.Lost(TimelineEdits.CreateArt(ArtKind.Rectangle, 0, 30)));
        }

        [Fact]
        public void LongText_ListsOnlyWhatTheClipUses_InScreenOrder()
        {
            // 예전 글자를 처음 꾸민 스타일(맑은 고딕·왼쪽 맞춤·단색)은 drawtext와 같다 — 긴 글이라는 것만 알린다
            var plain = Text(RenderGraph.MaxStyledTextLength + 1, Plain());
            Assert.True(StyledTextFallback.IsLong(plain));
            Assert.Empty(StyledTextFallback.Lost(plain));
            var notice = StyledTextFallback.Notice(plain);
            Assert.StartsWith("글이 2,000자를 넘어 간단한 방식으로 그립니다", notice);
            Assert.DoesNotContain("그리지 못하는 것", notice);

            // 이슈에 적힌 것: 상자·광선·반사·휘기, 그림자 흐림, 속 빈 글자
            var style = Plain();
            style.Glow.Enabled = true;
            style.Reflection.Enabled = true;
            style.Warp.Kind = WarpKind.ArchUp;
            style.Warp.Amount = 40;
            style.Shadow.Enabled = true;
            style.Shadow.Blur = 8;
            style.Outline.Enabled = true;
            style.Fill.Kind = FillKind.None;
            var clip = Text(RenderGraph.MaxStyledTextLength + 1, style);
            clip.TextBoxWidth = 800;
            Assert.Equal(new List<string> { "글자 상자", "속 빈 글자", "그림자 흐림", "광선", "반사", "휘기" }, StyledTextFallback.Lost(clip));
            Assert.Contains("그리지 못하는 것: 글자 상자 · 속 빈 글자 · 그림자 흐림 · 광선 · 반사 · 휘기.", StyledTextFallback.Notice(clip));
            Assert.Contains("2,000자 이하로 나눠", StyledTextFallback.Notice(clip));

            // 글꼴 묶음과 채우기
            var font = new TextStyle { FontFamily = "Arial", Bold = true, LetterSpacing = 100, LineSpacing = 1.5, Align = TextAlign.Center };
            font.Fill.Kind = FillKind.Linear;
            font.Fill.Colors = new List<string> { "#FF0000", "#0000FF" };
            font.Outline.Enabled = true;
            font.Outline.Join = LineJoinKind.Miter;
            Assert.Equal(new List<string> { "글꼴", "굵게·기울임", "자간", "행간", "정렬", "그라데이션", "윤곽선 이음" },
                StyledTextFallback.Lost(Text(RenderGraph.MaxStyledTextLength + 1, font)));
        }

        [Fact]
        public void OffOrInvisibleEffects_AreNotListed()
        {
            var style = Plain();
            // 꺼진 효과, 보이지 않는 값(불투명도 0·크기 0·정도 0·흐림 0), 같은 색 둘인 그라데이션, 불투명 채우기의 윤곽선(둥근 이음)
            style.Glow.Size = 50;
            style.Glow.Enabled = false;
            style.Reflection.Enabled = true;
            style.Reflection.Opacity = 0;
            style.Warp.Kind = WarpKind.Wave;
            style.Warp.Amount = 0;
            style.Shadow.Enabled = true;
            style.Shadow.Blur = 0;
            style.Fill.Kind = FillKind.Radial;
            style.Fill.Colors = new List<string> { "#ffffff", "#FFFFFF" };
            style.Outline.Enabled = true;
            Assert.Empty(StyledTextFallback.Lost(Text(RenderGraph.MaxStyledTextLength + 1, style)));
            // 반투명 채우기에 윤곽선이면 속 빈 글자처럼 그리지 못한다
            style.Fill.Kind = FillKind.Solid;
            style.Fill.Colors = new List<string> { "#80FFFFFF" };
            Assert.Equal(new List<string> { "속 빈 글자" }, StyledTextFallback.Lost(Text(RenderGraph.MaxStyledTextLength + 1, style)));
            // 상자만 정한(스타일이 없는) 긴 글
            var boxed = Text(RenderGraph.MaxStyledTextLength + 1, null);
            boxed.TextBoxHeight = 300;
            Assert.True(StyledTextFallback.IsLong(boxed));
            Assert.Equal(new List<string> { "글자 상자" }, StyledTextFallback.Lost(boxed));
        }

        [Fact]
        public void Lost_DoesNotChangeTheClipStyle()
        {
            var style = new TextStyle { LetterSpacing = double.NaN };
            var clip = Text(RenderGraph.MaxStyledTextLength + 1, style);
            StyledTextFallback.Lost(clip);
            Assert.True(double.IsNaN(clip.Style.LetterSpacing));
        }
    }
}
