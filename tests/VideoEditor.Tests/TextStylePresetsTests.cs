using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>스타일 모음(이슈 #17): 8개, 이름이 겹치지 않고 영문 글자가 없음, 정규화해도 그대로, 받은 사본끼리 독립.</summary>
    public class TextStylePresetsTests
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static string Json(TextStyle style)
        {
            return JsonSerializer.Serialize(style, Options);
        }

        [Fact]
        public void EightPresets_WithUniqueKoreanNames()
        {
            var presets = TextStylePresets.All;
            Assert.Equal(8, presets.Count);
            Assert.Equal(8, presets.Select(p => p.Name).Distinct().Count());
            foreach (var (name, style) in presets)
            {
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.NotNull(style);
                // 영문 상표 단어가 끼지 않게 — 이름에 라틴 글자를 아예 쓰지 않는다
                Assert.DoesNotMatch("[A-Za-z]", name);
            }
            Assert.Equal("기본", presets[0].Name);
        }

        [Fact]
        public void EveryPreset_IsUnchangedByNormalize()
        {
            foreach (var (name, style) in TextStylePresets.All)
            {
                var before = Json(style);
                style.Normalize();
                var after = Json(style);
                Assert.True(before == after, name + ": 정규화 전 " + before + " / 후 " + after);
            }
        }

        [Fact]
        public void All_GivesFreshDeepCopies()
        {
            var first = TextStylePresets.All;
            var expected = first.Select(p => Json(p.Style)).ToList();
            // 받은 사본을 고쳐도 모음은 그대로
            var yellow = first[1].Style;
            yellow.Bold = false;
            yellow.Fill.Colors[0] = "#123456";
            yellow.Fill.Colors.Add("#000000");
            yellow.Outline.Width = 50;
            yellow.Shadow.Enabled = true;
            first[0].Style.FontFamily = "Arial";
            var second = TextStylePresets.All;
            Assert.Equal(expected, second.Select(p => Json(p.Style)).ToList());
            Assert.NotSame(first, second);
            Assert.NotSame(first[1].Style, second[1].Style);
            Assert.NotSame(first[1].Style.Fill, second[1].Style.Fill);
            Assert.NotSame(first[1].Style.Fill.Colors, second[1].Style.Fill.Colors);
            Assert.NotSame(first[1].Style.Outline, second[1].Style.Outline);
            Assert.NotSame(first[1].Style.Shadow, second[1].Style.Shadow);
        }

        [Fact]
        public void Presets_DrawDifferently_AndMatchTheirNames()
        {
            // 모두 다른 그림이다 — 썸네일 캐시 키(요청 해시)가 겹치지 않는다
            var keys = TextStylePresets.All
                .Select(p => new TextImageRequest { Text = "가나Ab", Style = p.Style, FontPixelSize = 48, EffectScale = 1, CanvasWidth = 640, CanvasHeight = 360 }.Key(1))
                .ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());

            var presets = TextStylePresets.All.ToDictionary(p => p.Name, p => p.Style);
            Assert.Equal(Json(new TextStyle()), Json(presets["기본"]));
            var yellow = presets["노란 자막"];
            Assert.True(yellow.Bold);
            Assert.Equal(new[] { "#FACC15" }, yellow.Fill.Colors);
            Assert.True(yellow.Outline.Enabled);
            Assert.Equal("#000000", yellow.Outline.Color);
            Assert.Equal(6, yellow.Outline.Width);
            var framed = presets["흰 글자 검은 테"];
            Assert.Equal(8, framed.Outline.Width);
            Assert.True(framed.Shadow.Enabled);
            Assert.Equal(FillKind.Linear, presets["금빛"].Fill.Kind);
            Assert.True(presets["금빛"].Outline.Enabled && presets["금빛"].Shadow.Enabled);
            Assert.Equal(FillKind.Linear, presets["하늘 그라데이션"].Fill.Kind);
            Assert.Equal("#FFFFFF", presets["하늘 그라데이션"].Outline.Color);
            Assert.Equal("#EF4444", presets["빨간 강조"].Fill.Colors[0]);
            Assert.True(presets["빨간 강조"].Bold && presets["빨간 강조"].Shadow.Enabled);
            var title = presets["진한 그림자 제목"];
            Assert.False(title.Outline.Enabled);
            Assert.True(title.Shadow.Enabled && title.Shadow.Blur > new TextShadow().Blur && title.Shadow.Distance > new TextShadow().Distance);
            var hollow = presets["속 빈 글자"];
            Assert.Equal(FillKind.None, hollow.Fill.Kind);
            Assert.True(hollow.Outline.Enabled);
            Assert.Equal("#FFFFFF", hollow.Outline.Color);
            // 1단계 밖의 꾸밈(광선·반사·휘기)은 쓰지 않는다
            Assert.All(presets.Values, s => Assert.False(s.Glow.Enabled || s.Reflection.Enabled || s.Warp.Kind != WarpKind.None));
        }
    }
}
