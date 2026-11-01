using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>#4 이동 경로: 경로 위 자리(직선·호·원·물결·사용자 경로), 시간식(지연·반복·되돌아오기·끝 자리), 저장 형식·규칙, 렌더 그래프 글(합치는 자리).</summary>
    public class MotionPathTests
    {
        private static ClipAnimation Path(AnimationEffect effect, long frames = 10, params double[] points)
        {
            var animation = ClipAnimations.Create(ClipAnimations.Def(effect), AnimationKind.Path, FrameRate.Default);
            animation.Duration = frames;
            animation.Ease = AnimationEase.Linear;
            if (points.Length > 0)
                animation.Points = Enumerable.Range(0, points.Length / 2).Select(i => new PathPoint(points[i * 2], points[i * 2 + 1])).ToList();
            return animation;
        }

        private static (Project Project, Clip Video) VideoProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 60);
            return (project, clip);
        }

        private static RenderOptions Options(long from = 0, long to = 60)
        {
            return new RenderOptions { From = from, To = to, Width = 640, Height = 360, Audio = false, TempFolder = System.IO.Path.GetTempPath() };
        }

        private static void Near((double X, double Y) expected, (double X, double Y) actual, double tolerance = 1e-9)
        {
            Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance, "기대 " + expected + ", 실제 " + actual);
        }

        // ---------- 경로 위 자리 ----------

        [Fact]
        public void EveryPath_StartsAtZero_EndsExactlyAtItsEndPoint()
        {
            foreach (var def in ClipAnimations.Paths)
            {
                var animation = Path(def.Effect);
                Assert.Equal(AnimationKind.Path, animation.Kind);
                Assert.Equal((0.0, 0.0), (animation.Points[0].X, animation.Points[0].Y));
                Assert.Equal((0.0, 0.0), MotionPaths.OffsetAt(animation, 0));
                // 끝은 끝점(원은 한 바퀴 돌아 처음 자리)에 정확히 — 1/1024 픽셀로 맞춰 삼각 함수 오차가 남지 않는다
                var end = def.Effect == AnimationEffect.PathCircle ? (0.0, 0.0) : (animation.Points[^1].X, animation.Points[^1].Y);
                Assert.Equal(end, MotionPaths.OffsetAt(animation, 1));
            }
        }

        [Fact]
        public void Shapes_ArcPassesTheMiddle_CircleReachesTheOpposite_WaveSwingsUpFirst()
        {
            // 호: 세 점을 지나는 원 — 반원(0,0)→(100,−100)→(200,0)이면 s = 0.5에 가운데 점
            var arc = Path(AnimationEffect.PathArc, 10, 0, 0, 100, -100, 200, 0);
            Near((100, -100), MotionPaths.OffsetAt(arc, 0.5), 1e-3);
            // 가운데 점을 아래로 옮기면 아래로 돈다
            arc.Points[1].Y = 100;
            Near((100, 100), MotionPaths.OffsetAt(arc, 0.5), 1e-3);
            // 세 점이 한 줄이면 직선
            arc.Points[1] = new PathPoint(50, 0);
            Near((100, 0), MotionPaths.OffsetAt(arc, 0.5), 1e-3);

            // 원: 반 바퀴에 맞은편, 시계 방향(화면)이면 위에서 오른쪽으로 돈다(가운데 (0,150) — 1/4 바퀴에 (150,150))
            var circle = Path(AnimationEffect.PathCircle, 10, 0, 0, 0, 300);
            Near((0, 300), MotionPaths.OffsetAt(circle, 0.5), 1e-3);
            Near((150, 150), MotionPaths.OffsetAt(circle, 0.25), 1e-3);
            circle.Direction = AnimationDirection.Left;
            Near((-150, 150), MotionPaths.OffsetAt(circle, 0.25), 1e-3);

            // 물결: 오른쪽으로 가면 위로 먼저(높이 60, 두 번 오르내림)
            var wave = Path(AnimationEffect.PathWave, 10, 0, 0, 400, 0);
            wave.Amount = 60;
            Near((50, -60), MotionPaths.OffsetAt(wave, 0.125), 1e-3);
            Near((200, 0), MotionPaths.OffsetAt(wave, 0.5), 1e-3);
            Near((350, 60), MotionPaths.OffsetAt(wave, 0.875), 1e-3);

            // 사용자 경로: 길이에 맞춰 일정한 빠르기(100 + 300 — 1/4에 첫 꺾인 점), 같은 자리 점은 건너뜀
            var custom = Path(AnimationEffect.PathCustom, 10, 0, 0, 100, 0, 100, 0, 100, 300);
            Near((100, 0), MotionPaths.OffsetAt(custom, 0.25), 1e-9);
            Near((100, 150), MotionPaths.OffsetAt(custom, 0.625), 1e-9);
            // 점선은 꺾이는 점만
            Assert.Equal(4, MotionPaths.Samples(custom, 64).Count);
            Assert.Equal(65, MotionPaths.Samples(wave, 64).Count);
        }

        [Fact]
        public void Time_DelayRepeatRoundTrip_HoldsTheEnd()
        {
            var (_, clip) = VideoProject();
            var line = Path(AnimationEffect.PathLine, 10, 0, 0, 400, 0);
            line.Delay = 5;
            clip.Animations = new List<ClipAnimation> { line };
            double X(double time) => AnimationMotion.ObjectMotion(clip, false).ShiftX.Eval(time);
            // 지연 동안 제자리, 한 번 길이 뒤 끝점에 남는다
            Assert.Equal(new[] { 0.0, 0, 200, 400, 400, 400 }, new double[] { 0, 5, 10, 15, 30, 59 }.Select(X));
            Assert.Null(AnimationMotion.ObjectMotion(clip, false).ShiftY);
            // 반복 2: 두 번째도 처음부터, 다 하면 끝점
            line.Repeat = 2;
            Assert.Equal(new[] { 200.0, 0, 360, 400, 400 }, new double[] { 10, 15, 24, 25, 50 }.Select(X));
            // 반복 0 = 클립 끝까지(60장 — 5장부터 열 장마다)
            line.Repeat = 0;
            Assert.Equal(new[] { 0.0, 200, 160 }, new double[] { 55, 50, 59 }.Select(X));
            // 되돌아오기: 앞 절반에 가고 뒤 절반에 돌아와 시작 자리에 남는다
            line.Repeat = 1;
            line.RoundTrip = true;
            Assert.Equal(new[] { 0.0, 400, 240, 0, 0 }, new double[] { 5, 10, 12, 15, 40 }.Select(X));
            // 이징은 한 번마다(서서히 시작·끝 — 반쯤은 가운데)
            line.RoundTrip = false;
            line.Ease = AnimationEase.InOut;
            Assert.Equal(200, X(10), 9);
            Assert.True(X(6) < 40);
        }

        [Fact]
        public void Expression_SameAsEval_ExactConstants_NoExponent()
        {
            var (_, clip) = VideoProject();
            var arc = Path(AnimationEffect.PathArc, 20, 0, 0, 137, -91, 311, 23);
            arc.Ease = AnimationEase.Elastic;
            arc.Delay = 3;
            clip.Animations = new List<ClipAnimation> { arc };
            var motion = AnimationMotion.ObjectMotion(clip, false);
            var text = motion.ShiftX.ToFfmpeg("t");
            // 반지름·각도는 17자리까지(6자리면 끝 자리가 1/1024 픽셀보다 어긋난다), 지수 표기는 쓰지 않는다
            Assert.Matches("[0-9]\\.[0-9]{12,}", text);
            Assert.DoesNotMatch("[0-9][eE][-+]?[0-9]", text);
            Assert.Contains("round(", text);
            Assert.Contains("cos(", text);
            // 끝 장(지연 + 길이)부터 끝점에 정확히
            Assert.Equal((311.0, 23.0), (motion.ShiftX.Eval(23), motion.ShiftY.Eval(23)));
            Assert.Equal((311.0, 23.0), (motion.ShiftX.Eval(59), motion.ShiftY.Eval(59)));
            // 사용자 경로의 마디 고르기도 식으로(if 사슬)
            clip.Animations = new List<ClipAnimation> { Path(AnimationEffect.PathCustom, 9, 0, 0, 100, 0, 100, 100, 0, 100) };
            var custom = AnimationMotion.ObjectMotion(clip, false);
            Assert.Contains("if(lt(", custom.ShiftX.ToFfmpeg("t"));
            Assert.Equal(new[] { 0.0, 100, 100, 0 }, new double[] { 0, 3, 6, 9 }.Select(f => custom.ShiftX.Eval(f)));
            Assert.Equal(new[] { 0.0, 0, 100, 100 }, new double[] { 0, 3, 6, 9 }.Select(f => custom.ShiftY.Eval(f)));
        }

        [Fact]
        public void PathAndShake_ShiftsAdd_WithOtherEffects()
        {
            var (_, clip) = VideoProject();
            var shake = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Shake), AnimationKind.Emphasis, FrameRate.Default);
            shake.Duration = 12;
            clip.Animations = new List<ClipAnimation> { shake };
            var alone = AnimationMotion.ObjectMotion(clip, false);
            var line = Path(AnimationEffect.PathLine, 10, 0, 0, 100, 50);
            clip.Animations = new List<ClipAnimation> { shake, line };
            var both = AnimationMotion.ObjectMotion(clip, false);
            foreach (var f in new double[] { 0, 3, 7, 10, 20 })
            {
                Assert.Equal(alone.ShiftX.Eval(f) + Math.Min(1, f / 10) * 100, both.ShiftX.Eval(f), 6);
                Assert.Equal(Math.Min(1, f / 10) * 50, both.ShiftY.Eval(f), 6);
            }
        }

        // ---------- 모델·저장 ----------

        [Fact]
        public void Clean_OnePathPerClip_PointsFromStart_CountsAndValues()
        {
            var (_, clip) = VideoProject();
            var custom = Path(AnimationEffect.PathCustom, 10);
            custom.Points = Enumerable.Range(0, 40).Select(i => new PathPoint(10 + i, 20)).ToList();
            custom.Points.Insert(1, new PathPoint(double.NaN, 0));
            custom.Points.Insert(2, null);
            custom.Unit = AnimationUnit.Character;
            custom.Stagger = 40;
            var arc = Path(AnimationEffect.PathArc, 10, 0, 0, 1, 1, 2, 0, 3, 3);
            var fade = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.In, FrameRate.Default);
            fade.Points = new List<PathPoint> { new PathPoint(1, 2) };
            fade.RoundTrip = true;
            clip.Animations = new List<ClipAnimation>
            {
                custom, arc, fade,
                // 분류가 맞지 않으면(경로 효과를 나타나기로, 나타나기 효과를 경로로) 빠진다
                new ClipAnimation { Kind = AnimationKind.In, Effect = AnimationEffect.PathLine, Duration = 5 },
                new ClipAnimation { Kind = AnimationKind.Path, Effect = AnimationEffect.Fly, Duration = 5 }
            };
            var clean = ClipAnimations.Clean(clip);
            Assert.Equal(new[] { AnimationKind.In, AnimationKind.Path }, clean.Select(a => a.Kind));
            Assert.Null(clean[0].Points);
            Assert.False(clean[0].RoundTrip);
            var path = clean[1];
            Assert.Equal(AnimationEffect.PathCustom, path.Effect);
            Assert.Equal((AnimationUnit.All, 0.0), (path.Unit, path.Stagger));
            // NaN·null 점은 빠지고 시작점이 (0,0)이 되게 옮긴다, 상한까지
            Assert.Equal(MotionPaths.MaxPoints, path.Points.Count);
            Assert.Equal((0.0, 0.0), (path.Points[0].X, path.Points[0].Y));
            Assert.Equal((1.0, 0.0), (path.Points[1].X, path.Points[1].Y));
            // 점 수가 정해진 경로: 넘치면 앞에서부터, 모자라면 기본 점
            clip.Animations = new List<ClipAnimation> { arc };
            Assert.Equal(3, ClipAnimations.Clean(clip)[0].Points.Count);
            arc.Points = new List<PathPoint> { new PathPoint(0, 0) };
            Assert.Equal(MotionPaths.DefaultPoints(AnimationEffect.PathArc).Select(p => (p.X, p.Y)), ClipAnimations.Clean(clip)[0].Points.Select(p => (p.X, p.Y)));
            arc.Points = new List<PathPoint> { new PathPoint(0, 0), new PathPoint(1e9, -1e9), new PathPoint(5, 5) };
            Assert.Equal((MotionPaths.MaxOffset, -MotionPaths.MaxOffset), (ClipAnimations.Clean(clip)[0].Points[1].X, ClipAnimations.Clean(clip)[0].Points[1].Y));
        }

        [Fact]
        public void Model_SetReplacesThePath_KeepsOrder_CloneIsDeep_SplitKeepsBoth()
        {
            var (project, clip) = VideoProject();
            var pulse = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Pulse), AnimationKind.Emphasis, FrameRate.Default);
            clip.Animations = new List<ClipAnimation> { pulse };
            ClipAnimations.Set(clip, AnimationKind.Path, Path(AnimationEffect.PathLine));
            ClipAnimations.Set(clip, AnimationKind.In, ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.In, FrameRate.Default));
            ClipAnimations.Set(clip, AnimationKind.Path, Path(AnimationEffect.PathWave));
            Assert.Equal(new[] { AnimationKind.In, AnimationKind.Emphasis, AnimationKind.Path }, clip.Animations.Select(a => a.Kind));
            Assert.Equal(AnimationEffect.PathWave, ClipAnimations.PathOf(clip).Effect);
            Assert.Equal("이동 경로 · 물결", ClipAnimations.Name(ClipAnimations.PathOf(clip)));
            // 경로 효과는 경로 분류에만
            Assert.Null(ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.PathLine), AnimationKind.In, FrameRate.Default));
            Assert.Null(ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.Path, FrameRate.Default));
            // 사본은 점까지 따로
            var copy = clip.CloneWithNewId();
            ClipAnimations.PathOf(copy).Points[1].X = 999;
            Assert.NotEqual(999, ClipAnimations.PathOf(clip).Points[1].X);
            // 나눠도 두 조각 모두 경로가 남는다
            var right = Assert.Single(TimelineEdits.Split(project, new[] { clip }, 30));
            Assert.NotNull(ClipAnimations.PathOf(clip));
            Assert.NotNull(ClipAnimations.PathOf(right));
            ClipAnimations.Set(clip, AnimationKind.Path, null);
            Assert.Null(ClipAnimations.PathOf(clip));
        }

        [Fact]
        public void Save_PathIsVersion4_PointsAndRoundTripRoundTrip()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fv-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = System.IO.Path.Combine(folder, "p.fvproj");
                var project = TestProjects.Empty(magnetic: false);
                var clip = TimelineEdits.CreateText("경로", 0, 60);
                TimelineEdits.Place(project, project.MainTrack, clip, 0);
                var arc = Path(AnimationEffect.PathArc, 45, 0, 0, 120, -80, 260, 10);
                arc.RoundTrip = true;
                arc.Repeat = 3;
                clip.Animations = new List<ClipAnimation> { arc };
                ProjectFile.Save(project, file);
                var json = JsonNode.Parse(File.ReadAllText(file));
                Assert.Equal(4, (int)json["Version"]);
                var item = json["VideoTracks"][0]["Clips"][0]["Animations"][0];
                Assert.Equal("Path", (string)item["Kind"]);
                Assert.Equal("PathArc", (string)item["Effect"]);
                Assert.True((bool)item["RoundTrip"]);
                Assert.Equal(120, (double)item["Points"][1]["X"]);
                var loaded = ClipAnimations.PathOf(ProjectFile.Load(file).MainTrack.Clips[0]);
                Assert.Equal((AnimationEffect.PathArc, 45L, 3, true), (loaded.Effect, loaded.Duration, loaded.Repeat, loaded.RoundTrip));
                Assert.Equal(new[] { (0.0, 0.0), (120.0, -80.0), (260.0, 10.0) }, loaded.Points.Select(p => (p.X, p.Y)));
                // 나타나기 효과에는 점·되돌아오기가 저장 글에 없다
                clip.Animations = new List<ClipAnimation> { ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.In, FrameRate.Default) };
                var text = ProjectFile.Snapshot(project);
                Assert.DoesNotContain("Points", text);
                Assert.DoesNotContain("RoundTrip", text);
                // 점이 목록이 아니면 그 경로만 빠진다(너그럽게 읽음)
                item["Points"] = "wrong";
                Assert.Null(ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0].Animations);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Nearest_FindsTheSegmentAndTheFoot()
        {
            var points = new List<PathPoint> { new PathPoint(0, 0), new PathPoint(100, 0), new PathPoint(100, 100) };
            var (segment, x, y, distance) = MotionPaths.Nearest(points, 50, 8);
            Assert.Equal((0, 50.0, 0.0, 8.0), (segment, x, y, distance));
            Assert.Equal(1, MotionPaths.Nearest(points, 104, 60).Segment);
            Assert.Equal(-1, MotionPaths.Nearest(new List<PathPoint> { new PathPoint(0, 0) }, 0, 0).Segment);
        }

        // ---------- #42: 경로 방향으로 돌기 · 부드러운 곡선 · 물결 수 ----------

        /// <summary>경로 위 자리 s에서 가운데 차분으로 잰 진행 방향(도).</summary>
        private static double MeasuredHeading(ClipAnimation path, double s, double step = 1e-3)
        {
            var (x0, y0) = MotionPaths.OffsetAt(path, s - step);
            var (x1, y1) = MotionPaths.OffsetAt(path, s + step);
            return Math.Atan2(y1 - y0, x1 - x0) * 180 / Math.PI;
        }

        /// <summary>두 각도(도)가 360의 배수만큼을 빼고 tolerance 안인지.</summary>
        private static void SameAngle(double expected, double actual, double tolerance, string what)
        {
            var diff = ((actual - expected) % 360 + 540) % 360 - 180;
            Assert.True(Math.Abs(diff) <= tolerance, what + ": 기대 " + expected.ToString("0.###") + "°, 실제 " + actual.ToString("0.###") + "°");
        }

        [Fact]
        public void Orient_HeadingFollowsTheTravel_LineCircleArcWaveCustomCurve()
        {
            // 직선: 한 각도(오른쪽 아래 45°), 상수
            var line = Path(AnimationEffect.PathLine, 10, 0, 0, 100, 100);
            Assert.Equal(45, MotionPaths.HeadingAt(line, 0.3), 9);
            // 원(시계 방향, 시작이 위): 처음은 오른쪽(0°), 1/4 바퀴에 아래(90°) — 반대로 돌면 처음은 왼쪽(180°)
            var circle = Path(AnimationEffect.PathCircle, 10, 0, 0, 0, 300);
            Assert.Equal(0, MotionPaths.HeadingAt(circle, 0), 9);
            SameAngle(90, MotionPaths.HeadingAt(circle, 0.25), 1e-9, "원 1/4");
            circle.Direction = AnimationDirection.Left;
            SameAngle(180, MotionPaths.HeadingAt(circle, 0), 1e-9, "반대로 도는 원");
            // 장마다 진행 방향(차분으로 잰 것)과 같다: 호·원·물결(세 번)·꺾은선·곡선
            var wave = Path(AnimationEffect.PathWave, 10, 0, 0, 600, -80);
            wave.Amount = 60;
            wave.Waves = 3;
            var custom = Path(AnimationEffect.PathCustom, 10, 0, 0, 150, -150, 300, 0, 450, -150, 500, 100);
            var curve = Path(AnimationEffect.PathCustom, 10, 0, 0, 150, -150, 300, 0, 450, -150, 500, 100);
            curve.Smooth = true;
            var paths = new[] { Path(AnimationEffect.PathArc, 10, 0, 0, 137, -91, 311, 23), circle, wave, custom, curve };
            foreach (var path in paths)
            {
                foreach (var s in new[] { 0.03, 0.17, 0.31, 0.49, 0.62, 0.77, 0.91 })
                {
                    // 꺾은선은 꺾이는 점 근처를 피한다(차분이 두 마디에 걸침)
                    var measured = MeasuredHeading(path, s);
                    SameAngle(measured, MotionPaths.HeadingAt(path, s), 0.5, path.Effect + (path.Smooth ? "(곡선)" : "") + " s=" + s);
                }
            }
        }

        [Fact]
        public void Orient_AddsToBaseAndRotateEffect_RoundTripRetracesWithoutFlip_GraphRotates()
        {
            var (project, clip) = VideoProject();
            var circle = Path(AnimationEffect.PathCircle, 20, 0, 0, 0, 200);
            circle.Delay = 4;
            clip.Animations = new List<ClipAnimation> { circle };
            // 켜지 않으면 회전이 없다(예전 그대로)
            Assert.Null(AnimationMotion.ObjectMotion(clip, false).Rotation);
            circle.Orient = true;
            var motion = AnimationMotion.ObjectMotion(clip, false);
            // 지연 동안은 시작 자리의 방향, 장마다 그 자리의 진행 방향, 다 돈 뒤는 끝 자리(원은 시작과 같음)
            foreach (var f in new double[] { 0, 4, 9, 14, 19, 24, 40 })
            {
                var s = Math.Max(0, Math.Min(1, (f - 4) / 20));
                SameAngle(MotionPaths.HeadingAt(circle, s), motion.Rotation.Eval(f), 1e-9, f + "장");
            }
            // 회전 효과와 더한다(합치는 곳 RotateChain: 기준 회전 + 효과)
            var rotate = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Rotate), AnimationKind.Emphasis, FrameRate.Default);
            rotate.Duration = 10;
            rotate.Amount = 90;
            rotate.Ease = AnimationEase.Linear;
            clip.Animations = new List<ClipAnimation> { rotate, circle };
            var both = AnimationMotion.ObjectMotion(clip, false);
            foreach (var f in new double[] { 0, 5, 9, 14, 30 })
                Assert.Equal(motion.Rotation.Eval(f) + 90 * Math.Min(1, f / 10), both.Rotation.Eval(f), 9);
            // 되돌아오기: 돌아오는 동안 간 움직임을 거꾸로 되짚는다(같은 자리 = 같은 각도, 뒤집지 않음)
            var line = Path(AnimationEffect.PathCustom, 20, 0, 0, 100, 0, 100, 100);
            line.Orient = true;
            line.RoundTrip = true;
            clip.Animations = new List<ClipAnimation> { line };
            var back = AnimationMotion.ObjectMotion(clip, false);
            Assert.Equal(0, back.Rotation.Eval(2), 9);
            Assert.Equal(90, back.Rotation.Eval(8), 9);
            Assert.Equal(90, back.Rotation.Eval(12), 9);
            Assert.Equal(0, back.Rotation.Eval(18), 9);

            // 그래프: 직선의 진행 방향은 한 각도라 rotate 각도 하나, 기준 회전(클립 30°)에 더한다
            clip.Rotation = 30;
            var straight = Path(AnimationEffect.PathLine, 10, 0, 0, 100, 100);
            straight.Orient = true;
            clip.Animations = new List<ClipAnimation> { straight };
            var filter = RenderGraph.Build(project, Options(20, 30)).Filter;
            Assert.Contains("rotate=a='75*PI/180'", filter);
            // 원은 장마다 바뀌는 각도 식
            clip.Rotation = 0;
            clip.Animations = new List<ClipAnimation> { circle };
            Assert.Matches("rotate=a='[^']*round\\(t\\*30/1\\)[^']*\\*PI/180'", RenderGraph.Build(project, Options()).Filter);
        }

        [Fact]
        public void SmoothCurve_PassesThePoints_EndsExactly_TwoPointsIsTheLine()
        {
            var (_, clip) = VideoProject();
            var points = new double[] { 0, 0, 137, -91, 311, 23, 377, 205, 120, 260 };
            var polyline = Path(AnimationEffect.PathCustom, 24, points);
            var curve = Path(AnimationEffect.PathCustom, 24, points);
            curve.Smooth = true;
            // 시작은 (0, 0), 끝 장은 끝점에 정확히(1/1024 픽셀로 맞춤)
            Assert.Equal((0.0, 0.0), MotionPaths.OffsetAt(curve, 0));
            Assert.Equal((120.0, 260.0), MotionPaths.OffsetAt(curve, 1));
            curve.Delay = 3;
            curve.Ease = AnimationEase.Elastic;
            clip.Animations = new List<ClipAnimation> { curve };
            var motion = AnimationMotion.ObjectMotion(clip, false);
            Assert.Equal((120.0, 260.0), (motion.ShiftX.Eval(27), motion.ShiftY.Eval(27)));
            Assert.Equal((120.0, 260.0), (motion.ShiftX.Eval(59), motion.ShiftY.Eval(59)));
            var text = motion.ShiftX.ToFfmpeg("t");
            Assert.Contains("if(lt(", text);
            Assert.DoesNotMatch("[0-9][eE][-+]?[0-9]", text);
            // 점선은 마디마다 16칸 — 찍은 점을 지난다
            var samples = MotionPaths.Samples(curve, 8);
            Assert.Equal(1 + 4 * 16, samples.Count);
            for (var k = 1; k <= 4; k++)
                Near((points[k * 2], points[k * 2 + 1]), samples[k * 16], 1e-9);
            // 곧은 선이 아니다(첫 마디 가운데가 꺾은선보다 바깥으로 부푼다)
            var mid = samples[8];
            Assert.True(Math.Abs(mid.X - 68.5) + Math.Abs(mid.Y + 45.5) > 3, "곡선 가운데 " + mid);
            // 점이 둘이면 곡선도 직선과 같은 식(같은 글)
            var two = Path(AnimationEffect.PathCustom, 10, 0, 0, 200, 40);
            clip.Animations = new List<ClipAnimation> { two };
            var straight = AnimationMotion.ObjectMotion(clip, false).ShiftX.ToFfmpeg("t");
            two.Smooth = true;
            Assert.Equal(straight, AnimationMotion.ObjectMotion(clip, false).ShiftX.ToFfmpeg("t"));
            // 부드러운 곡선을 끄면 꺾은선(다른 식)
            clip.Animations = new List<ClipAnimation> { polyline };
            Assert.NotEqual(text, AnimationMotion.ObjectMotion(clip, false).ShiftX.ToFfmpeg("t"));
        }

        [Fact]
        public void SmoothCurve_NearestFindsTheCurve_InsertsBetweenTheRightPoints()
        {
            var points = new List<PathPoint> { new PathPoint(0, 0), new PathPoint(100, -100), new PathPoint(200, 0), new PathPoint(300, -100) };
            var curve = Path(AnimationEffect.PathCustom, 10);
            curve.Points = points;
            curve.Smooth = true;
            // 둘째 마디 위의 한 자리: 곡선 위에서는 거의 0, 꺾은선에서는 떨어져 있다
            var on = MotionPaths.Samples(curve, 8)[16 + 5];
            var (segment, _, _, distance) = MotionPaths.Nearest(points, on.X, on.Y, true);
            Assert.Equal(1, segment);
            Assert.True(distance < 0.5, "곡선 위 거리 " + distance);
            Assert.True(MotionPaths.Nearest(points, on.X, on.Y).Distance > distance);
        }

        [Fact]
        public void WaveCount_SetsTheSwings_DefaultIsTwo_CleanClamps()
        {
            var wave = Path(AnimationEffect.PathWave, 10, 0, 0, 600, 0);
            wave.Amount = 60;
            // 정하지 않으면 두 번(예전과 같은 자리) — 첫 마루는 1/8
            Assert.Null(wave.Waves);
            Near((75, -60), MotionPaths.OffsetAt(wave, 0.125), 1e-3);
            var (_, clip) = VideoProject();
            clip.Animations = new List<ClipAnimation> { wave };
            var two = AnimationMotion.ObjectMotion(clip, false).ShiftY.ToFfmpeg("t");
            wave.Waves = 2;
            Assert.Equal(two, AnimationMotion.ObjectMotion(clip, false).ShiftY.ToFfmpeg("t"));
            // 세 번: 마루는 1/12, 5/12, 9/12, 골은 3/12 — 끝은 끝점
            wave.Waves = 3;
            Near((50, -60), MotionPaths.OffsetAt(wave, 1.0 / 12), 1e-3);
            Near((150, 60), MotionPaths.OffsetAt(wave, 3.0 / 12), 1e-3);
            Near((450, -60), MotionPaths.OffsetAt(wave, 9.0 / 12), 1e-3);
            Assert.Equal((600.0, 0.0), MotionPaths.OffsetAt(wave, 1));
            // 읽을 때 1~20으로, 물결이 아니면 버린다(곡선은 사용자 경로만, 돌기·곡선·물결 수는 경로가 아니면 버린다)
            wave.Waves = 0;
            Assert.Equal(1, ClipAnimations.Clean(clip)[0].Waves);
            wave.Waves = 99;
            Assert.Equal(MotionPaths.MaxWaves, ClipAnimations.Clean(clip)[0].Waves);
            var line = Path(AnimationEffect.PathLine, 10);
            line.Waves = 5;
            line.Smooth = true;
            line.Orient = true;
            var fade = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.In, FrameRate.Default);
            fade.Orient = true;
            fade.Smooth = true;
            fade.Waves = 3;
            clip.Animations = new List<ClipAnimation> { fade, line };
            var clean = ClipAnimations.Clean(clip);
            Assert.Equal((false, false, (int?)null), (clean[0].Orient, clean[0].Smooth, clean[0].Waves));
            Assert.Equal((true, false, (int?)null), (clean[1].Orient, clean[1].Smooth, clean[1].Waves));
        }

        [Fact]
        public void Save_NewPathFieldsOptional_ReleasedFormat4LoadsUnchanged()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateText("경로", 0, 60);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            var custom = Path(AnimationEffect.PathCustom, 30, 0, 0, 100, -50, 200, 0);
            clip.Animations = new List<ClipAnimation> { custom };
            // 새 값을 쓰지 않으면 저장 글에 새 키가 없다(v0.3.0이 쓴 글과 같다) — 형식은 4 그대로
            var plain = ProjectFile.Snapshot(project);
            Assert.DoesNotContain("\"Orient\"", plain);
            Assert.DoesNotContain("\"Smooth\"", plain);
            Assert.DoesNotContain("\"Waves\"", plain);
            // v0.3.0이 쓴 글(새 키 없음)을 읽으면 꺼진 값이고, 다시 쓰면 같은 글
            var loaded = ProjectFile.FromSnapshot(plain);
            var path = ClipAnimations.PathOf(loaded.MainTrack.Clips[0]);
            Assert.Equal((false, false, (int?)null), (path.Orient, path.Smooth, path.Waves));
            Assert.Equal(plain, ProjectFile.Snapshot(loaded));
            // 켜면 저장되고 읽힌다
            custom.Orient = true;
            custom.Smooth = true;
            var wave = Path(AnimationEffect.PathWave, 30, 0, 0, 300, 0);
            wave.Waves = 4;
            var other = TimelineEdits.CreateText("물결", 0, 60);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), other, 0);
            other.Animations = new List<ClipAnimation> { wave };
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            // 저장 형식은 4 그대로(새 값은 형식 4 안의 선택 키)
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fv-path42-" + Guid.NewGuid().ToString("N") + ".fvproj");
            try
            {
                ProjectFile.Save(project, file);
                Assert.Equal(Project.AnimationVersion, (int)JsonNode.Parse(File.ReadAllText(file))["Version"]);
                Assert.Equal(4, Project.AnimationVersion);
            }
            finally
            {
                File.Delete(file);
            }
            var back = ProjectFile.FromSnapshot(json.ToJsonString());
            var customBack = ClipAnimations.PathOf(back.AllClips.First(c => c.Text == "경로"));
            var waveBack = ClipAnimations.PathOf(back.AllClips.First(c => c.Text == "물결"));
            Assert.Equal((true, true), (customBack.Orient, customBack.Smooth));
            Assert.Equal(4, waveBack.Waves);
        }

        // ---------- 렌더 그래프 ----------

        [Fact]
        public void Graph_NoPath_SameOverlayText_PathAddsToTheBasePosition()
        {
            var (project, video) = VideoProject();
            video.X = -20.5;
            video.Y = 7;
            // 경로가 없으면 overlay 글이 예전 그대로(기준 위치 = N(X × k), 시험 프로젝트는 k = 1)
            var plain = RenderGraph.Build(project, Options()).Filter;
            Assert.Contains("overlay=x='(main_w-overlay_w)/2+-20.5':y='(main_h-overlay_h)/2+7':eof_action=pass", plain);
            Assert.DoesNotContain("round(t*", plain);
            // 경로: 기준 위치 뒤에 변위(프로젝트 픽셀 × k)를 더한다. 시각은 바탕 흐름의 장 번호 + 그리는 구간 시작(클립 안 15장)
            video.Animations = new List<ClipAnimation> { Path(AnimationEffect.PathLine, 30, 0, 0, 400, -100) };
            var filter = RenderGraph.Build(project, Options(15, 60)).Filter;
            var overlay = Regex.Match(filter, "overlay=x='([^']*)':y='([^']*)'");
            Assert.StartsWith("(main_w-overlay_w)/2+-20.5+", overlay.Groups[1].Value);
            Assert.StartsWith("(main_h-overlay_h)/2+7+", overlay.Groups[2].Value);
            Assert.Contains("(round(t*30/1)+15)", overlay.Groups[1].Value);
            Assert.Contains("round((", overlay.Groups[1].Value);
            // 움직이지 않는 다른 필터(회전·크기·불투명도)는 넣지 않는다
            Assert.DoesNotContain("eval=frame", filter);
            Assert.DoesNotContain("sendcmd", filter);
            Assert.DoesNotContain("rotate", filter);
        }
    }
}
