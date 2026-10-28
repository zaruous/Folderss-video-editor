using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>동영상 효과의 한 장 그림 캐시(빛 띠·거리 지도·방향 지도·모양 표·잡음 판): 그래프에서 파일로 바꾸기, 없을 때 적기, 이름, 크기 상한·청소. FFmpeg 없이 돈다.</summary>
    public class StillImageCacheTests
    {
        /// <summary>있다고 할 경로를 정해 두는 가짜 캐시.</summary>
        private sealed class FakeStills : IStillImageCache
        {
            public HashSet<string> Present { get; } = new HashSet<string>(StringComparer.Ordinal);
            public bool All { get; set; }

            public string PathOf(string generator, string format)
            {
                return "C:/stills/" + format + "-" + (generator.GetHashCode() & 0x7fffffff).ToString(CultureInfo.InvariantCulture) + ".pam";
            }

            public bool Has(string path)
            {
                return All || Present.Contains(path);
            }
        }

        private static (Project Project, Clip Clip) VideoProject(VideoEffectDef def, int width = 1920, int height = 1080)
        {
            var project = TestProjects.Empty();
            project.Width = width;
            project.Height = height;
            var clip = TestProjects.Add(project, project.MainTrack, 0, 90);
            clip.Fit = ClipFit.Fill;
            clip.Effects = new List<ClipEffect> { VideoEffects.Create(def) };
            return (project, clip);
        }

        private static RenderPlan Plan(Project project, IStillImageCache stills)
        {
            return RenderGraph.Build(project, new RenderOptions { From = 10, To = 11, Width = project.Width, Height = project.Height, Audio = false, Preview = true, Stills = stills });
        }

        /// <summary>효과와 한 장 그림 수: 쇼크웨이브(거리 지도·모양 표·방향 지도), 레이(빛 띠·반짝임), 스캔 라인(띠), SF(무늬), 나쁜 TV(잡음 판).</summary>
        public static IEnumerable<object[]> Effects()
        {
            yield return new object[] { ShockwaveScan.Id, 3 };
            yield return new object[] { RayScan.Id, 2 };
            yield return new object[] { ScanLine.Id, 1 };
            yield return new object[] { SciFiScan.Id, 1 };
            yield return new object[] { BadSignal.Id, 1 };
        }

        [Theory]
        [MemberData(nameof(Effects))]
        public void Graph_WithoutCachedPictures_IsTheSameText_AndListsWhatToMake(string id, int pictures)
        {
            var (project, _) = VideoProject(VideoEffects.Find(id));
            var plain = Plan(project, null);
            var stills = new FakeStills();
            var listed = Plan(project, stills);
            // 캐시에 없으면 그래프 글은 예전 그대로(수식으로 그림)이고, 만들 그림을 적는다
            Assert.Equal(plain.Filter, listed.Filter);
            Assert.Equal(plain.Inputs.Count, listed.Inputs.Count);
            Assert.Equal(pictures, listed.StillImages.Count);
            Assert.Empty(plain.StillImages);
            foreach (var request in listed.StillImages.Values)
            {
                Assert.StartsWith("color=c=black:s=", request.Generator);
                Assert.Contains(",geq=", request.Generator);
                Assert.True(StillImageCache.Supports(request.Format), request.Format);
                // 그리는 글이 그래프에 그대로 들어 있다(같은 수식을 따로 그려 둔 것)
                Assert.Contains(request.Generator, plain.Filter);
            }
        }

        [Theory]
        [MemberData(nameof(Effects))]
        public void Graph_WithCachedPictures_ReadsTheFiles_InsteadOfTheFormulas(string id, int pictures)
        {
            var (project, _) = VideoProject(VideoEffects.Find(id));
            var plain = Plan(project, null);
            var cached = Plan(project, new FakeStills { All = true });
            Assert.Empty(cached.StillImages);
            var added = cached.Inputs.Skip(plain.Inputs.Count).ToList();
            Assert.Equal(pictures, added.Count);
            foreach (var input in added)
            {
                // 한 장(-loop 없음 — 뒤의 tpad가 늘인다), 도우미가 아는 옵션만
                Assert.Equal(new[] { "-framerate", "30/1", "-f", "image2", "-pattern_type", "none", "-i" }, input.Take(7));
                Assert.EndsWith(".pam", input[7]);
                var index = cached.Inputs.IndexOf(input);
                Assert.Contains("[" + index + ":v]setpts=PTS-STARTPTS,setsar=1,format=", cached.Filter);
            }
            foreach (var request in Plan(project, new FakeStills()).StillImages.Values)
                Assert.DoesNotContain(request.Generator, cached.Filter);
        }

        [Fact]
        public void Thumbnails_AndExport_AlwaysUseTheFormulas()
        {
            var (project, _) = VideoProject(ShockwaveScan.Def);
            // 내보내기 옵션에는 캐시가 없다(RenderEnvironment.Stills는 미리 보기만)
            var export = RenderGraph.Build(project, new RenderOptions { From = 0, To = 60, Width = 1920, Height = 1080 });
            Assert.Empty(export.StillImages);
            Assert.Contains("geq=lum='round(4*hypot(", export.Filter);
            var args = VideoEffects.PreviewArguments(ShockwaveScan.Def);
            Assert.DoesNotContain(".pam", string.Join(" ", args));
        }

        [Fact]
        public void PathOf_FollowsTheDrawingAndFormat()
        {
            var cache = new StillImageCache(Path.Combine(Path.GetTempPath(), "fv-stills-name"));
            var a = cache.PathOf("color=c=black:s=4x4:r=30/1:d=0.016667,format=gray,geq=lum='X'", "gray");
            Assert.Equal(a, cache.PathOf("color=c=black:s=4x4:r=30/1:d=0.016667,format=gray,geq=lum='X'", "gray"));
            Assert.NotEqual(a, cache.PathOf("color=c=black:s=4x4:r=30/1:d=0.016667,format=gray,geq=lum='Y'", "gray"));
            Assert.NotEqual(a, cache.PathOf("color=c=black:s=4x4:r=30/1:d=0.016667,format=gray,geq=lum='X'", "gray16"));
            Assert.Matches("^fx-[0-9a-f]{64}\\.pam$", Path.GetFileName(a));
            Assert.False(StillImageCache.Supports("yuv420p"));
        }

        private static string Fake(StillImageCache cache, char digit, int bytes, DateTime used)
        {
            Directory.CreateDirectory(cache.Folder);
            var path = Path.Combine(cache.Folder, "fx-" + new string(digit, 64) + ".pam");
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, used);
            File.SetLastAccessTimeUtc(path, used);
            return path;
        }

        [Fact]
        public void Has_Touches_Trim_RemovesLeastRecentlyUsed_Cleanup_RemovesStaleParts()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-stills-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cache = new StillImageCache(folder, 2500);
                var now = DateTime.UtcNow;
                var oldest = Fake(cache, '1', 1000, now.AddHours(-5));
                var old = Fake(cache, '2', 1000, now.AddHours(-3));
                var recent = Fake(cache, '3', 1000, now.AddMinutes(-1));
                Assert.True(cache.Has(old));
                Assert.True(DateTime.UtcNow - File.GetLastAccessTimeUtc(old) < TimeSpan.FromMinutes(1));
                Assert.False(cache.Has(Path.Combine(folder, "fx-" + new string('9', 64) + ".pam")));
                cache.Trim(2500, now);
                Assert.False(File.Exists(oldest));
                Assert.True(File.Exists(old));
                cache.Trim(10, now.AddMinutes(10));
                Assert.False(File.Exists(old));
                Assert.False(File.Exists(recent));

                var stale = Path.Combine(folder, "fx-" + new string('4', 64) + ".pam." + Guid.NewGuid().ToString("N") + ".part");
                File.WriteAllText(stale, "x");
                File.SetLastWriteTimeUtc(stale, now.AddHours(-2));
                var fresh = Path.Combine(folder, "fx-" + new string('5', 64) + ".pam." + Guid.NewGuid().ToString("N") + ".part");
                File.WriteAllText(fresh, "x");
                var other = Path.Combine(folder, "keep.pam");
                File.WriteAllText(other, "x");
                cache.Cleanup(TimeSpan.FromHours(1));
                Assert.False(File.Exists(stale));
                Assert.True(File.Exists(fresh));
                Assert.True(File.Exists(other));
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

        [Fact]
        public async Task Make_RefusesPathsOutsideTheCacheOrWithOtherNames()
        {
            var cache = new StillImageCache(Path.Combine(Path.GetTempPath(), "fv-stills-refuse"));
            var request = new StillImageRequest("color=c=black:s=4x4:r=30/1:d=0.016667,format=gray,geq=lum='X'", "gray");
            await Assert.ThrowsAsync<ArgumentException>(() => cache.Make(null, Path.Combine(Path.GetTempPath(), "fx-" + new string('a', 64) + ".pam"), request, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => cache.Make(null, Path.Combine(cache.Folder, "other.pam"), request, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => cache.Make(null, cache.PathOf(request.Generator, "yuv420p"), new StillImageRequest(request.Generator, "yuv420p"), CancellationToken.None));
        }
    }

    /// <summary>한 장 그림 캐시를 실제 FFmpeg로: 파일로 읽은 그림이 수식으로 그린 것과 바이트까지 같은지(도우미도), 한 장 준비 시간.</summary>
    [Collection(TimingCollection.Name)]
    public class StillImageCacheRenderTests
    {
        private readonly ITestOutputHelper _out;

        public StillImageCacheRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static string NewFolder()
        {
            return Path.Combine(Path.GetTempPath(), "fv-stills-" + Guid.NewGuid().ToString("N"));
        }

        private static void DeleteFolder(string folder)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception)
            {
            }
        }

        private static Project VideoProject(int width, int height, params (VideoEffectDef Def, (string, double)[] Values)[] effects)
        {
            var project = TestProjects.Empty();
            project.Width = width;
            project.Height = height;
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            clip.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            clip.Effects = new List<ClipEffect>();
            foreach (var (def, values) in effects)
            {
                var effect = VideoEffects.Create(def);
                foreach (var (key, value) in values)
                    VideoEffects.SetNumber(effect, key, value);
                clip.Effects.Add(effect);
            }
            return project;
        }

        private static RenderPlan Plan(Project project, long from, long to, int width, int height, IStillImageCache stills)
        {
            return RenderGraph.Build(project, new RenderOptions
            {
                From = from, To = to, Width = width, Height = height, Audio = false, Preview = true,
                TempFolder = RenderEnvironment.TempFolder(), FfmpegMajor = FfmpegMedia.Tool.MajorVersion, Stills = stills
            });
        }

        private static byte[] Render(RenderPlan plan)
        {
            var tool = FfmpegMedia.Tool;
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out var script);
            try
            {
                return FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                RenderCommand.TryDelete(script);
                foreach (var file in plan.TextFiles.Keys)
                    RenderCommand.TryDelete(file);
            }
        }

        private static void MakeAll(StillImageCache cache, RenderPlan plan)
        {
            foreach (var item in plan.StillImages)
                cache.Make(FfmpegMedia.Tool, item.Key, item.Value, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>그림을 담는 효과들(값은 그림이 다 생기게 — 떨림·빛줄기·잡음·주사선).</summary>
        private static IEnumerable<(VideoEffectDef, (string, double)[])> Cases()
        {
            yield return (ShockwaveScan.Def, new[] { ("period", 1.5) });
            yield return (RayScan.Def, new[] { ("rays", 80.0) });
            yield return (ScanLine.Def, new[] { ("jitter", 60.0) });
            yield return (SciFiScan.Def, new[] { ("period", 1.5) });
            yield return (BadSignal.Def, new[] { ("noise", 60.0), ("scan", 60.0) });
        }

        [FfmpegFact]
        public void CachedPictures_DrawTheSameBytes_AsTheFormulas()
        {
            var folder = NewFolder();
            try
            {
                var cache = new StillImageCache(folder);
                // 쇼크웨이브는 방향 지도의 세 갈래(픽셀마다·2×2·4×4 칸)를 다 지나게 세 크기로
                foreach (var (width, height) in new[] { (320, 240), (640, 360), (1280, 720) })
                {
                    foreach (var (def, values) in Cases())
                    {
                        if (def != ShockwaveScan.Def && width != 640)
                            continue;
                        var project = VideoProject(width, height, (def, values));
                        var first = Plan(project, 20, 23, width, height, cache);
                        Assert.NotEmpty(first.StillImages);
                        var formulas = Render(first);
                        MakeAll(cache, first);
                        var second = Plan(project, 20, 23, width, height, cache);
                        Assert.Empty(second.StillImages);
                        Assert.Contains(second.Inputs, i => i.Last().EndsWith(".pam", StringComparison.Ordinal));
                        var files = Render(second);
                        Assert.Equal(3L * width * height * 4, formulas.Length);
                        var differing = formulas.Where((b, i) => b != files[i]).Count();
                        _out.WriteLine(def.Id + " " + width + "×" + height + ": 그림 " + first.StillImages.Count + "장, 다른 바이트 " + differing);
                        Assert.True(differing == 0, def.Id + " " + width + "×" + height + ": 파일로 읽은 그림이 " + differing + "바이트 다릅니다");
                    }
                }
                // 사람에게만·여러 효과를 한 클립에(그림 라벨이 겹치지 않음)
                var all = VideoProject(640, 360, Cases().ToArray());
                var mixed = Plan(all, 30, 32, 640, 360, cache);
                Assert.Empty(mixed.StillImages);
                Assert.True(Render(mixed).SequenceEqual(Render(Plan(all, 30, 32, 640, 360, null))), "효과 다섯을 함께 건 그림이 다릅니다");
            }
            finally
            {
                DeleteFolder(folder);
            }
        }

        [FfmpegFact]
        public void Request_MakesThePicturesInTheBackground_Once()
        {
            var folder = NewFolder();
            try
            {
                var cache = new StillImageCache(folder);
                var project = VideoProject(320, 240, (ShockwaveScan.Def, new[] { ("period", 1.5) }));
                var plan = Plan(project, 5, 6, 320, 240, cache);
                Assert.Equal(3, plan.StillImages.Count);
                cache.Request(FfmpegMedia.Tool, plan.StillImages);
                cache.Request(FfmpegMedia.Tool, plan.StillImages);
                Assert.True(cache.WaitIdle(TimeSpan.FromMinutes(1)), "배경 만들기가 끝나지 않았습니다");
                Assert.All(plan.StillImages.Keys, path => Assert.True(File.Exists(path), path));
                Assert.Equal(3, Directory.GetFiles(folder).Length);
                Assert.Empty(Plan(project, 5, 6, 320, 240, cache).StillImages);
            }
            finally
            {
                DeleteFolder(folder);
            }
        }

        /// <summary>FFmpeg 한 번의 처리 시간(-benchmark rtime, 프로세스 시작 빼고) — 세 번 가운데 가장 짧은 값(ms).</summary>
        private static double Rtime(RenderPlan plan)
        {
            var tool = FfmpegMedia.Tool;
            var best = double.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var args = RenderCommand.Arguments(tool, plan, new[] { "-benchmark", "-map", "[vout]", "-frames:v", "1", "-f", "null", "-" }, out var script);
                args[args.IndexOf("error")] = "info";
                try
                {
                    var result = FfmpegRunner.RunText(tool.FfmpegPath, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
                    Assert.True(result.ExitCode == 0, result.Error);
                    var line = (result.Error + "\n" + result.Output).Split('\n').First(l => l.Contains("rtime="));
                    var text = line.Substring(line.IndexOf("rtime=", StringComparison.Ordinal) + 6).Trim().TrimEnd('s');
                    best = Math.Min(best, double.Parse(text, CultureInfo.InvariantCulture) * 1000);
                }
                finally
                {
                    RenderCommand.TryDelete(script);
                }
            }
            return best;
        }

        [FfmpegFact]
        public void Cost_OneFrame_WithAndWithoutCachedPictures()
        {
            // 멈춘 화면 한 장을 그리는 FFmpeg 처리 시간: 효과 없이 / 수식으로 / 캐시 파일로 — 4K 쇼크웨이브(#29 실측: 이 그림들 때문에 한 장 준비 +140ms)와 1080p 넷
            var folder = NewFolder();
            try
            {
                var cache = new StillImageCache(folder);
                var lines = new List<string>();
                foreach (var (def, values, width, height) in new[]
                {
                    (ShockwaveScan.Def, new[] { ("period", 2.2) }, 3840, 2160),
                    (ShockwaveScan.Def, new[] { ("period", 2.2) }, 1920, 1080),
                    (RayScan.Def, new (string, double)[0], 1920, 1080),
                    (ScanLine.Def, new (string, double)[0], 1920, 1080),
                    (BadSignal.Def, new (string, double)[0], 1920, 1080)
                })
                {
                    var plain = VideoProject(width, height);
                    var none = Rtime(Plan(plain, 30, 31, width, height, null));
                    var project = VideoProject(width, height, (def, values));
                    var formulas = Rtime(Plan(project, 30, 31, width, height, null));
                    MakeAll(cache, Plan(project, 30, 31, width, height, cache));
                    var files = Rtime(Plan(project, 30, 31, width, height, cache));
                    lines.Add(def.Name + " " + width + "×" + height + ": 효과 없이 " + none.ToString("0") + " · 수식 " + formulas.ToString("0") + "(+" + (formulas - none).ToString("0")
                        + ") → 캐시 " + files.ToString("0") + "ms(+" + (files - none).ToString("0") + ")");
                    // 시간 재기 컬렉션(병렬 끔)에서 잰다. 공용 CI 기계는 몇 배 느리고 흔들려 개발 PC에서만 본다
                    if (width == 3840 && Environment.GetEnvironmentVariable("CI") != "true")
                        Assert.True(formulas - files > 50, "4K 쇼크웨이브 한 장 준비가 캐시로 " + (formulas - files).ToString("0") + "ms만 줄었습니다");
                }
                foreach (var line in lines)
                    _out.WriteLine(line);
                _out.WriteLine("캐시 파일 " + Directory.GetFiles(folder).Length + "개, " + (Directory.GetFiles(folder).Sum(f => new FileInfo(f).Length) / 1024.0 / 1024).ToString("0.0") + "MB");
            }
            finally
            {
                DeleteFolder(folder);
            }
        }

        [HelperFact]
        public void Helper_ReadsTheCachedPictures_LikeFfmpeg()
        {
            var folder = NewFolder();
            try
            {
                var cache = new StillImageCache(folder);
                var project = VideoProject(320, 240, Cases().ToArray());
                MakeAll(cache, Plan(project, 12, 13, 320, 240, cache));
                var plan = Plan(project, 12, 13, 320, 240, cache);
                Assert.Empty(plan.StillImages);
                RenderCommand.Prepare(plan, CancellationToken.None);
                using (var helper = PreviewHelperTests.Start())
                {
                    var byHelper = helper.Frame(RenderCommand.ToHelperPlan(plan, true, 320, 240, project.SampleRate, 1), CancellationToken.None).GetAwaiter().GetResult();
                    var byFfmpeg = Render(Plan(project, 12, 13, 320, 240, cache));
                    Assert.True(byHelper.SequenceEqual(byFfmpeg), "도우미가 캐시 그림으로 그린 장이 ffmpeg.exe와 다릅니다");
                }
            }
            finally
            {
                DeleteFolder(folder);
            }
        }
    }
}
