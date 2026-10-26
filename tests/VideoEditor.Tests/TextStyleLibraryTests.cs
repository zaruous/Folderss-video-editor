using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>내 스타일(이슈 #22): 설정 textStyles ↔ 목록 왕복, 악성 설정(깨진 JSON·51개·이름·스타일 값·크기)을 읽어도 예외 없이 정규화, 넣기·바꾸기·지우기 규칙.</summary>
    public class TextStyleLibraryTests
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

        private static TextStyle AllFieldsChanged()
        {
            return new TextStyle
            {
                FontFamily = "Noto Sans KR",
                Bold = true,
                Italic = true,
                LetterSpacing = 150,
                LineSpacing = 1.5,
                Align = TextAlign.Right,
                Fill = new TextFill { Kind = FillKind.Linear, Colors = new List<string> { "#FF0000", "#8000FF00", "#0000FF" }, Angle = 90 },
                Outline = new TextOutline { Enabled = true, Color = "#112233", Width = 8, Join = LineJoinKind.Miter },
                Shadow = new TextShadow { Enabled = true, Color = "#445566", Distance = 10, Angle = 135, Blur = 12, Opacity = 0.3 },
                Glow = new TextGlow { Enabled = true, Color = "#778899", Size = 20, Opacity = 0.5 },
                Reflection = new TextReflection { Enabled = true, Size = 70, Gap = 8, Opacity = 0.2 },
                Warp = new TextWarpSetting { Kind = WarpKind.Wave, Amount = -30 }
            };
        }

        /// <summary>항목 하나의 JSON({"Name": name, "Style": style}).</summary>
        private static JsonObject Entry(JsonNode name, JsonNode style)
        {
            return new JsonObject { ["Name"] = name, ["Style"] = style };
        }

        private static JsonObject EmptyStyle()
        {
            return new JsonObject();
        }

        [Fact]
        public void WriteRead_RoundTripsNamesOrderAndEveryField()
        {
            var styles = new List<(string Name, TextStyle Style)>
            {
                ("노랑 제목", AllFieldsChanged()),
                ("기본", new TextStyle()),
                ("Neon 😀", TextStylePresets.All.Last().Style)
            };
            var json = TextStyleLibrary.Write(styles);
            var read = TextStyleLibrary.Read(json);
            Assert.Equal(styles.Select(s => s.Name), read.Select(s => s.Name));
            Assert.Equal(styles.Select(s => Json(s.Style)), read.Select(s => Json(s.Style)));
            // 다시 써도 같은 글(읽은 것이 이미 정규화되어 있다)
            Assert.Equal(json, TextStyleLibrary.Write(read));
            // 열거 값은 이름으로 쓴다(프로젝트 파일과 같음)
            Assert.Contains("\"Wave\"", json);
            Assert.Contains("\"Miter\"", json);
            Assert.Empty(TextStyleLibrary.Read(TextStyleLibrary.Write(new List<(string, TextStyle)>())));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{")]
        [InlineData("[")]
        [InlineData("nope")]
        [InlineData("null")]
        [InlineData("42")]
        [InlineData("\"글\"")]
        [InlineData("{\"Name\":\"a\",\"Style\":{}}")]
        [InlineData("[{\"Name\":\"a\",\"Style\":{}}")]
        [InlineData("[{\"Name\":\"a\",\"Style\":{}}] 쓰레기")]
        [InlineData("/* 주석 */ [{\"Name\":\"a\",\"Style\":{}}]")]
        [InlineData("[{\"Name\":\"a\",\"Style\":{},}]")]
        public void Read_BrokenJsonOrWrongShape_IsEmpty(string json)
        {
            Assert.Empty(TextStyleLibrary.Read(json));
        }

        [Fact]
        public void BrokenUtf16_IsRejectedWithoutException()
        {
            // 설정 글 자체에 짝 없는 서로게이트가 있으면 JSON으로 읽을 수 없다 — 빈 목록
            Assert.Empty(TextStyleLibrary.Read("[{\"Name\":\"a\uD800\",\"Style\":{}}]"));
            // JSON 이스케이프로 적은 짝 없는 서로게이트·비문자 이름은 그 항목만 건너뛴다
            var read = TextStyleLibrary.Read("[{\"Name\":\"a\\ud800\",\"Style\":{}},{\"Name\":\"b\\uffff\",\"Style\":{}},{\"Name\":\"c\",\"Style\":{}}]");
            Assert.Equal("c", Assert.Single(read).Name);
            Assert.Null(TextStyleLibrary.CleanName("짝\uD800없음"));
            Assert.Null(TextStyleLibrary.CleanName("짝\uDC00없음"));
            Assert.Null(TextStyleLibrary.CleanName("\uDC00\uD800"));
            Assert.Null(TextStyleLibrary.CleanName("비문자\uFFFE"));
            Assert.Null(TextStyleLibrary.CleanName("비문자\uFFFF"));
            Assert.Null(TextStyleLibrary.CleanName("비문자\uFDD0"));
            Assert.Null(TextStyleLibrary.CleanName("비문자\U0001FFFF"));
            Assert.Equal(TextStyleLibrary.NameRule, TextStyleLibrary.AddOrReplace(new List<(string, TextStyle)>(), "짝\uD800", new TextStyle()));
        }

        [Fact]
        public void Read_TooDeepOrTooLong_IsEmptyWithoutStalling()
        {
            var watch = Stopwatch.StartNew();
            // 깊은 중첩(목록 안, 스타일 안)
            Assert.Empty(TextStyleLibrary.Read(new string('[', 100_000) + new string(']', 100_000)));
            Assert.Empty(TextStyleLibrary.Read("[{\"Name\":\"a\",\"Style\":{\"Fill\":" + new string('[', 1000) + new string(']', 1000) + "}}]"));
            // 상한보다 긴 설정은 올바른 JSON이어도 읽지 않는다
            var valid = TextStyleLibrary.Write(new List<(string, TextStyle)> { ("a", new TextStyle()) });
            Assert.Single(TextStyleLibrary.Read(valid));
            var padded = valid.Insert(1, new string(' ', TextStyleLibrary.MaxJsonLength - valid.Length));
            Assert.Equal(TextStyleLibrary.MaxJsonLength, padded.Length);
            Assert.Single(TextStyleLibrary.Read(padded));
            Assert.Empty(TextStyleLibrary.Read(valid.Insert(1, new string(' ', TextStyleLibrary.MaxJsonLength + 1 - valid.Length))));
            Assert.Empty(TextStyleLibrary.Read(new string('a', 10_000_000)));
            Assert.True(watch.ElapsedMilliseconds < 5000, watch.ElapsedMilliseconds + "ms");
        }

        [Fact]
        public void Read_TakesFirst50_DropsTheRest()
        {
            var array = new JsonArray();
            for (var i = 1; i <= 51; i++)
                array.Add(Entry("스타일 " + i, EmptyStyle()));
            var read = TextStyleLibrary.Read(array.ToJsonString());
            Assert.Equal(TextStyleLibrary.MaxCount, read.Count);
            Assert.Equal("스타일 1", read[0].Name);
            Assert.Equal("스타일 50", read[49].Name);

            // 건너뛴 항목은 50개에 세지 않는다
            array.Insert(0, Entry("", EmptyStyle()));
            array.Insert(0, "쓰레기");
            read = TextStyleLibrary.Read(array.ToJsonString());
            Assert.Equal(50, read.Count);
            Assert.Equal("스타일 50", read[49].Name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\u3000\u00A0")]
        [InlineData("줄\n바꿈")]
        [InlineData("줄\r바꿈")]
        [InlineData("탭\t이름")]
        [InlineData("종\u0007소리")]
        [InlineData("지움\u007F")]
        [InlineData("C1\u0085제어")]
        [InlineData("방향\u202E바꿈")]
        [InlineData("방향\u202A끼움")]
        [InlineData("방향\u2066격리\u2069")]
        [InlineData("표시\u200F")]
        [InlineData("폭\u200B없음")]
        [InlineData("이음\u200D표")]
        [InlineData("\uFEFF머리")]
        [InlineData("줄\u2028구분")]
        [InlineData("문단\u2029구분")]
        [InlineData("태그\U000E0041")]
        [InlineData("\u3164")]
        [InlineData("네온\u3164")]
        [InlineData("\u2800")]
        [InlineData("조합\u034F이음")]
        [InlineData("채움\u115F자모")]
        [InlineData("반각\uFFA0채움")]
        [InlineData("몽골\u180B선택")]
        public void CleanName_RejectsEmptyControlFormatAndInvisibleCharacters(string name)
        {
            Assert.Null(TextStyleLibrary.CleanName(name));
        }

        [Theory]
        [InlineData("노랑 제목", "노랑 제목")]
        [InlineData("  노랑 제목  ", "노랑 제목")]
        [InlineData("\u3000노랑\u00A0", "노랑")]
        [InlineData("Neon 😀", "Neon 😀")]
        [InlineData("a  b", "a  b")]
        [InlineData("하트 ❤️", "하트 ❤️")]
        // 조합형 한글은 완성형으로 모은다
        [InlineData("\u1112\u1161\u11AB\u1100\u1173\u11AF", "한글")]
        public void CleanName_TrimsAndComposes(string name, string expected)
        {
            Assert.Equal(expected, TextStyleLibrary.CleanName(name));
        }

        [Fact]
        public void CleanName_LengthIsUtf16Units_1To40()
        {
            Assert.Equal(new string('가', 40), TextStyleLibrary.CleanName(new string('가', 40)));
            Assert.Null(TextStyleLibrary.CleanName(new string('가', 41)));
            Assert.Equal("a", TextStyleLibrary.CleanName("a"));
            // 앞뒤 공백은 세지 않는다
            Assert.NotNull(TextStyleLibrary.CleanName("  " + new string('a', 40) + "  "));
            // 이모지 하나는 2단위
            Assert.NotNull(TextStyleLibrary.CleanName(string.Concat(Enumerable.Repeat("😀", 20))));
            Assert.Null(TextStyleLibrary.CleanName(string.Concat(Enumerable.Repeat("😀", 21))));
            // 조합형 40글자(120단위)도 모으면 40자
            var decomposed = string.Concat(Enumerable.Repeat("\u1112\u1161\u11AB", 40));
            Assert.Equal(new string('한', 40), TextStyleLibrary.CleanName(decomposed));
            Assert.Null(TextStyleLibrary.CleanName(new string('a', 1_000_000)));
        }

        [Fact]
        public void Read_SkipsBadNamesAndStyles_FirstOfSameNameWins()
        {
            var array = new JsonArray
            {
                Entry("좋음", new JsonObject { ["Bold"] = true }),
                Entry("", EmptyStyle()),
                Entry("   ", EmptyStyle()),
                Entry(new string('가', 41), EmptyStyle()),
                Entry(new string('나', 40), EmptyStyle()),
                Entry("제어\u0001문자", EmptyStyle()),
                Entry("방향\u202E바꿈", EmptyStyle()),
                Entry(42, EmptyStyle()),
                Entry(null, EmptyStyle()),
                new JsonObject { ["Style"] = EmptyStyle() },
                new JsonObject { ["Name"] = "스타일 없음" },
                Entry("스타일 null", null),
                Entry("스타일 배열", new JsonArray()),
                Entry("스타일 글", "굵게"),
                // 형식이 맞지 않는 값은 그 항목만 건너뛴다
                Entry("굵게 자리에 글", new JsonObject { ["Bold"] = "yes" }),
                Entry("색 자리에 객체", new JsonObject { ["Fill"] = new JsonObject { ["Colors"] = new JsonObject() } }),
                Entry("채우기 자리에 숫자", new JsonObject { ["Fill"] = 3 }),
                // 겹친 이름: 대소문자·앞뒤 공백·조합형이 달라도 같은 이름 — 앞의 것
                Entry("Neon", new JsonObject { ["Italic"] = true }),
                Entry("  neon ", new JsonObject { ["Bold"] = true }),
                Entry("NEON", EmptyStyle()),
                Entry("한", new JsonObject { ["Italic"] = true }),
                Entry("\u1112\u1161\u11AB", new JsonObject { ["Bold"] = true }),
                // 앞의 같은 이름이 스타일이 없어 빠졌으면 뒤의 것을 받는다
                Entry("다시", null),
                Entry("다시", new JsonObject { ["Bold"] = true }),
                "쓰레기",
                12,
                null,
                new JsonArray()
            };
            var read = TextStyleLibrary.Read(array.ToJsonString());
            Assert.Equal(new[] { "좋음", new string('나', 40), "Neon", "한", "다시" }, read.Select(s => s.Name));
            Assert.True(read[0].Style.Bold);
            Assert.True(read[2].Style.Italic && !read[2].Style.Bold);
            Assert.True(read[3].Style.Italic && !read[3].Style.Bold);
            Assert.True(read[4].Style.Bold);
            // 빠진 묶음은 기본값으로 채워진다(정규화)
            Assert.All(read, s => Assert.True(s.Style.Fill != null && s.Style.Outline != null && s.Style.Shadow != null && s.Style.Glow != null && s.Style.Reflection != null && s.Style.Warp != null));
        }

        [Fact]
        public void Read_HostileStyleValues_AreNormalized()
        {
            var style = new JsonObject
            {
                ["FontFamily"] = @"\\host\share\#x",
                ["LetterSpacing"] = 1e9,
                ["LineSpacing"] = "NaN",
                ["Align"] = "Sideways",
                ["Fill"] = new JsonObject
                {
                    ["Kind"] = "Rainbow",
                    ["Colors"] = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)(i == 1 ? "red" : "#00000" + (i % 10))).ToArray()),
                    ["Angle"] = "Infinity"
                },
                ["Outline"] = new JsonObject { ["Enabled"] = true, ["Color"] = "#12", ["Width"] = -1e9, ["Join"] = 99 },
                ["Shadow"] = new JsonObject { ["Color"] = new string('#', 100_000), ["Distance"] = "-Infinity", ["Opacity"] = 5 },
                ["Glow"] = null,
                ["Reflection"] = new JsonObject { ["Size"] = 0, ["Gap"] = 1e300 },
                ["Warp"] = new JsonObject { ["Kind"] = "1", ["Amount"] = -1e9 },
                ["Unknown"] = new JsonObject { ["Deep"] = new JsonArray(1, 2, 3) }
            };
            var json = new JsonArray { Entry("악성", style) }.ToJsonString().Replace("\"LetterSpacing\":1000000000", "\"LetterSpacing\":1e400");
            var read = TextStyleLibrary.Read(json);
            var s = Assert.Single(read).Style;
            Assert.Equal(TextStyle.DefaultFont, s.FontFamily);
            // 1e400은 무한대로 읽혀 기본값
            Assert.Equal(0, s.LetterSpacing);
            Assert.Equal(1.2, s.LineSpacing);
            // 모르는 열거 이름·숫자 글은 첫 값(프로젝트 파일의 맞춤 방식과 같음)
            Assert.Equal(TextAlign.Left, s.Align);
            Assert.Equal(FillKind.None, s.Fill.Kind);
            Assert.Equal(new[] { "#000000", "#FFFFFF", "#000002", "#000003" }, s.Fill.Colors);
            Assert.Equal(0, s.Fill.Angle);
            Assert.True(s.Outline.Enabled);
            Assert.Equal("#000000", s.Outline.Color);
            Assert.Equal(0, s.Outline.Width);
            Assert.Equal(LineJoinKind.Round, s.Outline.Join);
            Assert.Equal("#000000", s.Shadow.Color);
            Assert.Equal(6, s.Shadow.Distance);
            Assert.Equal(1, s.Shadow.Opacity);
            Assert.NotNull(s.Glow);
            Assert.False(s.Glow.Enabled);
            Assert.Equal(10, s.Reflection.Size);
            Assert.Equal(100, s.Reflection.Gap);
            Assert.Equal(WarpKind.None, s.Warp.Kind);
            Assert.Equal(-100, s.Warp.Amount);
            // 읽은 것은 정규화가 끝난 값이다
            var before = Json(s);
            s.Normalize();
            Assert.Equal(before, Json(s));
        }

        [Fact]
        public void Write_KeepsTheRules_AndDoesNotTouchInput()
        {
            var wide = new TextStyle { LetterSpacing = 5000 };
            var styles = new List<(string Name, TextStyle Style)>
            {
                ("  하나 ", wide),
                ("하나", new TextStyle { Bold = true }),
                ("", new TextStyle()),
                ("줄\n바꿈", new TextStyle()),
                ("스타일 없음", null),
                (null, new TextStyle())
            };
            for (var i = 0; i < 60; i++)
                styles.Add(("채움 " + i, new TextStyle()));
            var read = TextStyleLibrary.Read(TextStyleLibrary.Write(styles));
            Assert.Equal(50, read.Count);
            Assert.Equal("하나", read[0].Name);
            Assert.Equal(1000, read[0].Style.LetterSpacing);
            Assert.False(read[0].Style.Bold);
            Assert.Equal("채움 48", read[49].Name);
            // 받은 스타일은 그대로
            Assert.Equal(5000, wide.LetterSpacing);
            Assert.Equal("[]", TextStyleLibrary.Write(null));
        }

        [Fact]
        public void Write_LongestPossible50_StaysUnderTheReadLimit()
        {
            var style = AllFieldsChanged();
            style.FontFamily = new string('굴', TextStyle.MaxFontNameLength);
            style.Fill.Colors = new List<string> { "#80FFFFFF", "#80FFFFFF", "#80FFFFFF", "#80FFFFFF" };
            style.LetterSpacing = 123.45678901234567;
            style.LineSpacing = 1.2345678901234567;
            style.Shadow.Opacity = 0.12345678901234567;
            var styles = Enumerable.Range(0, 50).Select(i => (Name: i.ToString("00") + new string('가', 38), Style: style)).ToList();
            var json = TextStyleLibrary.Write(styles);
            Assert.True(json.Length * 2 < TextStyleLibrary.MaxJsonLength, json.Length + "자");
            Assert.Equal(50, TextStyleLibrary.Read(json).Count);
        }

        [Fact]
        public void AddOrReplace_ClonesNormalizes_ReplacesByName_RefusesThe51st()
        {
            var styles = new List<(string Name, TextStyle Style)>();
            var style = new TextStyle { Bold = true, LetterSpacing = 5000 };
            Assert.Null(TextStyleLibrary.AddOrReplace(styles, "  노랑  ", style));
            Assert.Equal("노랑", Assert.Single(styles).Name);
            // 넣은 것은 정규화한 깊은 복사 — 클립의 스타일을 나중에 고쳐도 그대로
            Assert.NotSame(style, styles[0].Style);
            Assert.Equal(1000, styles[0].Style.LetterSpacing);
            style.Bold = false;
            style.Fill.Colors[0] = "#123456";
            Assert.True(styles[0].Style.Bold);
            Assert.Equal("#FFFFFF", styles[0].Style.Fill.Colors[0]);

            // 같은 이름(대소문자 무시)은 그 자리에서 바꾸고 이름은 새로 적은 대로
            Assert.Null(TextStyleLibrary.AddOrReplace(styles, "Neon", new TextStyle()));
            Assert.Null(TextStyleLibrary.AddOrReplace(styles, " NEON ", new TextStyle { Italic = true }));
            Assert.Equal(new[] { "노랑", "NEON" }, styles.Select(s => s.Name));
            Assert.True(styles[1].Style.Italic);

            // 규칙에 맞지 않는 이름은 알릴 글, 목록은 그대로
            Assert.Equal(TextStyleLibrary.NameRule, TextStyleLibrary.AddOrReplace(styles, "줄\n바꿈", new TextStyle()));
            Assert.Equal(TextStyleLibrary.NameRule, TextStyleLibrary.AddOrReplace(styles, new string('가', 41), new TextStyle()));
            Assert.Equal(2, styles.Count);

            // 50개가 차면 새 이름은 거절(조용히 버리지 않는다), 있는 이름은 바꿀 수 있다
            for (var i = styles.Count; i < TextStyleLibrary.MaxCount; i++)
                Assert.Null(TextStyleLibrary.AddOrReplace(styles, "채움 " + i, new TextStyle()));
            Assert.Equal(50, styles.Count);
            Assert.Equal(TextStyleLibrary.FullMessage, TextStyleLibrary.AddOrReplace(styles, "51번째", new TextStyle()));
            Assert.Equal(50, styles.Count);
            Assert.Equal(-1, TextStyleLibrary.IndexOf(styles, "51번째"));
            Assert.Null(TextStyleLibrary.AddOrReplace(styles, "노랑", new TextStyle { Italic = true }));
            Assert.True(styles[0].Style.Italic);
            Assert.Equal(50, styles.Count);
            Assert.Throws<ArgumentNullException>(() => TextStyleLibrary.AddOrReplace(styles, "노랑", null));
        }

        [Fact]
        public void Remove_And_IndexOf_ByName()
        {
            var styles = new List<(string Name, TextStyle Style)> { ("노랑", new TextStyle()), ("Neon", new TextStyle()), ("한", new TextStyle()) };
            Assert.Equal(1, TextStyleLibrary.IndexOf(styles, " neon "));
            Assert.Equal(2, TextStyleLibrary.IndexOf(styles, "\u1112\u1161\u11AB"));
            Assert.Equal(-1, TextStyleLibrary.IndexOf(styles, "없음"));
            Assert.Equal(-1, TextStyleLibrary.IndexOf(styles, ""));
            Assert.Equal(-1, TextStyleLibrary.IndexOf(null, "노랑"));
            Assert.True(TextStyleLibrary.Remove(styles, "NEON"));
            Assert.Equal(new[] { "노랑", "한" }, styles.Select(s => s.Name));
            Assert.False(TextStyleLibrary.Remove(styles, "NEON"));
            Assert.False(TextStyleLibrary.Remove(styles, "줄\n바꿈"));
            Assert.Equal(2, styles.Count);
        }

        // ---------- 이름 바꾸기·순서·파일로 옮기기(#27) ----------

        private static List<(string Name, TextStyle Style)> Three()
        {
            return new List<(string Name, TextStyle Style)> { ("노랑", new TextStyle { Bold = true }), ("Neon", new TextStyle()), ("한", new TextStyle { Italic = true }) };
        }

        [Fact]
        public void Rename_KeepsTheStyleAndPlace_RefusesBadOrTakenNames()
        {
            var styles = Three();
            var bold = styles[0].Style;
            Assert.Null(TextStyleLibrary.Rename(styles, "노랑", "  제목  "));
            Assert.Equal(new[] { "제목", "Neon", "한" }, styles.Select(s => s.Name));
            Assert.Same(bold, styles[0].Style);
            // 대소문자만 바꾸기는 된다(같은 스타일)
            Assert.Null(TextStyleLibrary.Rename(styles, "neon", "NEON"));
            Assert.Equal("NEON", styles[1].Name);
            // 다른 스타일의 이름(대소문자 무시)·규칙에 맞지 않는 이름·없는 스타일은 거절하고 그대로
            Assert.NotNull(TextStyleLibrary.Rename(styles, "한", "제목"));
            Assert.Equal(TextStyleLibrary.NameRule, TextStyleLibrary.Rename(styles, "한", "줄\n바꿈"));
            Assert.Equal(TextStyleLibrary.NameRule, TextStyleLibrary.Rename(styles, "한", new string('가', 41)));
            Assert.NotNull(TextStyleLibrary.Rename(styles, "없음", "새 이름"));
            Assert.Equal(new[] { "제목", "NEON", "한" }, styles.Select(s => s.Name));
        }

        [Fact]
        public void Move_ChangesTheOrder_IgnoresBadIndexes()
        {
            var styles = Three();
            Assert.True(TextStyleLibrary.Move(styles, 2, 0));
            Assert.Equal(new[] { "한", "노랑", "Neon" }, styles.Select(s => s.Name));
            Assert.True(TextStyleLibrary.Move(styles, 0, 1));
            Assert.Equal(new[] { "노랑", "한", "Neon" }, styles.Select(s => s.Name));
            Assert.False(TextStyleLibrary.Move(styles, 1, 1));
            Assert.False(TextStyleLibrary.Move(styles, -1, 0));
            Assert.False(TextStyleLibrary.Move(styles, 0, 3));
            Assert.False(TextStyleLibrary.Move(null, 0, 1));
            Assert.Equal(new[] { "노랑", "한", "Neon" }, styles.Select(s => s.Name));
            // 바뀐 순서가 설정에 그대로 남는다
            Assert.Equal(new[] { "노랑", "한", "Neon" }, TextStyleLibrary.Read(TextStyleLibrary.Write(styles)).Select(s => s.Name));
        }

        [Fact]
        public void Import_AddsNewNames_SkipsTakenNames_StopsAtTheLimit()
        {
            // 내보낸 파일 = 설정과 같은 글(다른 PC에서 가져옴)
            var file = TextStyleLibrary.Write(new List<(string Name, TextStyle Style)>
            {
                ("NEON", new TextStyle { Bold = true }), ("파랑", new TextStyle { Italic = true }), ("초록", new TextStyle())
            });
            var styles = Three();
            var neon = styles[1].Style;
            var result = TextStyleLibrary.Import(styles, file);
            Assert.Equal((2, 1, 0), (result.Added, result.Skipped, result.Full));
            Assert.Equal(new[] { "노랑", "Neon", "한", "파랑", "초록" }, styles.Select(s => s.Name));
            // 같은 이름은 내 것을 그대로 둔다
            Assert.Same(neon, styles[1].Style);
            Assert.True(styles[3].Style.Italic);
            Assert.Contains("2개", result.Message);
            Assert.Contains("1개", result.Message);

            // 같은 파일을 또 가져오면 모두 건너뛴다(쌓이지 않음)
            var again = TextStyleLibrary.Import(styles, file);
            Assert.Equal((0, 3, 0), (again.Added, again.Skipped, again.Full));
            Assert.Equal(5, styles.Count);

            // 50개가 차면 남은 것은 넣지 못한다
            var many = Enumerable.Range(1, 48).Select(i => ("가득 " + i, new TextStyle())).ToList();
            var full = Three();
            var outcome = TextStyleLibrary.Import(full, TextStyleLibrary.Write(many));
            Assert.Equal((47, 0, 1), (outcome.Added, outcome.Skipped, outcome.Full));
            Assert.Equal(TextStyleLibrary.MaxCount, full.Count);
            Assert.Contains("50", outcome.Message);
        }

        [Fact]
        public void Import_BadFile_AddsNothing_WithTheReadRules()
        {
            foreach (var bad in new[] { "", "not json", "{\"Name\":\"x\"}", "[]", new string('[', 100), "[" + new string(' ', TextStyleLibrary.MaxJsonLength) + "]" })
            {
                var styles = Three();
                var result = TextStyleLibrary.Import(styles, bad);
                Assert.Equal((0, 0, 0), (result.Added, result.Skipped, result.Full));
                Assert.Equal(3, styles.Count);
                Assert.Contains("가져올 스타일이 없습니다", result.Message);
            }
            // 규칙에 맞지 않는 항목(보이지 않는 글자 이름)은 읽기와 같이 버리고, 스타일 값은 정규화한다
            var mixed = "[{\"Name\":\"a\u200Bb\",\"Style\":{}},{\"Name\":\"크게\",\"Style\":{\"LetterSpacing\":1e9}}]";
            var list = Three();
            Assert.Equal(1, TextStyleLibrary.Import(list, mixed).Added);
            Assert.Equal("크게", list.Last().Name);
            Assert.True(list.Last().Style.LetterSpacing < 1e9);
        }

        [Fact]
        public void NewName_IsTheNextFreeNumber()
        {
            Assert.Equal("내 스타일 1", TextStyleLibrary.NewName(new List<(string, TextStyle)>()));
            Assert.Equal("내 스타일 1", TextStyleLibrary.NewName(null));
            var styles = new List<(string Name, TextStyle Style)> { ("내 스타일 2", new TextStyle()), ("내 스타일 3", new TextStyle()) };
            Assert.Equal("내 스타일 4", TextStyleLibrary.NewName(styles));
            styles.RemoveAt(1);
            Assert.Equal("내 스타일 3", TextStyleLibrary.NewName(styles));
            Assert.NotNull(TextStyleLibrary.CleanName(TextStyleLibrary.NewName(styles)));
        }
    }
}
