using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>
    /// 글리치 효과를 실제 FFmpeg로 그려 본다: RGB 분할의 플레인 밀기, 글리치가 일어나는 때만 바뀜, 롤링 띠의 자리·주사선, 한 장 그리기 = 이어 그리기(같은 프레임 두 번도 같음),
    /// 알파, 사람에게만, 썸네일, 1080p 걸리는 시간.
    /// </summary>
    public class GlitchRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;

        public GlitchRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly Lazy<string> LazyFolder = new Lazy<string>(CreateMedia);

        private static string Folder { get { return LazyFolder.Value; } }
        private static string Gradient { get { return Path.Combine(Folder, "grad.png"); } }
        private static string Edge { get { return Path.Combine(Folder, "edge.png"); } }
        private static string Half { get { return Path.Combine(Folder, "half.png"); } }
        private static string MaskLeft { get { return Path.Combine(Folder, "mask-left.png"); } }
        private static string Across { get { return Path.Combine(Folder, "across.png"); } }
        private static string Down { get { return Path.Combine(Folder, "down.png"); } }
        private static string Light { get { return Path.Combine(Folder, "light.png"); } }

        /// <summary>
        /// grad: 빨강 = 세로 기울기, 초록 = 가로 기울기, 파랑 = 16px 바둑판(옆·위아래로 밀리면 값이 바뀐다), edge: 왼쪽 검정·오른쪽 흰색,
        /// half: grad의 왼쪽 절반만 불투명, mask-left: 왼쪽 절반이 흰 마스크(원본의 절반 크기),
        /// across·down: 회색 가로·세로 기울기, light: 고른 밝은 회색(회색이라 출력의 색 줄임(4:2:0)에 값이 흔들리지 않는다 — 픽셀 값을 정확히 잴 때).
        /// </summary>
        private static string CreateMedia()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-glitch-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            const string grad = "r='Y*255/239':g='X*255/319':b='if(mod(floor(X/16)+floor(Y/16),2),200,40)'";
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=gbrp,geq=" + grad, "-frames:v", "1", "-pix_fmt", "rgb24", Path.Combine(folder, "grad.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=gray,geq=lum='if(lt(X,160),0,255)'", "-frames:v", "1", Path.Combine(folder, "edge.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=gbrap,geq=" + grad + ":a='if(lt(X,160),255,0)'", "-frames:v", "1", "-pix_fmt", "rgba", Path.Combine(folder, "half.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=160x120,format=gray,geq=lum='if(lt(X,80),255,0)'", "-frames:v", "1", Path.Combine(folder, "mask-left.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=gray,geq=lum='X*255/319'", "-frames:v", "1", Path.Combine(folder, "across.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=gray,geq=lum='Y*255/239'", "-frames:v", "1", Path.Combine(folder, "down.png"));
            Run("-f", "lavfi", "-i", "color=c=0xc8c8c8:size=320x240,format=gray", "-frames:v", "1", Path.Combine(folder, "light.png"));
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

        private static ClipEffect With(Clip clip, VideoEffectDef def, params (string Key, double Value)[] values)
        {
            var effect = VideoEffects.Create(def);
            foreach (var (key, value) in values)
                VideoEffects.SetNumber(effect, key, value);
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(effect);
            return effect;
        }

        /// <summary>[from, to)를 BGRA 원시 프레임으로.</summary>
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

        /// <summary>픽셀이 기대한 색에 가까운지(출력이 4:2:0을 거쳐 날카로운 색 경계에서 몇 단계 흔들린다).</summary>
        private static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 6)
        {
            Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
                "기대 " + expected + " · 실제 " + actual);
        }

        private static byte[] Frame(byte[] frames, int index)
        {
            var size = Width * Height * 4;
            return frames.Skip(index * size).Take(size).ToArray();
        }

        /// <summary>두 프레임에서 다른 픽셀 수(x 범위 안).</summary>
        private static int Changed(byte[] a, byte[] b, int fromX = 0, int toX = Width)
        {
            var count = 0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = fromX; x < toX; x++)
                {
                    var i = (y * Width + x) * 4;
                    if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])
                        count++;
                }
            }
            return count;
        }

        // ---------- RGB 분할 ----------

        [FfmpegFact]
        public void RgbSplit_MovesRedAndBlueOppositeWays_GreenStays()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Edge, 0);
            var effect = With(clip, RgbSplit.Def, ("amount", 10), ("angle", 0), ("jitter", 0), ("glow", 0));
            var frame = Render(project, 0, 1);
            _out.WriteLine("x 145 " + Pixel(frame, 145, 120) + " · 155 " + Pixel(frame, 155, 120) + " · 165 " + Pixel(frame, 165, 120) + " · 175 " + Pixel(frame, 175, 120));
            // 빨강은 오른쪽으로 10px(경계 160 → 170), 파랑은 왼쪽으로 10px(→ 150), 초록은 제자리
            Near((0, 0, 0), Pixel(frame, 145, 120));
            Near((0, 0, 255), Pixel(frame, 155, 120));
            Near((0, 255, 255), Pixel(frame, 165, 120));
            Near((255, 255, 255), Pixel(frame, 175, 120));
            Assert.Equal(10, RgbSplit.Offset(effect, 0, 30, 1).Distance, 6);
            // 방향 180: 빨강이 왼쪽, 파랑이 오른쪽
            VideoEffects.SetNumber(effect, "angle", 180);
            var left = Render(project, 0, 1);
            Near((255, 0, 0), Pixel(left, 155, 120));
            Near((255, 255, 0), Pixel(left, 165, 120));
            // 방향 90(아래): 세로 경계는 그대로
            VideoEffects.SetNumber(effect, "angle", 90);
            var down = Render(project, 0, 1);
            Near((0, 0, 0), Pixel(down, 155, 120));
            Near((255, 255, 255), Pixel(down, 165, 120));

            // 흔들림: 프레임마다 거리가 튀고, 빨강 경계는 그 식대로(±1px)
            VideoEffects.SetNumber(effect, "angle", 0);
            VideoEffects.SetNumber(effect, "jitter", 100);
            VideoEffects.SetNumber(effect, "amount", 20);
            var frames = Render(project, 0, 12);
            var edges = new List<int>();
            for (var f = 0; f < 12; f += 3)
            {
                var (distance, angle) = RgbSplit.Offset(effect, f, 30, 1);
                var redEdge = Enumerable.Range(100, 200).First(x => Pixel(frames, x, 120, f).R > 128);
                _out.WriteLine("프레임 " + f + ": 거리 " + distance.ToString("0.0") + " · 방향 " + (angle * 180 / Math.PI).ToString("0") + "° · 빨강 경계 " + redEdge);
                Assert.InRange(redEdge, 160 + distance * Math.Cos(angle) - 1.5, 160 + distance * Math.Cos(angle) + 1.5);
                edges.Add(redEdge);
            }
            Assert.True(edges.Distinct().Count() >= 2, "흔들림 100인데 거리가 그대로입니다");
        }

        // ---------- 글리치가 일어나는 때 ----------

        private static (long Quiet, long Burst) Frames(Func<long, bool> quiet, Func<long, bool> burst)
        {
            return (Enumerable.Range(0, 90).Select(f => (long)f).First(quiet), Enumerable.Range(0, 90).Select(f => (long)f).First(burst));
        }

        [FfmpegFact]
        public void BlockGlitch_ChangesThePictureOnlyDuringBursts()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Gradient, 0);
            var plain = Render(project, 0, 1);
            var effect = With(clip, BlockGlitch.Def, ("rate", 50), ("amount", 100), ("block", 40));
            var (quiet, burst) = Frames(f => BlockGlitch.IsQuiet(effect, f, 30, 0.05), f => BlockGlitch.IsBurst(effect, f, 30, 0.05));
            var calm = Render(project, quiet, quiet + 1);
            var glitch = Render(project, burst, burst + 1);
            var changed = Changed(plain, glitch);
            _out.WriteLine("조용한 프레임 " + quiet + " · 글리치 프레임 " + burst + " — 바뀐 픽셀 " + changed);
            Assert.Equal(plain, calm);
            Assert.True(changed > Width * Height / 50, "글리치 때 바뀐 픽셀 " + changed);
            // 같은 프레임을 다시 그려도 같다
            Assert.Equal(glitch, Render(project, burst, burst + 1));
        }

        [FfmpegFact]
        public void LineSlip_ShiftsWholeRowsSideways_OnlyDuringBursts()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Across, 0);
            var plain = Render(project, 0, 1);
            var effect = With(clip, LineSlip.Def, ("rate", 50), ("amount", 100), ("thick", 12), ("fringe", 0));
            var (quiet, burst) = Frames(f => LineSlip.IsQuiet(effect, f, 30, 0.05), f => LineSlip.IsBurst(effect, f, 30, 0.05));
            Assert.Equal(plain, Render(project, quiet, quiet + 1));
            var slip = Render(project, burst, burst + 1);
            var rows = Enumerable.Range(0, Height).Where(y => Enumerable.Range(0, Width).Any(x => Pixel(slip, x, y) != Pixel(plain, x, y))).ToList();
            _out.WriteLine("글리치 프레임 " + burst + " — 밀린 줄 " + rows.Count);
            Assert.NotEmpty(rows);
            Assert.True(rows.Count < Height, "모든 줄이 밀렸습니다");
            foreach (var y in rows)
            {
                // 줄 전체를 옆으로만 민다: 가로 기울기가 한 줄 안에서 같은 만큼 어긋난다(가장자리 늘임 빼고 가운데)
                var shift = Pixel(slip, 160, y).G - Pixel(plain, 160, y).G;
                Assert.All(Enumerable.Range(140, 40), x => Assert.InRange(Pixel(slip, x, y).G - Pixel(plain, x, y).G, shift - 2, shift + 2));
            }
        }

        [FfmpegFact]
        public void BadSignal_RollsOnlyInRollSlots_TheEmptyBarIsWhereTheTimingSays()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Down, 0, 300);
            var plain = Render(project, 0, 1);
            // 롤링만(잡음·떨림·주사선·색 번짐 없이)
            var effect = With(clip, BadSignal.Def, ("noise", 0), ("jitter", 0), ("scan", 0), ("bleed", 0), ("roll", 100), ("rate", 50));
            var rollFrame = Enumerable.Range(0, 300).First(f => BadSignal.Roll(effect, f, 30).Rolling && BadSignal.Roll(effect, f, 30).Progress > 0.25 && BadSignal.Roll(effect, f, 30).Progress < 0.75);
            var still = Enumerable.Range(0, 300).First(f => !BadSignal.Roll(effect, f, 30).Rolling);
            Assert.Equal(plain, Render(project, still, still + 1));
            var rolled = Render(project, rollFrame, rollFrame + 1);
            var bar = BadSignal.BarHeight(Height);
            var offset = (int)((Height + bar) * BadSignal.Roll(effect, rollFrame, 30).Progress);
            var barTop = Height - offset;
            _out.WriteLine("롤링 프레임 " + rollFrame + " · 올라간 높이 " + offset + " · 빈 띠 " + barTop + "~" + (barTop + bar));
            // 빈 띠(투명 — 검은 바탕이 보임)와, 그 위는 그림의 offset줄 아래(세로 기울기)
            Near((0, 0, 0), Pixel(rolled, 160, barTop + bar / 2), 2);
            Assert.InRange(Pixel(rolled, 160, 10).G, (10 + offset) * 255 / 239 - 3, (10 + offset) * 255 / 239 + 3);
            // 띠 아래는 다시 그림의 위쪽부터
            Assert.InRange(Pixel(rolled, 160, barTop + bar + 2).G, 0, 2 * 255 / 239 + 3);
        }

        [FfmpegFact]
        public void BadSignal_ScanlinesDarkenEveryOtherPair_NoiseIsAnimatedButDeterministic()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Light, 0);
            var plain = Render(project, 0, 1);
            var effect = With(clip, BadSignal.Def, ("noise", 0), ("jitter", 0), ("roll", 0), ("bleed", 0), ("scan", 100));
            var lines = Render(project, 7, 8);
            // 배율 1에서 주사선 간격 4px: 0·1줄은 그대로, 2·3줄은 60 어둡게(grainmerge)
            for (var y = 0; y < 12; y++)
            {
                var expected = y % 4 < 2 ? Pixel(plain, 300, y).G : Pixel(plain, 300, y).G - 60;
                Assert.InRange(Pixel(lines, 300, y).G, expected - 2, expected + 2);
            }
            // 잡음: 프레임마다 다르고(움직이는 지글거림), 같은 프레임은 같다
            VideoEffects.SetNumber(effect, "noise", 60);
            var a = Render(project, 10, 12);
            var b = Render(project, 11, 12);
            var noisy = Changed(plain, Frame(a, 0));
            _out.WriteLine("잡음이 바꾼 픽셀 " + noisy + " · 이웃 프레임끼리 다른 픽셀 " + Changed(Frame(a, 0), Frame(a, 1)));
            Assert.True(noisy > Width * Height / 2);
            Assert.True(Changed(Frame(a, 0), Frame(a, 1)) > Width * Height / 2);
            Assert.Equal(Frame(a, 1), b);
        }

        // ---------- 모든 글리치 공통 ----------

        /// <summary>글리치가 늘 일어나게(빈도 100) 한 네 효과.</summary>
        private static IEnumerable<(VideoEffectDef Def, (string, double)[] Values)> Busy()
        {
            yield return (RgbSplit.Def, new[] { ("amount", 12.0), ("jitter", 60.0), ("glow", 40.0) });
            yield return (BlockGlitch.Def, new[] { ("rate", 100.0), ("block", 40.0) });
            yield return (LineSlip.Def, new[] { ("rate", 100.0), ("thick", 12.0) });
            yield return (BadSignal.Def, new[] { ("rate", 100.0) });
        }

        [FfmpegFact]
        public void SinglePreviewFrame_EqualsTheSameFrameOfAContinuousRender_ForEachGlitch()
        {
            // 멈춘 화면(그 프레임부터)·재생(앞에서부터 이어)·내보내기가 같은 프레임을 바이트까지 같게 — 난수는 클립 안 프레임 번호에서만
            foreach (var (def, values) in Busy())
            {
                var project = NewProject(magnetic: false);
                var clip = Add(project, project.MainTrack, Gradient, 30);
                With(clip, def, values);
                var continuous = Render(project, 30, 60);
                foreach (var frame in new long[] { 41, 50, 59 })
                {
                    var single = Render(project, frame, frame + 1);
                    var same = Frame(continuous, (int)(frame - 30));
                    _out.WriteLine(def.Id + " 프레임 " + frame + " 다른 픽셀 " + Changed(single, same));
                    Assert.True(single.SequenceEqual(same), def.Id + " 프레임 " + frame + "이 다릅니다(" + Changed(single, same) + "픽셀)");
                }
            }
        }

        [FfmpegFact]
        public void ClipAlpha_IsKept_TransparentPartsShowTheTrackBelow()
        {
            foreach (var (def, values) in Busy())
            {
                var project = NewProject();
                Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
                var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
                var half = Add(project, overlay, Half, 0, 60);
                var plain = Render(project, 20, 21);
                With(half, def, values);
                var lit = Render(project, 20, 21);
                // 오른쪽(투명한 곳): 아래 트랙(파랑) 그대로 — 미는 효과는 알파도 함께 밀지만 경계에서 멀다(최대 밀기 < 130px)
                var fromX = def == RgbSplit.Def ? 162 : 300;
                var worst = 0;
                for (var y = 0; y < Height; y++)
                {
                    for (var x = fromX; x < Width; x++)
                    {
                        var (r0, g0, b0) = Pixel(plain, x, y);
                        var (r1, g1, b1) = Pixel(lit, x, y);
                        worst = Math.Max(worst, Math.Max(Math.Abs(r0 - r1), Math.Max(Math.Abs(g0 - g1), Math.Abs(b0 - b1))));
                    }
                }
                _out.WriteLine(def.Id + ": 투명한 곳의 가장 큰 차 " + worst + " · 그림 쪽 바뀐 픽셀 " + Changed(plain, lit, 0, 150));
                Assert.True(worst <= 4, def.Id + ": 투명한 곳이 " + worst + "만큼 바뀌었습니다");
            }
        }

        [FfmpegFact]
        public void PersonOnly_ChangesOnlyWhereTheMaskIsWhite()
        {
            foreach (var (def, values) in Busy())
            {
                var project = NewProject();
                var clip = Add(project, project.MainTrack, Gradient, 0);
                var plain = Render(project, 0, 1);
                var effect = With(clip, def, values);
                effect.PersonOnly = true;
                var person = Render(project, 12, 13, new Masks(Gradient, MaskLeft));
                var left = Changed(plain, person, 0, 140);
                var right = Changed(plain, person, 180, Width);
                _out.WriteLine(def.Id + ": 사람(왼쪽) 바뀐 픽셀 " + left + " · 오른쪽 " + right);
                Assert.True(left > 200, def.Id + ": 마스크가 흰 왼쪽이 그대로입니다");
                Assert.Equal(0, right);
                // 마스크가 아직 없으면 클립 전체에
                var whole = Render(project, 12, 13, new Masks("other.png", MaskLeft));
                Assert.True(Changed(plain, whole, 180, Width) > 200, def.Id + ": 마스크가 없을 때 오른쪽도 바뀌어야 합니다");
            }
        }

        [FfmpegFact]
        public void XfadeTransition_WithGlitchesOnBothClips_Renders()
        {
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var grad = Add(project, project.MainTrack, Gradient, 999, 60);
            TimelineEdits.SetTransition(project, grad, "WipeLeft", 30);
            With(blue, BadSignal.Def, ("rate", 100));
            With(blue, RgbSplit.Def);
            With(grad, BlockGlitch.Def, ("rate", 100));
            With(grad, LineSlip.Def, ("rate", 100));
            var frames = Render(project, 55, 95);
            // 전환 도중과 뒤에도 그림이 나온다(검은 화면이 아님)
            foreach (var index in new[] { 20, 35 })
                Assert.True(Enumerable.Range(0, Height).Max(y => Pixel(frames, 300, y, index).G) > 100, "프레임 " + (55 + index) + "이 비었습니다");
        }

        [FfmpegFact]
        public async System.Threading.Tasks.Task Playback_ThroughAnExpressionTransition_WithGlitches_DoesNotHang()
        {
            // 재생 그래프(수식 전환 묶음은 줄인 크기)로 전환 끝을 지나 이어 그린다 — 쌓기·늘인 판·지도가 든 조각도 멈추지 않고 모든 프레임이 나와야 한다
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var grad = Add(project, project.MainTrack, Gradient, 999, 60);
            TimelineEdits.SetTransition(project, grad, "Heart", 30);
            With(blue, BadSignal.Def, ("rate", 100));
            With(blue, BlockGlitch.Def, ("rate", 100));
            With(grad, RgbSplit.Def);
            With(grad, LineSlip.Def, ("rate", 100));
            // 640×360: 수식 전환 묶음의 재생 축소가 일어나는 크기(320×240은 축소 1이라 그 길을 타지 않는다)
            const int size = 640 * 360 * 4;
            for (var run = 0; run < 2; run++)
            {
                List<string> files;
                long read = 0;
                using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 40, 120, 640, 360, out files))
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
        public void PreviewStrips_AreDrawn()
        {
            foreach (var def in new[] { RgbSplit.Def, BlockGlitch.Def, LineSlip.Def, BadSignal.Def })
            {
                var png = MediaPictures.EffectStrip(FfmpegMedia.Tool, def, CancellationToken.None).GetAwaiter().GetResult();
                var width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
                var height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
                Assert.Equal(VideoEffects.PreviewWidth * VideoEffects.PreviewFrames, width);
                Assert.Equal(VideoEffects.PreviewHeight, height);
            }
        }

        [FfmpegFact]
        public void Cost_At1080p_StaysWithinBudget()
        {
            // 1080p 내보내기 그래프 2초(60장)를 -f null로: 효과 없이와 각 글리치(기본값)를 번갈아 두 번씩 재서 가장 빠른 것 — 장당 늘어난 시간
            var tool = FfmpegMedia.Tool;
            var project = TestProjects.Empty();
            project.Width = 1920;
            project.Height = 1080;
            var clip = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            clip.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            double Measure()
            {
                var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 60, Width = 1920, Height = 1080, Audio = false, TempFolder = RenderEnvironment.TempFolder(), FfmpegMajor = tool.MajorVersion });
                var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "null", "-" }, out var script);
                var watch = Stopwatch.StartNew();
                var result = FfmpegRunner.RunText(tool.FfmpegPath, args, TimeSpan.FromMinutes(2), CancellationToken.None).GetAwaiter().GetResult();
                watch.Stop();
                RenderCommand.TryDelete(script);
                Assert.True(result.ExitCode == 0, result.Error);
                return watch.Elapsed.TotalMilliseconds / 60;
            }
            var defs = new VideoEffectDef[] { null, RgbSplit.Def, BlockGlitch.Def, LineSlip.Def, BadSignal.Def };
            var best = defs.Select(_ => double.MaxValue).ToArray();
            // 다른 FFmpeg 시험과 함께 돌면 흔들리므로 세 번 재 가장 빠른 값으로
            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < defs.Length; i++)
                {
                    clip.Effects = defs[i] == null ? null : new List<ClipEffect> { VideoEffects.Create(defs[i]) };
                    best[i] = Math.Min(best[i], Measure());
                }
            }
            _out.WriteLine("1080p 장당: 효과 없이 " + best[0].ToString("0.0") + "ms · "
                + string.Join(" · ", Enumerable.Range(1, defs.Length - 1).Select(i => defs[i].Name + " " + best[i].ToString("0.0") + "ms(+" + (best[i] - best[0]).ToString("0.0") + ")")));
            // 목표는 +25ms 안팎 — 시험 기계의 속도와 부하를 생각해 넉넉하게
            for (var i = 1; i < defs.Length; i++)
                Assert.True(best[i] - best[0] < FfmpegMedia.CostBudget(best[0]),defs[i].Name + "가 장당 " + (best[i] - best[0]).ToString("0.0") + "ms 더 걸립니다");
        }
    }
}
