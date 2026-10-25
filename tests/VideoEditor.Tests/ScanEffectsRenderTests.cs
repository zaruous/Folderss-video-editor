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
    /// 스캔 계열 효과를 실제 FFmpeg로 그려 본다: 시각 식대로의 자리와 움직임, 효과마다의 모양(굴절·홀로그램 빛깔·꼬리·띠 안만 깨짐),
    /// 알파 그대로, 한 장 그리기 = 이어 그리기, 사람에게만(합성 마스크), 아주 작은 틀, 재생 전환에서 멈추지 않음, 썸네일, 1080p 걸리는 시간.
    /// </summary>
    public class ScanEffectsRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private readonly ITestOutputHelper _out;

        public ScanEffectsRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        public static IEnumerable<object[]> Ids()
        {
            return new[] { ShockwaveScan.Id, SciFiScan.Id, RayScan.Id, GlitchScan.Id }.Select(id => new object[] { id });
        }

        private static readonly Lazy<string> LazyFolder = new Lazy<string>(CreateMedia);

        private static string Folder { get { return LazyFolder.Value; } }
        private static string DarkImage { get { return Path.Combine(Folder, "dark.png"); } }
        private static string GrayImage { get { return Path.Combine(Folder, "gray.png"); } }
        private static string StripeImage { get { return Path.Combine(Folder, "stripes.png"); } }
        private static string HalfImage { get { return Path.Combine(Folder, "half.png"); } }
        private static string MaskImage { get { return Path.Combine(Folder, "mask-left.png"); } }

        /// <summary>어두운 사진(밝기 16), 회색 사진(96), 줄무늬 사진(가로로 촘촘한 무늬), 왼쪽 절반만 불투명한 줄무늬 사진, 왼쪽 절반이 흰 마스크(원본의 절반 크기).</summary>
        private static string CreateMedia()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-scan-effects-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            const string stripes = "geq=r='255*mod(floor(X/8),2)':g='255*mod(floor(X/12),2)':b='200*mod(floor(Y/10),2)'";
            Run("-f", "lavfi", "-i", "color=c=0x101010:size=320x240", "-frames:v", "1", Path.Combine(folder, "dark.png"));
            Run("-f", "lavfi", "-i", "color=c=0x606060:size=320x240", "-frames:v", "1", Path.Combine(folder, "gray.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=rgb24," + stripes, "-frames:v", "1", Path.Combine(folder, "stripes.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=320x240,format=rgba," + stripes.TrimEnd('\'') + "':a='if(lt(X,160),255,0)'", "-frames:v", "1", Path.Combine(folder, "half.png"));
            Run("-f", "lavfi", "-i", "color=c=black:size=160x120,format=gray,geq=lum='if(lt(X,80),255,0)'", "-frames:v", "1", Path.Combine(folder, "mask-left.png"));
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

        private static ClipEffect AddEffect(Clip clip, VideoEffectDef def, bool personOnly = false, params (string Key, double Value)[] values)
        {
            var effect = VideoEffects.Create(def);
            foreach (var (key, value) in values)
                VideoEffects.SetNumber(effect, key, value);
            effect.PersonOnly = personOnly;
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

        private static int Sum((int R, int G, int B) p)
        {
            return p.R + p.G + p.B;
        }

        private static int Change(byte[] a, byte[] b, int x, int y)
        {
            var p = Pixel(a, x, y);
            var q = Pixel(b, x, y);
            return Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
        }

        /// <summary>한 줄(또는 한 열)에서 가장 밝은 곳 — 흰 선은 몇 픽셀이 함께 하얗게 차므로 가장 밝은 곳들의 가운데. at(i)는 i번째 픽셀.</summary>
        private static int Brightest(Func<int, (int R, int G, int B)> at, int from, int to)
        {
            var best = from;
            for (var i = from + 1; i < to; i++)
            {
                if (Sum(at(i)) > Sum(at(best)))
                    best = i;
            }
            var peak = Sum(at(best));
            var last = best;
            while (last + 1 < to && Sum(at(last + 1)) >= peak - 6)
                last++;
            return (best + last) / 2;
        }

        private static ClipEffect Default(Clip clip, string id, bool personOnly = false)
        {
            return AddEffect(clip, VideoEffects.Find(id), personOnly);
        }

        // ---------- 효과마다 ----------

        [FfmpegFact]
        public void Shockwave_RingIsWhereTheTimingSays_Grows_AndRests()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, DarkImage, 0);
            var effect = AddEffect(clip, ShockwaveScan.Def, false, ("refract", 0));
            var columns = new List<int>();
            foreach (var frame in new long[] { 6, 15 })
            {
                var bgra = Render(project, frame, frame + 1);
                var expected = 160 + ShockwaveScan.Radius(effect, frame / 30.0, Width, Height, 1) - 0.5;
                var column = Brightest(x => Pixel(bgra, x, 120), 161, Width);
                _out.WriteLine("프레임 " + frame + ": 고리 " + column + "(예상 " + expected.ToString("0.0") + ") · 고리 " + Pixel(bgra, column, 120) + " · 바깥 빛 " + Pixel(bgra, column + 6, 120));
                Assert.InRange(column, expected - 1.5, expected + 1.5);
                var ring = Pixel(bgra, column, 120);
                Assert.True(Sum(ring) > 640, "흰 고리 " + ring);
                var glow = Pixel(bgra, column + 6, 120);
                Assert.True(glow.B > glow.R + 20 && glow.B > 60, "색 빛 " + glow);
                // 고리 밖은 그대로 어둡고, 안쪽은 조금 밝다
                Assert.True(Sum(Pixel(bgra, column + 45, 120)) < 80);
                Assert.True(Sum(Pixel(bgra, column - 15, 120)) > Sum(Pixel(bgra, column + 45, 120)) + 15);
                // 위쪽(같은 반지름)에도 고리가 있다 — 둥글다
                var up = Brightest(y => Pixel(bgra, 160, y), 0, 119);
                Assert.InRange(119.5 - up, ShockwaveScan.Radius(effect, frame / 30.0, Width, Height, 1) - 2, ShockwaveScan.Radius(effect, frame / 30.0, Width, Height, 1) + 2);
                columns.Add(column);
            }
            Assert.True(columns[1] > columns[0] + 20, "퍼진다: " + columns[0] + " → " + columns[1]);
            // 쉬는 동안(주기 2.2초의 끝 20%)에는 아무것도 없다
            var gap = Render(project, 60, 61);
            Assert.True(Enumerable.Range(0, Width).All(x => Sum(Pixel(gap, x, 120)) < 80), "쉬는 동안 고리가 보입니다");
        }

        [FfmpegFact]
        public void Shockwave_Refraction_MovesThePictureOnlyJustInsideTheFront()
        {
            // 왼쪽 끝 가운데에서 퍼지는 고리: 프레임 15에 반지름 약 172px, 물결 폭 90px(빛 번짐 10) — 가운데 줄에서 미는 방향은 가로
            var project = NewProject();
            var clip = Add(project, project.MainTrack, StripeImage, 0);
            var effect = AddEffect(clip, ShockwaveScan.Def, false, ("refract", 0), ("glow", 10), ("cx", 0));
            var flat = Render(project, 15, 16);
            VideoEffects.SetNumber(effect, "refract", 100);
            var bent = Render(project, 15, 16);
            var shape = ShockwaveScan.Measure(effect, Width, Height, 1);
            var radius = ShockwaveScan.Radius(effect, 0.5, Width, Height, 1);
            _out.WriteLine("반지름 " + radius + " · 물결 폭 " + shape.Wake + " · 미는 거리 " + shape.Refract);
            Assert.True(radius - shape.Wake > 60 && radius + shape.Glow + 10 < Width - 10);
            int MaxChange(int from, int to) => Enumerable.Range(from, to - from).Max(x => Enumerable.Range(110, 21).Max(y => Change(flat, bent, x, y)));
            var inside = MaxChange(2, (int)(radius - shape.Wake) - 3);
            var front = MaxChange((int)(radius - shape.Wake) + 5, (int)radius - 3);
            var outside = MaxChange((int)(radius + shape.Glow) + 3, Width);
            _out.WriteLine("가운데 쪽 " + inside + " · 물결 " + front + " · 바깥 " + outside);
            Assert.True(inside <= 6, "물결 안쪽(가운데 쪽)은 그대로여야 합니다: " + inside);
            Assert.True(front > 150, "물결 자리는 밀려야 합니다: " + front);
            Assert.True(outside <= 6, "고리 밖은 그대로여야 합니다: " + outside);
        }

        [FfmpegFact]
        public void SciFi_BandIsWhereTheTimingSays_TintsInside_LeavesAheadAlone_MovesDown()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, GrayImage, 0);
            var effect = Default(clip, SciFiScan.Id);
            var plain = Render(project, 30, 31);
            var plainPixel = Pixel(plain, 100, 120);
            var edges = new List<int>();
            foreach (var frame in new long[] { 30, 39 })
            {
                var time = frame / 30.0;
                var bgra = Render(project, frame, frame + 1);
                var edge = SciFiScan.EdgeY(effect, time, Width, Height, 1);
                var row = Brightest(y => Pixel(bgra, 100, y), 0, Height);
                _out.WriteLine("프레임 " + frame + ": 앞머리 " + row + "(예상 " + (edge - 0.5) + ") · 선 " + Pixel(bgra, 100, row) + " · 안 " + Pixel(bgra, 100, edge - 10) + " · 앞 " + Pixel(bgra, 100, edge + 30));
                Assert.InRange(row, edge - 2, edge + 1);
                var line = Pixel(bgra, 100, row);
                Assert.True(Sum(line) > 640, "흰 선 " + line);
                // 띠 안(앞머리 바로 위)은 푸른 홀로그램 빛깔
                var inside = Enumerable.Range(edge - 14, 8).Select(y => Pixel(bgra, 101, y)).ToList();
                Assert.True(inside.Average(p => p.B - p.R) > 40, "띠 안 " + string.Join(" ", inside));
                // 앞(아직 지나지 않은 곳)과 띠 위쪽 바깥은 그대로
                Assert.True(Change(bgra, plain, 100, edge + 30) <= 6, "앞 " + Pixel(bgra, 100, edge + 30) + " / " + plainPixel);
                var above = edge - SciFiScan.Measure(effect, Width, Height, 1).Band - 3;
                if (above >= 0)
                    Assert.True(Change(bgra, plain, 100, above) <= 6, "띠 위 " + Pixel(bgra, 100, above));
                edges.Add(row);
            }
            Assert.True(edges[1] > edges[0] + 5, "아래로 간다: " + edges[0] + " → " + edges[1]);
        }

        [FfmpegFact]
        public void Ray_BeamIsWhereTheTimingSays_InEveryDirection_TrailBehind_RestsInTheGap()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, DarkImage, 0);
            var effect = Default(clip, RayScan.Id);
            for (var dir = 0; dir < 4; dir++)
            {
                VideoEffects.SetNumber(effect, "dir", dir);
                var vertical = dir < 2;
                var forward = dir % 2 == 0 ? 1 : -1;
                var positions = new List<int>();
                foreach (var frame in new long[] { 30, 36 })
                {
                    var bgra = Render(project, frame, frame + 1);
                    var expected = RayScan.BeamPosition(effect, frame / 30.0, Width, Height, 1) - 0.5;
                    // 반짝임(빛을 따라 오르내림)에서 먼 줄·열에서 잰다
                    var found = vertical ? Brightest(x => Pixel(bgra, x, 60), 0, Width) : Brightest(y => Pixel(bgra, 60, y), 0, Height);
                    var core = vertical ? Pixel(bgra, found, 60) : Pixel(bgra, 60, found);
                    _out.WriteLine("방향 " + dir + " 프레임 " + frame + ": 빛 " + found + "(예상 " + expected.ToString("0.0") + ") · " + core);
                    Assert.InRange(found, expected - 1.5, expected + 1.5);
                    Assert.True(Sum(core) > 640, "흰 선 " + core);
                    // 지나온 쪽(꼬리)이 나아갈 쪽보다 밝다(여러 줄의 평균 — 빛줄기는 양쪽에 있다)
                    double Side(int offset) => Enumerable.Range(30, 40).Average(k =>
                    {
                        var at = found + offset;
                        if (at < 0 || at >= (vertical ? Width : Height))
                            return 0.0;
                        return Sum(vertical ? Pixel(bgra, at, k) : Pixel(bgra, k, at));
                    });
                    var behind = Side(-forward * 40);
                    var ahead = Side(forward * 40);
                    if (found - 40 >= 0 && found + 40 < (vertical ? Width : Height))
                        Assert.True(behind > ahead + 20, "방향 " + dir + " 꼬리 " + behind.ToString("0") + " · 앞 " + ahead.ToString("0"));
                    positions.Add(found);
                }
                Assert.True(Math.Sign(positions[1] - positions[0]) == forward, "방향 " + dir + ": " + positions[0] + " → " + positions[1]);
            }
            // 쉬는 동안(주기 2.4초의 끝 20%)에는 빛이 없다
            var gap = Render(project, 70, 71);
            Assert.True(Enumerable.Range(0, Height).All(y => Sum(Pixel(gap, 160, y)) < 80), "쉬는 동안 빛이 보입니다");
        }

        [FfmpegFact]
        public void Glitch_BreaksOnlyInsideTheBand_SharpEdges_MovesDown()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, StripeImage, 0);
            var plain = Render(project, 30, 31);
            var effect = Default(clip, GlitchScan.Id);
            var band = GlitchScan.Measure(effect, Width, Height, 1).Band;
            var firsts = new List<int>();
            foreach (var frame in new long[] { 30, 36 })
            {
                var bgra = Render(project, frame, frame + 1);
                var top = GlitchScan.BandTop(effect, frame / 30.0, Width, Height, 1);
                Assert.True(top > 0 && top + band < Height);
                int RowChange(int y) => Enumerable.Range(0, Width).Sum(x => Change(bgra, plain, x, y));
                var changed = Enumerable.Range(0, Height).Where(y => RowChange(y) > 0).ToList();
                _out.WriteLine("프레임 " + frame + ": 띠 " + top + "~" + (top + band - 1) + " · 바뀐 줄 " + changed.First() + "~" + changed.Last() + " (" + changed.Count + "줄)");
                // 띠 밖은 바뀌지 않고(날카로운 가장자리), 띠 안은 거의 모든 줄이 깨진다. 화면 합성은 4:2:0이라 띠 가장자리 줄과 색을 나눠 쓰는 바로 옆 줄은
                // 색이 섞이고, 그 바깥 몇 줄은 색 번짐만 조금 있다
                var partners = new[] { top % 2 == 1 ? top - 1 : -1, (top + band) % 2 == 1 ? top + band : -1 };
                var near = Enumerable.Range(1, 4).SelectMany(k => new[] { top - k, top + band - 1 + k }).Where(y => !partners.Contains(y))
                    .ToDictionary(y => y, y => Enumerable.Range(0, Width).Max(x => Change(bgra, plain, x, y)));
                _out.WriteLine("띠 바로 밖 줄의 최대 변화: " + string.Join(" ", near.OrderBy(v => v.Key).Select(v => v.Key + ":" + v.Value)));
                Assert.True(changed.All(y => y >= top - 4 && y < top + band + 4), "띠 밖이 바뀌었습니다");
                Assert.True(near.Values.All(v => v <= 40), "띠 가장자리 바깥이 깨졌습니다");
                Assert.True(changed.Count >= band * 0.8, "띠 안 바뀐 줄 " + changed.Count + "/" + band);
                Assert.True(RowChange(top) > 0 && RowChange(top + band - 1) > 0);
                firsts.Add(changed.First());
            }
            Assert.True(firsts[1] > firsts[0] + 5, "아래로 간다: " + firsts[0] + " → " + firsts[1]);
        }

        // ---------- 모든 스캔 효과 ----------

        [Theory]
        [MemberData(nameof(Ids))]
        public void ClipAlpha_IsKept_TransparentPartsShowTheTrackBelow(string id)
        {
            var project = NewProject();
            Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var half = Add(project, overlay, HalfImage, 0, 60);
            var plain = Render(project, 15, 45);
            Default(half, id);
            var lit = Render(project, 15, 45);
            var seen = 0;
            foreach (var frame in new[] { 5, 15, 25 })
            {
                var offset = frame * Width * Height * 4;
                for (var y = 0; y < Height; y += 3)
                {
                    // 오른쪽(투명한 곳)은 아래 트랙(파랑) 그대로
                    for (var x = 168; x < Width; x += 5)
                    {
                        var i = offset + (y * Width + x) * 4;
                        Assert.True(Math.Abs(plain[i] - lit[i]) <= 4 && Math.Abs(plain[i + 1] - lit[i + 1]) <= 4 && Math.Abs(plain[i + 2] - lit[i + 2]) <= 4,
                            id + " 프레임 " + frame + " (" + x + "," + y + "): " + plain[i + 2] + "," + plain[i + 1] + "," + plain[i] + " → " + lit[i + 2] + "," + lit[i + 1] + "," + lit[i]);
                    }
                    for (var x = 0; x < 150; x += 5)
                    {
                        var i = offset + (y * Width + x) * 4;
                        if (Math.Abs(plain[i] - lit[i]) + Math.Abs(plain[i + 1] - lit[i + 1]) + Math.Abs(plain[i + 2] - lit[i + 2]) > 60)
                            seen++;
                    }
                }
            }
            _out.WriteLine(id + ": 불투명한 쪽에서 바뀐 점 " + seen);
            Assert.True(seen > 10, id + ": 불투명한 쪽에 효과가 보여야 합니다");
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void SinglePreviewFrame_EqualsTheSameFrameOfAContinuousRender(string id)
        {
            // 멈춘 화면(그 프레임부터)·재생(앞에서부터 이어)·내보내기가 같은 프레임을 보여야 한다 — 무늬 고르기·빛줄기 흐름·덩어리 자리도
            var project = NewProject(magnetic: false);
            var clip = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 30);
            Default(clip, id);
            var continuous = Render(project, 30, 60);
            foreach (var frame in new long[] { 41, 50, 59 })
            {
                var single = Render(project, frame, frame + 1);
                long diff = 0;
                var offset = (int)(frame - 30) * Width * Height * 4;
                for (var i = 0; i < single.Length; i++)
                    diff += Math.Abs(single[i] - continuous[offset + i]);
                var mean = (double)diff / single.Length;
                _out.WriteLine(id + " 프레임 " + frame + " 평균 차 " + mean.ToString("0.000"));
                Assert.True(mean < 0.5, id + " 프레임 " + frame + " 평균 차 " + mean);
            }
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void PersonOnly_WithASyntheticMask_ChangesOnlyThePerson(string id)
        {
            // 왼쪽 절반이 흰(사람) 마스크: 효과가 보이는 프레임에서 오른쪽은 효과 없이와 같고 왼쪽만 바뀐다
            var project = NewProject();
            var clip = Add(project, project.MainTrack, StripeImage, 0);
            var plain = Render(project, 0, 90);
            var effect = Default(clip, id, personOnly: true);
            var frame = id == RayScan.Id
                ? Enumerable.Range(0, 70).First(f => RayScan.BeamPosition(effect, f / 30.0, Width, Height, 1) is var x && x > 40 && x < 120)
                : id == ShockwaveScan.Id ? 15 : 30;
            var lit = Render(project, frame, frame + 1, new Masks(StripeImage, MaskImage));
            var offset = frame * Width * Height * 4;
            int Diff(int x, int y)
            {
                var i = (y * Width + x) * 4;
                return Math.Abs(lit[i] - plain[offset + i]) + Math.Abs(lit[i + 1] - plain[offset + i + 1]) + Math.Abs(lit[i + 2] - plain[offset + i + 2]);
            }
            var right = Enumerable.Range(166, Width - 166).Max(x => Enumerable.Range(0, Height).Max(y => Diff(x, y)));
            var left = Enumerable.Range(0, 154).Max(x => Enumerable.Range(0, Height).Max(y => Diff(x, y)));
            _out.WriteLine(id + " 프레임 " + frame + ": 왼쪽(사람) 최대 변화 " + left + " · 오른쪽 " + right);
            Assert.True(left > 100, id + ": 사람 쪽에는 효과가 보여야 합니다");
            Assert.True(right <= 9, id + ": 사람이 아닌 쪽은 그대로여야 합니다: " + right);
        }

        [FfmpegFact]
        public void TinyAndFitPaddedBoxes_AllScanEffectsRender()
        {
            // 크기 배율 3%(틀 10×7)와 맞춤(빈 곳을 투명하게 채움)에서도 FFmpeg가 그래프를 받아 끝까지 그린다
            var project = NewProject();
            var tiny = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 0);
            tiny.Scale = 0.03;
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var red = Add(project, overlay, FfmpegMedia.RedImage, 0, 60);
            foreach (var clip in new[] { tiny, red })
            {
                foreach (var row in Ids())
                    Default(clip, (string)row[0]);
            }
            var bgra = Render(project, 20, 23);
            Assert.Equal(3 * Width * Height * 4, bgra.Length);
        }

        [FfmpegFact]
        public async Task Playback_ThroughAnExpressionTransition_WithScanEffects_DoesNotHang()
        {
            // 재생 그래프(수식 전환 묶음은 줄인 크기)로 전환 끝을 지나 이어 그린다 — 스캔 효과가 든 조각도 멈추지 않고 모든 프레임이 나와야 한다
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var dark = Add(project, project.MainTrack, StripeImage, 999, 60);
            TimelineEdits.SetTransition(project, dark, "Heart", 30);
            Default(blue, ShockwaveScan.Id);
            Default(blue, SciFiScan.Id);
            Default(dark, RayScan.Id);
            Default(dark, GlitchScan.Id);
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
                    var reading = Task.Run(async () =>
                    {
                        int n;
                        while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            read += n;
                    });
                    var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(45))) == reading;
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
            foreach (var row in Ids())
            {
                var def = VideoEffects.Find((string)row[0]);
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
            // 1080p 내보내기 그래프 2초(60장)를 -f null로: 효과 없이 / 효과마다(기본값) — 장당 늘어난 시간(한 번 그리는 그림 포함)
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
            var report = "1080p 장당: 효과 없이 " + plain.ToString("0.0") + "ms";
            var cases = Ids().Select(row => (Name: (string)row[0], Change: (Action<ClipEffect>)null)).ToList();
            cases.Insert(1, (ShockwaveScan.Id + "(일그러짐 0)", e => VideoEffects.SetNumber(e, "refract", 0)));
            foreach (var (name, change) in cases)
            {
                var effect = VideoEffects.Create(VideoEffects.Find(name.Split('(')[0]));
                change?.Invoke(effect);
                clip.Effects = new List<ClipEffect> { effect };
                var cost = Measure() - plain;
                report += " · " + name + " +" + cost.ToString("0.0");
                Assert.True(cost < FfmpegMedia.CostBudget, name + "이 장당 " + cost.ToString("0.0") + "ms 더 걸립니다");
            }
            _out.WriteLine(report);
        }
    }
}
