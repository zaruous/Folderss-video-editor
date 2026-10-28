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
    /// <summary>
    /// 미리 보기 도우미 시험: Windows에서 시험 출력의 helper\VideoEditor.Helper.exe와, FFmpeg 폴더의 공유 DLL(shared 빌드 — avcodec-63.dll 등)이 있을 때만.
    /// FOLDERSS_VE_FFMPEG에 BtbN win64 gpl-shared 빌드의 bin을 주면 돈다.
    /// </summary>
    public sealed class HelperFactAttribute : FactAttribute
    {
        public HelperFactAttribute()
        {
            if (FfmpegMedia.Tool == null)
                Skip = "FFmpeg를 찾지 못해 건너뜁니다(FOLDERSS_VE_FFMPEG에 ffmpeg 폴더를 주거나 PATH에 두세요).";
            else if (HelperMedia.Executable == null)
                Skip = "미리 보기 도우미(helper\\" + PreviewHelper.ExeName + ")가 시험 출력에 없어 건너뜁니다(Windows에서만).";
            else if (!PreviewHelper.HasLibraries(FfmpegMedia.Tool.Folder))
                Skip = "FFmpeg 폴더에 도우미가 쓸 공유 DLL(" + PreviewHelper.Libraries[3] + " 등)이 없어 건너뜁니다(FOLDERSS_VE_FFMPEG에 shared 빌드의 bin을 주세요).";
        }
    }

    /// <summary>도우미 시험 미디어(FfmpegMedia 폴더 아래): 회전 정보가 든 동영상, 인물 마스크 동영상, 반투명 PNG, 빨간 글자 PNG, 1080p H.264 60초.</summary>
    internal static class HelperMedia
    {
        private static readonly Lazy<string> LazyFolder = new Lazy<string>(Create);
        private static readonly Lazy<string> LazyHd = new Lazy<string>(CreateHd);

        public static string Executable { get { return PreviewHelper.FindExecutable(AppContext.BaseDirectory); } }
        public static string Folder { get { return LazyFolder.Value; } }
        public static string Rotated { get { return Path.Combine(Folder, "rotated.mp4"); } }
        public static string MaskVideo { get { return Path.Combine(Folder, "mask-left.mkv"); } }
        public static string HalfPng { get { return Path.Combine(Folder, "half.png"); } }
        public static string RedText { get { return Path.Combine(Folder, "red-text.png"); } }
        /// <summary>한글·공백이 든 파일 이름(시험 동영상 사본) — 경로가 UTF-8로 FFmpeg에 가는지.</summary>
        public static string KoreanName { get { return Path.Combine(Folder, "한글 이름 영상.mp4"); } }
        /// <summary>1080p 30fps 60초 H.264(2초마다 키프레임) + AAC — 재생 헤드 이동 시간을 잰다.</summary>
        public static string Hd { get { return LazyHd.Value; } }

        private static string Create()
        {
            var folder = Path.Combine(FfmpegMedia.Folder, "helper");
            Directory.CreateDirectory(folder);
            // 휴대폰 세로 영상처럼 회전 정보(displaymatrix 90°)만 붙인 것 — ffmpeg.exe는 자동 회전(transpose)한다
            Run("-display_rotation:v:0", "90", "-i", FfmpegMedia.TestVideo, "-c", "copy", Path.Combine(folder, "rotated.mp4"));
            Run("-f", "lavfi", "-i", "color=c=black:size=160x120:rate=30:duration=2,format=gray,geq=lum='if(lt(X,80),255,0)'", "-c:v", "ffv1", Path.Combine(folder, "mask-left.mkv"));
            Run("-f", "lavfi", "-i", "color=c=0x303030:size=320x240,format=rgba,geq=r=48:g=200:b=48:a='if(lt(X,160),255,90)'", "-frames:v", "1", Path.Combine(folder, "half.png"));
            Run("-f", "lavfi", "-i", "color=red:s=200x80", "-frames:v", "1", Path.Combine(folder, "red-text.png"));
            File.Copy(FfmpegMedia.TestVideo, Path.Combine(folder, "한글 이름 영상.mp4"), true);
            return folder;
        }

        private static string CreateHd()
        {
            var path = Path.Combine(Folder, "hd-1080p-60s.mp4");
            var codec = FfmpegMedia.Tool.HasEncoder("libx264") ? new[] { "-c:v", "libx264", "-preset", "veryfast" } : new[] { "-c:v", "mpeg4", "-q:v", "4" };
            var args = new List<string> { "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=60", "-f", "lavfi", "-i", "sine=frequency=330:duration=60" };
            args.AddRange(codec);
            args.AddRange(new[] { "-g", "60", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path });
            Run(args.ToArray());
            return path;
        }

        private static void Run(params string[] args)
        {
            var all = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            all.AddRange(args);
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfmpegPath, all, TimeSpan.FromMinutes(5), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException("시험 미디어를 만들지 못했습니다: " + result.Error);
        }
    }

    /// <summary>도우미 시험은 다른 시험과 함께 돌리지 않는다(시간을 재고, 프로세스를 끝내 본다).</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class PreviewHelperCollection : ICollectionFixture<PreviewHelperFixture>
    {
        public const string Name = "미리 보기 도우미";
    }

    /// <summary>여러 시험이 함께 쓰는 도우미(처음 쓸 때 띄움). 끝내 보는 시험은 따로 띄운다.</summary>
    public sealed class PreviewHelperFixture : IDisposable
    {
        private readonly Lazy<PreviewHelper> _helper = new Lazy<PreviewHelper>(PreviewHelperTests.Start);

        public PreviewHelper Helper { get { return _helper.Value; } }

        public void Dispose()
        {
            if (_helper.IsValueCreated)
                _helper.Value.Dispose();
        }
    }

    [Collection(PreviewHelperCollection.Name)]
    public class PreviewHelperTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;
        private readonly PreviewHelperFixture _fixture;

        public PreviewHelperTests(ITestOutputHelper output, PreviewHelperFixture fixture)
        {
            _out = output;
            _fixture = fixture;
        }

        internal static PreviewHelper Start()
        {
            return PreviewHelper.StartAsync(HelperMedia.Executable, FfmpegMedia.Tool.Folder, TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
        }

        private static Project NewProject(bool magnetic = true)
        {
            var project = TestProjects.Empty(magnetic);
            project.Width = Width;
            project.Height = Height;
            return project;
        }

        private static Clip Add(Project project, Track track, string path, long start, long imageFrames = 60)
        {
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(path), project.Rate, start, imageFrames);
            TimelineEdits.Place(project, track, clip, start);
            return clip;
        }

        private static RenderPlan FramePlan(Project project, long frame, int width, int height, ITextRasterizer rasterizer, IPersonMaskProvider masks)
        {
            return RenderGraph.Build(project, new RenderOptions
            {
                From = frame,
                To = frame + 1,
                Width = width,
                Height = height,
                Audio = false,
                Preview = true,
                FontFile = RenderEnvironment.DefaultFont(),
                TempFolder = RenderEnvironment.TempFolder(),
                FfmpegMajor = FfmpegMedia.Tool.MajorVersion,
                TextRasterizer = rasterizer,
                PersonMasks = masks
            });
        }

        /// <summary>같은 계획을 ffmpeg.exe로 한 장(PreviewRenderer.Frame의 ffmpeg.exe 길과 같은 인수).</summary>
        private static byte[] FfmpegFrame(RenderPlan plan)
        {
            var args = RenderCommand.Arguments(FfmpegMedia.Tool, plan, new[] { "-map", "[vout]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out var script);
            try
            {
                return FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath, args, TimeSpan.FromSeconds(60), CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }

        /// <summary>같은 계획을 도우미로 한 장(PreviewRenderer.Frame의 도우미 길과 같음).</summary>
        private static byte[] HelperFrame(PreviewHelper helper, RenderPlan plan, Project project, int width, int height)
        {
            RenderCommand.Prepare(plan, CancellationToken.None);
            return helper.Frame(RenderCommand.ToHelperPlan(plan, true, width, height, project.SampleRate, 1), CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>두 BGRA의 차이: 채널 값의 가장 큰 차이, 평균 차이, 2보다 크게 다른 채널 값의 비율.</summary>
        private static (int Max, double Mean, double Over2) Diff(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            long sum = 0;
            var max = 0;
            var over = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var d = Math.Abs(a[i] - b[i]);
                sum += d;
                if (d > max)
                    max = d;
                if (d > 2)
                    over++;
            }
            return (max, sum / (double)a.Length, over / (double)a.Length);
        }

        private static string Describe((int Max, double Mean, double Over2) diff)
        {
            return string.Format("최대 {0} · 평균 {1:0.000} · 2 넘게 다름 {2:0.00%}", diff.Max, diff.Mean, diff.Over2);
        }

        private sealed class Masks : IPersonMaskProvider
        {
            private readonly string _source;
            private readonly string _mask;

            public Masks(string source, string mask)
            {
                _source = source;
                _mask = mask;
            }

            public string Find(string sourcePath)
            {
                return string.Equals(sourcePath, _source, StringComparison.OrdinalIgnoreCase) ? _mask : null;
            }
        }

        private sealed class Case
        {
            public string Name;
            public Project Project;
            public long Frame;
            public ITextRasterizer Rasterizer;
            public IPersonMaskProvider Masks;
        }

        /// <summary>도우미가 ffmpeg.exe와 같게 그려야 하는 장면들: 입력 옵션(-ss·-t·-loop·-framerate·-sseof·image2), 회전, 필터(전환·글자·자막·효과·마스크).</summary>
        private static List<Case> Cases()
        {
            var cases = new List<Case>();
            void Case(string name, Project project, long frame, ITextRasterizer rasterizer = null, IPersonMaskProvider masks = null)
            {
                cases.Add(new Case { Name = name, Project = project, Frame = frame, Rasterizer = rasterizer, Masks = masks });
            }

            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            Case("동영상 — 키프레임 사이(-ss·-t)", project, 37);

            project = NewProject();
            Add(project, project.MainTrack, HelperMedia.KoreanName, 0);
            Case("한글 파일 이름", project, 25);

            project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var red = Add(project, TimelineEdits.AddTrack(project, TrackKind.Video), FfmpegMedia.RedImage, 0, 30);
            red.Scale = 0.5;
            red.X = 40;
            red.Y = -20;
            red.Rotation = 15;
            red.Opacity = 0.6;
            Case("사진 겹치기(-loop·-framerate, 위치·크기·회전·불투명도)", project, 10);

            project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            Add(project, TimelineEdits.AddTrack(project, TrackKind.Video), HelperMedia.HalfPng, 0, 30);
            Case("반투명 PNG", project, 12);

            project = NewProject(false);
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 30);
            Case("빈틈(검정)", project, 5);

            project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            var text = TimelineEdits.CreateText("가나다 ABC", 0, 30);
            text.FontSize = 40;
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), text, 0);
            Case("글자(drawtext)", project, 3);

            project = NewProject(false);
            var styled = TimelineEdits.CreateText("가나다", 0, 30);
            styled.Style = TextStyle.FromLegacy(styled);
            TimelineEdits.Place(project, project.MainTrack, styled, 0);
            Case("꾸민 글자 PNG(image2·pattern_type none)", project, 10, new FakeTextRasterizer(HelperMedia.RedText));

            project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var fade = Add(project, project.MainTrack, FfmpegMedia.RedImage, 999, 60);
            fade.TransitionIn = 30;
            Case("크로스페이드", project, fade.Start + 15);

            project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var heart = Add(project, project.MainTrack, FfmpegMedia.RedImage, 999, 60);
            heart.Scale = 4;
            TimelineEdits.SetTransition(project, heart, "Heart", 30);
            // 앞 클립(2초)은 원본이 끝난 뒤를 그린다 — 파일 끝의 마지막 장(-sseof)
            Case("모양 전환(xfade 수식) + 원본 끝 뒤(-sseof)", project, 75);

            project = NewProject();
            var fast = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            TimelineEdits.SetSpeed(project, fast, 2);
            Case("속도 2배", project, 20);

            project = NewProject();
            Add(project, project.MainTrack, HelperMedia.Rotated, 0);
            Case("회전 정보 90°(자동 회전)", project, 15);

            if (FfmpegMedia.Tool.HasFilter("ass"))
            {
                project = NewProject();
                Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
                TimelineEdits.ImportSubtitles(project, new[] { new SubtitleCue { Start = 1, End = 2, Text = "HELLO WORLD" } });
                Case("자막(libass)", project, 45);
            }

            foreach (var def in VideoEffects.All)
            {
                project = NewProject();
                var clip = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
                VideoEffects.Add(project, clip, def);
                Case("동영상 효과 " + def.Id, project, 20);
            }

            project = NewProject();
            var masked = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            VideoEffects.Add(project, masked, ScanLine.Def).PersonOnly = true;
            Case("사람에게만(인물 마스크 동영상)", project, 20, null, new Masks(FfmpegMedia.TestVideo, HelperMedia.MaskVideo));
            return cases;
        }

        [HelperFact]
        public void Start_ReportsVersion_AndQuitEndsTheProcess()
        {
            var watch = Stopwatch.StartNew();
            var helper = Start();
            var ready = watch.ElapsedMilliseconds;
            using (var process = Process.GetProcessById(helper.ProcessId))
            {
                _out.WriteLine("준비까지 " + ready + "ms · FFmpeg " + helper.Version + " · " + helper.Folder);
                Assert.True(helper.IsAlive);
                Assert.False(string.IsNullOrEmpty(helper.Version));
                helper.Dispose();
                Assert.True(process.WaitForExit(3000), "끝내라고 하면 끝난다");
                Assert.False(helper.IsAlive);
            }
        }

        [HelperFact]
        public void Frame_SameAsFfmpegExe()
        {
            var helper = _fixture.Helper;
            var failures = new List<string>();
            foreach (var item in Cases())
            {
                var plan = FramePlan(item.Project, item.Frame, Width, Height, item.Rasterizer, item.Masks);
                try
                {
                    var expected = FfmpegFrame(plan);
                    Assert.Equal(Width * Height * 4, expected.Length);
                    byte[] actual;
                    try
                    {
                        actual = HelperFrame(helper, plan, item.Project, Width, Height);
                    }
                    catch (HelperException ex)
                    {
                        _out.WriteLine(item.Name + ": 도우미 실패 — " + ex.Message);
                        failures.Add(item.Name + ": 도우미 실패");
                        continue;
                    }
                    var diff = Diff(expected, actual);
                    _out.WriteLine(item.Name + ": " + Describe(diff));
                    // 같은 libavfilter 그래프라 같아야 한다 — 디코더 스레드·SIMD 경로에 따른 반올림 차이만 둔다
                    if (diff.Max > 3 || diff.Mean > 0.05)
                        failures.Add(item.Name + ": " + Describe(diff));
                }
                finally
                {
                    foreach (var file in plan.TextFiles.Keys)
                        RenderCommand.TryDelete(file);
                }
            }
            Assert.True(helper.IsAlive, "장면마다 그린 뒤에도 도우미가 살아 있다");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [HelperFact]
        public void PreviewRendererFrame_UsesTheHelper_WithoutFallingBack()
        {
            var helper = _fixture.Helper;
            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            var failures = new List<HelperException>();
            var viaHelper = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 31, 64, 48, CancellationToken.None, null, helper, failures.Add).GetAwaiter().GetResult();
            var viaFfmpeg = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 31, 64, 48, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Empty(failures);
            var diff = Diff(viaFfmpeg, viaHelper);
            _out.WriteLine(Describe(diff));
            Assert.True(diff.Max <= 3, Describe(diff));
        }

        /// <summary>흐름에서 장 단위로 끝까지 읽는다(시간 제한 안에).</summary>
        private static List<byte[]> ReadFrames(Stream input, int size, int limit = int.MaxValue)
        {
            var frames = new List<byte[]>();
            var reading = Task.Run(() =>
            {
                while (frames.Count < limit)
                {
                    var buffer = new byte[size];
                    if (!HelperProtocol.ReadExactly(input, buffer, size))
                        break;
                    frames.Add(buffer);
                }
            });
            Assert.True(reading.Wait(TimeSpan.FromSeconds(60)), "흐름을 다 읽지 못했습니다(멈춤)");
            return frames;
        }

        private static byte[] ReadAll(Stream input)
        {
            using (var memory = new MemoryStream())
            {
                Assert.True(input.CopyToAsync(memory).Wait(TimeSpan.FromSeconds(60)), "흐름을 다 읽지 못했습니다(멈춤)");
                return memory.ToArray();
            }
        }

        [HelperFact]
        public void Playback_VideoStream_SameAsFfmpegExe()
        {
            // 전환·글자·속도가 섞인 구간을 재생처럼 이어 그린다(사진 → 크로스페이드 → 2배속 동영상, 위에 글자)
            var helper = _fixture.Helper;
            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var test = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 999);
            TimelineEdits.SetTransition(project, test, "CircleOpen", 20);
            var text = TimelineEdits.CreateText("재생 ABC", 30, 60);
            text.FontSize = 30;
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), text, 30);
            const long from = 40, to = 110;
            var size = Width * Height * 4;
            var failures = new List<HelperException>();
            List<byte[]> actual;
            using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, from, to, Width, Height, null, failures.Add))
            {
                Assert.True(stream.UsesHelper, "도우미 흐름");
                actual = ReadFrames(stream.Input, size);
            }
            List<byte[]> expected;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, from, to, Width, Height, out var files))
            {
                process.BeginErrorReadLine();
                expected = ReadFrames(process.StandardOutput.BaseStream, size);
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Empty(failures);
            _out.WriteLine("장수: ffmpeg.exe " + expected.Count + " · 도우미 " + actual.Count);
            Assert.Equal(to - from, expected.Count);
            Assert.Equal(expected.Count, actual.Count);
            var worst = (Max: 0, Mean: 0.0, Over2: 0.0);
            var worstIndex = 0;
            for (var i = 0; i < expected.Count; i++)
            {
                var diff = Diff(expected[i], actual[i]);
                if (diff.Mean > worst.Mean || diff.Max > worst.Max)
                {
                    worst = diff;
                    worstIndex = i;
                }
            }
            _out.WriteLine("가장 다른 장 " + (from + worstIndex) + ": " + Describe(worst));
            Assert.True(worst.Max <= 3 && worst.Mean <= 0.05, Describe(worst));
        }

        /// <summary>소리 흐름 하나(helper가 null이면 ffmpeg.exe)를 끝까지 읽는다.</summary>
        private byte[] AudioVia(PreviewHelper helper, Project project, long from, long to)
        {
            if (helper == null)
            {
                using (var process = PreviewRenderer.StartAudio(FfmpegMedia.Tool, project, from, to, out var script))
                {
                    process.BeginErrorReadLine();
                    var bytes = ReadAll(process.StandardOutput.BaseStream);
                    RenderCommand.TryDelete(script);
                    return bytes;
                }
            }
            var failures = new List<HelperException>();
            using (var stream = PreviewRenderer.OpenAudio(FfmpegMedia.Tool, helper, project, from, to, failures.Add))
            {
                Assert.True(stream.UsesHelper, "도우미 흐름" + (failures.Count > 0 ? ": " + failures[0].Message : ""));
                var bytes = ReadAll(stream.Input);
                if (stream.Error.Length > 0)
                    _out.WriteLine("도우미 오류: " + stream.Error);
                return bytes;
            }
        }

        [HelperFact]
        public void Playback_AudioStream_SameAsFfmpegExe()
        {
            // 같은 도우미로 차례로 — 앞 요청이 연 파일을 다음 요청이 다시 여는 경우까지.
            // 예전에는 소리만 보고 옮겨(AAC 프라이밍 보정으로 목표를 지나침) 앞부분이 빠지고, 되감은 AAC 디코더는 첫 프레임이 달라 시각이 어긋났다
            var helper = _fixture.Helper;
            var cases = new List<(string Name, Project Project, long From, long To)>();
            Project Video(string path, long start = 0)
            {
                var project = NewProject(false);
                Add(project, project.MainTrack, path, start);
                return project;
            }
            cases.Add(("AAC 처음부터", Video(FfmpegMedia.TestVideo), 0, 50));
            cases.Add(("AAC 0.17초부터(-ss)", Video(FfmpegMedia.TestVideo), 5, 50));
            cases.Add(("AAC 클립을 10프레임에(adelay)", Video(FfmpegMedia.TestVideo, 10), 5, 50));
            cases.Add(("AAC 다시 처음부터", Video(FfmpegMedia.TestVideo), 0, 50));
            var tone = NewProject(false);
            Add(tone, tone.AudioTracks[0], FfmpegMedia.Tone, 0);
            cases.Add(("WAV 0.17초부터", tone, 5, 50));
            var mixed = NewProject();
            Add(mixed, mixed.MainTrack, FfmpegMedia.TestVideo, 0);
            var quiet = Add(mixed, mixed.AudioTracks[0], FfmpegMedia.Tone, 10);
            quiet.Volume = 0.5;
            quiet.FadeIn = 15;
            cases.Add(("동영상 소리 + WAV(음량·페이드)", mixed, 5, 80));
            var fast = Video(FfmpegMedia.TestVideo);
            TimelineEdits.SetSpeed(fast, fast.MainTrack.Clips[0], 2);
            cases.Add(("2배속(음높이 유지)", fast, 3, 28));
            cases.Add(("1080p H.264(B 프레임) + AAC 41.5초부터", HdProject(), 1245, 1305));
            var failures = new List<string>();
            foreach (var (name, project, from, to) in cases)
            {
                var actual = AudioVia(helper, project, from, to);
                var expected = AudioVia(null, project, from, to);
                var max = 0;
                var first = -1;
                for (var i = 0; i + 1 < Math.Min(expected.Length, actual.Length); i += 2)
                {
                    var d = Math.Abs(BitConverter.ToInt16(expected, i) - BitConverter.ToInt16(actual, i));
                    if (d > 2 && first < 0)
                        first = i / 4;
                    max = Math.Max(max, d);
                }
                _out.WriteLine(name + ": 바이트 ffmpeg.exe " + expected.Length + " · 도우미 " + actual.Length + " · 표본 최대 차이 " + max + (first >= 0 ? " (처음 다른 표본 " + first + ")" : ""));
                if (expected.Length != actual.Length || max > 2)
                    failures.Add(name);
            }
            Assert.True(failures.Count == 0, "ffmpeg.exe와 다른 소리: " + string.Join(", ", failures));
        }

        [HelperFact]
        public void Frame_1080pWithBFrames_SameAsFfmpegExe()
        {
            // B 프레임이 있는 파일이면 ffmpeg.exe는 3/23초 앞에서 옮긴다 — 키프레임(2초마다) 바로 앞·뒤와 끝 장도 같은 장이 나와야 한다
            var project = HdProject();
            var helper = _fixture.Helper;
            var failures = new List<string>();
            foreach (var frame in new long[] { 1245, 1200, 1201, 1203, 1199, 0, 1799 })
            {
                var plan = FramePlan(project, frame, 640, 360, null, null);
                try
                {
                    var diff = Diff(FfmpegFrame(plan), HelperFrame(helper, plan, project, 640, 360));
                    _out.WriteLine(frame + "번 장: " + Describe(diff));
                    if (diff.Max > 3 || diff.Mean > 0.05)
                        failures.Add(frame + "번 장: " + Describe(diff));
                }
                finally
                {
                    foreach (var file in plan.TextFiles.Keys)
                        RenderCommand.TryDelete(file);
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        [HelperFact]
        public void Killed_ReportsDied_AndPreviewFallsBackToFfmpegExe()
        {
            var helper = Start();
            try
            {
                var died = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                helper.Died += reason => died.TrySetResult(reason);
                var project = NewProject();
                Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);

                // 재생 중에 끝남: 읽던 흐름이 끝나고(멈추지 않음) Died가 온다
                var size = Width * Height * 4;
                using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, 0, 60, Width, Height))
                {
                    Assert.True(stream.UsesHelper);
                    Assert.Single(ReadFrames(stream.Input, size, 1));
                    using (var process = Process.GetProcessById(helper.ProcessId))
                        process.Kill();
                    var rest = Task.Run(() =>
                    {
                        try
                        {
                            ReadFrames(stream.Input, size);
                        }
                        catch (IOException)
                        {
                            // 끊긴 파이프 — 끝난 것으로
                        }
                    });
                    Assert.True(rest.Wait(TimeSpan.FromSeconds(10)), "도우미가 끝나면 읽던 흐름도 끝난다");
                }
                Assert.True(died.Task.Wait(TimeSpan.FromSeconds(10)), "Died 알림");
                _out.WriteLine("Died: " + died.Task.Result);
                Assert.False(helper.IsAlive);

                // 죽은 도우미를 주어도 ffmpeg.exe로 그린다
                var failures = new List<HelperException>();
                var frame = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 15, Width, Height, CancellationToken.None, null, helper, failures.Add).GetAwaiter().GetResult();
                Assert.NotNull(frame);
                Assert.True(failures.All(f => f.HelperDied));
                var expected = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 15, Width, Height, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(expected, frame);
                using (var video = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, 0, 10, Width, Height))
                {
                    Assert.False(video.UsesHelper, "죽은 도우미면 ffmpeg.exe 흐름");
                    Assert.Equal(10, ReadFrames(video.Input, size).Count);
                }
            }
            finally
            {
                helper.Dispose();
            }
        }

        [HelperFact]
        public void Killed_JustBeforeFrameRequest_FallsBackWithHelperDied()
        {
            // 끝난 것을 아직 모를 때(읽는 스레드가 알아채기 전) 요청해도 ffmpeg.exe로 그리고, 실패는 "도우미가 죽음"으로 알린다
            // (그 작업만의 실패로 세면 EditorServices가 도우미를 다시 띄우지 않고 끈다)
            var helper = Start();
            try
            {
                var project = NewProject();
                Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
                using (var process = Process.GetProcessById(helper.ProcessId))
                {
                    process.Kill();
                    Assert.True(process.WaitForExit(5000));
                }
                var failures = new List<HelperException>();
                var frame = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 20, Width, Height, CancellationToken.None, null, helper, failures.Add).GetAwaiter().GetResult();
                Assert.NotNull(frame);
                _out.WriteLine("실패 알림 " + failures.Count + "개" + (failures.Count > 0 ? ": " + failures[0].Message : "(이미 죽은 것을 알아 바로 ffmpeg.exe로)"));
                Assert.True(failures.All(f => f.HelperDied), "죽어서 실패하면 HelperDied");
                Assert.False(helper.IsAlive);
            }
            finally
            {
                helper.Dispose();
            }
        }

        [HelperFact]
        public void BadRequest_FailsOnlyThatRequest_HelperStaysAlive()
        {
            var helper = _fixture.Helper;
            var unknown = new HelperPlan { Filter = "[0:v]null[vout]", Width = 64, Height = 48 };
            unknown.Inputs.Add(new List<string> { "-stream_loop", "1", "-i", FfmpegMedia.TestVideo });
            var error = Assert.Throws<HelperException>(() => helper.Frame(unknown, CancellationToken.None).GetAwaiter().GetResult());
            _out.WriteLine("모르는 옵션: " + error.Message);
            Assert.False(error.HelperDied);
            Assert.Contains("-stream_loop", error.Message);

            var badGraph = new HelperPlan { Filter = "[0:v]no_such_filter[vout]", Width = 64, Height = 48 };
            badGraph.Inputs.Add(new List<string> { "-i", FfmpegMedia.TestVideo });
            error = Assert.Throws<HelperException>(() => helper.Frame(badGraph, CancellationToken.None).GetAwaiter().GetResult());
            _out.WriteLine("없는 필터: " + error.Message);
            Assert.False(error.HelperDied);

            var missing = new HelperPlan { Filter = "[0:v]scale=64:48[vout]", Width = 64, Height = 48 };
            missing.Inputs.Add(new List<string> { "-i", Path.Combine(HelperMedia.Folder, "없는 파일.mp4") });
            error = Assert.Throws<HelperException>(() => helper.Frame(missing, CancellationToken.None).GetAwaiter().GetResult());
            _out.WriteLine("없는 파일: " + error.Message);
            Assert.False(error.HelperDied);

            Assert.True(helper.IsAlive);
            var good = new HelperPlan { Filter = "[0:v]scale=64:48[vout]", Width = 64, Height = 48 };
            good.Inputs.Add(new List<string> { "-ss", "0.5", "-t", "0.5", "-i", FfmpegMedia.TestVideo });
            Assert.Equal(64 * 48 * 4, helper.Frame(good, CancellationToken.None).GetAwaiter().GetResult().Length);
        }

        [HelperFact]
        public void StopAndRestart_Repeatedly_DoesNotLeakOrHang()
        {
            // 재생 중 멈추고 바로 옮기기·다시 재생을 되풀이: 도우미가 굳지 않고, 잠시 쉬면(열어 둔 파일을 10초 뒤 닫음) 스레드·핸들이 처음만큼으로 돌아오며 파일을 놓는다
            var video = Path.Combine(HelperMedia.Folder, "repeat-" + Guid.NewGuid().ToString("N") + ".mp4");
            File.Copy(FfmpegMedia.TestVideo, video);
            var project = NewProject();
            Add(project, project.MainTrack, video, 0);
            Add(project, project.AudioTracks[0], FfmpegMedia.Tone, 0);
            var size = Width * Height * 4;
            var helper = Start();
            try
            {
                using (var process = Process.GetProcessById(helper.ProcessId))
                {
                    (int Handles, int Threads) Idle()
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(16));
                        process.Refresh();
                        return (process.HandleCount, process.Threads.Count);
                    }
                    for (var i = 0; i < 3; i++)
                        Iterate(i);
                    var before = Idle();
                    var watch = Stopwatch.StartNew();
                    for (var i = 3; i < 33; i++)
                        Iterate(i);
                    _out.WriteLine("30번 되풀이 " + watch.ElapsedMilliseconds + "ms");
                    Assert.True(helper.IsAlive);
                    process.Refresh();
                    var busy = (process.HandleCount, process.Threads.Count);
                    var after = Idle();
                    _out.WriteLine("쉰 뒤 핸들 " + before.Handles + " → (되풀이 직후 " + busy.Item1 + ") → " + after.Handles + " · 스레드 " + before.Threads + " → (" + busy.Item2 + ") → " + after.Threads);
                    // 30번 되풀이 — 요청마다 하나씩만 새도 30 넘게 는다(.NET 스레드 풀·타이머 스레드 몇 개는 늘 수 있다)
                    Assert.InRange(after.Handles, 0, before.Handles + 30);
                    Assert.InRange(after.Threads, 0, before.Threads + 6);
                    // 쉬는 동안 닫았으니 다른 프로그램이 파일을 독점으로 열 수 있다
                    using (File.Open(video, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                    watch.Restart();
                    Assert.NotNull(PreviewRenderer.Frame(FfmpegMedia.Tool, project, 30, Width, Height, CancellationToken.None, null, helper).GetAwaiter().GetResult());
                    _out.WriteLine("쉰 뒤 한 장 " + watch.ElapsedMilliseconds + "ms");
                    Assert.True(watch.ElapsedMilliseconds < 2000, "되풀이 뒤에도 바로 그린다");
                }
            }
            finally
            {
                helper.Dispose();
                RenderCommand.TryDelete(video);
            }

            void Iterate(int i)
            {
                using (var video = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, i * 2 % 50, 60, Width, Height))
                using (var audio = PreviewRenderer.OpenAudio(FfmpegMedia.Tool, helper, project, i * 2 % 50, 60))
                {
                    Assert.True(video.UsesHelper && audio.UsesHelper, "도우미 흐름 " + i);
                    ReadFrames(video.Input, size, 1 + i % 3);
                    var chunk = new byte[4096];
                    Assert.True(HelperProtocol.ReadExactly(audio.Input, chunk, chunk.Length), "소리 " + i);
                }
                // 멈춘 사이의 재생 헤드 이동(가끔 취소)
                using (var cancel = new CancellationTokenSource())
                {
                    if (i % 4 == 3)
                        cancel.Cancel();
                    try
                    {
                        PreviewRenderer.Frame(FfmpegMedia.Tool, project, (i * 7) % 60, Width, Height, cancel.Token, null, helper).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        // 취소한 요청
                    }
                }
            }
        }

        /// <summary>도우미를 직접 띄우고(--parent 선택) 준비 응답까지 기다린다.</summary>
        private static Process StartRaw(int parent)
        {
            var process = Launch(parent);
            var ready = Task.Run(() =>
            {
                HelperProtocol.ReadResponse(process.StandardOutput.BaseStream, out _, out var kind, out _);
                return kind;
            });
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "준비 응답");
            Assert.Equal(HelperProtocol.KindReady, ready.Result);
            return process;
        }

        private static Process Launch(int parent)
        {
            var info = new ProcessStartInfo(HelperMedia.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("--ffmpeg");
            info.ArgumentList.Add(FfmpegMedia.Tool.Folder);
            if (parent > 0)
            {
                info.ArgumentList.Add("--parent");
                info.ArgumentList.Add(parent.ToString());
            }
            var process = Process.Start(info);
            JobObject.Add(process);
            process.ErrorDataReceived += (s, e) => { };
            process.BeginErrorReadLine();
            return process;
        }

        [HelperFact]
        public void EndsWithParent_AndWhenInputCloses()
        {
            // 부모(Folderss)가 어떻게 끝나든 함께 끝난다 — 부모 대신 다른 도우미를 띄워 끝내 본다.
            // 준비 응답을 받자마자 끝낸다(예전에는 준비 뒤에 부모를 찾아, 그 사이에 끝난 부모를 놓치고 혼자 남았다)
            using (var parent = StartRaw(0))
            using (var child = StartRaw(parent.Id))
            {
                parent.Kill();
                Assert.True(child.WaitForExit(5000), "부모가 끝나면 끝난다");
            }
            // 띄우기도 전에 부모가 끝났으면 바로 끝난다
            int gone;
            using (var parent = StartRaw(0))
            {
                gone = parent.Id;
                parent.Kill();
                Assert.True(parent.WaitForExit(5000));
            }
            using (var orphan = Launch(gone))
                Assert.True(orphan.WaitForExit(10000), "이미 끝난 부모");
            using (var helper = StartRaw(0))
            {
                helper.StandardInput.Close();
                Assert.True(helper.WaitForExit(5000), "표준 입력이 닫히면 끝난다");
                Assert.Equal(0, helper.ExitCode);
            }
        }

        [Fact]
        public void HasLibraries_NeedsEverySharedDll()
        {
            // ffmpeg.exe만 있는 빌드(winget Gyan full_build 등)면 도우미를 쓰지 않는다
            var folder = Path.Combine(Path.GetTempPath(), "fv-helper-libs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "ffmpeg.exe"), "");
                Assert.False(PreviewHelper.HasLibraries(folder));
                foreach (var name in PreviewHelper.Libraries.Take(PreviewHelper.Libraries.Length - 1))
                    File.WriteAllText(Path.Combine(folder, name), "");
                Assert.False(PreviewHelper.HasLibraries(folder));
                File.WriteAllText(Path.Combine(folder, PreviewHelper.Libraries.Last()), "");
                Assert.Equal(OperatingSystem.IsWindows(), PreviewHelper.HasLibraries(folder));
                Assert.False(PreviewHelper.HasLibraries(null));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        private static Project HdProject()
        {
            var project = TestProjects.Empty();
            project.Width = 1920;
            project.Height = 1080;
            Add(project, project.MainTrack, HelperMedia.Hd, 0);
            return project;
        }

        /// <summary>완료 기준: 재생 헤드 이동(멈춘 화면 한 장) 0.3초 안 — 1080p H.264 60초, 640×360 미리 보기(이슈 #1의 측정과 같은 조건).</summary>
        [HelperFact]
        public void Seek_1080p_Under300ms()
        {
            var project = HdProject();
            var helper = _fixture.Helper;
            var tool = FfmpegMedia.Tool;
            long At(double seconds) => (long)Math.Round(seconds * 30);
            double Time(Func<byte[]> draw)
            {
                var watch = Stopwatch.StartNew();
                var bytes = draw();
                var ms = watch.Elapsed.TotalMilliseconds;
                Assert.Equal(640 * 360 * 4, bytes.Length);
                return ms;
            }
            var exe = new List<double>();
            foreach (var seconds in new[] { 41.5, 12.3, 55.0 })
                exe.Add(Time(() => PreviewRenderer.Frame(tool, project, At(seconds), 640, 360, CancellationToken.None).GetAwaiter().GetResult()));
            var failures = new List<HelperException>();
            byte[] Helper(double seconds) => PreviewRenderer.Frame(tool, project, At(seconds), 640, 360, CancellationToken.None, null, helper, failures.Add).GetAwaiter().GetResult();
            // 처음(파일 열기 포함) 한 번, 그 뒤 여기저기로 옮기기 — 다음 장(→ 키), 멀리, 뒤로
            var cold = Time(() => Helper(41.5));
            var warm = new List<double>();
            foreach (var seconds in new[] { 41.5 + 1 / 30.0, 12.3, 55.0, 41.5, 30.0, 3.0, 59.9, 20.7 })
                warm.Add(Time(() => Helper(seconds)));
            Assert.Empty(failures);
            _out.WriteLine(string.Format("ffmpeg.exe: {0} → 중앙 {1:0}ms", string.Join(" · ", exe.Select(v => v.ToString("0"))), Median(exe)));
            _out.WriteLine(string.Format("도우미: 처음 {0:0}ms, 그 뒤 {1} → 중앙 {2:0}ms · 가장 느림 {3:0}ms", cold, string.Join(" · ", warm.Select(v => v.ToString("0"))), Median(warm), warm.Max()));
            Assert.True(Median(warm) < 300, "재생 헤드 이동 중앙값 " + Median(warm).ToString("0") + "ms");
            Assert.True(Median(warm) < Median(exe), "도우미가 ffmpeg.exe보다 빠르다");
        }

        /// <summary>완료 기준: 재생 시작(화면 첫 장·소리 첫 조각까지) 0.3초 안.</summary>
        [HelperFact]
        public void PlaybackStart_1080p_Under300ms()
        {
            var project = HdProject();
            var helper = _fixture.Helper;
            var tool = FfmpegMedia.Tool;
            const long from = 1245;
            const int size = 640 * 360 * 4;
            // 파일을 한 번 열어 둔다(편집기에서는 재생 전에 멈춘 화면을 그려 둔 상태)
            PreviewRenderer.Frame(tool, project, from, 640, 360, CancellationToken.None, null, helper).GetAwaiter().GetResult();
            (double Video, double Audio) Measure(PreviewHelper with)
            {
                var watch = Stopwatch.StartNew();
                using (var video = PreviewRenderer.OpenVideo(tool, with, project, from, from + 300, 640, 360))
                using (var audio = PreviewRenderer.OpenAudio(tool, with, project, from, from + 300))
                {
                    Assert.Equal(with != null, video.UsesHelper);
                    var audioTask = Task.Run(() =>
                    {
                        var chunk = new byte[16384];
                        Assert.True(HelperProtocol.ReadExactly(audio.Input, chunk, chunk.Length));
                        return watch.Elapsed.TotalMilliseconds;
                    });
                    Assert.Single(ReadFrames(video.Input, size, 1));
                    var first = watch.Elapsed.TotalMilliseconds;
                    Assert.True(audioTask.Wait(TimeSpan.FromSeconds(30)));
                    return (first, audioTask.Result);
                }
            }
            var exe = Enumerable.Range(0, 3).Select(_ => Measure(null)).ToList();
            var viaHelper = Enumerable.Range(0, 5).Select(_ => Measure(helper)).ToList();
            _out.WriteLine(string.Format("ffmpeg.exe: 화면 첫 장 {0} · 소리 첫 16KB {1} (ms)", string.Join(" · ", exe.Select(m => m.Video.ToString("0"))), string.Join(" · ", exe.Select(m => m.Audio.ToString("0")))));
            _out.WriteLine(string.Format("도우미: 화면 첫 장 {0} · 소리 첫 16KB {1} (ms)", string.Join(" · ", viaHelper.Select(m => m.Video.ToString("0"))), string.Join(" · ", viaHelper.Select(m => m.Audio.ToString("0")))));
            Assert.True(Median(viaHelper.Select(m => m.Video)) < 300, "재생 시작(화면) 중앙값 " + Median(viaHelper.Select(m => m.Video)).ToString("0") + "ms");
            Assert.True(Median(viaHelper.Select(m => m.Audio)) < 300, "재생 시작(소리) 중앙값 " + Median(viaHelper.Select(m => m.Audio)).ToString("0") + "ms");
        }
    }
}
