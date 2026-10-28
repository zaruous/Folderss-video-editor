using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>
    /// 잴 줄 아는 시험용 그리개: 출력 픽셀(해상도 1)로 baseWidth×baseHeight인 글을 흉내 낸다 — 잰 크기 = 그 크기 × 요청 해상도(EffectScale / 출력 배율).
    /// 그리면(FFmpeg가 있을 때) 그 크기를 캔버스로 자른 빨간 PNG를 쓴다(진짜 그리개처럼 캔버스보다 큰 곳은 잘린다).
    /// </summary>
    internal sealed class MeasuringRasterizer : ITextRasterizer
    {
        private static int _versions = 5000;
        private readonly double _baseWidth;
        private readonly double _baseHeight;
        private readonly double _outputScale;

        /// <param name="outputScale">출력 폭 / 프로젝트 폭(요청의 EffectScale에서 해상도를 꺼낼 때 나눔)</param>
        public MeasuringRasterizer(double baseWidth, double baseHeight, double outputScale = 1)
        {
            _baseWidth = baseWidth;
            _baseHeight = baseHeight;
            _outputScale = outputScale;
        }

        public int Version { get; } = Interlocked.Increment(ref _versions);

        public int Measures;

        public (double Width, double Height) Measure(TextImageRequest request)
        {
            Interlocked.Increment(ref Measures);
            var resolution = request.EffectScale / _outputScale;
            return (_baseWidth * resolution, _baseHeight * resolution);
        }

        public void Render(TextImageRequest request, string pngPath, CancellationToken cancellation)
        {
            var (width, height) = Measure(request);
            var w = Math.Max(2, Math.Min(request.CanvasWidth, (int)Math.Round(width)) / 2 * 2);
            var h = Math.Max(2, Math.Min(request.CanvasHeight, (int)Math.Round(height)) / 2 * 2);
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfmpegPath,
                // 캐시는 .part 이름에 쓰므로 형식·코덱을 정해 준다
                new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=" + w + "x" + h, "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", pngPath },
                TimeSpan.FromMinutes(1), cancellation).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.Error);
        }
    }

    /// <summary>
    /// 그리개 범위(화면의 몇 배)보다 큰 꾸민 글자(#34): 범위 안이면 예전과 같고, 넘치면 화면에 보일 수 있는 부분이 범위 안에 들 때까지 해상도를 반씩 낮춘다.
    /// </summary>
    public class StyledTextRangeTests
    {
        private readonly ITestOutputHelper _out;

        public StyledTextRangeTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static (Project Project, Clip Text) Styled(int width = 640, int height = 360)
        {
            var project = TestProjects.Empty(magnetic: false);
            project.Width = width;
            project.Height = height;
            var clip = TimelineEdits.CreateText("아주 긴 글", 0, 30);
            clip.Style = TextStyle.FromLegacy(clip);
            clip.FontSize = 80;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static RenderOptions Options(ITextRasterizer rasterizer, int width = 640, int height = 360)
        {
            return new RenderOptions { From = 0, To = 30, Width = width, Height = height, Audio = false, TempFolder = @"C:\Temp", TextRasterizer = rasterizer };
        }

        /// <summary>그래프가 PNG를 줄이는 배율(scale=w='max(1,iw*…)').</summary>
        private static double Factor(RenderPlan plan)
        {
            var match = Regex.Match(plan.Filter, @"scale=w='max\(1,iw\*([0-9.]+)\)'");
            Assert.True(match.Success, plan.Filter);
            return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        [Fact]
        public void FitsTheRange_SameRequestAsBefore()
        {
            // 640×360이면 범위는 화면의 4배(2560×1440). 그보다 작은 글은 잰 것과 상관없이 예전 요청 그대로(같은 캐시 파일)
            var (project, clip) = Styled();
            clip.Scale = 0.1;
            clip.X = 2000;
            var old = Assert.Single(RenderGraph.Build(project, Options(new FakeTextRasterizer())).TextImages).Value;
            var measuring = new MeasuringRasterizer(2400, 300);
            var plan = RenderGraph.Build(project, Options(measuring));
            var request = Assert.Single(plan.TextImages).Value;
            Assert.Equal(old.Key(1), request.Key(1));
            Assert.Equal(80, request.FontPixelSize, 6);
            Assert.Equal(2560, request.CanvasWidth);
            Assert.Equal(0.1, Factor(plan), 6);
            Assert.True(measuring.Measures > 0);
        }

        [Fact]
        public void ScaledDownHugeText_LowersResolutionByHalves_UntilTheVisiblePartFits()
        {
            // 20,000×200px 글을 10%로: 화면에 보일 수 있는 가로 범위의 반 = 320 / 0.1 = 3,200 → 범위의 반(1,280)에 들게 1/4로 그린다
            var (project, clip) = Styled();
            clip.Scale = 0.1;
            var plan = RenderGraph.Build(project, Options(new MeasuringRasterizer(20000, 200)));
            var request = Assert.Single(plan.TextImages).Value;
            Assert.Equal(80 * 0.25, request.FontPixelSize, 6);
            Assert.Equal(0.25, request.EffectScale, 6);
            // 캔버스(그리개 픽셀)는 그대로, 그래프가 크기 배율 / 해상도로 한 번에 줄인다 — 화면에서는 1/4 해상도 그림을 0.4배로(선명함 그대로)
            Assert.Equal(2560, request.CanvasWidth);
            Assert.Equal(0.4, Factor(plan), 6);
            Assert.Single(Regex.Matches(plan.Filter, "scale="));
            // 글 상자도 같은 해상도로
            clip.TextBoxWidth = 1000;
            Assert.Equal(250, Assert.Single(RenderGraph.Build(project, Options(new MeasuringRasterizer(20000, 200))).TextImages).Value.WrapWidth, 6);
        }

        [Fact]
        public void MovedFarAtFullSize_LowersResolution_AndCenteredDoesNot()
        {
            var (project, clip) = Styled();
            var rasterizer = new MeasuringRasterizer(20000, 200);
            // 가운데: 보일 수 있는 범위의 반 = 320 — 잰 글이 커도 범위 안
            var centered = RenderGraph.Build(project, Options(rasterizer));
            Assert.Equal(80, Assert.Single(centered.TextImages).Value.FontPixelSize, 6);
            Assert.Equal(1, Factor(centered), 6);
            // 오른쪽으로 3,000px: 보일 수 있는 범위의 반 = 3,320 → 1/4(830 ≤ 1,280)
            clip.X = 3000;
            var moved = RenderGraph.Build(project, Options(rasterizer));
            Assert.Equal(20, Assert.Single(moved.TextImages).Value.FontPixelSize, 6);
            Assert.Equal(4, Factor(moved), 6);
            // 옮기면 다른 그림(다시 그림)
            Assert.NotEqual(Assert.Single(centered.TextImages).Key, Assert.Single(moved.TextImages).Key);
            // 아주 멀리여도 글 전체가 범위에 드는 만큼만 낮춘다(20,000 / 8 = 2,500 ≤ 2,560)
            clip.X = 1e7;
            Assert.Equal(10, Assert.Single(RenderGraph.Build(project, Options(rasterizer)).TextImages).Value.FontPixelSize, 6);
            // 글이 아주 커도 1/64에서 멈춘다
            Assert.Equal(80.0 / 64, Assert.Single(RenderGraph.Build(project, Options(new MeasuringRasterizer(1e8, 200))).TextImages).Value.FontPixelSize, 6);
            Assert.Equal(1.0 / 64, RenderGraph.MinStyledResolution);
        }

        [Fact]
        public void Rotation_UsesTheClipAxes()
        {
            // 90° 돌린 넓은 글: 글의 가로축이 화면 세로 — 보일 수 있는 가로 범위의 반 = 180 / 0.1 = 1,800 → 1/2(900 ≤ 1,280)
            var (project, clip) = Styled();
            clip.Scale = 0.1;
            clip.Rotation = 90;
            var request = Assert.Single(RenderGraph.Build(project, Options(new MeasuringRasterizer(20000, 200))).TextImages).Value;
            Assert.Equal(40, request.FontPixelSize, 6);
        }

        [Fact]
        public void VisibleHalf_UndoesPositionScaleAndRotation()
        {
            var (project, clip) = Styled();
            var options = Options(null);
            Assert.Equal((320, 180), Round(RenderGraph.VisibleHalf(project, options, clip)));
            clip.X = -100;
            clip.Y = 50;
            Assert.Equal((420, 230), Round(RenderGraph.VisibleHalf(project, options, clip)));
            clip.Scale = 0.5;
            Assert.Equal((840, 460), Round(RenderGraph.VisibleHalf(project, options, clip)));
            clip.Rotation = 90;
            Assert.Equal((460, 840), Round(RenderGraph.VisibleHalf(project, options, clip)));
            // 출력이 프로젝트의 절반이면 출력 픽셀도 절반
            clip.Rotation = 0;
            Assert.Equal((420, 230), Round(RenderGraph.VisibleHalf(project, Options(null, 320, 180), clip)));
            // 잘못된 값은 0·배율 1로
            clip.X = double.NaN;
            clip.Y = 0;
            clip.Scale = double.PositiveInfinity;
            clip.Rotation = double.NaN;
            Assert.Equal((320, 180), Round(RenderGraph.VisibleHalf(project, options, clip)));
        }

        private static (double, double) Round((double Width, double Height) value)
        {
            return (Math.Round(value.Width, 6), Math.Round(value.Height, 6));
        }

        [Fact]
        public void Art_LowersResolutionTheSameWay()
        {
            var (project, text) = Styled();
            project.MainTrack.Clips.Remove(text);
            var art = TimelineEdits.CreateArt(ArtKind.Rectangle, 0, 30);
            art.Scale = 0.1;
            TimelineEdits.Place(project, project.MainTrack, art, 0);
            var plan = RenderGraph.Build(project, Options(new MeasuringRasterizer(20000, 200)));
            Assert.Equal(0.25, Assert.Single(plan.TextImages).Value.EffectScale, 6);
            Assert.Equal(0.4, Factor(plan), 6);
        }

        [FfmpegFact]
        public void ScaledDownWideText_IsNotCut_OnScreen()
        {
            // 320×240 화면의 범위는 1,280×960. 2,400×100px 글을 10%로 줄이면 화면에서 240×10 — 예전에는 범위에서 잘려 128px만 보였다
            var (project, clip) = Styled(320, 240);
            clip.Scale = 0.1;
            var rasterizer = new MeasuringRasterizer(2400, 100);
            var frame = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 10, 320, 240, CancellationToken.None, rasterizer).GetAwaiter().GetResult();
            Assert.NotNull(frame);
            int left = int.MaxValue, right = -1;
            for (var x = 0; x < 320; x++)
            {
                var i = (120 * 320 + x) * 4;
                if (frame[i + 2] > 200 && frame[i + 1] < 60 && frame[i] < 60)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
            _out.WriteLine("빨간 폭 " + (right - left + 1) + " (" + left + "~" + right + ")");
            Assert.InRange(right - left + 1, 238, 242);
            Assert.InRange(left, 39, 41);
        }
    }
}
