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
