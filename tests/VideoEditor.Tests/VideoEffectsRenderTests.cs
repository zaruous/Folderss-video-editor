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
    /// <summary>스캔 라인을 실제 FFmpeg로 그려 본다: 띠의 높이·휘어짐, 인물 마스크, 알파, 떨림, 이어 그리기와 한 장 그리기, 전환, 썸네일, 걸리는 시간.</summary>
    public class VideoEffectsRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;

        public VideoEffectsRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly Lazy<string> LazyFolder = new Lazy<string>(CreateMedia);

        private static string Folder { get { return LazyFolder.Value; } }
        private static string DarkImage { get { return Path.Combine(Folder, "dark.png"); } }
        private static string DarkVideo { get { return Path.Combine(Folder, "dark.mp4"); } }
        private static string HalfImage { get { return Path.Combine(Folder, "half.png"); } }
        private static string MaskImage { get { return Path.Combine(Folder, "mask-left.png"); } }
        private static string MaskVideo { get { return Path.Combine(Folder, "mask-left.mkv"); } }

        /// <summary>어두운 사진·동영상(밝기 16), 왼쪽 절반만 불투명한 회색 사진, 왼쪽 절반이 흰 마스크(사진·동영상, 원본의 절반 크기).</summary>
        private static string CreateMedia()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-effects-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            Run("-f", "lavfi", "-i", "color=c=0x101010:size=320x240", "-frames:v", "1", Path.Combine(folder, "dark.png"));
            Run("-f", "lavfi", "-i", "color=c=0x101010:size=320x240:rate=30:duration=2", "-c:v", "mpeg4", "-q:v", "2", Path.Combine(folder, "dark.mp4"));
            Run("-f", "lavfi", "-i", "color=c=0x303030:size=320x240,format=rgba,geq=r=48:g=48:b=48:a='if(lt(X,160),255,0)'", "-frames:v", "1", Path.Combine(folder, "half.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=160x120,format=gray,geq=lum='if(lt(X,80),255,0)'", "-frames:v", "1", Path.Combine(folder, "mask-left.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=160x120:rate=30:duration=2,format=gray,geq=lum='if(lt(X,80),255,0)'", "-c:v", "ffv1", Path.Combine(folder, "mask-left.mkv"));
            // 16:9 어두운 사진과, 비율이 원본과 아주 다른(정사각) 흰 마스크 — 모델 크기 마스크(448×256 등)의 비율 차이를 크게 키워 본다
            Run("-f", "lavfi", "-i", "color=c=0x101010:size=640x360", "-frames:v", "1", Path.Combine(folder, "wide.png"));
            Run("-f", "lavfi", "-i", "color=c=white:size=256x256,format=gray", "-frames:v", "1", Path.Combine(folder, "mask-square.png"));
            return folder;
        }

        private static void Run(params string[] args)
        {
            var all = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            all.AddRange(args);
            var result = FfmpegRunner.RunText(FfmpegMedia.Tool.FfmpegPath, all, TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException("시험 미디어를 만들지 못했습니다: " + result.Error);
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

        private static Project NewProject(bool magnetic = true)
        {
            var project = TestProjects.Empty(magnetic);
            project.Width = Width;
            project.Height = Height;
            return project;
        }

        private static Clip Add(Project project, Track track, string path, long start, long imageFrames = 90)
        {
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(path), project.Rate, start, imageFrames);
            TimelineEdits.Place(project, track, clip, start);
            return clip;
        }

        /// <summary>떨림 없는 스캔 라인(빛 번짐 10px, 굵기 4px, 휘어짐 50%, 2.4초).</summary>
        private static ClipEffect AddLine(Clip clip, double jitter = 0, bool personOnly = false)
        {
            var effect = VideoEffects.Create(ScanLine.Def);
            VideoEffects.SetNumber(effect, "glow", 10);
            VideoEffects.SetNumber(effect, "core", 4);
            VideoEffects.SetNumber(effect, "bend", 50);
            VideoEffects.SetNumber(effect, "jitter", jitter);
            effect.PersonOnly = personOnly;
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(effect);
            return effect;
        }

        /// <summary>[from, to)를 BGRA 원시 프레임으로(한 장이면 한 장).</summary>
        private static byte[] Render(Project project, long from, long to, IPersonMaskProvider masks = null)
        {
            var tool = FfmpegMedia.Tool;
            var plan = RenderGraph.Build(project, new RenderOptions
            {
                From = from,
                To = to,
                Width = Width,
                Height = Height,
                Audio = false,
                Preview = true,
                TempFolder = RenderEnvironment.TempFolder(),
                FfmpegMajor = tool.MajorVersion,
                PersonMasks = masks
            });
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out var script);
            try
            {
                var bytes = FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromSeconds(60), CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal((to - from) * Width * Height * 4, bytes.Length);
                return bytes;
            }
            finally
            {
                RenderCommand.TryDelete(script);
                foreach (var file in plan.TextFiles.Keys)
                    RenderCommand.TryDelete(file);
            }
        }

        private static (int R, int G, int B) Pixel(byte[] bgra, int x, int y, int frame = 0)
        {
            var i = frame * Width * Height * 4 + (y * Width + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        private static int Sum((int R, int G, int B) p)
        {
            return p.R + p.G + p.B;
        }

        /// <summary>x열에서 가장 밝은 줄(흰 선이 지나는 곳) — 흰 선은 몇 줄이 함께 하얗게 차므로 가장 밝은 줄들의 가운데.</summary>
        private static int BrightestRow(byte[] bgra, int x, int frame = 0)
        {
            var best = 0;
            for (var y = 1; y < Height; y++)
            {
                if (Sum(Pixel(bgra, x, y, frame)) > Sum(Pixel(bgra, x, best, frame)))
                    best = y;
            }
            var peak = Sum(Pixel(bgra, x, best, frame));
            var last = best;
            while (last + 1 < Height && Sum(Pixel(bgra, x, last + 1, frame)) >= peak - 6)
                last++;
            return (best + last) / 2;
        }

        [FfmpegFact]
        public void Band_IsWhereTheTimingSays_MovesDown_AndBowsDown()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, DarkImage, 0);
            var effect = AddLine(clip);
            var rows = new List<int>();
            foreach (var frame in new long[] { 18, 36 })
            {
                var bgra = Render(project, frame, frame + 1);
                var center = BrightestRow(bgra, 160);
                var edge = BrightestRow(bgra, 4);
                var expectedCenter = ScanLine.LineY(effect, frame / 30.0, Width, Height, 1, 160);
                var expectedEdge = ScanLine.LineY(effect, frame / 30.0, Width, Height, 1, 4);
                _out.WriteLine("프레임 " + frame + ": 가운데 " + center + "(예상 " + expectedCenter.ToString("0.0") + ") · 끝 " + edge + "(예상 " + expectedEdge.ToString("0.0") + ") · 선 "
                    + Pixel(bgra, 160, center) + " · 아래 빛 " + Pixel(bgra, 160, center + 6));
                Assert.InRange(center, expectedCenter - 1.5, expectedCenter + 1.5);
                Assert.InRange(edge, expectedEdge - 1.5, expectedEdge + 1.5);
                // 아래로 휜다: 가운데가 끝보다 아래
                Assert.True(center > edge + 15, "가운데 " + center + " · 끝 " + edge);
                // 가운데는 흰 선, 조금 떨어진 곳은 파란 빛
                var line = Pixel(bgra, 160, center);
                Assert.True(line.R > 200 && line.G > 200 && line.B > 200, "흰 선 " + line);
                var glow = Pixel(bgra, 160, center + 6);
                Assert.True(glow.B > glow.R + 20 && glow.B > 40, "파란 빛 " + glow);
                // 띠 밖은 그대로 어둡다
                Assert.True(Sum(Pixel(bgra, 160, (center + 60) % Height)) < 80);
                rows.Add(center);
            }
            Assert.True(rows[1] > rows[0] + 10, "뒤 시각에 더 아래: " + rows[0] + " → " + rows[1]);

            // 쉬는 동안(주기의 끝 20%)에는 선이 없다
            var gap = Render(project, 66, 67);
            Assert.True(Enumerable.Range(0, Height).All(y => Sum(Pixel(gap, 160, y)) < 80), "쉬는 동안 선이 보입니다");
        }

        [FfmpegFact]
        public void PersonOnly_WithImageMask_DrawsOnlyWhereTheMaskIsWhite()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, DarkImage, 0);
            var effect = AddLine(clip, personOnly: true);
            var bgra = Render(project, 30, 31, new Masks(DarkImage, MaskImage));
            var left = (int)Math.Round(ScanLine.LineY(effect, 1, Width, Height, 1, 40));
            var right = (int)Math.Round(ScanLine.LineY(effect, 1, Width, Height, 1, 280));
            var leftMax = Enumerable.Range(left - 3, 7).Max(y => Sum(Pixel(bgra, 40, y)));
            var rightMax = Enumerable.Range(right - 3, 7).Max(y => Sum(Pixel(bgra, 280, y)));
            _out.WriteLine("왼쪽(사람) " + leftMax + " · 오른쪽 " + rightMax);
            Assert.True(leftMax > 600, "마스크가 흰 왼쪽에는 선이 보여야 합니다");
            Assert.True(rightMax < 80, "마스크가 검은 오른쪽에는 선이 없어야 합니다");

            // 마스크가 없으면(아직 만들지 않음) 클립 전체에
            var whole = Render(project, 30, 31, new Masks("other.png", MaskImage));
            Assert.True(Enumerable.Range(right - 3, 7).Max(y => Sum(Pixel(whole, 280, y))) > 600);
        }

        [PersonMaskFact]
        public async Task PersonOnly_WithARealPersonMask_LineOnlyOverThePerson()
        {
            // 처음부터 끝까지: 인물 분리 모델로 마스크를 만들고(실제 제공자), 사람에게만 켠 스캔 라인이 사람 위에만 그려진다
            var folder = Path.Combine(Folder, "person-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var photo = PersonMaskIntegrationTests.Person(folder, "사람.png", 640, 320);
                var service = new PersonMaskService(Path.Combine(folder, "person-mask"));
                var media = await MediaProbe.Probe(FfmpegMedia.Tool, photo, CancellationToken.None);
                var mask = await service.Build(FfmpegMedia.Tool, media, null, CancellationToken.None);
                Assert.Equal(mask, service.Find(photo));

                // 640×360 그림을 320×240 화면에 늘여 놓는다 — 사람 가운데 x 160, 몸통은 화면 y 약 187~233, 벽은 x 30
                var project = NewProject();
                var clip = Add(project, project.MainTrack, photo, 0);
                clip.Fit = ClipFit.Stretch;
                var effect = AddLine(clip, personOnly: true);
                var frame = Enumerable.Range(0, 90).First(f =>
                {
                    var y = ScanLine.LineY(effect, f / 30.0, Width, Height, 1, 160);
                    return y > 195 && y < 225;
                });
                var time = frame / 30.0;
                var lit = Render(project, frame, frame + 1, service);
                clip.Effects = null;
                var plain = Render(project, frame, frame + 1, service);
                int Change(int x, double lineY) => Enumerable.Range((int)Math.Round(lineY) - 3, 7)
                    .Max(y => Math.Abs(Sum(Pixel(lit, x, y)) - Sum(Pixel(plain, x, y))));
                var body = Change(160, ScanLine.LineY(effect, time, Width, Height, 1, 160));
                var wall = Change(30, ScanLine.LineY(effect, time, Width, Height, 1, 30));
                _out.WriteLine("프레임 " + frame + " · 몸통 위 변화 " + body + " · 벽 위 변화 " + wall);
                Assert.True(body > 150, "사람(몸통) 위에는 선이 보여야 합니다: " + body);
                Assert.True(wall <= 15, "벽 위에는 선이 없어야 합니다: " + wall);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [FfmpegFact]
        public void PersonOnly_MaskOfAnotherAspect_StillReachesThePictureEdges_InFit()
        {
            // 마스크를 마스크 자신의 비율로 맞추면(정사각 → 240×240) 16:9 그림 양옆에 검은 띠가 생겨 화면 끝에 닿은 사람에게 효과가 빠진다 — 원본 비율로 늘여 끝까지 겹친다
            var wide = Path.Combine(Folder, "wide.png");
            var project = NewProject();
            var clip = Add(project, project.MainTrack, wide, 0);
            project.Media.Add(FfmpegMedia.Probe(wide));
            clip.Fit = ClipFit.Fit;
            var effect = AddLine(clip, personOnly: true);
            var masks = new Masks(wide, Path.Combine(Folder, "mask-square.png"));
            // 선이 그림 안(맞춤 320×180, 위아래 30px 빈 곳)을 지나는 프레임
            var frame = Enumerable.Range(0, 90).First(f =>
            {
                var y = ScanLine.LineY(effect, f / 30.0, Width, Height, 1, 1);
                return y > 60 && y < 180;
            });
            var bgra = Render(project, frame, frame + 1, masks);
            int Peak(int x) => Enumerable.Range((int)Math.Round(ScanLine.LineY(effect, frame / 30.0, Width, Height, 1, x)) - 3, 7).Max(y => Sum(Pixel(bgra, x, y)));
            _out.WriteLine("왼쪽 끝 " + Peak(1) + " · 가운데 " + Peak(160) + " · 오른쪽 끝 " + Peak(318));
            Assert.True(Peak(1) > 600, "왼쪽 끝까지 선이 보여야 합니다: " + Peak(1));
            Assert.True(Peak(318) > 600, "오른쪽 끝까지 선이 보여야 합니다: " + Peak(318));
        }

        [FfmpegFact]
        public void PersonOnly_WithVideoMask_ReadsTheMaskLikeTheSource()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, DarkVideo, 0);
            clip.SourceIn = 15;
            var effect = AddLine(clip, personOnly: true);
            var bgra = Render(project, 20, 21, new Masks(DarkVideo, MaskVideo));
            var time = 20 / 30.0;
            var left = (int)Math.Round(ScanLine.LineY(effect, time, Width, Height, 1, 40));
            var right = (int)Math.Round(ScanLine.LineY(effect, time, Width, Height, 1, 280));
            var leftMax = Enumerable.Range(left - 3, 7).Max(y => Sum(Pixel(bgra, 40, y)));
            var rightMax = Enumerable.Range(right - 3, 7).Max(y => Sum(Pixel(bgra, 280, y)));
            _out.WriteLine("왼쪽(사람) " + leftMax + " · 오른쪽 " + rightMax);
            Assert.True(leftMax > 600);
            Assert.True(rightMax < 90);
        }

        public static IEnumerable<object[]> EffectIds()
        {
            return VideoEffects.All.Select(d => new object[] { d.Id });
        }

        [Theory]
        [MemberData(nameof(EffectIds))]
        public void Effect_EndsWithTheClip_NoExtraFrameOverTheGap(string id)
        {
            if (FfmpegMedia.Tool == null)
                return;
            // 한 장 그림을 tpad로 늘인 효과 그림은 한 장이 길어, 클립이 끝난 뒤 빈 곳에 한 장이 더 보였다(이어 그리기만 — 한 장 그리기와도 달랐다)
            var project = NewProject(magnetic: false);
            var clip = Add(project, project.MainTrack, DarkImage, 0, 30);
            Add(project, project.MainTrack, DarkImage, 45, 15);
            clip.Effects = new List<ClipEffect> { VideoEffects.Create(VideoEffects.Find(id)) };
            var frames = Render(project, 25, 35);
            long Rgb(byte[] bgra, int frame)
            {
                long sum = 0;
                for (var i = frame * Width * Height * 4; i < (frame + 1) * Width * Height * 4; i += 4)
                    sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
                return sum;
            }
            Assert.True(Rgb(frames, 4) > 0, "마지막 장(29)에는 클립이 보여야 합니다");
            for (var f = 5; f < 10; f++)
                Assert.True(Rgb(frames, f) == 0, id + ": 클립이 끝난 뒤 " + (25 + f) + "번째 장에 그림이 남았습니다(" + Rgb(frames, f) + ")");
            Assert.Equal(0, Rgb(Render(project, 30, 31), 0));
        }

        [FfmpegFact]
        public void ClipAlpha_IsKept_TransparentPartsShowTheTrackBelow()
        {
            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var half = Add(project, overlay, HalfImage, 0, 60);
            var plain = Render(project, 30, 31);
            var effect = AddLine(half);
            var lit = Render(project, 30, 31);
            var row = (int)Math.Round(ScanLine.LineY(effect, 1, Width, Height, 1, 280));
            // 오른쪽(투명한 곳): 선이 지나는 줄도 아래 트랙(파랑) 그대로
            for (var y = Math.Max(0, row - 20); y < Math.Min(Height, row + 20); y++)
            {
                var before = Pixel(plain, 280, y);
                var after = Pixel(lit, 280, y);
                Assert.True(Math.Abs(before.R - after.R) <= 4 && Math.Abs(before.G - after.G) <= 4 && Math.Abs(before.B - after.B) <= 4,
                    "y " + y + ": " + before + " → " + after);
            }
            // 왼쪽(불투명한 곳)에는 선이 보인다
            var leftRow = (int)Math.Round(ScanLine.LineY(effect, 1, Width, Height, 1, 40));
            _out.WriteLine("왼쪽 선 " + Pixel(lit, 40, leftRow) + " · 효과 없이 " + Pixel(plain, 40, leftRow));
            Assert.True(Sum(Pixel(lit, 40, leftRow)) > Sum(Pixel(plain, 40, leftRow)) + 300);
        }

        [FfmpegFact]
        public void FitPadding_StaysTransparent_LineSpansOnlyThePicture()
        {
            // 정사각 사진을 4:3 화면에 맞춤: 양옆 40px는 빈 곳(틀을 투명하게 채움) — 선이 지나도 아래 트랙(파랑)이 그대로 보인다
            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var red = Add(project, overlay, FfmpegMedia.RedImage, 0, 60);
            Assert.Equal(ClipFit.Fit, red.Fit);
            var effect = AddLine(red);
            var bgra = Render(project, 30, 31);
            var edge = (int)Math.Round(ScanLine.LineY(effect, 1, Width, Height, 1, 10));
            for (var y = Math.Max(0, edge - 15); y < Math.Min(Height, edge + 15); y++)
            {
                var side = Pixel(bgra, 10, y);
                Assert.True(side.B > 200 && side.R < 40 && side.G < 40, "y " + y + " 빈 곳 " + side);
            }
            var inside = (int)Math.Round(ScanLine.LineY(effect, 1, Width, Height, 1, 160));
            Assert.True(Sum(Pixel(bgra, 160, inside)) > 600, "사진 위에는 선 " + Pixel(bgra, 160, inside));
        }

        [FfmpegFact]
        public void Jitter_ZigZagsOverDarkPicture()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, DarkImage, 0);
            var effect = AddLine(clip);
            int Roughness(byte[] bgra)
            {
                var rows = Enumerable.Range(0, Width / 4).Select(i => BrightestRow(bgra, i * 4)).ToList();
                return rows.Zip(rows.Skip(1), (a, b) => Math.Abs(a - b)).Max();
            }
            var straight = Roughness(Render(project, 30, 31));
            VideoEffects.SetNumber(effect, "jitter", 100);
            var shaky = Roughness(Render(project, 30, 31));
            _out.WriteLine("이웃 열의 선 높이 차: 떨림 없이 " + straight + " · 떨림 100 " + shaky + " (폭 " + ScanLine.Measure(effect, Width, Height, 1).Jitter + "px)");
            Assert.True(straight <= 3);
            Assert.True(shaky >= 8);
        }

        [FfmpegFact]
        public void SinglePreviewFrame_EqualsTheSameFrameOfAContinuousRender()
        {
            // 멈춘 화면(그 프레임부터 그림)·재생(앞에서부터 이어 그림)·내보내기가 같은 프레임을 보여야 한다 — 떨림 무늬도
            var project = NewProject(magnetic: false);
            var clip = Add(project, project.MainTrack, DarkVideo, 30);
            AddLine(clip, jitter: 60);
            var continuous = Render(project, 30, 60);
            foreach (var frame in new long[] { 41, 50, 59 })
            {
                var single = Render(project, frame, frame + 1);
                long diff = 0;
                var offset = (int)(frame - 30) * Width * Height * 4;
                for (var i = 0; i < single.Length; i++)
                    diff += Math.Abs(single[i] - continuous[offset + i]);
                var mean = (double)diff / single.Length;
                _out.WriteLine("프레임 " + frame + " 평균 차 " + mean.ToString("0.000"));
                Assert.True(mean < 0.5, "프레임 " + frame + " 평균 차 " + mean);
            }
        }

        [FfmpegFact]
        public void XfadeTransition_WithEffectsOnBothClips_Renders()
        {
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var dark = Add(project, project.MainTrack, DarkImage, 999, 60);
            TimelineEdits.SetTransition(project, dark, "WipeLeft", 30);
            AddLine(blue);
            AddLine(dark, jitter: 50);
            var frames = Render(project, 55, 95);
            // 전환 도중(프레임 75)과 뒤(90)에도 흰 선이 있다
            foreach (var index in new[] { 20, 35 })
                Assert.True(Enumerable.Range(0, Height).Max(y => Sum(Pixel(frames, 300, y, index))) > 600, "프레임 " + (55 + index) + "에 선이 없습니다");
        }

        [FfmpegFact]
        public async System.Threading.Tasks.Task Playback_ThroughAnExpressionTransition_WithEffects_DoesNotHang()
        {
            // 재생 그래프(수식 전환 묶음은 줄인 크기)로 전환 끝을 지나 이어 그린다 — 효과가 든 조각도 멈추지 않고 모든 프레임이 나와야 한다
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var dark = Add(project, project.MainTrack, DarkImage, 999, 60);
            TimelineEdits.SetTransition(project, dark, "Heart", 30);
            AddLine(blue, jitter: 40);
            AddLine(dark, jitter: 40);
            const int size = Width * Height * 4;
            for (var run = 0; run < 3; run++)
            {
                List<string> files;
                long read = 0;
                using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 40, 120, Width, Height, out files))
                {
                    var errors = process.StandardError.ReadToEndAsync();
                    var buffer = new byte[size];
                    var reading = System.Threading.Tasks.Task.Run(async () =>
                    {
                        int n;
                        while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            read += n;
                    });
                    var finished = await System.Threading.Tasks.Task.WhenAny(reading, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30))) == reading;
                    if (!finished)
                        FfmpegRunner.Kill(process);
                    Assert.True(finished, "멈춤(" + (read / size) + "장에서)");
                    await process.WaitForExitAsync();
                    Assert.True(read == 80L * size, "읽은 장 " + (read / size) + " · " + await errors);
                }
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
            }
        }

        [FfmpegFact]
        public void PreviewStrip_IsDrawn()
        {
            var png = MediaPictures.EffectStrip(FfmpegMedia.Tool, ScanLine.Def, CancellationToken.None).GetAwaiter().GetResult();
            var width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            var height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            Assert.Equal(VideoEffects.PreviewWidth * VideoEffects.PreviewFrames, width);
            Assert.Equal(VideoEffects.PreviewHeight, height);
        }

        [FfmpegFact]
        public void PreviewSource_IsTexturedHighContrastAndStill()
        {
            // 썸네일 바탕: 15장이 모두 같고(정지 — 움직임은 효과만), 어두운 사람 실루엣·밝은 해·흑백 바둑판·격자·왼쪽이 어두운 하늘이 있다
            int w = VideoEffects.PreviewWidth, h = VideoEffects.PreviewHeight, size = w * h * 3;
            var bytes = FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath,
                new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", VideoEffects.PreviewSource, "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" },
                TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(VideoEffects.PreviewFrames * size, bytes.Length);
            for (var frame = 1; frame < VideoEffects.PreviewFrames; frame++)
                Assert.True(bytes.AsSpan(frame * size, size).SequenceEqual(bytes.AsSpan(0, size)), "장 " + frame);
            int Luma(int x, int y)
            {
                var i = (y * w + x) * 3;
                return (bytes[i] * 3 + bytes[i + 1] * 6 + bytes[i + 2]) / 10;
            }
            // 실루엣(머리·어깨)은 거의 검고, 해는 아주 밝다
            Assert.True(Luma((int)(0.4 * w), (int)(0.35 * h)) < 20, "머리 " + Luma((int)(0.4 * w), (int)(0.35 * h)));
            Assert.True(Luma((int)(0.4 * w), h - 3) < 20, "어깨 " + Luma((int)(0.4 * w), h - 3));
            Assert.True(Luma((int)(0.79 * w), (int)(0.3 * h)) > 220, "해 " + Luma((int)(0.79 * w), (int)(0.3 * h)));
            // 바닥 바둑판: 이웃 칸의 밝기 차가 크다(흔들림·글리치가 잘 보이게)
            Assert.True(Math.Abs(Luma(w - 12, h - 4) - Luma(w - 4, h - 4)) > 80, "바둑판 " + Luma(w - 12, h - 4) + " / " + Luma(w - 4, h - 4));
            // 하늘의 격자 줄은 바로 옆보다 밝고, 하늘은 왼쪽이 오른쪽보다 어둡다(어둠에 반응하는 효과용)
            Assert.True(Luma(12, 30) > Luma(14, 30) + 15, "격자 " + Luma(12, 30) + " / " + Luma(14, 30));
            Assert.True(Luma(4, 40) + 30 < Luma(w - 6, 40), "하늘 " + Luma(4, 40) + " / " + Luma(w - 6, 40));
        }

        [FfmpegFact]
        public void Cost_At1080p_StaysWithinBudget()
        {
            // 1080p 내보내기 그래프 2초(60장)를 -f null로: 효과 없이 / 스캔 라인(기본값, 떨림 포함) / 떨림 없이 — 장당 늘어난 시간
            var tool = FfmpegMedia.Tool;
            var project = TestProjects.Empty();
            project.Width = 1920;
            project.Height = 1080;
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            clip.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            double Measure()
            {
                var best = double.MaxValue;
                // 다른 FFmpeg 시험과 함께 돌면 흔들리므로 세 번 재 가장 빠른 값으로
                for (var run = 0; run < 3; run++)
                {
                    var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 60, Width = 1920, Height = 1080, Audio = false, TempFolder = RenderEnvironment.TempFolder(), FfmpegMajor = tool.MajorVersion });
                    var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "null", "-" }, out var script);
                    var watch = Stopwatch.StartNew();
                    var result = FfmpegRunner.RunText(tool.FfmpegPath, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
                    watch.Stop();
                    RenderCommand.TryDelete(script);
                    Assert.True(result.ExitCode == 0, result.Error);
                    best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 60);
                }
                return best;
            }
            var plain = Measure();
            clip.Effects = new List<ClipEffect> { VideoEffects.Create(ScanLine.Def) };
            var full = Measure();
            VideoEffects.SetNumber(clip.Effects[0], "jitter", 0);
            var smooth = Measure();
            _out.WriteLine("1080p 장당: 효과 없이 " + plain.ToString("0.0") + "ms · 스캔 라인 " + full.ToString("0.0") + "ms(+" + (full - plain).ToString("0.0")
                + ") · 떨림 없이 " + smooth.ToString("0.0") + "ms(+" + (smooth - plain).ToString("0.0") + ")");
            Assert.True(full - plain < FfmpegMedia.CostBudget, "스캔 라인이 장당 " + (full - plain).ToString("0.0") + "ms 더 걸립니다");
        }
    }
}
