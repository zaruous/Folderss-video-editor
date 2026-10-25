using System;
using System.Collections.Generic;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>동영상 효과 "흔들림·줌"(카메라 흔들림·지진·하트비트·빠른 줌·줌 바운스): 목록·매개변수, 렌더 그래프 글, 움직임 계산.</summary>
    public class ShakeZoomTests
    {
        private static readonly VideoEffectDef[] Family = { ShakeZoom.CameraShake, ShakeZoom.Earthquake, ShakeZoom.Heartbeat, ShakeZoom.PunchZoom, ShakeZoom.ZoomBounce };

        private sealed class AllMasks : IPersonMaskProvider
        {
            public int Calls { get; private set; }

            public string Find(string sourcePath)
            {
                Calls++;
                return "C:/masks/any.mkv";
            }
        }

        private static RenderOptions Options(Project project, long from, long to, IPersonMaskProvider masks = null)
        {
            return new RenderOptions { From = from, To = to, Width = project.Width, Height = project.Height, Audio = false, Preview = true, TempFolder = "T:/tmp", PersonMasks = masks };
        }

        private static (Project Project, Clip Clip) OneClip(VideoEffectDef def, ClipFit fit = ClipFit.Fill, MediaInfo media = null)
        {
            var project = TestProjects.Empty();
            media ??= TestProjects.Video();
            project.Media.Add(media);
            var clip = TimelineEdits.CreateClip(media, project.Rate, 0, 60);
            clip.SourceOut = Math.Min(clip.SourceOut, 90);
            clip.Fit = fit;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            clip.Effects = new List<ClipEffect> { VideoEffects.Create(def) };
            return (project, clip);
        }

        /// <summary>그래프에서 이 클립의 체인(효과가 든 줄).</summary>
        private static string ClipChain(string filter)
        {
            return filter.Split(";\n").Single(c => c.Contains("perspective="));
        }

        private static int CountOf(string text, string part)
        {
            var count = 0;
            for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        // ---------- 목록·값 ----------

        [Fact]
        public void Catalog_HasTheFiveEffects_AfterScanLine_InOrder_WithTypedParams()
        {
            Assert.Same(ScanLine.Def, VideoEffects.All[0]);
            var ids = VideoEffects.All.Select(d => d.Id).ToList();
            var at = ids.IndexOf(ShakeZoom.CameraShakeId);
            Assert.True(at > 0);
            Assert.Equal(new[] { "camera-shake", "earthquake", "heartbeat", "punch-zoom", "zoom-bounce" }, ids.Skip(at).Take(5));
            Assert.Equal(new[] { "카메라 흔들림", "지진", "하트비트", "빠른 줌", "줌 바운스" }, Family.Select(d => d.Name));
            foreach (var def in Family)
            {
                Assert.Same(def, VideoEffects.Find(def.Id));
                Assert.Equal("흔들림·줌", def.Group);
                Assert.False(string.IsNullOrWhiteSpace(def.Description));
                Assert.DoesNotContain("\n", def.Description);
                Assert.True(VideoEffects.IsValidId(def.Id));
                Assert.Null(def.ColorParam);
                Assert.All(def.Params, p =>
                {
                    Assert.Equal(EffectParamKind.Number, p.Kind);
                    Assert.InRange(p.Default, p.Min, p.Max);
                    Assert.False(string.IsNullOrWhiteSpace(p.Name));
                    Assert.False(string.IsNullOrWhiteSpace(p.Unit));
                    Assert.False(string.IsNullOrWhiteSpace(p.Hint));
                });
                // 썸네일 값은 매개변수 범위 안의 아는 키
                Assert.NotEmpty(def.PreviewValues);
                Assert.All(def.PreviewValues, v => Assert.InRange(v.Value, def.Param(v.Key).Min, def.Param(v.Key).Max));
                // 그림 전체를 움직이는 효과라 사람에게만은 없다
                Assert.False(def.AllowsPersonOnly);
            }
            Assert.True(ScanLine.Def.AllowsPersonOnly);
            Assert.Equal(new[] { "horizontal", "vertical", "rotate", "speed" }, ShakeZoom.CameraShake.Params.Select(p => p.Key));
            Assert.Equal(new[] { "amount", "speed", "blur" }, ShakeZoom.Earthquake.Params.Select(p => p.Key));
            Assert.Equal(new[] { "amount", "bpm", "second", "centerx", "centery" }, ShakeZoom.Heartbeat.Params.Select(p => p.Key));
            Assert.Equal(new[] { "zoom", "duration", "start", "centerx", "centery" }, ShakeZoom.PunchZoom.Params.Select(p => p.Key));
            Assert.Equal(new[] { "zoom", "period", "elastic", "repeat", "centerx", "centery" }, ShakeZoom.ZoomBounce.Params.Select(p => p.Key));
            Assert.Equal(VideoEffects.All.Count, ids.Distinct().Count());
        }

        [Fact]
        public void PersonOnly_IsTurnedOffOnLoad_AndNeverReadsAMask()
        {
            var (project, clip) = OneClip(ShakeZoom.CameraShake);
            clip.Effects[0].PersonOnly = true;
            clip.Effects.Add(VideoEffects.Create(ScanLine.Def));
            clip.Effects[1].PersonOnly = true;
            // 열 때: 흔들림의 사람에게만은 끄고, 스캔 라인 것은 그대로
            var loaded = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project)).FindClip(clip.Id).Effects;
            Assert.False(loaded[0].PersonOnly);
            Assert.True(loaded[1].PersonOnly);
            // 메모리에서 켜져 있어도 그릴 때 무시한다 — 마스크는 스캔 라인 몫 하나만
            var plan = RenderGraph.Build(project, Options(project, 0, 30, new AllMasks()));
            Assert.Equal(2, plan.Inputs.Count);
            Assert.DoesNotContain("split=2[fx0m0][fx0m1]", plan.Filter);
            Assert.Contains("[fx0e1l][fx0m0]blend=all_mode=multiply", plan.Filter);
            // 흔들림만 있으면 마스크를 찾지도 않는다
            clip.Effects.RemoveAt(1);
            var masks = new AllMasks();
            plan = RenderGraph.Build(project, Options(project, 0, 30, masks));
            Assert.Single(plan.Inputs);
            Assert.Equal(0, masks.Calls);
        }

        // ---------- 렌더 그래프 ----------

        [Fact]
        public void Graph_EachEffectIsOnePerspective_NoSecondScale_NoForbiddenFunctions()
        {
            foreach (var def in Family)
            {
                var (project, clip) = OneClip(def);
                var filter = RenderGraph.Build(project, Options(project, 0, 60)).Filter;
                var chain = ClipChain(filter);
                // 틀 맞춤의 scale 하나뿐(효과는 scale을 잇지 않음), perspective 하나가 프레임마다 수식을 푼다
                Assert.Equal(1, CountOf(chain, "scale="));
                Assert.Equal(1, CountOf(chain, "perspective="));
                Assert.Contains(":eval=frame", chain);
                Assert.Contains("crop=640:360,format=gbrap,perspective=", chain);
                // 효과 뒤는 구간 길이로 자르고(효과 사슬 끝) 예전처럼 시각 맞춤(같은 흐름에 이어 붙음 — 라벨로 끊지 않음)
                Assert.Matches(":eval=frame(,convolution=[^,]+)?,trim=end_frame=\\d+,setpts=PTS-STARTPTS", chain);
                foreach (var forbidden in new[] { "st(", "ld(", "random(", "time(", "scale" + "2ref", "zoompan" })
                    Assert.DoesNotContain(forbidden, chain);
                // 시각은 프레임 번호로(perspective에는 t가 없다)
                Assert.Contains("((in-1)/30+0)", chain);
                // 수식 따옴표는 짝이 맞는다(모서리 8개)
                var start = chain.IndexOf("perspective=", StringComparison.Ordinal);
                Assert.Equal(16, CountOf(chain.Substring(start, chain.IndexOf(":eval=frame", StringComparison.Ordinal) - start), "'"));
            }
        }

        [Fact]
        public void Graph_TimeIsClipLocal_ForSeekAndPlayback()
        {
            var (project, clip) = OneClip(ShakeZoom.Heartbeat);
            // 클립 안 2초(프레임 60)부터 한 장: 흐름의 첫 프레임(in = 1)이 클립 안 2초
            Assert.Contains("mod(((in-1)/30+2),0.75)", RenderGraph.Build(project, Options(project, 60, 61)).Filter);
            Assert.Contains("mod(((in-1)/30+1.5),0.75)", RenderGraph.Build(project, Options(project, 45, 80)).Filter);
            // 23.976fps 프로젝트에서도 프레임 길이 그대로
            project.RateNumerator = 24000;
            project.RateDenominator = 1001;
            Assert.Contains("((in-1)/23.976024+", RenderGraph.Build(project, Options(project, 0, 10)).Filter);
        }

        [Fact]
        public void Graph_Fit_MovesOnlyThePicture_LetterboxStaysTransparent()
        {
            // 4:3(800×600) 사진을 16:9(640×360) 틀에 맞춤 → 그림 480×360, 양옆 80px 빈 곳
            var (project, clip) = OneClip(ShakeZoom.CameraShake, ClipFit.Fit, TestProjects.Image());
            var chain = ClipChain(RenderGraph.Build(project, Options(project, 0, 60)).Filter);
            Assert.Contains("format=gbrap,pad=640:360:(ow-iw)/2:(oh-ih)/2:color=black@0,crop=480:360:80:0,perspective=", chain);
            Assert.Contains(":eval=frame,pad=640:360:80:0:color=black@0", chain);
            Assert.Equal(1, CountOf(chain, "scale="));
            // 틀 크기는 그림을 따라 줄어든 크기로(흔들림 배율도 그림 크기로 잰다)
            var (x, y, angle) = ShakeZoom.CameraShakeReach(clip.Effects[0], 480, 360, 1);
            Assert.Contains("/(" + RenderGraph.N(ShakeZoom.CoverZoom(480, 360, x, y, angle)) + ")", chain);
            // 홀수 차이는 pad처럼 버림: 798×600 → 479×360, 왼쪽 (640 − 479) ÷ 2 = 80.5 → 80
            project.Media[0].Width = 798;
            Assert.Contains("crop=479:360:80:0", ClipChain(RenderGraph.Build(project, Options(project, 0, 60)).Filter));
            // 원본 크기를 모르면 틀 전체를 움직인다(빈 곳은 투명한 가장자리가 늘어나 그대로 투명)
            project.Media.Clear();
            var whole = ClipChain(RenderGraph.Build(project, Options(project, 0, 60)).Filter);
            Assert.DoesNotContain("crop=", whole);
            Assert.Contains("color=black@0,perspective=", whole);
            // 채우기·늘이기·흐린 배경은 틀 전체가 그림
            foreach (var fit in new[] { ClipFit.Fill, ClipFit.Stretch, ClipFit.Blur })
            {
                var (p2, _) = OneClip(ShakeZoom.ZoomBounce, fit, TestProjects.Image());
                var other = ClipChain(RenderGraph.Build(p2, Options(p2, 0, 60)).Filter);
                Assert.DoesNotContain(",pad=", other);
                Assert.DoesNotContain("crop=480", other);
            }
        }

        [Fact]
        public void Graph_Earthquake_BlurIsOptional_AndFollowsTheShake()
        {
            var (project, clip) = OneClip(ShakeZoom.Earthquake);
            var effect = clip.Effects[0];
            // 기본값(세기 30px·흐림 40%): 가로 이웃 무게 round(8 × 0.4) = 3, 세로(24px) 3 — 가로 [3 8 3] × 세로 [3 8 3], R·G·B만
            Assert.Equal((3, 3), ShakeZoom.EarthquakeBlur(effect, 640, 360, 1));
            var filter = RenderGraph.Build(project, Options(project, 0, 30)).Filter;
            Assert.Contains(":eval=frame,convolution=0m='9 24 9 24 64 24 9 24 9':1m='9 24 9 24 64 24 9 24 9':2m='9 24 9 24 64 24 9 24 9',trim=end_frame=", filter);
            Assert.DoesNotContain("3m=", filter);
            Assert.DoesNotContain("avgblur", filter);
            VideoEffects.SetNumber(effect, "blur", 0);
            Assert.DoesNotContain("convolution", RenderGraph.Build(project, Options(project, 0, 30)).Filter);
            Assert.Equal((0, 0), ShakeZoom.EarthquakeBlur(effect, 640, 360, 1));
            // 세면 더 흐리고(이웃 무게 8까지 — 세 칸 고르게), 줄인 재생 크기·작은 흔들림에서는 그만큼 약하게
            VideoEffects.SetNumber(effect, "blur", 100);
            VideoEffects.SetNumber(effect, "amount", 200);
            Assert.Equal((8, 8), ShakeZoom.EarthquakeBlur(effect, 1920, 1080, 1));
            VideoEffects.SetNumber(effect, "amount", 10);
            Assert.Equal((4, 3), ShakeZoom.EarthquakeBlur(effect, 1920, 1080, 1));
            Assert.Equal((1, 1), ShakeZoom.EarthquakeBlur(effect, 480, 270, 0.25));
        }

        [Fact]
        public void Graph_ShakeSizesFollowTheBox_ClipScale()
        {
            var (project, clip) = OneClip(ShakeZoom.CameraShake);
            clip.Scale = 0.5;
            var chain = ClipChain(RenderGraph.Build(project, Options(project, 0, 30)).Filter);
            // 크기 50% 클립: 틀 320×180, 흔드는 거리도 절반
            var (x, _, _) = ShakeZoom.CameraShakeReach(clip.Effects[0], 320, 180, 0.5);
            Assert.Equal(9, x, 6);
            Assert.Contains("x0='159.5+", chain);
            Assert.Contains("+(9*((", chain);
        }

        // ---------- 움직임 계산 ----------

        /// <summary>출력 모서리 네 곳이 읽는 원본 자리(그림 픽셀, 가운데 기준).</summary>
        private static IEnumerable<(double X, double Y)> Corners(ShakeZoom.Pose pose, double width, double height)
        {
            double cx = width / 2, cy = height / 2;
            foreach (var (qx, qy) in new[] { (0.0, 0.0), (width, 0.0), (0.0, height), (width, height) })
            {
                double ax = qx - cx, ay = qy - cy;
                yield return (cx + (ax * Math.Cos(pose.Angle) - ay * Math.Sin(pose.Angle)) / pose.Zoom + pose.X,
                    cy + (ax * Math.Sin(pose.Angle) + ay * Math.Cos(pose.Angle)) / pose.Zoom + pose.Y);
            }
        }

        [Fact]
        public void Shakes_NeverReadOutsideThePicture_AndMoveSmoothlyOrJerkily()
        {
            var shake = VideoEffects.Create(ShakeZoom.CameraShake);
            var quake = VideoEffects.Create(ShakeZoom.Earthquake);
            foreach (var (w, h) in new[] { (1920, 1080), (320, 240), (1080, 1920), (64, 36) })
            {
                foreach (var strong in new[] { false, true })
                {
                    if (strong)
                    {
                        foreach (var key in new[] { "horizontal", "vertical", "rotate", "speed" })
                            VideoEffects.SetNumber(shake, key, 1e9);
                        VideoEffects.SetNumber(quake, "amount", 1e9);
                        VideoEffects.SetNumber(quake, "speed", 1e9);
                    }
                    for (var t = 0.0; t < 20; t += 0.013)
                    {
                        foreach (var pose in new[] { ShakeZoom.CameraShakePose(shake, t, w, h, 1), ShakeZoom.EarthquakePose(quake, t, w, h, 1) })
                        {
                            Assert.True(pose.Zoom >= 1);
                            foreach (var (x, y) in Corners(pose, w, h))
                            {
                                Assert.InRange(x, -1e-6, w + 1e-6);
                                Assert.InRange(y, -1e-6, h + 1e-6);
                            }
                        }
                    }
                }
                shake = VideoEffects.Create(ShakeZoom.CameraShake);
                quake = VideoEffects.Create(ShakeZoom.Earthquake);
            }
            // 카메라 흔들림은 부드럽다(한 프레임에 조금씩), 지진은 홱홱(어떤 프레임에서 크게 뛴다)
            double MaxStep(Func<double, double> at) => Enumerable.Range(0, 300).Max(f => Math.Abs(at((f + 1) / 30.0) - at(f / 30.0)));
            var smooth = MaxStep(t => ShakeZoom.CameraShakePose(shake, t, 1920, 1080, 1).X);
            var jerky = MaxStep(t => ShakeZoom.EarthquakePose(quake, t, 1920, 1080, 1).X);
            Assert.True(smooth < 0.3 * 18, "카메라 흔들림 한 프레임 " + smooth);
            Assert.True(jerky > 0.5 * 30, "지진 한 프레임 " + jerky);
            // 흔드는 거리는 설정까지 닿는다(한 바퀴 안에 80% 넘게)
            Assert.True(Enumerable.Range(0, 600).Max(f => Math.Abs(ShakeZoom.CameraShakePose(shake, f / 30.0, 1920, 1080, 1).X)) > 0.8 * 18);
            // 같은 시각이면 같은 자리(무작위가 아님)
            Assert.Equal(ShakeZoom.EarthquakePose(quake, 3.21, 1920, 1080, 1).X, ShakeZoom.EarthquakePose(quake, 3.21, 1920, 1080, 1).X);
            // 빠르기를 두 배로 하면 같은 움직임이 두 배 빨리
            var fast = VideoEffects.Create(ShakeZoom.CameraShake);
            VideoEffects.SetNumber(fast, "speed", 2);
            Assert.Equal(ShakeZoom.CameraShakePose(shake, 1.4, 1920, 1080, 1).Y, ShakeZoom.CameraShakePose(fast, 0.7, 1920, 1080, 1).Y, 9);
        }

        [Fact]
        public void Heartbeat_PulsesOnTheBeat_SettlesBeforeTheNext()
        {
            var effect = VideoEffects.Create(ShakeZoom.Heartbeat);
            // 80bpm = 0.75초마다. 박의 처음엔 1배, 곧 1 + 세기(12%), 두 번째 박동(0.22초 뒤)은 작게
            Assert.Equal(1, ShakeZoom.HeartbeatZoom(effect, 0), 3);
            Assert.Equal(1.12, ShakeZoom.HeartbeatZoom(effect, 0.07), 3);
            var second = ShakeZoom.HeartbeatZoom(effect, 0.22 + 0.07);
            Assert.InRange(second, 1.06, 1.1);
            Assert.True(ShakeZoom.HeartbeatZoom(effect, 0.2) < second);
            Assert.Equal(1, ShakeZoom.HeartbeatZoom(effect, 0.749), 2);
            Assert.Equal(ShakeZoom.HeartbeatZoom(effect, 0.1), ShakeZoom.HeartbeatZoom(effect, 0.1 + 0.75 * 7), 6);
            // 두 번째 박동 0이면 한 번만
            VideoEffects.SetNumber(effect, "second", 0);
            Assert.True(ShakeZoom.HeartbeatZoom(effect, 0.29) < 1.02);
            // 빠른 박자(200bpm)에서도 다음 박 전에 가라앉고, 1배 아래로 가지 않는다
            VideoEffects.SetNumber(effect, "bpm", 200);
            VideoEffects.SetNumber(effect, "second", 100);
            for (var t = 0.0; t < 3; t += 0.004)
                Assert.True(ShakeZoom.HeartbeatZoom(effect, t) >= 1);
            Assert.True(ShakeZoom.HeartbeatZoom(effect, 0.299) - 1 < 0.01 * 0.12 * 2);
        }

        [Fact]
        public void PunchZoom_ZoomsQuicklyAtTheStart_AndHolds_NegativeZoomsOut()
        {
            var effect = VideoEffects.Create(ShakeZoom.PunchZoom);
            VideoEffects.SetNumber(effect, "start", 1);
            Assert.Equal(1, ShakeZoom.PunchZoomAt(effect, 0.5), 6);
            Assert.Equal(1, ShakeZoom.PunchZoomAt(effect, 1), 6);
            // 빨리 시작해 천천히 멈춘다(길이 0.3초의 절반에 이미 90% 넘게)
            Assert.True(ShakeZoom.PunchZoomAt(effect, 1.15) > 1 + 0.4 * 0.9);
            Assert.Equal(1.4, ShakeZoom.PunchZoomAt(effect, 1.3), 6);
            Assert.Equal(1.4, ShakeZoom.PunchZoomAt(effect, 9), 6);
            VideoEffects.SetNumber(effect, "zoom", -50);
            Assert.Equal(1.5, ShakeZoom.PunchZoomAt(effect, 0.9), 6);
            Assert.Equal(1, ShakeZoom.PunchZoomAt(effect, 2), 6);
            // 중심은 그림 안 어디든(그림 밖을 읽지 않는다 — 중심을 두고 키우면 읽는 자리는 그림 안)
            VideoEffects.SetNumber(effect, "centerx", 0);
            VideoEffects.SetNumber(effect, "centery", 100);
            Assert.Equal((0.0, 360.0), ShakeZoom.PunchZoomCenter(effect, 640, 360));
        }

        [Fact]
        public void HeartbeatAndZoomBounce_HaveACenter_DefaultIsTheMiddle_OldProjectsOpenTheSame()
        {
            foreach (var def in new[] { ShakeZoom.Heartbeat, ShakeZoom.ZoomBounce, ShakeZoom.PunchZoom })
            {
                // 예전 프로젝트(중심 값 없음)는 가운데 — 지금과 같게
                var old = new ClipEffect { Id = def.Id, Values = new Dictionary<string, double>() };
                Assert.Equal((320.0, 180.0), ShakeZoom.ZoomCenter(old, def, 640, 360));
                var effect = VideoEffects.Create(def);
                VideoEffects.SetNumber(effect, "centerx", 25);
                VideoEffects.SetNumber(effect, "centery", 100);
                Assert.Equal((160.0, 360.0), ShakeZoom.ZoomCenter(effect, def, 640, 360));
                Assert.Equal("%", def.Param("centerx").Unit);
                Assert.Equal(50, def.Param("centery").Default);
            }
        }

        [Fact]
        public void ZoomBounce_OvershootsIn_BouncesOffOne_Loops_OrHolds()
        {
            var effect = VideoEffects.Create(ShakeZoom.ZoomBounce);
            // 1.6초 주기: 0~0.8초 커지고 0.8~1.6초 돌아온다
            Assert.Equal(1, ShakeZoom.ZoomBounceAt(effect, 0), 6);
            Assert.Equal(1.25, ShakeZoom.ZoomBounceAt(effect, 0.8), 6);
            Assert.Equal(1, ShakeZoom.ZoomBounceAt(effect, 1.6), 6);
            var peak = Enumerable.Range(0, 80).Max(i => ShakeZoom.ZoomBounceAt(effect, i / 100.0));
            Assert.True(peak > 1.25 + 0.25 * 0.08, "넘침 " + peak);
            // 1배 아래로는 가지 않고, 이어진다(주기의 반·끝에서 끊기지 않음)
            for (var t = 0.0; t < 5; t += 0.002)
            {
                Assert.True(ShakeZoom.ZoomBounceAt(effect, t) >= 1 - 1e-9);
                Assert.True(Math.Abs(ShakeZoom.ZoomBounceAt(effect, t + 0.002) - ShakeZoom.ZoomBounceAt(effect, t)) < 0.02, "t " + t);
            }
            Assert.Equal(ShakeZoom.ZoomBounceAt(effect, 0.3), ShakeZoom.ZoomBounceAt(effect, 0.3 + 1.6 * 3), 6);
            // 탄성 0이면 넘치지 않는다(거의)
            VideoEffects.SetNumber(effect, "elastic", 0);
            Assert.True(Enumerable.Range(0, 80).Max(i => ShakeZoom.ZoomBounceAt(effect, i / 100.0)) < 1.25 + 0.25 * 0.01);
            // 반복 1: 한 번 커져 머문다 / 반복 2: 한 번 되풀이한 뒤 커져 머문다
            VideoEffects.SetNumber(effect, "repeat", 1);
            Assert.Equal(1.25, ShakeZoom.ZoomBounceAt(effect, 1.2), 6);
            Assert.Equal(1.25, ShakeZoom.ZoomBounceAt(effect, 30), 6);
            VideoEffects.SetNumber(effect, "repeat", 2.7);
            Assert.Equal(1, ShakeZoom.ZoomBounceAt(effect, 1.6), 6);
            Assert.Equal(1.25, ShakeZoom.ZoomBounceAt(effect, 2.4), 6);
            Assert.Equal(1.25, ShakeZoom.ZoomBounceAt(effect, 10), 6);
            // 탄성 곡선은 0에서 0, 1에서 1, 0 아래로 가지 않는다
            foreach (var elastic in new[] { 0, 0.5, 1 })
            {
                Assert.Equal(0, ShakeZoom.Elastic(0, elastic), 9);
                Assert.Equal(1, ShakeZoom.Elastic(1, elastic), 9);
                Assert.All(Enumerable.Range(0, 101), i => Assert.True(ShakeZoom.Elastic(i / 100.0, elastic) >= 0));
            }
        }

        [Fact]
        public void PreviewArguments_UseTheSameRecipe()
        {
            foreach (var def in Family)
            {
                var args = VideoEffects.PreviewArguments(def);
                var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
                Assert.StartsWith("[0]format=gbrap,perspective=", graph);
                Assert.Contains("((in-1)/10+0)", graph);
                Assert.EndsWith(",format=rgb24,tile=15x1", graph);
                Assert.Equal(24, VideoEffects.PreviewKey(def).Length);
            }
            Assert.Equal(VideoEffects.All.Count, VideoEffects.All.Select(VideoEffects.PreviewKey).Distinct().Count());
        }
    }
}
