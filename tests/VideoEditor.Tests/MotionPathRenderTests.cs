using System;
using System.Collections.Generic;
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
    /// <summary>#4 이동 경로를 실제 FFmpeg로: 끝 장이 경로 끝점(원은 제자리)에 정확히, 미리 보기 한 장 = 내보낸 장.</summary>
    public class MotionPathRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;

        public MotionPathRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        internal static ClipAnimation Path(AnimationEffect effect, long frames, AnimationEase ease, params double[] points)
        {
            var animation = ClipAnimations.Create(ClipAnimations.Def(effect), AnimationKind.Path, FrameRate.Default);
            animation.Duration = frames;
            animation.Ease = ease;
            animation.Points = Enumerable.Range(0, points.Length / 2).Select(i => new PathPoint(points[i * 2], points[i * 2 + 1])).ToList();
            return animation;
        }

        /// <summary>파란 동영상(320×240)을 반 크기로 가운데 — 정지 상자 (80, 60)-(239, 179).</summary>
        private static (Project Project, Clip Clip) BlueProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = Width;
            project.Height = Height;
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.BlueVideo), project.Rate, 0, 30);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            clip.Scale = 0.5;
            return (project, clip);
        }

        private static byte[] Frame(Project project, long frame, ITextRasterizer rasterizer = null)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None, rasterizer).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        private static (int Left, int Top, int Right, int Bottom) BlueBox(byte[] bgra)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var i = (y * Width + x) * 4;
                    if (!(bgra[i] > 150 && bgra[i + 2] < 90 && bgra[i + 1] < 90))
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            return right < 0 ? (-1, -1, -1, -1) : (left, top, right, bottom);
        }

        private static int MaxDiff(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            var max = 0;
            for (var i = 0; i < a.Length; i++)
                max = Math.Max(max, Math.Abs(a[i] - b[i]));
            return max;
        }

        /// <summary>애니메이션 없이 (x, y)에 둔 그림 — 경로 끝 장과 바이트까지 같아야 한다.</summary>
        private static byte[] StillAt(Project project, Clip clip, double x, double y, long frame)
        {
            var (animations, oldX, oldY) = (clip.Animations, clip.X, clip.Y);
            clip.Animations = null;
            clip.X = oldX + x;
            clip.Y = oldY + y;
            try
            {
                return Frame(project, frame);
            }
            finally
            {
                (clip.Animations, clip.X, clip.Y) = (animations, oldX, oldY);
            }
        }

        [FfmpegFact]
        public void VideoLine_HalfwayHalf_EndFrameExactlyAtTheEndPoint()
        {
            var (project, clip) = BlueProject();
            Assert.Equal((80, 60, 239, 179), BlueBox(Frame(project, 12)));
            // 4:2:0 바탕에 얹을 때 overlay는 자리를 짝수로 내리므로 반쯤 자리가 짝수가 되는 값으로 본다
            clip.Animations = new List<ClipAnimation> { Path(AnimationEffect.PathLine, 10, AnimationEase.InOut, 0, 0, 40, 32) };
            Assert.Equal((80, 60, 239, 179), BlueBox(Frame(project, 0)));
            var half = BlueBox(Frame(project, 5));
            _out.WriteLine("반쯤 " + half);
            Assert.Equal((100, 76, 259, 195), half);
            // 지정한 장(10)부터 끝점 — 그 자리에 둔 그림과 바이트까지 같다
            var end = StillAt(project, clip, 40, 32, 10);
            Assert.Equal((120, 92, 279, 211), BlueBox(end));
            Assert.Equal(0, MaxDiff(end, Frame(project, 10)));
            Assert.Equal(0, MaxDiff(StillAt(project, clip, 40, 32, 25), Frame(project, 25)));
        }

        [FfmpegFact]
        public void CircleArcCustomWave_EndExactly_ClipPositionMovesThePath()
        {
            var (project, clip) = BlueProject();
            // 원: 반 바퀴에 맞은편, 한 바퀴 돌면 제자리
            clip.Animations = new List<ClipAnimation> { Path(AnimationEffect.PathCircle, 10, AnimationEase.Linear, 0, 0, 0, 60) };
            Assert.Equal((80, 120, 239, 239), BlueBox(Frame(project, 5)));
            Assert.Equal(0, MaxDiff(StillAt(project, clip, 0, 0, 10), Frame(project, 10)));
            // 호·사용자 경로·물결(되돌아오기 없이 탄성 이징도): 끝점에 정확히
            var cases = new (ClipAnimation Path, double X, double Y)[]
            {
                (Path(AnimationEffect.PathArc, 12, AnimationEase.Elastic, 0, 0, -23, -37, -41, 17), -41, 17),
                (Path(AnimationEffect.PathCustom, 12, AnimationEase.Out, 0, 0, 33, 0, 33, -29, -7, -45), -7, -45),
                (Path(AnimationEffect.PathWave, 12, AnimationEase.Linear, 0, 0, 57, 31), 57, 31)
            };
            foreach (var (path, x, y) in cases)
            {
                clip.Animations = new List<ClipAnimation> { path };
                Assert.Equal(0, MaxDiff(StillAt(project, clip, x, y, 12), Frame(project, 12)));
                _out.WriteLine(path.Effect + " 끝 장 = (" + x + ", " + y + ")에 둔 그림");
            }
            // 클립을 옮기면 경로도 함께(점은 클립 자리 기준)
            clip.X = -50;
            clip.Animations = new List<ClipAnimation> { Path(AnimationEffect.PathLine, 10, AnimationEase.Linear, 0, 0, 40, 30) };
            Assert.Equal((70, 90, 229, 209), BlueBox(Frame(project, 10)));
        }

        [FfmpegFact]
        public async Task PathOnVideoImageAndStyledText_PreviewEqualsExport()
        {
            var (project, video) = BlueProject();
            var arc = Path(AnimationEffect.PathArc, 20, AnimationEase.InOut, 0, 0, 30, -40, 70, 10);
            arc.RoundTrip = true;
            video.Animations = new List<ClipAnimation> { arc, ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.In, project.Rate) };
            var image = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 30);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), image, 0);
            image.Scale = 0.3;
            image.X = -80;
            var wave = Path(AnimationEffect.PathWave, 15, AnimationEase.Linear, 0, 0, 120, 0);
            wave.Amount = 20;
            wave.Repeat = 0;
            image.Animations = new List<ClipAnimation> { wave };
            var text = TimelineEdits.CreateText("경로", 0, 30);
            text.Style = TextStyle.FromLegacy(text);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), text, 0);
            var custom = Path(AnimationEffect.PathCustom, 18, AnimationEase.Out, 0, 0, 40, -30, 80, 0, 40, 30);
            custom.Delay = 4;
            text.Animations = new List<ClipAnimation> { custom };
            var rasterizer = new FrameColorRasterizer();
            var checks = new long[] { 3, 9, 14, 22, 29 };
            var previews = checks.Select(f => Frame(project, f, rasterizer)).ToList();
            var output = System.IO.Path.Combine(FfmpegMedia.Folder, "path-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await Exporter.Export(FfmpegMedia.Tool, project, new ExportSettings
                {
                    OutputPath = output,
                    Width = Width,
                    Height = Height,
                    Encoder = "mpeg4",
                    Quality = 0,
                    From = 0,
                    To = project.Duration
                }, null, CancellationToken.None, rasterizer);
                for (var i = 0; i < checks.Length; i++)
                {
                    var exported = await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath,
                        new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", output, "-vf", "select=eq(n\\," + checks[i] + ")", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" },
                        TimeSpan.FromMinutes(1), CancellationToken.None);
                    Assert.Equal(previews[i].Length, exported.Length);
                    long diff = 0;
                    for (var k = 0; k < exported.Length; k++)
                    {
                        if (k % 4 != 3)
                            diff += Math.Abs(previews[i][k] - exported[k]);
                    }
                    var mean = diff / (exported.Length * 0.75);
                    _out.WriteLine(checks[i] + "장: 평균 차이 " + mean.ToString("0.00"));
                    Assert.True(mean < 4, checks[i] + "장 미리 보기와 내보내기의 평균 차이 " + mean);
                }
            }
            finally
            {
                File.Delete(output);
            }
        }
    }

    /// <summary>미리 보기 도우미가 이동 경로(overlay x·y의 if·cos·round 식)를 ffmpeg.exe와 같게 그리는지.</summary>
    [Collection(PreviewHelperCollection.Name)]
    public class MotionPathHelperTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;
        private readonly PreviewHelperFixture _fixture;

        public MotionPathHelperTests(ITestOutputHelper output, PreviewHelperFixture fixture)
        {
            _out = output;
            _fixture = fixture;
        }

        [HelperFact]
        public void Playback_PathClips_SameAsFfmpegExe()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = Width;
            project.Height = Height;
            var video = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, video, 0);
            video.Scale = 0.5;
            var circle = MotionPathRenderTests.Path(AnimationEffect.PathCircle, 20, AnimationEase.InOut, 0, 0, 40, 40);
            circle.Repeat = 0;
            video.Animations = new List<ClipAnimation> { circle };
            var image = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 5, 40);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), image, 5);
            image.Scale = 0.3;
            image.Animations = new List<ClipAnimation> { MotionPathRenderTests.Path(AnimationEffect.PathCustom, 25, AnimationEase.Linear, 0, 0, 60, -40, 100, 30, -20, 50) };
            const long from = 3, to = 45;
            var failures = new List<HelperException>();
            List<byte[]> actual;
            using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, _fixture.Helper, project, from, to, Width, Height, null, failures.Add))
            {
                Assert.True(stream.UsesHelper, "도우미 흐름" + (failures.Count > 0 ? ": " + failures[0].Message : ""));
                actual = ReadFrames(stream.Input);
            }
            List<byte[]> expected;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, from, to, Width, Height, out var files, null))
            {
                process.BeginErrorReadLine();
                expected = ReadFrames(process.StandardOutput.BaseStream);
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Empty(failures);
            _out.WriteLine("장수: ffmpeg.exe " + expected.Count + " · 도우미 " + actual.Count);
            Assert.Equal(to - from, expected.Count);
            Assert.Equal(expected.Count, actual.Count);
            var worst = 0;
            for (var i = 0; i < expected.Count; i++)
            {
                for (var k = 0; k < expected[i].Length; k++)
                    worst = Math.Max(worst, Math.Abs(expected[i][k] - actual[i][k]));
            }
            _out.WriteLine("가장 큰 채널 차이 " + worst);
            Assert.True(worst <= 3, "가장 큰 채널 차이 " + worst);
        }

        private static List<byte[]> ReadFrames(Stream input)
        {
            var frames = new List<byte[]>();
            var size = Width * Height * 4;
            var reading = Task.Run(() =>
            {
                while (true)
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
    }
}
