using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>무거운 수식 전환 미리 그리기(재생 캐시): 구간 찾기, 재생 그래프, 캐시 이름·무효화, 크기 상한·청소. FFmpeg 없이 돈다.</summary>
    public class TransitionPrerenderTests
    {
        /// <summary>A [0, 90) → B [90, 180)(하트 15장) → C [180, 270)(닦아내기 15장) → D [270, 360)(디졸브 15장). 640×360(재생 축소 2).</summary>
        private static Project FourClips(out Clip b, out Clip c, out Clip d)
        {
            var project = TestProjects.Empty();
            var a = TestProjects.Add(project, project.MainTrack, 0, 90, "a.mp4");
            b = TestProjects.Add(project, project.MainTrack, 90, 90, "b.mp4");
            c = TestProjects.Add(project, project.MainTrack, 180, 90, "c.mp4");
            d = TestProjects.Add(project, project.MainTrack, 270, 90, "d.mp4");
            Transitions.Assign(b, Transitions.Find("Heart"));
            b.TransitionIn = 15;
            Transitions.Assign(c, Transitions.Find("WipeLeft"));
            c.TransitionIn = 15;
            Transitions.Assign(d, Transitions.Dissolve);
            d.TransitionIn = 15;
            Assert.NotNull(a);
            return project;
        }

        private static RenderOptions Playback(long from, long to, IReadOnlyList<PrerenderedSpan> spans)
        {
            return new RenderOptions { From = from, To = to, Width = 640, Height = 360, Audio = false, Preview = true, Playback = true, Prerendered = spans };
        }

        [Fact]
        public void Windows_AreTheExpressionTransitions_ThatTouchTheRange()
        {
            var project = FourClips(out var b, out _, out _);
            var windows = TransitionPrerender.Windows(project, 0, 360, 9);
            // 내장 xfade(닦아내기)·디졸브는 가볍다 — 수식 전환(하트)만
            var window = Assert.Single(windows);
            Assert.Same(b, window.Clip);
            Assert.Equal(90, window.Start);
            Assert.Equal(105, window.End);
            Assert.Equal("Heart", window.Transition.Id);
            // 구간에 걸칠 때만: 끝 바로 뒤부터·시작 바로 앞까지면 없음, 한 장이라도 걸치면 있음
            Assert.Empty(TransitionPrerender.Windows(project, 105, 360, 9));
            Assert.Empty(TransitionPrerender.Windows(project, 0, 90, 9));
            Assert.Single(TransitionPrerender.Windows(project, 104, 360, 9));
            Assert.Single(TransitionPrerender.Windows(project, 0, 91, 9));
            // 숨긴 트랙은 그리지 않으므로 없음
            project.MainTrack.Hidden = true;
            Assert.Empty(TransitionPrerender.Windows(project, 0, 360, 9));
        }

        [Fact]
        public void Windows_ShortClip_TransitionIsClippedToTheClip_AndTooOldFfmpegIsADissolve()
        {
            var project = FourClips(out var b, out _, out _);
            b.TransitionIn = 500;
            var window = Assert.Single(TransitionPrerender.Windows(project, 0, 360, 9));
            Assert.Equal(90 + b.Duration, window.End);
            // 효과에 필요한 FFmpeg보다 낮으면 디졸브로 그리므로 미리 그릴 것이 없다
            var custom = Transitions.Check(new TransitionDef { Id = "NewHeart", Name = "새 하트", Expr = "if(gt(X,W*P),B,A)", Ffmpeg = 12 }, out _);
            Transitions.Assign(b, custom);
            Assert.Empty(TransitionPrerender.Windows(project, 0, 360, 9));
            Assert.Single(TransitionPrerender.Windows(project, 0, 360, 12));
        }

        [Fact]
        public void PlaybackGraph_WithASpan_HidesTheExpression_DrawsFullSize_AndCoversTheSpan()
        {
            var project = FourClips(out var b, out _, out _);
            var plain = RenderGraph.Build(project, Playback(0, 360, null));
            // 캐시 없이: 수식 그대로, 묶음을 절반 크기로 섞어 늘인다(지금 방식)
            Assert.Contains("custom:expr=", plain.Filter);
            Assert.Contains("unpremultiply=inplace=1,scale=640:360,", plain.Filter);
            Assert.DoesNotContain("format=rgb:", plain.Filter);

            var spans = new[] { new PrerenderedSpan { ClipId = b.Id, Start = 90, End = 105, Path = "C:/cache/pr-a.mkv" } };
            var plan = RenderGraph.Build(project, Playback(0, 360, spans));
            _ = plan.Filter;
            Assert.DoesNotContain("custom:expr=", plan.Filter);
            Assert.Contains("xfade=" + TransitionPrerender.HiddenXfade + ":duration=0.5:offset=", plan.Filter);
            // 묶음을 줄이지 않는다(구간 밖도 멈춘 화면과 같은 크기)
            Assert.DoesNotContain("unpremultiply=inplace=1,scale=", plan.Filter);
            // 캐시 입력: 처음부터 읽고(-ss 없음), 구간 [90, 105)의 장에서만 화면 전체를 덮는다
            var input = plan.Inputs.Last();
            Assert.Equal(new[] { "-i", "C:/cache/pr-a.mkv" }, input);
            var index = plan.Inputs.Count - 1;
            Assert.Contains("[" + index + ":v]setpts=PTS-STARTPTS,fps=30/1,setpts=PTS+round(3/TB),format=bgra[pc0]", plan.Filter);
            Assert.Contains("[pv0][pc0]overlay=x=0:y=0:format=rgb:eof_action=pass:enable='between(t,2.983333,3.483333)'[vout]", plan.Filter);
            Assert.Contains("format=bgra[pv0]", plan.Filter);
            // 도우미가 아는 입력 옵션만
            Assert.All(plan.Inputs.SelectMany(i => i.Where((_, k) => k % 2 == 0)), o => Assert.Contains(o, new[] { "-ss", "-t", "-sseof", "-loop", "-framerate", "-f", "-pattern_type", "-i" }));
        }

        [Fact]
        public void PlaybackGraph_StartingInsideTheSpan_SkipsIntoTheCache()
        {
            var project = FourClips(out var b, out _, out _);
            var spans = new[] { new PrerenderedSpan { ClipId = b.Id, Start = 90, End = 105, Path = "C:/cache/pr-a.mkv" } };
            var plan = RenderGraph.Build(project, Playback(96, 360, spans));
            // 6장 건너뜀 — 반 프레임 앞을 찾는다(컨테이너 시각의 ms 반올림에 흔들리지 않게), 흐름 0초부터 덮는다
            Assert.Equal(new[] { "-ss", "0.183333", "-i", "C:/cache/pr-a.mkv" }, plan.Inputs.Last());
            Assert.Contains("fps=30/1,setpts=PTS+round(0/TB),format=bgra[pc0]", plan.Filter);
            Assert.Contains("enable='between(t,-0.016667,0.283333)'[vout]", plan.Filter);
        }

        [Fact]
        public void Spans_AreIgnored_ForStillFramesExportAndOtherRanges()
        {
            var project = FourClips(out var b, out _, out _);
            var spans = new[] { new PrerenderedSpan { ClipId = b.Id, Start = 90, End = 105, Path = "C:/cache/pr-a.mkv" } };
            var expected = RenderGraph.Build(project, new RenderOptions { From = 95, To = 96, Width = 640, Height = 360, Audio = false, Preview = true }).Filter;
            // 멈춘 화면·내보내기는 캐시를 쓰지 않는다(늘 그리는 그래프 그대로)
            Assert.Equal(expected, RenderGraph.Build(project, new RenderOptions { From = 95, To = 96, Width = 640, Height = 360, Audio = false, Preview = true, Prerendered = spans }).Filter);
            var export = RenderGraph.Build(project, new RenderOptions { From = 0, To = 360, Width = 640, Height = 360, Prerendered = spans });
            Assert.DoesNotContain("pr-a.mkv", string.Join(" ", export.Inputs.SelectMany(i => i)));
            Assert.Contains("custom:expr=", export.Filter);
            // 재생 구간이 구간 밖이면 그 캐시는 읽지 않는다
            var after = RenderGraph.Build(project, Playback(120, 360, spans));
            Assert.DoesNotContain("pr-a.mkv", string.Join(" ", after.Inputs.SelectMany(i => i)));
        }

        // ---------- 캐시 이름 ----------

        private sealed class TempFolder : IDisposable
        {
            public TempFolder()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fv-prerender-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string File(string name, string content = "x")
            {
                var path = System.IO.Path.Combine(Path, name);
                System.IO.File.WriteAllText(path, content);
                return path;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static string WindowKey(Project project, PrerenderWindow window)
        {
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = window.Start, To = window.End, Width = 640, Height = 360, Audio = false, Preview = true,
                TempFolder = Path.GetTempPath(), FfmpegMajor = 9
            });
            return PrerenderCache.Key(plan, "ffmpeg version 9.0");
        }

        [Fact]
        public void Key_FollowsWhatTheWindowDraws_NotTemporaryNames()
        {
            using (var folder = new TempFolder())
            {
                var project = FourClips(out var b, out var c, out _);
                foreach (var clip in project.MainTrack.Clips)
                    clip.Path = folder.File(Path.GetFileName(clip.Path));
                // 구간 위에 글자 클립(그릴 때마다 임시 글 파일 이름이 새로 생긴다)
                var title = TimelineEdits.CreateText("제목", 80, 60);
                TimelineEdits.AddTrack(project, TrackKind.Video).Clips.Add(title);
                var window = TransitionPrerender.Windows(project, 0, 360, 9).Single();
                var key = WindowKey(project, window);
                Assert.Matches("^[0-9a-f]{64}$", key);
                Assert.Equal(key, WindowKey(project, window));

                // 구간 밖 클립을 바꾸면 그대로, 전환·클립·글자·원본 파일·FFmpeg 판을 바꾸면 다르다
                c.Opacity = 0.5;
                Assert.Equal(key, WindowKey(project, window));
                var keys = new HashSet<string> { key };
                var eased = Transitions.Check(new TransitionDef { Id = "EasedHeart", Name = "하트(천천히)", Expr = Transitions.Find("Heart").Expr, Ease = "inout" }, out _);
                Transitions.Assign(b, eased);
                Assert.True(keys.Add(WindowKey(project, window)), "이징");
                Transitions.Assign(b, Transitions.Find("Heart"));
                Assert.Equal(key, WindowKey(project, window));
                b.Scale = 0.8;
                Assert.True(keys.Add(WindowKey(project, window)), "크기");
                b.Scale = 1;
                title.Text = "다른 제목";
                Assert.True(keys.Add(WindowKey(project, window)), "글자");
                title.Text = "제목";
                Assert.Equal(key, WindowKey(project, window));
                File.SetLastWriteTimeUtc(b.Path, DateTime.UtcNow.AddMinutes(-3));
                Assert.True(keys.Add(WindowKey(project, window)), "원본 파일을 바꿔 넣음");
                var plan = RenderGraph.Build(project, new RenderOptions { From = 90, To = 105, Width = 640, Height = 360, Audio = false, Preview = true, TempFolder = Path.GetTempPath(), FfmpegMajor = 9 });
                Assert.NotEqual(PrerenderCache.Key(plan, "ffmpeg version 9.0"), PrerenderCache.Key(plan, "ffmpeg version 9.1"));
                // 크기가 다르면(미리 보기 품질) 다른 이름
                var half = RenderGraph.Build(project, new RenderOptions { From = 90, To = 105, Width = 320, Height = 180, Audio = false, Preview = true, TempFolder = Path.GetTempPath(), FfmpegMajor = 9 });
                Assert.NotEqual(PrerenderCache.Key(plan, "v"), PrerenderCache.Key(half, "v"));
            }
        }

        // ---------- 캐시 파일 ----------

        private static string Fake(PrerenderCache cache, char digit, int bytes, DateTime used)
        {
            Directory.CreateDirectory(cache.Folder);
            var path = cache.PathOf(new string(digit, 64));
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, used);
            File.SetLastAccessTimeUtc(path, used);
            return path;
        }

        [Fact]
        public void Find_TouchesTheFile_AndIgnoresMissingOrEmpty()
        {
            using (var folder = new TempFolder())
            {
                var cache = new PrerenderCache(Path.Combine(folder.Path, "prerender"));
                Assert.Null(cache.Find(new string('a', 64)));
                var path = Fake(cache, 'a', 10, DateTime.UtcNow.AddDays(-2));
                Assert.Equal(path, cache.Find(new string('a', 64)));
                Assert.True(DateTime.UtcNow - File.GetLastAccessTimeUtc(path) < TimeSpan.FromMinutes(1));
                File.WriteAllBytes(path, Array.Empty<byte>());
                Assert.Null(cache.Find(new string('a', 64)));
            }
        }

        [Fact]
        public void Trim_RemovesLeastRecentlyUsed_KeepsRecentAndOtherFiles()
        {
            using (var folder = new TempFolder())
            {
                var cache = new PrerenderCache(folder.Path, 2500);
                var now = DateTime.UtcNow;
                var oldest = Fake(cache, '1', 1000, now.AddHours(-5));
                var old = Fake(cache, '2', 1000, now.AddHours(-3));
                var recent = Fake(cache, '3', 1000, now.AddMinutes(-1));
                var other = folder.File("keep.mkv");
                // 3000 > 2500: 가장 오래 안 쓴 것 하나만 지우면 된다
                cache.Trim(2500, now);
                Assert.False(File.Exists(oldest));
                Assert.True(File.Exists(old));
                // 5분 안에 쓴 것(재생이 읽는 중일 수 있음)은 넘어도 남긴다
                cache.Trim(10, now);
                Assert.False(File.Exists(old));
                Assert.True(File.Exists(recent));
                Assert.True(File.Exists(other));
            }
        }

        [Fact]
        public void Cleanup_RemovesStaleParts_ButNotFreshOnes()
        {
            using (var folder = new TempFolder())
            {
                var cache = new PrerenderCache(folder.Path);
                var stale = folder.File("pr-" + new string('b', 64) + ".mkv." + Guid.NewGuid().ToString("N") + ".part");
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
                var fresh = folder.File("pr-" + new string('c', 64) + ".mkv." + Guid.NewGuid().ToString("N") + ".part");
                var lookalike = folder.File("pr-1.mkv.part");
                File.SetLastWriteTimeUtc(lookalike, DateTime.UtcNow.AddHours(-2));
                cache.Cleanup(TimeSpan.FromHours(1));
                Assert.False(File.Exists(stale));
                Assert.True(File.Exists(fresh));
                Assert.True(File.Exists(lookalike));
            }
        }
    }

    /// <summary>미리 그리기를 실제 FFmpeg로: 캐시로 재생한 장이 멈춘 화면과 바이트까지 같은지, 줄·취소, 걸리는 시간.</summary>
    public class TransitionPrerenderRenderTests
    {
        private readonly ITestOutputHelper _out;

        public TransitionPrerenderRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>시험 화면 [0, 60) → 파란 화면 [60, 120)(하트 15장), width×height 프로젝트(동영상은 화면에 맞춤).</summary>
        private static Project HeartProject(int width, int height, string ease = null, int frames = 15)
        {
            var project = TestProjects.Empty();
            project.Width = width;
            project.Height = height;
            var a = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            var b = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.BlueVideo), project.Rate, 0, 0);
            b.Scale = 0.9;
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            var heart = Transitions.Find("Heart");
            if (ease != null)
                heart = Transitions.Check(new TransitionDef { Id = "HeartEase", Name = "하트", Expr = heart.Expr, Ease = ease }, out _);
            Transitions.Assign(b, heart);
            b.TransitionIn = frames;
            return project;
        }

        private static List<byte[]> Play(Project project, long from, long to, int width, int height, IReadOnlyList<PrerenderedSpan> spans, PreviewHelper helper = null)
        {
            using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, from, to, width, height, null, null, spans))
            {
                var frames = new List<byte[]>();
                var size = width * height * 4;
                while (true)
                {
                    var pixels = new byte[size];
                    var read = 0;
                    int n;
                    while (read < size && (n = stream.Input.Read(pixels, read, size - read)) > 0)
                        read += n;
                    if (read < size)
                        break;
                    frames.Add(pixels);
                }
                Assert.True(frames.Count > 0, "재생 흐름이 한 장도 내지 않았습니다: " + stream.Error);
                return frames;
            }
        }

        private static byte[] Still(Project project, long frame, int width, int height)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, width, height, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        /// <summary>[from, to)를 멈춘 화면 그래프(재생 축소 없음)로 이어 그린 BGRA — 내보내기를 미리 보기 크기로 한 것과 같다.</summary>
        private static List<byte[]> Continuous(Project project, long from, long to, int width, int height)
        {
            var tool = FfmpegMedia.Tool;
            var plan = RenderGraph.Build(project, PrerenderCache.StillOptions(tool, from, to, width, height, null));
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out var script);
            try
            {
                var bytes = FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
                var size = width * height * 4;
                return Enumerable.Range(0, bytes.Length / size).Select(i => bytes.Skip(i * size).Take(size).ToArray()).ToList();
            }
            finally
            {
                RenderCommand.TryDelete(script);
                foreach (var file in plan.TextFiles.Keys)
                    RenderCommand.TryDelete(file);
            }
        }

        private static (List<PrerenderedSpan> Spans, PrerenderCache Cache) Prerender(Project project, long from, long to, int width, int height, string folder)
        {
            var cache = new PrerenderCache(folder);
            var (spans, jobs) = cache.Survey(FfmpegMedia.Tool, project, from, to, width, height, null);
            Assert.Empty(spans);
            Assert.NotEmpty(jobs);
            foreach (var job in jobs)
                cache.Render(FfmpegMedia.Tool, job.Plan, job.Key, null, CancellationToken.None).GetAwaiter().GetResult();
            var again = cache.Survey(FfmpegMedia.Tool, project, from, to, width, height, null);
            Assert.Empty(again.Jobs);
            return (again.Spans, cache);
        }

        private static int Differing(byte[] a, byte[] b)
        {
            var count = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    count++;
            }
            return count;
        }

        [FfmpegFact]
        public void PlaybackWithPrerender_IsTheExportPicture_InsideAndAroundTheTransition()
        {
            // 640×360은 재생 축소 2 — 캐시 없이 재생하면 전환 묶음을 절반 크기로 섞는다(내보내기와 다름)
            var project = HeartProject(640, 360);
            var folder = Path.Combine(Path.GetTempPath(), "fv-prerender-" + Guid.NewGuid().ToString("N"));
            try
            {
                var (spans, _) = Prerender(project, 50, 90, 640, 360, folder);
                var span = Assert.Single(spans);
                Assert.Equal(60, span.Start);
                Assert.Equal(75, span.End);
                var played = Play(project, 50, 90, 640, 360, spans);
                Assert.Equal(40, played.Count);
                var plain = Play(project, 50, 90, 640, 360, null);
                // 기준: 같은 구간을 멈춘 화면 그래프로 이어 그린 것(= 내보내기를 이 크기로).
                // 전환 구간 밖의 앞 클립은 이어 그리면 전환 묶음 안에서 그려진다 — 그 장만 따로 그린 멈춘 화면(묶음 없이)과는 반올림만큼 다를 수 있어 이것과 견준다
                var export = Continuous(project, 50, 90, 640, 360);
                Assert.Equal(40, export.Count);
                var shrunk = 0;
                for (var i = 0; i < played.Count; i++)
                {
                    var diff = Differing(played[i], export[i]);
                    Assert.True(diff == 0, "프레임 " + (50 + i) + ": 캐시 재생과 내보내기 그림이 " + diff + "바이트 다릅니다");
                    if (Differing(plain[i], export[i]) > 0)
                        shrunk++;
                }
                // 전환 구간 안은 멈춘 화면(재생 헤드를 옮겨 본 그림)과도 같다
                for (var frame = 60; frame < 75; frame++)
                    Assert.True(Differing(played[frame - 50], Still(project, frame, 640, 360)) == 0, "전환 구간의 멈춘 화면 " + frame);
                _out.WriteLine("40장 모두 내보내기 그림과 같음(구간 15장은 멈춘 화면과도) — 캐시 없이 재생하면 " + shrunk + "장이 다름(줄여 섞은 묶음)");
                Assert.True(shrunk > 0, "캐시 없는 재생도 내보내기와 같다면 이 시험이 아무것도 확인하지 못합니다");

                // 전환 도중부터 재생해도 그 장부터 캐시를 읽는다
                var middle = Play(project, 66, 80, 640, 360, spans);
                for (var i = 0; i < middle.Count; i++)
                    Assert.True(Differing(middle[i], played[16 + i]) == 0, "도중부터 재생한 프레임 " + (66 + i));
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception)
                {
                }
            }
        }

        [FfmpegFact]
        public void Queue_DrawsOneAtATime_ReportsFrames_AndStopsObsoleteWork()
        {
            var project = HeartProject(320, 240);
            var other = HeartProject(320, 240, "inout");
            var folder = Path.Combine(Path.GetTempPath(), "fv-prerender-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new PrerenderCache(folder);
                using (var queue = new PrerenderQueue(cache))
                {
                    var finished = new List<(PrerenderJob Job, Exception Error)>();
                    var counts = new List<long>();
                    var done = new ManualResetEventSlim();
                    queue.Changed += () =>
                    {
                        var status = queue.Status;
                        if (status != null)
                        {
                            lock (counts)
                                counts.Add(status.Done);
                        }
                    };
                    queue.Finished += (job, error) =>
                    {
                        lock (finished)
                            finished.Add((job, error));
                        done.Set();
                    };
                    var first = cache.Survey(FfmpegMedia.Tool, project, 0, 120, 320, 240, null).Jobs;
                    var second = cache.Survey(FfmpegMedia.Tool, other, 0, 120, 320, 240, null).Jobs;
                    // 먼저 청한 일을 곧바로 다른 목록으로 바꾸면 앞의 것은 그만두고 뒤의 것만 그린다
                    Assert.Equal(1, queue.Request(FfmpegMedia.Tool, first));
                    Assert.Equal(1, queue.Request(FfmpegMedia.Tool, second));
                    // 같은 일을 다시 청하면 새로 든 것이 없다(안내를 되풀이하지 않게)
                    Assert.Equal(0, queue.Request(FfmpegMedia.Tool, second));
                    Assert.True(done.Wait(TimeSpan.FromMinutes(1)), "미리 그리기가 끝나지 않았습니다");
                    var result = Assert.Single(finished);
                    Assert.Null(result.Error);
                    Assert.Equal(second[0].Key, result.Job.Key);
                    Assert.NotNull(cache.Find(second[0].Key));
                    Assert.Null(cache.Find(first[0].Key));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (queue.Status != null && DateTime.UtcNow < deadline)
                        Thread.Sleep(20);
                    Assert.Null(queue.Status);
                    lock (counts)
                        Assert.Contains(15L, counts);
                    Assert.Empty(Directory.GetFiles(folder, "*.part"));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception)
                {
                }
            }
        }

        [FfmpegFact]
        public void Cost_HeartAt960x540_PlaybackPerFrame_WithAndWithoutPrerender()
        {
            // #32 실측과 같은 조건: 하트 960×540 전환 30장, 재생(재생 축소 2) — 캐시 없이 / 이징 inout / 캐시로
            var tool = FfmpegMedia.Tool;
            var folder = Path.Combine(Path.GetTempPath(), "fv-prerender-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var ease in new[] { null, "inout" })
                {
                    var project = HeartProject(960, 540, ease, 30);
                    double Measure(IReadOnlyList<PrerenderedSpan> spans)
                    {
                        var best = double.MaxValue;
                        for (var run = 0; run < 2; run++)
                        {
                            var watch = Stopwatch.StartNew();
                            var count = Play(project, 60, 90, 960, 540, spans).Count;
                            watch.Stop();
                            Assert.Equal(30, count);
                            best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 30);
                        }
                        return best;
                    }
                    var plain = Measure(null);
                    var cache = new PrerenderCache(folder);
                    var jobs = cache.Survey(tool, project, 60, 90, 960, 540, null).Jobs;
                    var job = Assert.Single(jobs);
                    var watch = Stopwatch.StartNew();
                    var path = cache.Render(tool, job.Plan, job.Key, null, CancellationToken.None).GetAwaiter().GetResult();
                    watch.Stop();
                    var spans = cache.Survey(tool, project, 60, 90, 960, 540, null).Spans;
                    var cached = Measure(spans);
                    _out.WriteLine((ease ?? "일정") + ": 재생 장당 캐시 없이 " + plain.ToString("0.0") + "ms → 캐시로 " + cached.ToString("0.0") + "ms, 미리 그리기 "
                        + watch.Elapsed.TotalSeconds.ToString("0.0") + "초(장당 " + (watch.Elapsed.TotalMilliseconds / 30).ToString("0") + "ms), 캐시 "
                        + (new FileInfo(path).Length / 1024.0 / 1024).ToString("0.0") + "MB");
                    Assert.True(cached < 1000.0 / 30, "캐시로 읽는 재생이 장당 " + cached.ToString("0.0") + "ms — 30fps보다 느립니다");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception)
                {
                }
            }
        }

        [HelperFact]
        public void Helper_PlaysThePrerenderedSpan_LikeFfmpeg()
        {
            var project = HeartProject(320, 240);
            var folder = Path.Combine(Path.GetTempPath(), "fv-prerender-" + Guid.NewGuid().ToString("N"));
            try
            {
                var (spans, _) = Prerender(project, 55, 80, 320, 240, folder);
                using (var helper = PreviewHelperTests.Start())
                {
                    var byHelper = Play(project, 63, 80, 320, 240, spans, helper);
                    var byFfmpeg = Play(project, 63, 80, 320, 240, spans);
                    Assert.Equal(byFfmpeg.Count, byHelper.Count);
                    for (var i = 0; i < byHelper.Count; i++)
                        Assert.True(Differing(byHelper[i], byFfmpeg[i]) == 0, "도우미 프레임 " + (63 + i));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
