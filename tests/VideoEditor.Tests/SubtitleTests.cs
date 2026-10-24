using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>자막 트랙(이슈 #25): 자막 파일 읽기, 가져오기 배치, ASS 만들기, 렌더 그래프.</summary>
    public class SubtitleTests
    {
        private const string Srt =
            "1\n00:00:00,000 --> 00:00:06,040\n10년 넘게 형제처럼\n지내던 후배가\n\n" +
            "2\n00:00:06,040 --> 00:00:11,120\n<i>어글에서</i> 피가 &amp; {\\an8}꺾고\n\n";

        private static SubtitleCue Cue(double start, double end, string text)
        {
            return new SubtitleCue { Start = start, End = end, Text = text };
        }

        // ---------- 읽기 ----------

        [Fact]
        public void Parse_Srt_CleansTagsAndEntities()
        {
            var cues = SubtitleFile.Parse(Srt);
            Assert.Equal(2, cues.Count);
            Assert.Equal(0, cues[0].Start, 6);
            Assert.Equal(6.04, cues[0].End, 6);
            Assert.Equal("10년 넘게 형제처럼\n지내던 후배가", cues[0].Text);
            Assert.Equal(6.04, cues[1].Start, 6);
            Assert.Equal(11.12, cues[1].End, 6);
            Assert.Equal("어글에서 피가 & 꺾고", cues[1].Text);
        }

        [Fact]
        public void Parse_WebVtt_SkipsHeaderAndNotes()
        {
            const string vtt = "WEBVTT\n\nNOTE 메모\n아무 줄\n\n00:01.500 --> 00:03.000 align:start position:10%\n<v 철수>안녕 <c.yellow>세상</c>\n\nid-2\n01:00:00.000 --> 01:00:01.250\n끝\n";
            var cues = SubtitleFile.Parse(vtt);
            Assert.Equal(2, cues.Count);
            Assert.Equal(1.5, cues[0].Start, 6);
            Assert.Equal(3.0, cues[0].End, 6);
            Assert.Equal("안녕 세상", cues[0].Text);
            Assert.Equal(3600, cues[1].Start, 6);
            Assert.Equal(3601.25, cues[1].End, 6);
        }

        [Fact]
        public void Parse_Robust_ToMissingBlankLines_CrLf_AndBadBlocks()
        {
            var text = string.Join("\r\n", new[]
            {
                "00:00:01,000 --> 00:00:02,000", "가",
                "2", "00:00:02,000 --> 00:00:03,000", "나" + (char)1 + "다",
                "",
                "4", "00:00:05,000 --> 00:00:04,000", "끝이 시작보다 앞",
                "",
                "5", "00:61:00,000 --> 00:62:00,000", "분이 60 이상",
                "",
                "6", "00:00:07,000 --> 00:00:08,000", "   ", "<b></b>",
                "",
                "7", "아무 시각이 아님 --> 이것도", "버림",
                "",
                "8", "00:00:09,5 --> 00:00:10,25", "  앞뒤 빈칸  "
            });
            var cues = SubtitleFile.Parse(text);
            Assert.Equal(new[] { "가", "나다", "앞뒤 빈칸" }, cues.Select(c => c.Text));
            Assert.Equal(9.5, cues[2].Start, 6);
            Assert.Equal(10.25, cues[2].End, 6);
        }

        [Fact]
        public void Parse_Limits()
        {
            var many = new StringBuilder();
            for (var i = 0; i < SubtitleFile.MaxCues + 100; i++)
                many.Append("00:00:01,000 --> 00:00:02,000\n가\n\n");
            Assert.Equal(SubtitleFile.MaxCues, SubtitleFile.Parse(many.ToString()).Count);
            var longText = SubtitleFile.Parse("00:00:01,000 --> 00:00:02,000\n" + new string('가', 5000));
            Assert.Equal(SubtitleFile.MaxTextLength, Assert.Single(longText).Text.Length);
            // 24시간이 넘는 시각은 버린다
            Assert.Empty(SubtitleFile.Parse("25:00:00,000 --> 25:00:01,000\n가"));
            Assert.Empty(SubtitleFile.Parse(null));
        }

        [Fact]
        public void Decode_Utf8Bom_Utf16_PlainUtf8_AndCp949()
        {
            const string text = "00:00:01,000 --> 00:00:02,000\n한글 자막";
            var utf8 = Encoding.UTF8.GetBytes(text);
            Assert.Equal(text, SubtitleFile.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray()));
            Assert.Equal(text, SubtitleFile.Decode(utf8));
            Assert.Equal(text, SubtitleFile.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray()));
            Assert.Equal(text, SubtitleFile.Decode(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray()));
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Assert.Equal(text, SubtitleFile.Decode(Encoding.GetEncoding(949).GetBytes(text)));
            Assert.Equal("", SubtitleFile.Decode(null));
        }

        [Theory]
        [InlineData("a.srt", true)]
        [InlineData(@"C:\x\B.SRT", true)]
        [InlineData("c.vtt", true)]
        [InlineData("d.txt", false)]
        [InlineData(null, false)]
        public void IsSubtitleFile(string path, bool expected)
        {
            Assert.Equal(expected, SubtitleFile.IsSubtitleFile(path));
        }

        // ---------- 가져오기 ----------

        [Fact]
        public void Import_PutsCuesOnTopSubtitleTrack_AtCueTimes()
        {
            var project = TestProjects.Empty();
            var track = TimelineEdits.ImportSubtitles(project, SubtitleFile.Parse(Srt));
            Assert.Same(track, project.VideoTracks.Last());
            Assert.True(track.Subtitles);
            Assert.Equal("자막", track.Name);
            Assert.Equal(new long[] { 0, project.Rate.ToFrames(6.04) }, track.Clips.Select(c => c.Start));
            Assert.Equal(new long[] { project.Rate.ToFrames(6.04), project.Rate.ToFrames(11.12) }, track.Clips.Select(c => c.End));
            Assert.All(track.Clips, c => Assert.Equal(ClipKind.Text, c.Kind));
        }

        [Fact]
        public void Import_OverlapTrimsPrevious_SameStartMerges_TinyCueDropped()
        {
            var project = TestProjects.Empty();
            var track = TimelineEdits.ImportSubtitles(project, new[]
            {
                Cue(1, 5, "가"), Cue(3, 6, "나"), Cue(3, 7, "다"), Cue(3, 7, "나"), Cue(10, 10.001, "짧음")
            });
            var rate = project.Rate;
            Assert.Equal(2, track.Clips.Count);
            Assert.Equal(rate.ToFrames(3), track.Clips[0].End);
            // 같은 프레임에 시작하는 큐는 한 큐로 — 같은 글은 한 번만
            Assert.Equal("나\n다", track.Clips[1].Text);
            Assert.Equal(rate.ToFrames(7), track.Clips[1].End);
            Assert.Null(TimelineEdits.ImportSubtitles(project, new SubtitleCue[0]));
            Assert.Equal(2, project.VideoTracks.Count);
        }

        [Fact]
        public void Import_ManySameStartCues_StayWithinTextLimit()
        {
            var project = TestProjects.Empty();
            var cues = Enumerable.Range(0, 5000).Select(i => Cue(1, 2, "줄 " + i)).ToList();
            var track = TimelineEdits.ImportSubtitles(project, cues);
            Assert.True(Assert.Single(track.Clips).Text.Length <= SubtitleFile.MaxTextLength);
        }

        [Fact]
        public void Parse_NonAsciiDigits_AreNotTimes_AndDoNotThrow()
        {
            // 전각·아랍 숫자는 \d에는 맞지만 시각으로 읽을 수 없다 — 버리고 계속한다
            Assert.Empty(SubtitleFile.Parse("００:００:０１,０００ --> ００:００:０２,０００\n가"));
            Assert.Empty(SubtitleFile.Parse("٠٠:٠٠:٠١,٠٠٠ --> ٠٠:٠٠:٠٢,٠٠٠\n가"));
            Assert.Single(SubtitleFile.Parse("００:００:０１,０００ --> ００:００:０２,０００\n가\n\n00:00:03,000 --> 00:00:04,000\n나"));
        }

        [Fact]
        public void Clean_AssLineBreaks_AndSurrogatePairsAtLimit()
        {
            Assert.Equal("첫 줄\n둘째 줄", SubtitleFile.Clean("첫 줄\\N둘째 줄"));
            Assert.Equal("가\n나", SubtitleFile.Clean("가\\n나"));
            var text = SubtitleFile.Clean(new string('가', SubtitleFile.MaxTextLength - 1) + "😀끝");
            Assert.Equal(SubtitleFile.MaxTextLength - 1, text.Length);
            Assert.False(char.IsHighSurrogate(text[text.Length - 1]));
        }

        [Fact]
        public void Decode_Utf16WithoutBom()
        {
            const string text = "00:00:01,000 --> 00:00:02,000\n한글";
            Assert.Equal(text, SubtitleFile.Decode(Encoding.Unicode.GetBytes(text)));
            Assert.Equal(text, SubtitleFile.Decode(Encoding.BigEndianUnicode.GetBytes(text)));
        }

        [Fact]
        public void TrackNames_And_NewVideoTrackGoesUnderSubtitles()
        {
            var project = TestProjects.Empty();
            TimelineEdits.ImportSubtitles(project, new[] { Cue(0, 1, "가") });
            TimelineEdits.ImportSubtitles(project, new[] { Cue(0, 1, "나") });
            var video = TimelineEdits.AddTrack(project, TrackKind.Video);
            Assert.Equal(new[] { "동영상 1", "동영상 2", "자막 1", "자막 2" }, project.VideoTracks.Select(t => t.Name));
            Assert.Same(video, project.VideoTracks[1]);
        }

        [Fact]
        public void SubtitleTrack_AcceptsOnlyText()
        {
            var project = TestProjects.Empty(magnetic: false);
            var track = TimelineEdits.ImportSubtitles(project, new[] { Cue(0, 1, "가") });
            TimelineEdits.Place(project, track, TimelineEdits.CreateText("나", 60, 30), 60);
            var image = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 30);
            var error = Assert.Throws<EditException>(() => TimelineEdits.Place(project, track, image, 120));
            Assert.Contains("자막", error.Message);
            Assert.False(TimelineEdits.Accepts(track, image));
            Assert.True(TimelineEdits.Accepts(project.MainTrack, image));
        }

        [Fact]
        public void Import_IsUndoable()
        {
            var session = new EditorSession();
            Assert.True(session.Execute(p => TimelineEdits.ImportSubtitles(p, SubtitleFile.Parse(Srt))));
            Assert.Contains(session.Project.VideoTracks, t => t.Subtitles);
            Assert.True(session.Undo());
            Assert.DoesNotContain(session.Project.VideoTracks, t => t.Subtitles);
        }

        [Fact]
        public void ProjectFile_KeepsSubtitleFlag_AndRejectsItOnAudioAndMainTrack()
        {
            var project = TestProjects.Empty();
            TimelineEdits.ImportSubtitles(project, SubtitleFile.Parse(Srt));
            // 조작한 파일: 자막 트랙 하나 더(사진이 든) — 보통 트랙으로 바뀌어 사진이 숨지 않아야 한다
            var withImage = TimelineEdits.ImportSubtitles(project, new[] { Cue(0, 1, "가") });
            withImage.Clips.Add(TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 60, 30));
            var json = JsonNode.Parse(ProjectFile.Snapshot(project)).AsObject();
            json["AudioTracks"][0]["Subtitles"] = true;
            json["VideoTracks"][0]["Subtitles"] = true;
            var folder = Path.Combine(Path.GetTempPath(), "fv-subs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "a" + ProjectFile.Extension);
                File.WriteAllText(path, json.ToJsonString());
                var loaded = ProjectFile.Load(path);
                Assert.True(loaded.VideoTracks[1].Subtitles);
                Assert.Equal("자막", loaded.VideoTracks[1].Name);
                Assert.Equal(2, loaded.VideoTracks[1].Clips.Count);
                Assert.False(loaded.VideoTracks[2].Subtitles);
                Assert.False(loaded.MainTrack.Subtitles);
                Assert.False(loaded.AudioTracks[0].Subtitles);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // ---------- ASS ----------

        private static (Project Project, Track Track) Vertical()
        {
            var project = TestProjects.Empty();
            project.Width = 1080;
            project.Height = 1920;
            var track = TimelineEdits.ImportSubtitles(project, new[] { Cue(0, 6.04, "가나"), Cue(6.04, 11.12, "다\n라") });
            return (project, track);
        }

        [Fact]
        public void Ass_HasProjectSizedStyle_AndHalfFrameEarlierTimes()
        {
            var (project, track) = Vertical();
            var ass = SubtitleAss.Build(project, track, 0, project.Duration);
            Assert.Contains("PlayResX: 1080\n", ass);
            Assert.Contains("PlayResY: 1920\n", ass);
            // 짧은 변 1080의 6% = 65px, 아래 여백 = 1920의 8%
            Assert.Contains("Style: Default,Malgun Gothic,65,", ass);
            Assert.Contains(",2,65,65,154,1\n", ass);
            // [0, 181) → 0 ~ (181 − ½)/30 = 6.0167초, [181, 334) → 6.0167 ~ 11.1167초
            Assert.Contains("Dialogue: 0,0:00:00.00,0:00:06.02,Default,,0,0,0,,가나\n", ass);
            Assert.Contains("Dialogue: 0,0:00:06.02,0:00:11.12,Default,,0,0,0,,다\\N라\n", ass);
        }

        [Fact]
        public void Ass_TimesAreRelativeToWindow_AndOnlyOverlappingCues()
        {
            var (project, track) = Vertical();
            var ass = SubtitleAss.Build(project, track, 200, 201);
            Assert.DoesNotContain("가나", ass);
            // (334 − 200 − ½)/30 = 4.45초
            Assert.Contains("Dialogue: 0,0:00:00.00,0:00:04.45,Default,,0,0,0,,다\\N라\n", ass);
            Assert.Null(SubtitleAss.Build(project, track, 400, 401));
        }

        [Fact]
        public void Ass_EscapesBracesBackslashesAndControls()
        {
            Assert.Equal("a｛\\b｝c", SubtitleAss.Escape("a{\\b}c"));
            Assert.Equal("x\\" + (char)0x2060 + "Ny\\" + (char)0x2060 + "hz", SubtitleAss.Escape("x\\Ny\\hz"));
            Assert.Equal("가\\N나", SubtitleAss.Escape("가\r\n\n나"));
            Assert.Equal("줄", SubtitleAss.Escape(" \n 줄 " + (char)7 + "\n\n"));
            Assert.Equal(SubtitleFile.MaxTextLength, SubtitleAss.Escape(new string('가', 5000)).Length);
            // 악성 글이 들어간 큐도 대사 한 줄이고 꾸밈 태그가 생기지 않는다
            var (project, track) = Vertical();
            track.Clips[0].Text = "{\\fs500\\pos(0,0)}크게\nDialogue: 0,0:00:00.00,9:00:00.00,Default,,0,0,0,,가짜";
            var ass = SubtitleAss.Build(project, track, 0, 10);
            var dialogues = ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();
            Assert.Single(dialogues);
            Assert.DoesNotContain("{", dialogues[0]);
        }

        [Fact]
        public void HighFrameRate_StretchesTimeForAss()
        {
            // 120fps: 반 프레임(4.2ms)이 ASS 단위(10ms)보다 짧다 — 시간을 3배로 늘여 적고 그래프도 3배로 늘였다 되돌린다
            var project = TestProjects.Empty();
            project.RateNumerator = 120;
            project.RateDenominator = 1;
            var track = TimelineEdits.ImportSubtitles(project, new[] { Cue(7 / 120.0, 13 / 120.0, "가") });
            Assert.Equal(3, SubtitleAss.TimeScale(project.Rate));
            Assert.Equal(1, SubtitleAss.TimeScale(FrameRate.Default));
            // (7 − ½)/120 × 3 = 0.1625초, (13 − ½)/120 × 3 = 0.3125초
            Assert.Contains("Dialogue: 0,0:00:00.16,0:00:00.31,", SubtitleAss.Build(project, track, 0, 30));
            var plan = RenderGraph.Build(project, Options(project, 0, 30));
            Assert.Contains("[base]tpad=stop=1:stop_mode=clone,setpts=PTS*3,ass=filename=", plan.Filter);
            Assert.Contains(",setpts=PTS/3,fps=120/1,trim=end_frame=30[o0]", plan.Filter);
        }

        [Theory]
        [InlineData(0, "0:00:00.00")]
        [InlineData(3661.239, "1:01:01.24")]
        [InlineData(-3, "0:00:00.00")]
        public void Ass_Time(double seconds, string expected)
        {
            Assert.Equal(expected, SubtitleAss.Time(seconds));
        }

        // ---------- 렌더 그래프 ----------

        private static RenderOptions Options(Project project, long from = 0, long to = -1)
        {
            return new RenderOptions { From = from, To = to < 0 ? project.Duration : to, Width = 540, Height = 960, Audio = false, TempFolder = "T:/tmp" };
        }

        [Fact]
        public void Graph_SubtitleTrack_UsesAssFilterOnTop()
        {
            var (project, _) = Vertical();
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 400), 0);
            var plan = RenderGraph.Build(project, Options(project));
            Assert.True(plan.UsesLibass);
            var file = Assert.Single(plan.TextFiles);
            Assert.Matches(@"fv-subs-[0-9a-f]{32}\.ass$", file.Key);
            Assert.Contains("Dialogue:", file.Value);
            Assert.Contains("[o0]ass=filename='T\\:/tmp/fv-subs-", plan.Filter);
            Assert.Contains("[o1]format=yuv420p[vout]", plan.Filter);
            Assert.DoesNotContain("drawtext", plan.Filter);
            // 동영상 입력만 있다(자막은 입력이 아니다)
            Assert.Single(plan.Inputs);
        }

        [Fact]
        public void Graph_HiddenOrEmptyWindow_HasNoSubtitles()
        {
            var (project, track) = Vertical();
            var outside = RenderGraph.Build(project, Options(project, 400, 401));
            Assert.False(outside.UsesLibass);
            Assert.DoesNotContain("ass=", outside.Filter);
            track.Hidden = true;
            var hidden = RenderGraph.Build(project, Options(project));
            Assert.False(hidden.UsesLibass);
            Assert.Empty(hidden.TextFiles);
            Assert.Contains("[base]format=yuv420p[vout]", hidden.Filter);
        }

        [Fact]
        public void Filters_ParsesFfmpegList()
        {
            const string output = "Filters:\n  T.. = Timeline support\n  ..C = Command support\n" +
                                  " ... ass               V->V       Render ASS subtitles onto input video using the libass library.\n" +
                                  " T.C drawtext          V->V       Draw text on top of video frames using libfreetype library.\n" +
                                  " ... subtitles         V->V       Render text subtitles onto input video using the libass library.\n" +
                                  " ... amix              N->A       Audio mixing.\n";
            var filters = FfmpegText.Filters(output);
            Assert.Contains("ass", filters);
            Assert.Contains("drawtext", filters);
            Assert.Contains("amix", filters);
            Assert.DoesNotContain("=", filters);
            Assert.Equal(4, filters.Count);
        }
    }

    /// <summary>실제 FFmpeg로 자막을 입혀 본다(libass).</summary>
    public class SubtitleFfmpegTests
    {
        private readonly ITestOutputHelper _out;

        public SubtitleFfmpegTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Project WithCues()
        {
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            // 글꼴이 없는 CI(리눅스)에서도 보이게 라틴 글자
            TimelineEdits.ImportSubtitles(project, new[] { new SubtitleCue { Start = 1, End = 2, Text = "HELLO WORLD" }, new SubtitleCue { Start = 3, End = 4, Text = "SECOND" } });
            return project;
        }

        /// <summary>BGRA 한 장에서 (위 1/3, 아래 1/3)의 흰 픽셀 수.</summary>
        private static (int Top, int Bottom) White(byte[] bgra, int offset = 0)
        {
            int top = 0, bottom = 0;
            for (var y = 0; y < 240; y++)
            {
                for (var x = 0; x < 320; x++)
                {
                    var i = offset + (y * 320 + x) * 4;
                    if (bgra[i] < 200 || bgra[i + 1] < 200 || bgra[i + 2] < 200)
                        continue;
                    if (y < 80)
                        top++;
                    else if (y >= 160)
                        bottom++;
                }
            }
            return (top, bottom);
        }

        private static (int Top, int Bottom) Frame(Project project, long frame)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, 320, 240, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return White(bytes);
        }

        [FfmpegFact]
        public void Subtitle_IsBurnedInAtBottom_OnlyDuringCue()
        {
            var project = WithCues();
            var inside = Frame(project, 45);
            var between = Frame(project, 75);
            _out.WriteLine("큐 안 " + inside + " · 큐 사이 " + between);
            Assert.True(inside.Bottom > 10, "아래쪽 흰 픽셀 " + inside.Bottom);
            Assert.Equal(0, inside.Top);
            Assert.Equal((0, 0), between);
        }

        [FfmpegFact]
        public async Task Subtitle_ContinuousRender_CueBoundariesAreFrameExact()
        {
            // 재생·내보내기처럼 구간을 이어 그리면 libass가 큐 시각으로 켜고 끈다 — 큐 [30, 60) 프레임: 29·60에는 없고 30·59에는 있다
            var result = await Boundaries(WithCues(), 90, new[] { 29, 30, 59, 60 });
            Assert.Equal((0, 0), result[0]);
            Assert.True(result[1].Bottom > 10, "첫 프레임 " + result[1]);
            Assert.True(result[2].Bottom > 10, "마지막 프레임 " + result[2]);
            Assert.Equal((0, 0), result[3]);
        }

        [FfmpegFact]
        public async Task Subtitle_At120fps_CueBoundariesAreFrameExact()
        {
            // ASS 단위(1/100초)보다 프레임이 짧아도 시간을 늘여 입히므로 큐 [7, 13): 6·13에는 없고 7·12에는 있다
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            project.RateNumerator = 120;
            project.RateDenominator = 1;
            TimelineEdits.ImportSubtitles(project, new[] { new SubtitleCue { Start = 7 / 120.0, End = 13 / 120.0, Text = "HELLO WORLD" }, new SubtitleCue { Start = 0.2, End = 0.25, Text = "END" } });
            var result = await Boundaries(project, 24, new[] { 6, 7, 12, 13 });
            Assert.Equal((0, 0), result[0]);
            Assert.True(result[1].Bottom > 10, "첫 프레임 " + result[1]);
            Assert.True(result[2].Bottom > 10, "마지막 프레임 " + result[2]);
            Assert.Equal((0, 0), result[3]);
        }

        /// <summary>[0, count) 프레임을 재생처럼 이어 그려 고른 프레임들의 (위, 아래) 흰 픽셀 수.</summary>
        private async Task<(int Top, int Bottom)[]> Boundaries(Project project, int count, int[] frames)
        {
            const int size = 320 * 240 * 4;
            var pixels = new byte[size * count];
            List<string> files;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 0, count, 320, 240, out files))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var read = 0;
                while (read < pixels.Length)
                {
                    var n = await process.StandardOutput.BaseStream.ReadAsync(pixels, read, pixels.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                await process.WaitForExitAsync();
                Assert.True(read == pixels.Length, "읽은 바이트 " + read + " · " + await errors);
            }
            foreach (var file in files)
                RenderCommand.TryDelete(file);
            var result = frames.Select(f => White(pixels, f * size)).ToArray();
            _out.WriteLine(string.Join(" · ", frames.Select((f, i) => f + " " + result[i])));
            return result;
        }

        [FfmpegFact]
        public void Arguments_WithoutLibass_ExplainsInsteadOfFailing()
        {
            var tool = FfmpegTool.Locate(FfmpegMedia.Tool.Folder, null);
            typeof(FfmpegTool).GetProperty("Filters").SetValue(tool, new HashSet<string> { "drawtext" });
            var plan = RenderGraph.Build(WithCues(), new RenderOptions { From = 30, To = 31, Width = 320, Height = 240, Audio = false, Preview = true, TempFolder = RenderEnvironment.TempFolder() });
            string script;
            var error = Assert.Throws<FfmpegException>(() => RenderCommand.Arguments(tool, plan, new[] { "-f", "null", "-" }, out script));
            Assert.Contains("libass", error.Message);
        }
    }
}
