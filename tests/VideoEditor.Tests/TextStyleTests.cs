using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class TextStyleTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-style-" + Guid.NewGuid().ToString("N"));

        public TextStyleTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); } catch (IOException) { }
        }

        private static (Project Project, Clip Text) TextProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            var text = TimelineEdits.CreateText("안녕", 0, 30);
            text.TextColor = "#ff0000";
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            return (project, text);
        }

        /// <summary>프로젝트를 JSON으로 바꿔 고친 뒤 파일로 써서 연다(남이 만든 파일 흉내).</summary>
        private Project LoadEdited(Project project, Action<JsonObject> edit)
        {
            var json = JsonNode.Parse(ProjectFile.Snapshot(project)).AsObject();
            edit(json);
            var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ProjectFile.Extension);
            File.WriteAllText(path, json.ToJsonString());
            return ProjectFile.Load(path);
        }

        private static JsonObject FirstClip(JsonObject project)
        {
            return project["VideoTracks"][0]["Clips"][0].AsObject();
        }

        private static RenderOptions Options()
        {
            return new RenderOptions { From = 0, To = 30, Width = 640, Height = 360, Audio = false, FontFile = @"C:\Windows\Fonts\malgun.ttf", TempFolder = @"C:\Temp" };
        }

        /// <summary>필터 문자열(글을 담는 임시 파일 이름은 매번 다르므로 지운다).</summary>
        private static string Filter(Project project)
        {
            return System.Text.RegularExpressions.Regex.Replace(RenderGraph.Build(project, Options()).Filter, "fv-text-[0-9a-f]+", "fv-text-*");
        }

        [Fact]
        public void V1File_HasNoStyle_AndRendersAsBefore()
        {
            var (project, _) = TextProject();
            var expected = Filter(project);
            var loaded = LoadEdited(project, json =>
            {
                json["Version"] = 1;
                FirstClip(json).Remove("Style");
            });
            Assert.Null(loaded.AllClips.Single().Style);
            var filter = Filter(loaded);
            Assert.Equal(expected, filter);
            Assert.Contains("drawtext", filter);
        }

        [Fact]
        public void SavingV1File_WritesCurrentVersion()
        {
            var (project, _) = TextProject();
            var loaded = LoadEdited(project, json => json["Version"] = 1);
            Assert.Equal(1, loaded.Version);
            var path = Path.Combine(_folder, "saved" + ProjectFile.Extension);
            ProjectFile.Save(loaded, path);
            Assert.Equal(2, Project.CurrentVersion);
            Assert.Equal(Project.CurrentVersion, ProjectFile.Load(path).Version);
        }

        [Fact]
        public void NewerVersion_IsRejected()
        {
            var (project, _) = TextProject();
            Assert.Throws<InvalidDataException>(() => LoadEdited(project, json => json["Version"] = Project.AnimationVersion + 1));
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

        [Fact]
        public void SaveLoad_RoundTripsEveryField()
        {
            var (project, text) = TextProject();
            text.Style = AllFieldsChanged();
            var path = Path.Combine(_folder, "round" + ProjectFile.Extension);
            ProjectFile.Save(project, path);
            var loaded = ProjectFile.Load(path).AllClips.Single().Style;
            var expected = System.Text.Json.JsonSerializer.Serialize(AllFieldsChanged());
            Assert.Equal(expected, System.Text.Json.JsonSerializer.Serialize(loaded));
        }

        [Fact]
        public void Load_NormalizesHostileValues()
        {
            var (project, text) = TextProject();
            text.Style = new TextStyle();
            var loaded = LoadEdited(project, json =>
            {
                var style = FirstClip(json)["Style"].AsObject();
                style["FontFamily"] = @"\\host\f#x";
                style["LetterSpacing"] = 1e9;
                style["LineSpacing"] = -1e9;
                style["Align"] = 99;
                style["Fill"]["Kind"] = 42;
                style["Fill"]["Colors"] = new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode)("#00000" + i)).ToArray());
                style["Fill"]["Colors"][1] = "red";
                style["Fill"]["Angle"] = 1e9;
                style["Outline"]["Color"] = "#12";
                style["Outline"]["Width"] = 1e9;
                style["Outline"]["Join"] = -1;
                style["Shadow"]["Color"] = "";
                style["Shadow"]["Distance"] = -1e9;
                style["Shadow"]["Opacity"] = 5;
                style["Glow"] = null;
                style["Reflection"]["Size"] = 0;
                style["Warp"]["Kind"] = 1000;
                style["Warp"]["Amount"] = -1e9;
            });
            var s = loaded.AllClips.Single().Style;
            Assert.Equal(TextStyle.DefaultFont, s.FontFamily);
            Assert.Equal(1000, s.LetterSpacing);
            Assert.Equal(0.5, s.LineSpacing);
            Assert.Equal(TextAlign.Center, s.Align);
            Assert.Equal(FillKind.Solid, s.Fill.Kind);
            Assert.Equal(new[] { "#000000", "#FFFFFF", "#000002", "#000003" }, s.Fill.Colors);
            Assert.Equal(360, s.Fill.Angle);
            Assert.Equal("#000000", s.Outline.Color);
            Assert.Equal(50, s.Outline.Width);
            Assert.Equal(LineJoinKind.Round, s.Outline.Join);
            Assert.Equal("#000000", s.Shadow.Color);
            Assert.Equal(0, s.Shadow.Distance);
            Assert.Equal(1, s.Shadow.Opacity);
            Assert.NotNull(s.Glow);
            Assert.Equal(10, s.Reflection.Size);
            Assert.Equal(WarpKind.None, s.Warp.Kind);
            Assert.Equal(-100, s.Warp.Amount);
        }

        [Fact]
        public void Normalize_NaNAndInfinity_BecomeDefaults()
        {
            var style = new TextStyle { LetterSpacing = double.NaN, LineSpacing = double.PositiveInfinity };
            style.Shadow.Blur = double.NaN;
            style.Warp.Amount = double.NegativeInfinity;
            style.Fill.Colors = new List<string>();
            style.Normalize();
            Assert.Equal(0, style.LetterSpacing);
            Assert.Equal(1.2, style.LineSpacing);
            Assert.Equal(6, style.Shadow.Blur);
            Assert.Equal(50, style.Warp.Amount);
            Assert.Equal(new[] { "#FFFFFF" }, style.Fill.Colors);

            style.Fill.Colors = null;
            style.Normalize();
            Assert.Equal(new[] { "#FFFFFF" }, style.Fill.Colors);
        }

        [Theory]
        [InlineData(@"\\host\f#x")]
        [InlineData("file:///c:/x")]
        [InlineData("a/b")]
        [InlineData("a#b")]
        [InlineData("a%20b")]
        [InlineData("a\u0001b")]
        [InlineData("a\nb")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void FontName_PathLikeOrControl_IsRejected(string name)
        {
            Assert.False(TextStyle.IsSafeFontName(name));
            var style = new TextStyle { FontFamily = name };
            style.Normalize();
            Assert.Equal(TextStyle.DefaultFont, style.FontFamily);
        }

        [Fact]
        public void FontName_Length_IsLimited()
        {
            Assert.True(TextStyle.IsSafeFontName(new string('가', 100)));
            Assert.False(TextStyle.IsSafeFontName(new string('가', 101)));
            Assert.True(TextStyle.IsSafeFontName("맑은 고딕"));
        }

        [Theory]
        [InlineData("#ff00aa", "#FF00AA")]
        [InlineData("#80ff00aa", "#80FF00AA")]
        [InlineData("red", "#FALLBK")]
        [InlineData("#12", "#FALLBK")]
        [InlineData("", "#FALLBK")]
        [InlineData(null, "#FALLBK")]
        [InlineData("#GG0000", "#FALLBK")]
        [InlineData("ff00aa0", "#FALLBK")]
        public void NormalizeColor(string value, string expected)
        {
            Assert.Equal(expected, TextStyle.NormalizeColor(value, "#FALLBK"));
        }

        [Fact]
        public void NonTextClip_LosesStyle()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 30);
            clip.Style = new TextStyle();
            var loaded = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            Assert.Null(loaded.AllClips.Single().Style);
        }

        [Fact]
        public void FromLegacy_TakesTextColor()
        {
            var style = TextStyle.FromLegacy(new Clip { Kind = ClipKind.Text, TextColor = "#ff0000" });
            Assert.Equal(FillKind.Solid, style.Fill.Kind);
            Assert.Equal(new[] { "#FF0000" }, style.Fill.Colors);
            Assert.Equal(TextStyle.DefaultFont, style.FontFamily);
            // 예전 그리기(drawtext)는 여러 줄을 왼쪽 맞춤으로 그렸다
            Assert.Equal(TextAlign.Left, style.Align);
        }

        [Fact]
        public void CloneAndSplit_AreDeepCopies()
        {
            var (project, text) = TextProject();
            text.Style = AllFieldsChanged();
            var right = TimelineEdits.Split(project, new[] { text }, 15).Single();
            var copy = text.CloneWithNewId();

            right.Style.Fill.Colors[0] = "#ABCDEF";
            right.Style.Outline.Width = 1;
            copy.Style.Warp.Amount = 99;
            copy.Style.FontFamily = "다른 글꼴";

            Assert.Equal("#FF0000", text.Style.Fill.Colors[0]);
            Assert.Equal(8, text.Style.Outline.Width);
            Assert.Equal(-30, text.Style.Warp.Amount);
            Assert.Equal("Noto Sans KR", text.Style.FontFamily);
            Assert.NotSame(text.Style, right.Style);
            Assert.NotSame(text.Style.Fill.Colors, copy.Style.Fill.Colors);
        }

        [Fact]
        public void StyleEdit_IsUndoable()
        {
            var session = new EditorSession();
            var text = TimelineEdits.CreateText("안녕", 0, 30);
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, text, 0));
            Assert.True(session.Execute(p => p.FindClip(text.Id).Style = TextStyle.FromLegacy(p.FindClip(text.Id))));
            Assert.True(session.Execute(p => p.FindClip(text.Id).Style.Outline.Width = 20, "outline:" + text.Id));
            Assert.Equal(20, session.Project.FindClip(text.Id).Style.Outline.Width);

            Assert.True(session.Undo());
            Assert.Equal(4, session.Project.FindClip(text.Id).Style.Outline.Width);
            Assert.True(session.Undo());
            Assert.Null(session.Project.FindClip(text.Id).Style);
        }
    }
}
