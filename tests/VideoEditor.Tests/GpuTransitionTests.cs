using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>
    /// 시험은 기본으로 GPU 전환을 끈다 — 다른 시험이 기대하는 그림(CPU 수식·미리 그리기)이 PC의 GPU에 따라 바뀌지 않게.
    /// GPU를 켜 보는 시험은 다른 시험과 함께 돌지 않는 묶음(<see cref="PreviewHelperCollection"/>)에서 켰다가 끈다.
    /// </summary>
    internal static class GpuTransitionsOffForTests
    {
        [ModuleInitializer]
        internal static void Init()
        {
            GpuTransitions.Enabled = false;
        }
    }

    /// <summary>GPU 전환(xfade_opencl) 그래프·고르기 — FFmpeg 없이.</summary>
    public class GpuTransitionGraphTests
    {
        /// <summary>시험 화면 [0, 60) → 파란 화면 [60, 120)(전환 15장).</summary>
        internal static Project TwoClips(string transition)
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60, "a.mp4");
            var b = TestProjects.VideoClip(0, 60, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            TimelineEdits.SetTransition(project, b, transition, 15);
            return project;
        }

        [Fact]
        public void EveryBuiltInExpression_HasAKernel_UserAndEasedDoNot()
        {
            var expressions = Transitions.All.Where(d => d.BuiltIn && d.IsExpression).ToList();
            Assert.NotEmpty(expressions);
            Assert.All(expressions, d => Assert.True(Transitions.HasKernel(d), d.Id + "의 커널이 transitions.cl에 없습니다"));
            Assert.False(Transitions.HasKernel(Transitions.Find("WipeLeft")));
            // 같은 수식이라도 사용자 효과(커널을 받지 않음)·이징을 건 것은 CPU로
            var swirl = Transitions.Find("Swirl");
            Assert.False(Transitions.HasKernel(Transitions.Check(new TransitionDef { Id = "Swirl", Name = "내 소용돌이", Expr = swirl.Expr }, out _)));
            var eased = Transitions.Check(new TransitionDef { Id = "SwirlEase", Name = "소용돌이", Expr = swirl.Expr, Ease = "inout" }, out _);
            Assert.False(Transitions.HasKernel(eased));
        }

        [Fact]
        public void WithKernels_ExpressionTransition_UsesXfadeOpenCl_AndIsNotShrunk()
        {
            var project = TwoClips("Ripple");
            var gpu = RenderGraph.Build(project, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, Preview = true, Playback = true, TransitionKernels = @"C:\k\transitions.cl" });
            Assert.True(gpu.UsesOpenCl);
            Assert.Contains("hwupload", gpu.Filter);
            Assert.Contains("xfade_opencl=transition=custom:source=" + RenderGraph.FilterValue(@"C:\k\transitions.cl") + ":kernel=Ripple:duration=0.5:offset=", gpu.Filter);
            Assert.Contains("hwdownload,format=rgba,format=gbrap", gpu.Filter);
            // 전환 구간(앞 흐름의 60~75장, 뒤 흐름의 0~15장)만 GPU에 올리고 앞·뒤는 CPU 그대로 concat — 전환 밖 장이 GPU를 거치지 않는다
            Assert.Contains("trim=start_frame=60:end_frame=75,setpts=PTS-STARTPTS,settb=1/30,format=rgba,hwupload", gpu.Filter);
            Assert.Contains("trim=end_frame=15,setpts=PTS-STARTPTS,settb=1/30,format=rgba,hwupload", gpu.Filter);
            // concat은 시간 단위를 바꾸므로 1/프레임 레이트로 되돌린다(다음 전환과 이어지게)
            Assert.Contains("concat=n=3:v=1:a=0,settb=1/30", gpu.Filter);
            // 실시간이라 재생에서도 줄이지 않는다(줄였다면 얹기 전에 늘인다)
            Assert.DoesNotContain("unpremultiply=inplace=1,scale=", gpu.Filter);
            Assert.DoesNotContain("custom:expr", gpu.Filter);

            var cpu = RenderGraph.Build(project, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, Preview = true, Playback = true });
            Assert.False(cpu.UsesOpenCl);
            Assert.DoesNotContain("xfade_opencl", cpu.Filter);
            Assert.Contains("transition=custom:expr=", cpu.Filter);
            Assert.Contains("unpremultiply=inplace=1,scale=640:360", cpu.Filter);
        }

        [Fact]
        public void WithKernels_BuiltInXfade_StaysOnCpu()
        {
            var plan = RenderGraph.Build(TwoClips("WipeLeft"), new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, TransitionKernels = @"C:\k.cl" });
            Assert.False(plan.UsesOpenCl);
            Assert.Contains("xfade=transition=wipeleft", plan.Filter);
        }

        [Fact]
        public void PrerenderWindows_SkipKernelTransitions_OnlyWhenGpu()
        {
            var project = TwoClips("Ripple");
            Assert.Single(TransitionPrerender.Windows(project, 0, 120, 7));
            Assert.Empty(TransitionPrerender.Windows(project, 0, 120, 7, gpu: true));
        }
    }

    /// <summary>GPU 전환을 실제 FFmpeg·도우미로: 이 PC에서 OpenCL을 쓸 수 있으면 CPU 수식과 같은 그림인지(커널은 보간해 읽어 조금 다를 수 있다).</summary>
    [Collection(PreviewHelperCollection.Name)]
    public class GpuTransitionRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;
        private readonly PreviewHelperFixture _fixture;

        public GpuTransitionRenderTests(ITestOutputHelper output, PreviewHelperFixture fixture)
        {
            _out = output;
            _fixture = fixture;
        }

        private static Project RippleProject(string transition)
        {
            var project = TestProjects.Empty();
            project.Width = Width;
            project.Height = Height;
            var a = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            var b = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.BlueVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            Transitions.Assign(b, Transitions.Find(transition));
            b.TransitionIn = 15;
            return project;
        }

        private static List<byte[]> Render(Project project, long from, long to, string kernels)
        {
            var tool = FfmpegMedia.Tool;
            var options = PrerenderCache.StillOptions(tool, from, to, Width, Height, null);
            options.TransitionKernels = kernels;
            var plan = RenderGraph.Build(project, options);
            Assert.Equal(kernels != null, plan.UsesOpenCl);
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out var script);
            try
            {
                var bytes = FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
                var size = Width * Height * 4;
                return Enumerable.Range(0, bytes.Length / size).Select(i => bytes.Skip(i * size).Take(size).ToArray()).ToList();
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }

        private static double MeanDiff(byte[] a, byte[] b)
        {
            long sum = 0;
            for (var i = 0; i < a.Length; i++)
                sum += Math.Abs(a[i] - b[i]);
            return (double)sum / a.Length;
        }

        private string Kernels()
        {
            GpuTransitions.Enabled = true;
            try
            {
                var path = GpuTransitions.KernelFileAsync(FfmpegMedia.Tool).GetAwaiter().GetResult();
                _out.WriteLine(GpuTransitions.Status + (path != null ? " · " + path : ""));
                return path;
            }
            finally
            {
                GpuTransitions.Enabled = false;
            }
        }

        [FfmpegFact]
        public void Export_GpuMatchesCpu_SameFrameCount_InsideAndAroundTheTransition()
        {
            var kernels = Kernels();
            if (kernels == null)
            {
                _out.WriteLine("이 PC·FFmpeg에서는 OpenCL을 쓸 수 없어 CPU 경로만 — 비교하지 않음");
                return;
            }
            foreach (var id in new[] { "Ripple", "Heart", "Swirl", "StarWipe" })
            {
                var project = RippleProject(id);
                const long from = 50, to = 85;
                var cpu = Render(project, from, to, null);
                var gpu = Render(project, from, to, kernels);
                Assert.Equal(to - from, cpu.Count);
                Assert.Equal(cpu.Count, gpu.Count);
                var diffs = cpu.Zip(gpu, MeanDiff).ToList();
                _out.WriteLine(id + ": 장마다 평균 차이 " + string.Join(" ", diffs.Select(d => d.ToString("0.0"))));
                // 전환 밖(앞 10장, 전환이 끝난 뒤)은 같은 그림
                Assert.All(diffs.Take(10).Concat(diffs.Skip(10 + 15)), d => Assert.True(d < 0.5, id + " 전환 밖 차이 " + d));
                // 전환 안: 보간·경계 1픽셀 정도만 다르다
                Assert.All(diffs.Skip(10).Take(15), d => Assert.True(d < 6, id + " 전환 안 차이 " + d));
                // 실제로 전환을 그렸다(앞 화면에서도 새 화면에서도 멀어진 장이 있다)
                Assert.Contains(gpu.Skip(12).Take(10), f => MeanDiff(f, gpu[0]) > 5 && MeanDiff(f, gpu[gpu.Count - 1]) > 5);
            }
        }

        /// <summary>
        /// GPU 전환이 잇달아 있을 때(첫 클립은 투명에서 하트, 다음은 물방울 — 사용자 프로젝트에서 재생이 한 장도 안 나오던 경우):
        /// 앞 전환의 concat 출력과 다음 클립의 시간 단위가 달라 xfade_opencl이 맞춰지지 않았다. 끝까지 같은 장수, CPU와 같은 그림.
        /// </summary>
        [FfmpegFact]
        public void ChainedGpuTransitions_FromTransparent_RenderToTheEnd()
        {
            var kernels = Kernels();
            if (kernels == null)
                return;
            var project = RippleProject("WaterDrop");
            var clips = project.MainTrack.Clips.OrderBy(c => c.Start).ToList();
            TimelineEdits.SetTransition(project, clips[0], "Heart", 15);
            var c = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, c, 999);
            TimelineEdits.SetTransition(project, c, "Swirl", 15);
            var end = project.Duration;
            foreach (var from in new long[] { 0, 5, 50, 70 })
            {
                var gpu = Render(project, from, end, kernels);
                var cpu = Render(project, from, end, null);
                Assert.Equal(end - from, gpu.Count);
                Assert.Equal(cpu.Count, gpu.Count);
                var worst = cpu.Zip(gpu, MeanDiff).Max();
                _out.WriteLine(from + "~" + end + ": " + gpu.Count + "장, 가장 큰 장의 평균 차이 " + worst.ToString("0.00"));
                Assert.True(worst < 1, from + ": " + worst);
            }
        }

        /// <summary>
        /// 첫 클립의 휘는 전환(물방울): 앞 화면이 없으면 새 화면의 사본이 투명에서 나타나며 휜다 — 단순 페이드와 다른 그림이고,
        /// 처음은 거의 비고 끝은 클립 그대로. GPU·CPU가 같다.
        /// </summary>
        [FfmpegFact]
        public void FirstClipWarp_AppearsWarping_NotJustAFade()
        {
            var project = TestProjects.Empty();
            project.Width = Width;
            project.Height = Height;
            var a = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.SetTransition(project, a, "WaterDrop", 30);
            var warp = Render(project, 0, 40, null);
            Assert.Equal(40, warp.Count);
            TimelineEdits.SetTransition(project, a, "Fade", 30);
            var fade = Render(project, 0, 40, null);
            var dump = Environment.GetEnvironmentVariable("FV_DUMP_FRAMES");
            if (dump != null)
            {
                foreach (var i in new[] { 3, 10, 17, 24, 35 })
                {
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, "warp" + i + ".bgra"), warp[i]);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, "fade" + i + ".bgra"), fade[i]);
                }
            }
            var differences = Enumerable.Range(0, 40).Select(i => MeanDiff(warp[i], fade[i])).ToList();
            _out.WriteLine("물방울 ↔ 페이드 장마다 평균 차이 " + string.Join(" ", differences.Select(d => d.ToString("0.0"))));
            // 전환 동안 페이드와 뚜렷이 다르고(물결), 전환이 끝나면 같다
            Assert.True(differences.Skip(5).Take(20).Max() > 8, "물결이 보이지 않음");
            // 끝난 뒤의 작은 차이는 묶음(알파를 곱했다 되돌림)의 반올림 — 디졸브는 묶음을 거치지 않는다
            Assert.True(differences.Skip(31).Max() < 2);
            var kernels = Kernels();
            if (kernels == null)
                return;
            TimelineEdits.SetTransition(project, a, "WaterDrop", 30);
            var gpu = Render(project, 0, 40, kernels);
            var worst = warp.Zip(gpu, MeanDiff).Max();
            _out.WriteLine("GPU ↔ CPU 가장 큰 장의 평균 차이 " + worst.ToString("0.00"));
            Assert.True(worst < 1, "GPU " + worst);
        }

        /// <summary>재생 헤드가 전환 도중일 때: 멈춘 화면 한 장, 그 자리부터 재생(ffmpeg.exe) — GPU도 CPU와 같은 장을 낸다.</summary>
        [FfmpegFact]
        public void StillAndPlayback_FromInsideTheTransition_GpuMatchesCpu()
        {
            var kernels = Kernels();
            if (kernels == null)
                return;
            var project = RippleProject("Swirl");
            GpuTransitions.Enabled = true;
            try
            {
                foreach (var frame in new long[] { 60, 61, 67, 74, 75 })
                {
                    var gpu = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None).GetAwaiter().GetResult();
                    Assert.True(gpu != null, frame + "장: GPU 한 장이 나오지 않음");
                    GpuTransitions.Enabled = false;
                    var cpu = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None).GetAwaiter().GetResult();
                    GpuTransitions.Enabled = true;
                    _out.WriteLine(frame + "장 평균 차이 " + MeanDiff(gpu, cpu).ToString("0.00"));
                    Assert.True(MeanDiff(gpu, cpu) < 1, frame + "장 " + MeanDiff(gpu, cpu));
                }
                foreach (var (from, to) in new[] { (65L, 120L), (62L, 70L), (40L, 66L) })
                {
                    List<byte[]> frames;
                    using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, from, to, Width, Height, out var files))
                    {
                        var error = process.StandardError.ReadToEndAsync();
                        frames = ReadAll(process.StandardOutput.BaseStream);
                        process.WaitForExit();
                        foreach (var file in files)
                            RenderCommand.TryDelete(file);
                        _out.WriteLine(from + "~" + to + ": " + frames.Count + "장 " + error.Result.Trim());
                    }
                    Assert.Equal(to - from, frames.Count);
                }
            }
            finally
            {
                GpuTransitions.Enabled = false;
            }
        }

        [HelperFact]
        public void Playback_Helper_OpensOpenCl_SameAsFfmpegExe()
        {
            GpuTransitions.Enabled = true;
            try
            {
                var kernels = GpuTransitions.KernelFileAsync(FfmpegMedia.Tool).GetAwaiter().GetResult();
                if (kernels == null)
                {
                    _out.WriteLine("OpenCL을 쓸 수 없음 — " + GpuTransitions.Status);
                    return;
                }
                var project = RippleProject("Swirl");
                const long from = 50, to = 85;
                var gpu = HelperVsFfmpeg(project, from, to);
                Assert.Empty(TransitionPrerender.Windows(project, from, to, FfmpegMedia.Tool.MajorVersion, gpu: true));
                // 대조: 같은 비교를 CPU 수식으로 — 도우미와 ffmpeg.exe는 디코드·크기 바꾸기가 조금 달라(전환 앞 장도 평균 0.4쯤)
                // 소용돌이처럼 좌표를 휘는 전환은 그 차이를 키운다. GPU가 그 위에 더하는 차이가 없어야 한다
                GpuTransitions.Enabled = false;
                var cpu = HelperVsFfmpeg(project, from, to);
                _out.WriteLine("가장 큰 장의 평균 차이: GPU " + gpu.ToString("0.00") + " · CPU " + cpu.ToString("0.00"));
                Assert.True(gpu <= cpu + 0.5, "GPU " + gpu + " · CPU " + cpu);
            }
            finally
            {
                GpuTransitions.Enabled = false;
            }
        }

        /// <summary>같은 재생 구간을 도우미와 ffmpeg.exe로 — 장수가 같고, 가장 큰 장의 평균 차이를 돌려준다.</summary>
        private double HelperVsFfmpeg(Project project, long from, long to)
        {
            var failures = new List<HelperException>();
            List<byte[]> viaHelper;
            using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, _fixture.Helper, project, from, to, Width, Height, null, failures.Add))
            {
                Assert.True(stream.UsesHelper, "도우미 흐름" + (failures.Count > 0 ? ": " + failures[0].Message : ""));
                viaHelper = ReadAll(stream.Input);
            }
            Assert.Empty(failures);
            List<byte[]> viaFfmpeg;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, from, to, Width, Height, out var files))
            {
                process.BeginErrorReadLine();
                viaFfmpeg = ReadAll(process.StandardOutput.BaseStream);
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Equal(to - from, viaFfmpeg.Count);
            Assert.Equal(viaFfmpeg.Count, viaHelper.Count);
            var diffs = viaFfmpeg.Zip(viaHelper, MeanDiff).ToList();
            _out.WriteLine("도우미 ↔ ffmpeg.exe 장마다 평균 차이 " + string.Join(" ", diffs.Select(d => d.ToString("0.0"))));
            return diffs.Max();
        }

        private static List<byte[]> ReadAll(System.IO.Stream input)
        {
            var frames = new List<byte[]>();
            var size = Width * Height * 4;
            while (true)
            {
                var pixels = new byte[size];
                var read = 0;
                int n;
                while (read < size && (n = input.Read(pixels, read, size - read)) > 0)
                    read += n;
                if (read < size)
                    return frames;
                frames.Add(pixels);
            }
        }
    }
}
