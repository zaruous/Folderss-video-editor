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
    /// <summary>#4 사용자 키프레임을 실제 FFmpeg로: 키프레임 시각에 정확히 그 값(위치·크기·회전·불투명도), 효과와 합치기, 미리 보기 = 내보내기.</summary>
    public class KeyframeRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;

        public KeyframeRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static ClipKeyframe Key(KeyframeProperty property, long frame, double value, double y = 0, AnimationEase ease = AnimationEase.Linear)
        {
            return new ClipKeyframe { Property = property, Frame = frame, Value = value, Y = y, Ease = ease };
        }

        /// <summary>파란 동영상(320×240)을 반 크기로 가운데 — 정지 상자 (80, 60)-(239, 179).</summary>
        internal static (Project Project, Clip Clip) BlueProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = Width;
            project.Height = Height;
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.BlueVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            clip.Scale = 0.5;
            return (project, clip);
        }

        private static byte[] Frame(Project project, long frame)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        /// <summary>파란 픽셀의 (왼쪽, 위, 오른쪽, 아래) — 없으면 (-1, -1, -1, -1).</summary>
        private static (int Left, int Top, int Right, int Bottom) BlueBox(byte[] bgra)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var i = (y * Width + x) * 4;
                    if (!(bgra[i] > 150 && bgra[i + 1] < 90 && bgra[i + 2] < 90))
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
            return a.Zip(b, (x, y) => Math.Abs(x - y)).Max();
        }

        [FfmpegFact]
        public void Position_KeyframeFrames_LandExactlyOnTheirValues_EaseBetween()
        {
            var (project, clip) = BlueProject();
            // 고정 X −60이 그리는 그림(키프레임 0장과 같아야 함)
            clip.X = -60;
            var fixedLeft = Frame(project, 0);
            Assert.Equal((20, 60, 179, 179), BlueBox(fixedLeft));
            clip.X = 0;
            // 0장 −60 → 20장 60(일정) → 30장 0(서서히 시작·끝)
            clip.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Position, 0, -60), Key(KeyframeProperty.Position, 20, 60, 0, AnimationEase.InOut), Key(KeyframeProperty.Position, 30, 0)
            };
            Assert.Equal(0, MaxDiff(fixedLeft, Frame(project, 0)));
            Assert.Equal((50, 60, 209, 179), BlueBox(Frame(project, 5)));
            Assert.Equal((80, 60, 239, 179), BlueBox(Frame(project, 10)));
            Assert.Equal((140, 60, 299, 179), BlueBox(Frame(project, 20)));
            // 20 → 30 가운데(25장): smoothstep(0.5) = 0.5라 X 30
            Assert.Equal((110, 60, 269, 179), BlueBox(Frame(project, 25)));
            // 22장: 60 − 60 × smoothstep(0.2) = 60 − 60 × 0.104 = 53.76 → 133.76, overlay가 4:2:0 바탕에서 짝수 픽셀로 버림해 132(효과·고정 값과 같은 규칙)
            Assert.Equal((132, 60, 291, 179), BlueBox(Frame(project, 22)));
            Assert.Equal((80, 60, 239, 179), BlueBox(Frame(project, 30)));
            Assert.Equal((80, 60, 239, 179), BlueBox(Frame(project, 45)));
        }

        [FfmpegFact]
        public void Scale_FromTheLargestBox_ExactSizeAtKeys()
        {
            var (project, clip) = BlueProject();
            var still = Frame(project, 0);
            // 0장 0.5 → 20장 0.25: 틀은 0.5(160×120)로 만들고 장마다 줄인다
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Scale, 0, 0.5), Key(KeyframeProperty.Scale, 20, 0.25) };
            Assert.Equal(0, MaxDiff(still, Frame(project, 0)));
            var quarter = BlueBox(Frame(project, 20));
            _out.WriteLine("20장 " + quarter);
            Assert.Equal((120, 90, 199, 149), quarter);
            var middle = BlueBox(Frame(project, 10));
            _out.WriteLine("10장 " + middle);
            Assert.InRange(middle.Right - middle.Left + 1, 119, 121);
            Assert.InRange(middle.Bottom - middle.Top + 1, 89, 91);
            // 키우는 쪽(0.25 → 0.5)도 끝에서 정확히 반 크기 — 틀은 가장 큰 0.5
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Scale, 0, 0.25), Key(KeyframeProperty.Scale, 20, 0.5) };
            Assert.Equal((120, 90, 199, 149), BlueBox(Frame(project, 0)));
            Assert.Equal(0, MaxDiff(still, Frame(project, 20)));
        }

        [FfmpegFact]
        public void Rotation_And_Opacity_KeyframeValues()
        {
            var (project, clip) = BlueProject();
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Rotation, 0, 0), Key(KeyframeProperty.Rotation, 20, 90) };
            Assert.Equal((80, 60, 239, 179), BlueBox(Frame(project, 0)));
            var turned = BlueBox(Frame(project, 20));
            _out.WriteLine("90° " + turned);
            Assert.InRange(turned.Right - turned.Left + 1, 118, 122);
            Assert.InRange(turned.Bottom - turned.Top + 1, 158, 162);

            // 불투명도 1 → 0(0~10장): 5장에 반, 10장부터 보이지 않음
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Opacity, 0, 1), Key(KeyframeProperty.Opacity, 10, 0) };
            var center = (120 * Width + 160) * 4;
            var half = Frame(project, 5)[center];
            _out.WriteLine("5장 파랑 " + half);
            Assert.InRange(half, 110, 145);
            Assert.True(Frame(project, 0)[center] > 200);
            Assert.True(Frame(project, 10)[center] < 10);
            Assert.True(Frame(project, 15)[center] < 10);
        }

        [FfmpegFact]
        public void KeyframesUnderEffects_FlyLandsOnTheKeyframedPlace()
        {
            // 위치 = 키프레임 + 효과: 날아오기(10장)가 끝나면 그 시각의 키프레임 자리에 정확히
            var (project, clip) = BlueProject();
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Position, 0, 40, -20), Key(KeyframeProperty.Position, 30, 40, 20) };
            var fly = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fly), AnimationKind.In, project.Rate, clip);
            fly.Duration = 10;
            fly.Direction = AnimationDirection.Left;
            clip.Animations = new List<ClipAnimation> { fly };
            Assert.Equal((-1, -1, -1, -1), BlueBox(Frame(project, 0)));
            // 10장: Y = −20 + 40 × 10/30 = −6.67 → 위 60 − 6.67 = 53.33(짝수로 버림 52)
            Assert.Equal((120, 52, 279, 171), BlueBox(Frame(project, 10)));
            Assert.Equal((120, 60, 279, 179), BlueBox(Frame(project, 15)));
        }

        [FfmpegFact]
        public async Task Keyframes_PreviewEqualsExport()
        {
            var (project, clip) = BlueProject();
            clip.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Position, 0, -50, 10, AnimationEase.Out), Key(KeyframeProperty.Position, 25, 40, -15),
                Key(KeyframeProperty.Scale, 5, 0.4), Key(KeyframeProperty.Scale, 30, 0.6, 0, AnimationEase.Bounce),
                Key(KeyframeProperty.Rotation, 0, -10), Key(KeyframeProperty.Rotation, 40, 30),
                Key(KeyframeProperty.Opacity, 10, 1), Key(KeyframeProperty.Opacity, 20, 0.4)
            };
            var pulse = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Pulse), AnimationKind.Emphasis, project.Rate, clip);
            clip.Animations = new List<ClipAnimation> { pulse };
            var checks = new long[] { 0, 7, 13, 20, 26, 44 };
            var previews = checks.Select(f => Frame(project, f)).ToList();
            var output = Path.Combine(FfmpegMedia.Folder, "keys-" + Guid.NewGuid().ToString("N") + ".mp4");
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
                }, null, CancellationToken.None);
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
                    _out.WriteLine(checks[i] + "장: 평균 차이 " + mean.ToString("0.00") + " · 상자 " + BlueBox(previews[i]));
                    Assert.True(mean < 4, checks[i] + "장 미리 보기와 내보내기의 평균 차이 " + mean);
                }
            }
            finally
            {
                File.Delete(output);
            }
        }
    }

    /// <summary>미리 보기 도우미가 키프레임 그래프(위치·회전 if 식, scale eval=frame, sendcmd)를 ffmpeg.exe와 같게 그리는지 — 새 입력 옵션은 없다.</summary>
    [Collection(PreviewHelperCollection.Name)]
    public class KeyframeHelperTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;
        private readonly PreviewHelperFixture _fixture;

        public KeyframeHelperTests(ITestOutputHelper output, PreviewHelperFixture fixture)
        {
            _out = output;
            _fixture = fixture;
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

        [HelperFact]
        public void Playback_KeyframedClips_SameAsFfmpegExe()
        {
            var helper = _fixture.Helper;
            var (project, clip) = KeyframeRenderTests.BlueProject();
            clip.Keyframes = new List<ClipKeyframe>
            {
                new ClipKeyframe { Property = KeyframeProperty.Position, Frame = 0, Value = -60, Y = 20, Ease = AnimationEase.InOut },
                new ClipKeyframe { Property = KeyframeProperty.Position, Frame = 30, Value = 60, Y = -20 },
                new ClipKeyframe { Property = KeyframeProperty.Scale, Frame = 0, Value = 0.3 },
                new ClipKeyframe { Property = KeyframeProperty.Scale, Frame = 20, Value = 0.6 },
                new ClipKeyframe { Property = KeyframeProperty.Rotation, Frame = 10, Value = 0, Ease = AnimationEase.Elastic },
                new ClipKeyframe { Property = KeyframeProperty.Rotation, Frame = 40, Value = 120 },
                new ClipKeyframe { Property = KeyframeProperty.Opacity, Frame = 0, Value = 0.2 },
                new ClipKeyframe { Property = KeyframeProperty.Opacity, Frame = 15, Value = 1 }
            };
            var image = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 5, 40);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), image, 5);
            image.Scale = 0.3;
            image.Keyframes = new List<ClipKeyframe>
            {
                new ClipKeyframe { Property = KeyframeProperty.Position, Frame = 0, Value = 100, Y = -80 },
                new ClipKeyframe { Property = KeyframeProperty.Position, Frame = 35, Value = -100, Y = 80, Ease = AnimationEase.Bounce }
            };
            const long from = 3, to = 50;
            var failures = new List<HelperException>();
            List<byte[]> actual;
            using (var stream = PreviewRenderer.OpenVideo(FfmpegMedia.Tool, helper, project, from, to, Width, Height, null, failures.Add))
            {
                Assert.True(stream.UsesHelper, "도우미 흐름" + (failures.Count > 0 ? ": " + failures[0].Message : ""));
                actual = ReadFrames(stream.Input);
            }
            List<byte[]> expected;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, from, to, Width, Height, out var files))
            {
                process.BeginErrorReadLine();
                expected = ReadFrames(process.StandardOutput.BaseStream);
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
            Assert.Empty(failures);
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
            foreach (var frame in new long[] { 0, 15, 30 })
            {
                var viaHelper = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None, null, helper, failures.Add).GetAwaiter().GetResult();
                var viaFfmpeg = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, Width, Height, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Empty(failures);
                Assert.True(viaHelper.Zip(viaFfmpeg, (a, b) => Math.Abs(a - b)).Max() <= 3, frame + "장");
            }
        }
    }
}
