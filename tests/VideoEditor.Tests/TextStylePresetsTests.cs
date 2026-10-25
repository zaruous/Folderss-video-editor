using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>스타일 모음(이슈 #17·#22): 16개, 이름이 겹치지 않고 영문 글자가 없음, 정규화해도 그대로, 받은 사본끼리 독립, 뒤 8개는 광선·반사·휘기를 씀.</summary>
    public class TextStylePresetsTests
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        // 1단계(#17)의 8개 — 순서가 갤러리 단추 번호(StylePreset0~7)라 화면 시험이 이 순서에 기댄다
        private static readonly string[] FirstEight = { "기본", "노란 자막", "흰 글자 검은 테", "금빛", "하늘 그라데이션", "빨간 강조", "진한 그림자 제목", "속 빈 글자" };

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
        public void SixteenPresets_WithUniqueKoreanNames_FirstEightKeepTheirOrder()
        {
            var presets = TextStylePresets.All;
            Assert.Equal(16, presets.Count);
            Assert.Equal(16, presets.Select(p => p.Name).Distinct().Count());
            foreach (var (name, style) in presets)
            {
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.Equal(name.Trim(), name);
                Assert.NotNull(style);
                // 영문 상표 단어가 끼지 않게 — 이름에 라틴 글자를 아예 쓰지 않는다
                Assert.DoesNotMatch("[A-Za-z]", name);
            }
            Assert.Equal(FirstEight, presets.Take(8).Select(p => p.Name).ToArray());
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
            var neon = first.First(p => p.Name == "네온").Style;
            neon.Glow.Size = 100;
            neon.Reflection.Enabled = true;
            neon.Warp.Kind = WarpKind.Pinch;
            var second = TextStylePresets.All;
            Assert.Equal(expected, second.Select(p => Json(p.Style)).ToList());
            Assert.NotSame(first, second);
            Assert.NotSame(first[1].Style, second[1].Style);
            Assert.NotSame(first[1].Style.Fill, second[1].Style.Fill);
            Assert.NotSame(first[1].Style.Fill.Colors, second[1].Style.Fill.Colors);
            Assert.NotSame(first[1].Style.Outline, second[1].Style.Outline);
            Assert.NotSame(first[1].Style.Shadow, second[1].Style.Shadow);
            Assert.NotSame(first[1].Style.Glow, second[1].Style.Glow);
            Assert.NotSame(first[1].Style.Reflection, second[1].Style.Reflection);
            Assert.NotSame(first[1].Style.Warp, second[1].Style.Warp);
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
            // 1단계의 8개는 1단계 밖의 꾸밈(광선·반사·휘기)을 쓰지 않는다
            Assert.All(FirstEight.Select(n => presets[n]), s => Assert.False(s.Glow.Enabled || s.Reflection.Enabled || s.Warp.Kind != WarpKind.None));

            // 2단계의 8개: 이름이 말하는 효과를 쓴다
            var neon = presets["네온"];
            Assert.Equal(FillKind.None, neon.Fill.Kind);
            Assert.True(neon.Outline.Enabled && neon.Glow.Enabled && neon.Glow.Size > 0 && neon.Glow.Opacity > 0);
            Assert.True(presets["유리 반사"].Reflection.Enabled && presets["유리 반사"].Reflection.Opacity > 0);
            Assert.Equal(WarpKind.ArchUp, presets["아치 제목"].Warp.Kind);
            Assert.True(presets["아치 제목"].Warp.Amount > 0);
            Assert.Equal(WarpKind.Wave, presets["물결 글자"].Warp.Kind);
            Assert.Equal(WarpKind.Circle, presets["원형 배지"].Warp.Kind);
            var gold = presets["금빛 반사"];
            Assert.Equal(FillKind.Linear, gold.Fill.Kind);
            Assert.True(gold.Reflection.Enabled && gold.Reflection.Opacity > 0);
            var rainbow = presets["무지개 그라데이션"];
            Assert.Equal(FillKind.Linear, rainbow.Fill.Kind);
            Assert.Equal(TextStyle.MaxFillColors, rainbow.Fill.Colors.Count);
            Assert.True(rainbow.Glow.Enabled);
            Assert.Equal(WarpKind.Stairs, presets["계단 글자"].Warp.Kind);
            Assert.All(presets.Where(p => !FirstEight.Contains(p.Key)), p =>
                Assert.True(p.Value.Glow.Enabled || p.Value.Reflection.Enabled || (p.Value.Warp.Kind != WarpKind.None && p.Value.Warp.Amount != 0), p.Key));
        }

        [Fact]
        public void CircleBadge_DoesNotFoldShortText()
        {
            // 원 휘기는 정도가 크면 글자 아래 끝이 원 가운데를 넘어가 좌우가 뒤집힌다(짧은 글이 뭉개짐).
            // 두 글자(배치 상자 높이/폭 ≈ 0.67)에서도 글자 아래 끝이 왼쪽 → 오른쪽 순서를 지키는지 본다
            const double twoLetters = 0.67;
            var amount = TextStylePresets.All.First(p => p.Name == "원형 배지").Style.Warp.Amount;
            foreach (var v in new[] { 0.0, 0.5, 1.0 })
            {
                var left = TextWarp.Map(WarpKind.Circle, amount, 0.1, v, twoLetters);
                var right = TextWarp.Map(WarpKind.Circle, amount, 0.9, v, twoLetters);
                Assert.True(left.U < right.U, "v " + v + ": 왼쪽 " + left.U + " · 오른쪽 " + right.U);
            }
            // 비교가 헛돌지 않는지: 정도 50이면 같은 글자의 아래 끝이 뒤집힌다
            Assert.True(TextWarp.Map(WarpKind.Circle, 50, 0.1, 1, twoLetters).U > TextWarp.Map(WarpKind.Circle, 50, 0.9, 1, twoLetters).U);
        }
    }
}
