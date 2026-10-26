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
    /// "흔들림·줌" 효과를 실제 FFmpeg로 그려 본다: 표지(가운데 흰 네모)의 자리·크기가 움직임 계산과 맞는지, 흔들어도 빈 가장자리가 없는지,
    /// [화면에 맞춤]의 빈 곳이 그대로인지, 한 장 그리기 = 이어 그리기, 전환·재생에서 멈추지 않는지, 썸네일, 1080p 걸리는 시간.
    /// </summary>
    public class ShakeZoomRenderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        // 표지 사진의 바탕 밝기와 흰 네모(20×20, 가운데 = (160, 120))
        private const int Background = 48;
        private const int MarkerSize = 20;
        private readonly ITestOutputHelper _out;

        public ShakeZoomRenderTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static readonly VideoEffectDef[] Family = { ShakeZoom.CameraShake, ShakeZoom.Earthquake, ShakeZoom.Heartbeat, ShakeZoom.PunchZoom, ShakeZoom.ZoomBounce };
        private static readonly Lazy<string> LazyFolder = new Lazy<string>(CreateMedia);

        private static string Folder { get { return LazyFolder.Value; } }
        private static string Marker { get { return Path.Combine(Folder, "marker.png"); } }
        private static string CornerMarker { get { return Path.Combine(Folder, "marker-corner.png"); } }
        private static string Ring { get { return Path.Combine(Folder, "ring.png"); } }

        /// <summary>표지 사진(어두운 회색에 가운데 흰 네모, 왼쪽 위 1/4 자리에 흰 네모), 테두리 사진(빨강에 3px 초록 테두리) — 모두 320×240.</summary>
        private static string CreateMedia()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-shakezoom-" + Environment.ProcessId);
            Directory.CreateDirectory(folder);
            Run("-f", "lavfi", "-i", "color=c=0x303030:size=320x240,drawbox=x=150:y=110:w=20:h=20:c=white:t=fill", "-frames:v", "1", Path.Combine(folder, "marker.png"));
            Run("-f", "lavfi", "-i", "color=c=0x303030:size=320x240,drawbox=x=70:y=50:w=20:h=20:c=white:t=fill", "-frames:v", "1", Path.Combine(folder, "marker-corner.png"));
            Run("-f", "lavfi", "-i", "color=c=0x00ff00:size=320x240,drawbox=x=3:y=3:w=314:h=234:c=0xff0000:t=fill", "-frames:v", "1", Path.Combine(folder, "ring.png"));
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

        private static Project NewProject(bool magnetic = true)
        {
            var project = TestProjects.Empty(magnetic);
            project.Width = Width;
            project.Height = Height;
            return project;
        }

        private static Clip Add(Project project, Track track, string path, long start, long imageFrames = 90)
        {
            var media = FfmpegMedia.Probe(path);
            project.Media.Add(media);
            var clip = TimelineEdits.CreateClip(media, project.Rate, start, imageFrames);
            TimelineEdits.Place(project, track, clip, start);
            return clip;
        }

        private static ClipEffect AddEffect(Clip clip, VideoEffectDef def, params (string Key, double Value)[] values)
        {
            var effect = VideoEffects.Create(def);
            foreach (var (key, value) in values)
                VideoEffects.SetNumber(effect, key, value);
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(effect);
            return effect;
        }

        /// <summary>[from, to)를 BGRA 원시 프레임으로.</summary>
        private static byte[] Render(Project project, long from, long to)
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
                FfmpegMajor = tool.MajorVersion
            });
            var args = RenderCommand.Arguments(tool, plan, new[] { "-map", "[vout]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }, out var script);
            try
            {
                var bytes = FfmpegRunner.RunBytes(tool.FfmpegPath, args, TimeSpan.FromSeconds(60), CancellationToken.None).GetAwaiter().GetResult();
                // 크기는 그대로(틀 크기 → 화면 크기)
                Assert.Equal((to - from) * Width * Height * 4, bytes.Length);
                return bytes;
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }

        private static (int R, int G, int B) Pixel(byte[] bgra, int x, int y, int frame = 0)
        {
            var i = frame * Width * Height * 4 + (y * Width + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        /// <summary>표지(바탕보다 밝은 곳)의 무게와 무게중심(픽셀 가운데 기준 연속 좌표).</summary>
        private static (double Mass, double X, double Y) Spot(byte[] bgra, int frame)
        {
            double mass = 0, sx = 0, sy = 0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var p = Pixel(bgra, x, y, frame);
                    var v = Math.Max(0, (p.R + p.G + p.B) / 3.0 - Background - 4);
                    mass += v;
                    sx += v * (x + 0.5);
                    sy += v * (y + 0.5);
                }
            }
            return (mass, sx / Math.Max(1, mass), sy / Math.Max(1, mass));
        }

        /// <summary>효과 없이 그린 표지의 무게(배율 1).</summary>
        private double PlainMass(string path)
        {
            var project = NewProject();
            Add(project, project.MainTrack, path, 0);
            return Spot(Render(project, 0, 1), 0).Mass;
        }

        /// <summary>점 (sx, sy)(원본 그림)가 출력에 보이는 자리: 출력 p는 원본 c + R·(p − c) ÷ 배율 + 옮김을 읽으므로 p = c + 배율·R(−돌림)·(s − c − 옮김).</summary>
        private static (double X, double Y) Seen(ShakeZoom.Pose pose, double cx, double cy, double sx, double sy)
        {
            double dx = sx - cx - pose.X, dy = sy - cy - pose.Y;
            double cos = Math.Cos(pose.Angle), sin = Math.Sin(pose.Angle);
            return (cx + pose.Zoom * (cos * dx + sin * dy), cy + pose.Zoom * (-sin * dx + cos * dy));
        }

        [FfmpegFact]
        public void CameraShake_MarkerMovesAsTheMotionSays_Smoothly()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Marker, 0);
            var effect = AddEffect(clip, ShakeZoom.CameraShake);
            var frames = Render(project, 0, 45);
            var plain = PlainMass(Marker);
            var travel = 0.0;
            for (var f = 0; f < 45; f++)
            {
                var pose = ShakeZoom.CameraShakePose(effect, f / 30.0, Width, Height, 1);
                var expected = Seen(pose, 160, 120, 160, 120);
                var spot = Spot(frames, f);
                if (f % 5 == 0)
                    _out.WriteLine("프레임 " + f + ": 표지 (" + spot.X.ToString("0.00") + ", " + spot.Y.ToString("0.00") + ") 예상 (" + expected.X.ToString("0.00") + ", " + expected.Y.ToString("0.00") + ") 배율 " + pose.Zoom.ToString("0.000"));
                Assert.InRange(spot.X, expected.X - 0.6, expected.X + 0.6);
                Assert.InRange(spot.Y, expected.Y - 0.6, expected.Y + 0.6);
                // 흔드는 만큼 키운 배율(고정)
                Assert.InRange(Math.Sqrt(spot.Mass / plain), pose.Zoom * 0.98, pose.Zoom * 1.02);
                if (f > 0)
                {
                    var before = Spot(frames, f - 1);
                    travel = Math.Max(travel, Math.Abs(spot.X - before.X));
                }
            }
            // 부드럽게(한 프레임에 조금씩) 움직이고, 실제로 움직인다
            _out.WriteLine("한 프레임에 가장 많이 움직인 거리 " + travel.ToString("0.00") + "px");
            Assert.InRange(travel, 0.5, 6);
        }

        [FfmpegFact]
        public void Earthquake_MarkerJolts_AsTheMotionSays()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Marker, 0);
            var effect = AddEffect(clip, ShakeZoom.Earthquake);
            var frames = Render(project, 0, 45);
            var jump = 0.0;
            for (var f = 0; f < 45; f++)
            {
                var pose = ShakeZoom.EarthquakePose(effect, f / 30.0, Width, Height, 1);
                var expected = Seen(pose, 160, 120, 160, 120);
                var spot = Spot(frames, f);
                Assert.InRange(spot.X, expected.X - 0.8, expected.X + 0.8);
                Assert.InRange(spot.Y, expected.Y - 0.8, expected.Y + 0.8);
                if (f > 0)
                    jump = Math.Max(jump, Math.Abs(spot.X - Spot(frames, f - 1).X));
            }
            _out.WriteLine("한 프레임에 가장 크게 뛴 거리 " + jump.ToString("0.00") + "px");
            Assert.True(jump > 10, "홱홱 뛰어야 합니다: " + jump);
            // 흐림: 흰 네모의 가장자리가 번진다(흐림 0과 비교 — 네모 가운데를 지나는 줄에서 바탕과 흰색 사이 밝기의 픽셀 수)
            int Edge(byte[] bgra)
            {
                var row = (int)Spot(bgra, 0).Y;
                return Enumerable.Range(0, Width).Count(x => Pixel(bgra, x, row).R > Background + 20 && Pixel(bgra, x, row).R < 235);
            }
            VideoEffects.SetNumber(effect, "blur", 100);
            var blurred = Edge(Render(project, 10, 11));
            VideoEffects.SetNumber(effect, "blur", 0);
            var sharp = Edge(Render(project, 10, 11));
            _out.WriteLine("가장자리 중간 밝기 픽셀: 흐림 100% " + blurred + " · 흐림 없이 " + sharp);
            Assert.True(blurred >= sharp + 3);
        }

        [FfmpegFact]
        public void Heartbeat_MarkerGrowsOnTheBeat()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Marker, 0);
            var effect = AddEffect(clip, ShakeZoom.Heartbeat, ("amount", 30));
            var frames = Render(project, 0, 45);
            var plain = PlainMass(Marker);
            for (var f = 0; f < 45; f++)
            {
                var expected = ShakeZoom.HeartbeatZoom(effect, f / 30.0);
                var spot = Spot(frames, f);
                var zoom = Math.Sqrt(spot.Mass / plain);
                if (f % 3 == 0)
                    _out.WriteLine("프레임 " + f + ": 배율 " + zoom.ToString("0.000") + " (예상 " + expected.ToString("0.000") + ")");
                Assert.InRange(zoom, expected - 0.02, expected + 0.02);
                // 가운데를 두고 커진다
                Assert.InRange(spot.X, 159.5, 160.5);
                Assert.InRange(spot.Y, 119.5, 120.5);
            }
        }

        [FfmpegFact]
        public void TwoEffectsCombined_MarkerIsWhereBothMotionsPutIt()
        {
            // 빠른 줌(중심 25%·25%)을 먼저, 카메라 흔들림을 그 위에 — 표지는 두 움직임을 차례로 건 자리, 크기는 두 배율의 곱
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Marker, 0);
            var punch = AddEffect(clip, ShakeZoom.PunchZoom, ("zoom", 60), ("start", 0.3), ("duration", 0.5), ("centerx", 25), ("centery", 25));
            var shake = AddEffect(clip, ShakeZoom.CameraShake);
            var frames = Render(project, 0, 40);
            var plain = PlainMass(Marker);
            for (var f = 0; f < 40; f++)
            {
                var time = f / 30.0;
                var zoom = ShakeZoom.PunchZoomAt(punch, time);
                var (cx, cy) = ShakeZoom.ZoomCenter(punch, ShakeZoom.PunchZoom, Width, Height);
                var first = Seen(new ShakeZoom.Pose(zoom, 0, 0, 0), cx, cy, 160, 120);
                var pose = ShakeZoom.CameraShakePose(shake, time, Width, Height, 1);
                var expected = Seen(pose, 160, 120, first.X, first.Y);
                var spot = Spot(frames, f);
                if (f % 5 == 0)
                    _out.WriteLine("프레임 " + f + ": 표지 (" + spot.X.ToString("0.00") + ", " + spot.Y.ToString("0.00") + ") 예상 (" + expected.X.ToString("0.00") + ", " + expected.Y.ToString("0.00") + ") 배율 " + (zoom * pose.Zoom).ToString("0.000"));
                Assert.InRange(spot.X, expected.X - 0.8, expected.X + 0.8);
                Assert.InRange(spot.Y, expected.Y - 0.8, expected.Y + 0.8);
                Assert.InRange(Math.Sqrt(spot.Mass / plain), zoom * pose.Zoom * 0.97, zoom * pose.Zoom * 1.03);
            }
        }

        [FfmpegFact]
        public void HeartbeatAndZoomBounce_ZoomAroundTheirCenter()
        {
            // 중심 = 표지(왼쪽 위 네모의 가운데 (80, 60) = 25%, 25%) — 표지는 제자리에서 배율만큼 커진다
            foreach (var def in new[] { ShakeZoom.Heartbeat, ShakeZoom.ZoomBounce })
            {
                var project = NewProject();
                var clip = Add(project, project.MainTrack, CornerMarker, 0);
                var effect = AddEffect(clip, def, ("centerx", 25), ("centery", 25));
                var frames = Render(project, 0, 30);
                var plain = PlainMass(CornerMarker);
                for (var f = 0; f < 30; f++)
                {
                    var expected = def == ShakeZoom.Heartbeat ? ShakeZoom.HeartbeatZoom(effect, f / 30.0) : ShakeZoom.ZoomBounceAt(effect, f / 30.0);
                    var spot = Spot(frames, f);
                    var zoom = Math.Sqrt(spot.Mass / plain);
                    if (f % 5 == 0)
                        _out.WriteLine(def.Id + " 프레임 " + f + ": 배율 " + zoom.ToString("0.000") + " (예상 " + expected.ToString("0.000") + ") 자리 (" + spot.X.ToString("0.0") + ", " + spot.Y.ToString("0.0") + ")");
                    Assert.InRange(zoom, expected - 0.025, expected + 0.025);
                    Assert.InRange(spot.X, 79.5, 80.5);
                    Assert.InRange(spot.Y, 59.5, 60.5);
                }
            }
        }

        [FfmpegFact]
        public void PunchZoom_ZoomsAtTheStartTime_AroundTheCenter_AndHolds()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, CornerMarker, 0);
            // 중심 = 표지(왼쪽 위 네모의 가운데 (80, 60) = 25%, 25%) — 표지는 제자리에서 커진다
            var effect = AddEffect(clip, ShakeZoom.PunchZoom, ("zoom", 100), ("start", 0.5), ("duration", 0.4), ("centerx", 25), ("centery", 25));
            var frames = Render(project, 0, 45);
            var plain = PlainMass(CornerMarker);
            for (var f = 0; f < 45; f++)
            {
                var expected = ShakeZoom.PunchZoomAt(effect, f / 30.0);
                var spot = Spot(frames, f);
                var zoom = Math.Sqrt(spot.Mass / plain);
                if (f % 3 == 0)
                    _out.WriteLine("프레임 " + f + ": 배율 " + zoom.ToString("0.000") + " (예상 " + expected.ToString("0.000") + ") 자리 (" + spot.X.ToString("0.0") + ", " + spot.Y.ToString("0.0") + ")");
                Assert.InRange(zoom, expected - 0.025, expected + 0.025);
                Assert.InRange(spot.X, 79.5, 80.5);
                Assert.InRange(spot.Y, 59.5, 60.5);
            }
            Assert.Equal(2, ShakeZoom.PunchZoomAt(effect, 1.4), 6);

            // 가운데 중심이면 표지는 가운데에서 멀어진다(p = c + 배율 × (s − c)), 배율 −면 커진 데서 빠져나온다
            VideoEffects.SetNumber(effect, "centerx", 50);
            VideoEffects.SetNumber(effect, "centery", 50);
            VideoEffects.SetNumber(effect, "zoom", -60);
            foreach (var f in new long[] { 3, 20, 40 })
            {
                var zoom = ShakeZoom.PunchZoomAt(effect, f / 30.0);
                var spot = Spot(Render(project, f, f + 1), 0);
                _out.WriteLine("빠져나오기 프레임 " + f + ": 자리 (" + spot.X.ToString("0.0") + ", " + spot.Y.ToString("0.0") + ") 예상 배율 " + zoom.ToString("0.000"));
                Assert.InRange(spot.X, 160 + zoom * (80 - 160) - 0.6, 160 + zoom * (80 - 160) + 0.6);
                Assert.InRange(spot.Y, 120 + zoom * (60 - 120) - 0.6, 120 + zoom * (60 - 120) + 0.6);
            }

            // 느린 줌(2초에 10%)도 프레임마다 조금씩 커진다 — 정수 픽셀 계단이면 같은 크기의 프레임이 이어진다
            VideoEffects.SetNumber(effect, "zoom", 10);
            VideoEffects.SetNumber(effect, "start", 0);
            VideoEffects.SetNumber(effect, "duration", 2);
            var slow = Render(project, 0, 36);
            var sizes = Enumerable.Range(0, 36).Select(f => Math.Sqrt(Spot(slow, f).Mass / plain)).ToList();
            _out.WriteLine("느린 줌 배율: " + string.Join(" ", sizes.Select(s => s.ToString("0.0000"))));
            for (var f = 1; f < sizes.Count; f++)
            {
                Assert.True(sizes[f] > sizes[f - 1], "프레임 " + f + "에서 커지지 않음: " + sizes[f - 1] + " → " + sizes[f]);
                Assert.InRange(sizes[f], ShakeZoom.PunchZoomAt(effect, f / 30.0) - 0.01, ShakeZoom.PunchZoomAt(effect, f / 30.0) + 0.01);
            }
        }

        [FfmpegFact]
        public void ZoomBounce_MarkerFollowsTheElasticCurve()
        {
            var project = NewProject();
            var clip = Add(project, project.MainTrack, Marker, 0);
            var effect = AddEffect(clip, ShakeZoom.ZoomBounce, ("zoom", 40), ("period", 1.2));
            var frames = Render(project, 0, 60);
            var plain = PlainMass(Marker);
            var peak = 0.0;
            for (var f = 0; f < 60; f++)
            {
                var expected = ShakeZoom.ZoomBounceAt(effect, f / 30.0);
                var zoom = Math.Sqrt(Spot(frames, f).Mass / plain);
                if (f % 4 == 0)
                    _out.WriteLine("프레임 " + f + ": 배율 " + zoom.ToString("0.000") + " (예상 " + expected.ToString("0.000") + ")");
                Assert.InRange(zoom, expected - 0.025, expected + 0.025);
                peak = Math.Max(peak, zoom);
            }
            // 넘쳤다 돌아온다(1.4배보다 더 커지는 때가 있다)
            Assert.True(peak > 1.43, "넘침 " + peak);
        }

        [FfmpegFact]
        public void Shakes_NeverShowEmptyOrSmearedEdges()
        {
            // 빨강에 3px 초록 테두리: 그림 밖을 읽으면 perspective가 가장자리(초록)를 늘여 초록이 테두리보다 깊이 보이고, 투명하면 검게 보인다
            foreach (var (def, values) in new[]
            {
                (ShakeZoom.CameraShake, new[] { ("horizontal", 60.0), ("vertical", 45.0), ("rotate", 5.0), ("speed", 3.0) }),
                (ShakeZoom.Earthquake, new[] { ("amount", 60.0), ("blur", 0.0) })
            })
            {
                var project = NewProject();
                var clip = Add(project, project.MainTrack, Ring, 0);
                var effect = AddEffect(clip, def, values);
                var zoom = (def == ShakeZoom.CameraShake ? ShakeZoom.CameraShakePose(effect, 0, Width, Height, 1) : ShakeZoom.EarthquakePose(effect, 0, Width, Height, 1)).Zoom;
                var frames = Render(project, 0, 45);
                var deepest = 0;
                for (var f = 0; f < 45; f++)
                {
                    bool Green(int x, int y) => Pixel(frames, x, y, f).G > 128 && Pixel(frames, x, y, f).R < 128;
                    int Depth(Func<int, (int X, int Y)> at, int limit)
                    {
                        var d = 0;
                        while (d < limit && Green(at(d).X, at(d).Y))
                            d++;
                        return d;
                    }
                    for (var y = Height / 4; y < Height * 3 / 4; y++)
                    {
                        deepest = Math.Max(deepest, Depth(d => (d, y), Width / 2));
                        deepest = Math.Max(deepest, Depth(d => (Width - 1 - d, y), Width / 2));
                    }
                    for (var x = Width / 4; x < Width * 3 / 4; x++)
                    {
                        deepest = Math.Max(deepest, Depth(d => (x, d), Height / 2));
                        deepest = Math.Max(deepest, Depth(d => (x, Height - 1 - d), Height / 2));
                    }
                    // 검은(빈) 픽셀이 없다
                    for (var i = 0; i < Width; i++)
                    {
                        foreach (var p in new[] { Pixel(frames, i, 0, f), Pixel(frames, i, Height - 1, f) })
                            Assert.True(p.R + p.G + p.B > 100, def.Name + " 프레임 " + f + " 빈 가장자리 " + p);
                    }
                }
                _out.WriteLine(def.Name + ": 가장자리 초록의 가장 깊은 곳 " + deepest + "px (테두리 3px × 배율 " + zoom.ToString("0.000") + ")");
                Assert.True(deepest <= Math.Ceiling(3 * zoom) + 2, def.Name + " 초록 " + deepest + "px — 그림 밖을 읽었습니다");
            }
        }

        [FfmpegFact]
        public void Fit_LetterboxStaysTransparent_PictureMovesInsideItsArea()
        {
            // 정사각 빨간 사진을 4:3 화면에 맞춤: 양옆 40px는 빈 곳 — 흔들고 키워도 아래 트랙(파랑)이 그대로 보이고, 그림 자리는 빨강으로 꽉 찬다
            foreach (var def in Family)
            {
                var project = NewProject();
                Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
                var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
                var red = Add(project, overlay, FfmpegMedia.RedImage, 0, 60);
                Assert.Equal(ClipFit.Fit, red.Fit);
                AddEffect(red, def, ("horizontal", 60), ("vertical", 45), ("rotate", 4), ("amount", 50), ("zoom", 60), ("start", 0.2));
                var frames = Render(project, 0, 30);
                for (var f = 0; f < 30; f += 3)
                {
                    for (var y = 0; y < Height; y += 7)
                    {
                        foreach (var x in new[] { 0, 20, 39, 280, 300, 319 })
                        {
                            var side = Pixel(frames, x, y, f);
                            Assert.True(side.B > 200 && side.R < 40, def.Name + " 프레임 " + f + " 빈 곳 (" + x + ", " + y + ") " + side);
                        }
                        foreach (var x in new[] { 40, 41, 100, 160, 220, 278, 279 })
                        {
                            var inside = Pixel(frames, x, y, f);
                            Assert.True(inside.R > 200 && inside.B < 40, def.Name + " 프레임 " + f + " 그림 (" + x + ", " + y + ") " + inside);
                        }
                    }
                }
            }
        }

        [FfmpegFact]
        public void SinglePreviewFrame_EqualsTheSameFrameOfAContinuousRender()
        {
            // 멈춘 화면(그 프레임부터 그림)·재생(앞에서부터 이어 그림)이 같은 프레임을 보여야 한다 — 시각을 프레임 번호로 재므로 앞선 초가 맞아야 한다
            foreach (var def in Family)
            {
                var project = NewProject(magnetic: false);
                var clip = Add(project, project.MainTrack, FfmpegMedia.TestVideo, 30);
                AddEffect(clip, def, ("start", 0.5));
                var continuous = Render(project, 30, 60);
                foreach (var frame in new long[] { 41, 50, 59 })
                {
                    var single = Render(project, frame, frame + 1);
                    long diff = 0;
                    var offset = (int)(frame - 30) * Width * Height * 4;
                    for (var i = 0; i < single.Length; i++)
                        diff += Math.Abs(single[i] - continuous[offset + i]);
                    var mean = (double)diff / single.Length;
                    _out.WriteLine(def.Name + " 프레임 " + frame + " 평균 차 " + mean.ToString("0.000"));
                    Assert.True(mean < 0.5, def.Name + " 프레임 " + frame + " 평균 차 " + mean);
                }
            }
        }

        [FfmpegFact]
        public void XfadeTransition_WithShakeAndZoom_Renders()
        {
            var project = NewProject();
            var first = Add(project, project.MainTrack, Marker, 0, 60);
            var second = Add(project, project.MainTrack, Ring, 999, 60);
            TimelineEdits.SetTransition(project, second, "WipeLeft", 30);
            AddEffect(first, ShakeZoom.Earthquake);
            AddEffect(second, ShakeZoom.PunchZoom);
            var frames = Render(project, 15, 105);
            // 전환(60~90) 뒤 프레임 100은 테두리 사진을 빠른 줌으로 키운 것 — 가장자리 초록이 밀려나 보이지 않는다
            var after = Pixel(frames, 2, 120, 85);
            Assert.True(after.R > 200 && after.G < 60, "키운 테두리 사진 " + after);
            // 전환 도중(프레임 75)에도 그림이 있다(앞 클립의 표지 바탕 또는 뒤 클립)
            Assert.True(Enumerable.Range(0, Width).All(x => Pixel(frames, x, 120, 60).R + Pixel(frames, x, 120, 60).G > 60));
        }

        [FfmpegFact]
        public async Task Playback_ThroughAnExpressionTransition_WithShakeAndZoom_DoesNotHang()
        {
            // 재생 그래프(수식 전환 묶음은 줄인 크기)로 전환 끝을 지나 이어 그린다 — perspective가 든 조각도 멈추지 않고 모든 프레임이 나와야 한다
            var project = NewProject();
            var blue = Add(project, project.MainTrack, FfmpegMedia.BlueVideo, 0);
            var marker = Add(project, project.MainTrack, Marker, 999, 60);
            TimelineEdits.SetTransition(project, marker, "Heart", 30);
            AddEffect(blue, ShakeZoom.CameraShake);
            AddEffect(blue, ShakeZoom.Heartbeat);
            AddEffect(marker, ShakeZoom.ZoomBounce);
            AddEffect(marker, ShakeZoom.Earthquake);
            // 640×360: 수식 전환 묶음의 재생 축소가 일어나는 크기(320×240은 축소 1이라 그 길을 타지 않는다)
            const int size = 640 * 360 * 4;
            for (var run = 0; run < 3; run++)
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
                    var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(30))) == reading;
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
            foreach (var def in Family)
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
            // 1080p 내보내기 그래프 2초(60장)를 -f null로: 효과 없이 / 효과마다(기본값) — 장당 늘어난 시간
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
            var line = "1080p 장당: 효과 없이 " + plain.ToString("0.0") + "ms";
            var worst = 0.0;
            foreach (var def in Family)
            {
                clip.Effects = new List<ClipEffect> { VideoEffects.Create(def) };
                var cost = Measure() - plain;
                worst = Math.Max(worst, cost);
                line += " · " + def.Name + " +" + cost.ToString("0.0");
            }
            _out.WriteLine(line);
            Assert.True(worst < FfmpegMedia.CostBudget, line);
        }
    }
}
