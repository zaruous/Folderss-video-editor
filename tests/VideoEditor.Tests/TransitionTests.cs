using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>전환 효과: 디졸브가 아닌 효과는 xfade로 잇고, 타임라인 위치는 그대로다.</summary>
    public class TransitionTests
    {
        /// <summary>a.mp4 [0, 60) → b.mp4 [60, 120) — b 앞에 kind 전환 15프레임(0.5초).</summary>
        private static (Project Project, Clip A, Clip B) TwoClips(string kind, long frames = 15)
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60, "a.mp4");
            var b = TestProjects.VideoClip(0, 60, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            TimelineEdits.SetTransition(project, b, kind, frames);
            return (project, a, b);
        }

        private static RenderPlan Build(Project project, long from, long to)
        {
            return RenderGraph.Build(project, new RenderOptions { From = from, To = to, Width = 640, Height = 360, Audio = false });
        }

        private static int Count(string text, string part)
        {
            return Regex.Matches(text, Regex.Escape(part)).Count;
        }

        // ---------- 그래프 ----------

        [Fact]
        public void Wipe_UsesXfadeBetweenBakedCanvases_AndKeepsTimeline()
        {
            var (project, a, b) = TwoClips("WipeLeft");
            var plan = Build(project, 0, 120);
            // 타임라인 위치는 그대로, 앞 클립만 전환 길이만큼 더 읽는다(꼬리)
            Assert.Equal(60, b.Start);
            Assert.Equal(new[] { "-ss", "0", "-t", "2.5", "-i", "a.mp4" }, plan.Inputs[0]);
            // 두 클립을 캔버스 크기로 제자리에 얹은 뒤 묶음에서 xfade(묶음은 0초에 시작하므로 offset = b 시작 2초)
            Assert.Contains("format=gbrap,premultiply=inplace=1[k0]", plan.Filter);
            Assert.Contains("[k0][k1]xfade=transition=wipeleft:duration=0.5:offset=2[x1]", plan.Filter);
            Assert.Contains("[x1]unpremultiply=inplace=1,format=rgba,setpts=PTS-STARTPTS+round(0/TB)[c2]", plan.Filter);
            Assert.Contains("[base][c2]overlay=x=0:y=0:eof_action=pass[o2]", plan.Filter);
            // 디졸브의 알파 페이드는 없다
            Assert.DoesNotContain("fade=t=in", plan.Filter);
        }

        [Fact]
        public void WindowInsideTransition_StartsBothAtTransitionStart()
        {
            var (project, _, _) = TwoClips("SlideUp");
            var plan = Build(project, 65, 66);
            // a는 전환이 시작하는 60프레임(원본 2초)부터 꼬리 끝까지, b는 자기 처음부터
            Assert.Equal(new[] { "-ss", "2", "-t", "0.5", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Equal(new[] { "-ss", "0", "-t", "0.2", "-i", "b.mp4" }, plan.Inputs[1]);
            Assert.Contains("xfade=transition=slideup:duration=0.5:offset=0[x1]", plan.Filter);
            // 묶음은 그리는 구간보다 5프레임 앞에서 시작한다(앞부분은 바탕 시작 전이라 버려짐)
            Assert.Contains("setpts=PTS-STARTPTS+round(-0.166667/TB)[c2]", plan.Filter);
        }

        [Fact]
        public void WindowOutsideTransition_HasNoXfade()
        {
            var (project, _, _) = TwoClips("WipeLeft");
            var before = Build(project, 10, 11);
            Assert.DoesNotContain("xfade", before.Filter);
            Assert.Single(before.Inputs);
            var after = Build(project, 100, 101);
            Assert.DoesNotContain("xfade", after.Filter);
            Assert.Single(after.Inputs);
        }

        [Fact]
        public void FirstClip_TransitionsFromTransparent()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60, "a.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.SetTransition(project, a, "CircleOpen", 15);
            var plan = Build(project, 0, 60);
            Assert.Contains("color=c=black@0.0:s=640x360:r=30/1:d=0.5,format=gbrap[t0]", plan.Filter);
            Assert.Contains("[t0][k0]xfade=transition=circleopen:duration=0.5:offset=0[x0]", plan.Filter);
        }

        [Fact]
        public void ChainedTransitions_FormOneGroup()
        {
            var (project, _, b) = TwoClips("WipeRight");
            var c = TestProjects.VideoClip(0, 60, "c.mp4");
            TimelineEdits.Place(project, project.MainTrack, c, 999);
            TimelineEdits.SetTransition(project, c, "ZoomIn", 30);
            var plan = Build(project, 0, 180);
            Assert.Contains("[k0][k1]xfade=transition=wiperight:duration=0.5:offset=2[x1]", plan.Filter);
            Assert.Contains("[x1][k2]xfade=transition=zoomin:duration=1:offset=4[x2]", plan.Filter);
            Assert.Equal(1, Count(plan.Filter, "overlay=x=0:y=0"));
        }

        [Fact]
        public void DissolveThenWipe_MixesAlphaFadeAndXfade()
        {
            var (project, _, b) = TwoClips("Fade");
            var c = TestProjects.VideoClip(0, 60, "c.mp4");
            TimelineEdits.Place(project, project.MainTrack, c, 999);
            TimelineEdits.SetTransition(project, c, "WipeLeft", 15);
            var plan = Build(project, 0, 180);
            // b는 a 위로 서서히 나타나고(디졸브), 그 b에서 c로 닦아낸다
            Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", plan.Filter);
            Assert.Contains("[k1][k2]xfade=transition=wipeleft:duration=0.5:offset=2[x2]", plan.Filter);
            Assert.Contains("setpts=PTS-STARTPTS+round(2/TB)[c3]", plan.Filter);
        }

        [Fact]
        public void Dissolve_StillUsesAlphaFade()
        {
            var (project, _, _) = TwoClips("Fade");
            var plan = Build(project, 0, 120);
            Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", plan.Filter);
            Assert.DoesNotContain("xfade", plan.Filter);
        }

        // ---------- 소리 ----------

        private static RenderPlan BuildAudio(Project project, long from, long to)
        {
            return RenderGraph.Build(project, new RenderOptions { From = from, To = to, Width = 640, Height = 360, Video = false });
        }

        [Fact]
        public void Transition_CrossfadesAudio_PreviousSoundContinuesUnder()
        {
            // 화면처럼 소리도 겹쳐 바뀐다: a 소리는 전환 길이만큼 더 읽어 cos로 줄고, b 소리는 sin으로 커진다(같은 크기감)
            var (project, _, _) = TwoClips("WipeLeft");
            var plan = BuildAudio(project, 0, 120);
            Assert.Equal(new[] { "-ss", "0", "-t", "2.5", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Equal(new[] { "-ss", "0", "-t", "2", "-i", "b.mp4" }, plan.Inputs[1]);
            Assert.Contains("[0:a]asetpts=PTS-STARTPTS,volume='1*cos(PI/2*min(1,max(0,(t+0-2)/0.5)))':eval=frame", plan.Filter);
            Assert.Contains("[1:a]asetpts=PTS-STARTPTS,volume='1*sin(PI/2*min(1,max(0,(t+0)/0.5)))':eval=frame", plan.Filter);
            Assert.Contains("adelay=2000:all=1[a1]", plan.Filter);
        }

        [Fact]
        public void WindowInsideTransition_IncludesPreviousSound()
        {
            // 전환 도중(앞 클립이 끝난 뒤)부터 그려도 앞 클립 소리가 이어서 들린다
            var (project, _, _) = TwoClips("Fade");
            var plan = BuildAudio(project, 65, 66);
            Assert.Equal(new[] { "-ss", "2.166667", "-t", "0.033333", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Contains("volume='1*cos(PI/2*min(1,max(0,(t+2.166667-2)/0.5)))':eval=frame", plan.Filter);
            Assert.Contains("volume='1*sin(PI/2*min(1,max(0,(t+0.166667)/0.5)))':eval=frame", plan.Filter);
        }

        [Fact]
        public void NoTransition_AudioUnchanged_AndAudioTracksAreNotCrossfaded()
        {
            var project = TestProjects.Empty(magnetic: false);
            TestProjects.Add(project, project.MainTrack, 0, 60, "a.mp4");
            TestProjects.Add(project, project.MainTrack, 60, 60, "b.mp4");
            var music = TimelineEdits.CreateClip(TestProjects.Audio(), FrameRate.Default, 0, 0);
            music.SourceOut = 120;
            TimelineEdits.Place(project, project.AudioTracks[0], music, 0);
            var plan = BuildAudio(project, 0, 120);
            Assert.DoesNotContain("sin(", plan.Filter);
            Assert.DoesNotContain("cos(", plan.Filter);
            Assert.Equal(new[] { "-ss", "0", "-t", "2", "-i", "a.mp4" }, plan.Inputs[0]);
        }

        [Fact]
        public void ExpressionTransition_UsesCustomXfade()
        {
            var (project, _, _) = TwoClips("Heart");
            var plan = Build(project, 0, 120);
            Assert.Contains("[k0][k1]xfade=transition=custom:expr='if(lte(pow(", plan.Filter);
            Assert.Contains("),B,A)':duration=0.5:offset=2[x1]", plan.Filter);
        }

        [Fact]
        public void Playback_MixesExpressionGroupsSmaller_AndScalesBack()
        {
            // 재생 중 수식 전환 묶음은 줄인 캔버스(640×360 → 2분의 1)에서 섞고 얹기 전에 늘인다 — 줄이기는 틀 계산에서 바로(크기 바꾸기 두 번 잇지 않음)
            var (project, _, _) = TwoClips("Heart");
            var playback = RenderGraph.Build(project, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, Playback = true }).Filter;
            Assert.Contains("scale=w=320:h=180:force_original_aspect_ratio=decrease,format=rgba,setpts=PTS-STARTPTS[s0]", playback);
            Assert.Contains("color=c=black@0.0:s=320x180:", playback);
            Assert.Contains("[x1]unpremultiply=inplace=1,scale=640:360,format=rgba,", playback);
            Assert.DoesNotContain("iw/2", playback);
            // 멈춘 화면 한 장·내보내기, 내장 효과는 원래 크기
            Assert.Contains("color=c=black@0.0:s=640x360:", Build(project, 0, 120).Filter);
            var (wipe, _, _) = TwoClips("WipeLeft");
            Assert.DoesNotContain("320x180", RenderGraph.Build(wipe, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, Playback = true }).Filter);
            // 작은 화면(1/4 화질 수준)은 줄이지 않는다, 전체 화질 1080×1920은 4분의 1
            Assert.Equal(1, RenderGraph.PlaybackShrink(new RenderOptions { Width = 270, Height = 480 }));
            Assert.Equal(2, RenderGraph.PlaybackShrink(new RenderOptions { Width = 540, Height = 960 }));
            Assert.Equal(4, RenderGraph.PlaybackShrink(new RenderOptions { Width = 1080, Height = 1920 }));
        }

        [Fact]
        public void OlderFfmpeg_FallsBackToDissolve()
        {
            // 덮기는 FFmpeg 7부터 — 6이면 디졸브로 그린다(모르면 그대로)
            var (project, _, _) = TwoClips("CoverLeft");
            var older = RenderGraph.Build(project, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, FfmpegMajor = 6 });
            Assert.DoesNotContain("xfade", older.Filter);
            Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", older.Filter);
            var newer = RenderGraph.Build(project, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false, FfmpegMajor = 7 });
            Assert.Contains("xfade=transition=coverleft:duration=0.5", newer.Filter);
            Assert.Contains("xfade=transition=coverleft", Build(project, 0, 120).Filter);
        }

        // ---------- 효과 데이터 ----------

        [Fact]
        public void BuiltInData_IsValid_AndKeepsEarlierIds()
        {
            var all = Transitions.All.Where(d => d.BuiltIn).ToList();
            Assert.True(all.Count >= 60, "기본 효과 " + all.Count);
            Assert.Equal("Fade", all[0].Id);
            Assert.True(all[0].IsDissolve);
            Assert.Equal(all.Count, all.Select(d => d.Id).Distinct().Count());
            Assert.Equal(all.Count, all.Select(d => d.Name).Distinct().Count());
            Assert.All(all, d => Assert.False(string.IsNullOrEmpty(d.Group)));
            Assert.All(all, d => Assert.NotNull(Transitions.Check(d, out _)));
            // 효과 목록이 코드에 있던 때 저장한 프로젝트의 효과가 그대로 열린다
            foreach (var id in new[] { "Fade", "FadeBlack", "FadeWhite", "Dissolve", "WipeLeft", "WipeRight", "WipeUp", "WipeDown", "SlideLeft", "SlideRight",
                "SlideUp", "SlideDown", "SmoothLeft", "SmoothRight", "CircleOpen", "CircleClose", "HorizontalOpen", "VerticalOpen", "Radial", "ZoomIn",
                "Pixelize", "Blur", "Diagonal" })
                Assert.True(Transitions.Find(id)?.BuiltIn == true, id);
            Assert.Contains(all, d => d.IsExpression);
            Assert.Equal(5, Transitions.Find("WipeLeft").Ffmpeg);
            Assert.Equal(7, Transitions.Find("CoverLeft").Ffmpeg);
        }

        [Theory]
        [InlineData("if(lt(X,W*P),A,B)", true)]
        [InlineData("if(lt(hypot(X-W/2,Y-H/2),(1-P)*W),b0(X,Y),a1(X,H-Y))", true)]
        [InlineData("if(lt(X,W*P),A,B)':x='1", false)]
        [InlineData("A[out];[in]B", false)]
        [InlineData("if(lt(X,W*P),A,B);1", false)]
        [InlineData("A\\B", false)]
        [InlineData("while(1,A)", false)]
        [InlineData("st(0,X)", false)]
        [InlineData("random(0)*A", false)]
        [InlineData("if(lt(X,W*P),A,B", false)]
        [InlineData("1e9*A", false)]
        [InlineData("Z*A", false)]
        [InlineData("", false)]
        public void Check_Expr_AllowsOnlyKnownNamesAndCharacters(string expr, bool ok)
        {
            var def = Transitions.Check(new TransitionDef { Id = "Test", Name = "시험", Expr = expr }, out var error);
            Assert.True(ok == (def != null), error);
        }

        [Fact]
        public void Check_RejectsBadFields_AndRaisesFfmpegVersion()
        {
            Assert.Null(Transitions.Check(new TransitionDef { Id = "1a", Name = "가", Xfade = "fade" }, out _));
            Assert.Null(Transitions.Check(new TransitionDef { Id = "a b", Name = "가", Xfade = "fade" }, out _));
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "  ", Xfade = "fade" }, out _));
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = new string('가', 41), Xfade = "fade" }, out _));
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Xfade = "fade", Expr = "A" }, out _));
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가" }, out _));
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Xfade = "wipeleft:offset=9" }, out var error));
            Assert.Contains("모르는 xfade", error);
            var def = Transitions.Check(new TransitionDef { Id = "A", Name = " 덮기 ", Xfade = "coverleft", Ffmpeg = 5 }, out _);
            Assert.Equal(("덮기", 7), (def.Name, def.Ffmpeg));
            Assert.False(def.BuiltIn);
        }

        // ---------- 이징(#32) ----------

        private const string WipeExpr = "if(gt(X,W*P),B,if(eq(PLANE,0),a0(X,Y)*PI/PI,A))";

        [Fact]
        public void Ease_Check_NormalizesKnownNames_RejectsUnknownAndBuiltInXfade()
        {
            Assert.Equal("inout", Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = " InOut " }, out _).Ease);
            Assert.Equal("in", Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "in" }, out _).Ease);
            Assert.Equal("out", Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "OUT" }, out _).Ease);
            // 일정(linear)·없음은 적지 않는다 — 예전 정의와 같은 수식·같은 썸네일
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "linear" }, out _).Ease);
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr }, out _).Ease);
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "bounce" }, out var error));
            Assert.Contains("ease", error);
            // xfade 내장 효과는 이징을 걸 수 없다
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Xfade = "wipeleft", Ease = "inout" }, out error));
            Assert.Contains("ease", error);
            Assert.Null(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Xfade = "wipeleft", Ease = "linear" }, out _).Ease);
        }

        [Fact]
        public void Ease_ReplacesOnlyTheProgressVariable_LinearIsUnchanged()
        {
            var linear = Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr }, out _);
            var eased = Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "inout" }, out _);
            Assert.Equal("transition=custom:expr='" + WipeExpr + "'", Transitions.XfadeOption(linear));
            var option = Transitions.XfadeOption(eased);
            Assert.Equal("transition=custom:expr='if(gt(X,W*(P*P*(3-2*P))),B,if(eq(PLANE,0),a0(X,Y)*PI/PI,A))'", option);
            Assert.Equal("transition=custom:expr='if(gt(X,W*(1-(1-P)*(1-P))),B,if(eq(PLANE,0),a0(X,Y)*PI/PI,A))'",
                Transitions.XfadeOption(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "in" }, out _)));
            Assert.Equal("transition=custom:expr='if(gt(X,W*(P*P)),B,if(eq(PLANE,0),a0(X,Y)*PI/PI,A))'",
                Transitions.XfadeOption(Transitions.Check(new TransitionDef { Id = "A", Name = "가", Expr = WipeExpr, Ease = "out" }, out _)));
            // 썸네일 캐시는 이징이 다르면 다르다
            Assert.NotEqual(Transitions.PreviewKey(linear), Transitions.PreviewKey(eased));
            Assert.Contains("P*P*(3-2*P)", string.Join(" ", Transitions.PreviewArguments(eased)));
            // 기본 효과는 이징이 없다(모양 그대로)
            Assert.All(Transitions.All, d => Assert.Null(d.Ease));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("in")]
        [InlineData("out")]
        [InlineData("inout")]
        public void Ease_Progress_KeepsTheEnds_AndOnlyGoesOneWay(string ease)
        {
            Assert.Equal(1, Transitions.EasedProgress(ease, 1), 9);
            Assert.Equal(0, Transitions.EasedProgress(ease, 0), 9);
            var previous = 1.0;
            for (var i = 99; i >= 0; i--)
            {
                var value = Transitions.EasedProgress(ease, i / 100.0);
                Assert.True(value <= previous + 1e-12, ease + " " + i);
                previous = value;
            }
        }

        [Fact]
        public void Ease_Progress_SlowStartAndEnd()
        {
            // P는 1(시작)에서 0(끝)으로 — 진행 u = 1 − P. 시작에서 1/4 지났을 때(P = 0.75)
            Assert.Equal(0.75, Transitions.EasedProgress(null, 0.75), 9);
            Assert.Equal(0.84375, Transitions.EasedProgress("inout", 0.75), 9);
            Assert.Equal(0.9375, Transitions.EasedProgress("in", 0.75), 9);
            Assert.Equal(0.5625, Transitions.EasedProgress("out", 0.75), 9);
            // 둘 다(inout)는 가운데에서 같다
            Assert.Equal(0.5, Transitions.EasedProgress("inout", 0.5), 9);
        }

        [Fact]
        public void PreviewArguments_UseTheSameXfadeOption()
        {
            var args = Transitions.PreviewArguments(Transitions.Find("Heart")).ToList();
            var graph = args[args.IndexOf("-filter_complex") + 1];
            Assert.Contains("[a][b]xfade=transition=custom:expr='if(", graph);
            Assert.EndsWith("tile=15x1", graph);
            Assert.NotEqual(Transitions.PreviewKey(Transitions.Find("WipeLeft")), Transitions.PreviewKey(Transitions.Find("WipeRight")));
            // 그리는 방법이 같으면 같은 캐시를 쓴다
            Assert.Equal(Transitions.PreviewKey(Transitions.Find("WipeLeft")), Transitions.PreviewKey(new TransitionDef { Id = "Other", Name = "다른", Xfade = "wipeleft" }));
        }

        /// <summary>사용자 효과 폴더를 잠시 만들어 읽고, 끝나면 기본 효과만 남긴다.</summary>
        private static void WithUserFolder(Action<string> body, params (string Name, string Json)[] files)
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-user-transitions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var (name, json) in files)
                    File.WriteAllText(Path.Combine(folder, name), json);
                body(folder);
            }
            finally
            {
                Transitions.LoadFolder(null);
                Directory.Delete(folder, true);
            }
        }

        private const string RingExpr = "if(lt(abs(hypot(X-W/2,Y-H/2)/H-0.3),(1-P)*0.8),B,A)";

        [Fact]
        public void UserFolder_AddsTransitions_AndSkipsBadOnes()
        {
            WithUserFolder(folder =>
            {
                var errors = Transitions.LoadFolder(folder);
                Assert.Equal(3, errors.Count);
                Assert.Contains(errors, e => e.StartsWith("a.json: id 'WipeLeft'", StringComparison.Ordinal));
                Assert.Contains(errors, e => e.StartsWith("a.json: Bad: expr에 쓸 수 없는 이름 'while'", StringComparison.Ordinal));
                Assert.Contains(errors, e => e.StartsWith("b.json: JSON 형식이 아닙니다", StringComparison.Ordinal));
                var ring = Transitions.Find("Ring");
                Assert.False(ring.BuiltIn);
                Assert.Equal(("고리", "내 효과"), (ring.Name, ring.Group));
                Assert.Equal(Transitions.All.Count(d => d.BuiltIn) + 1, Transitions.All.Count);
                Assert.Same(ring, Transitions.All[Transitions.All.Count - 1]);
            },
            ("a.json", "{ \"transitions\": [ { \"id\": \"Ring\", \"name\": \"고리\", \"expr\": \"" + RingExpr + "\" }, // 주석도 된다\n"
                + "{ \"id\": \"WipeLeft\", \"name\": \"겹침\", \"xfade\": \"wipeleft\" }, { \"id\": \"Bad\", \"name\": \"나쁨\", \"expr\": \"while(1,A)\" }, ] }"),
            ("b.json", "{ not json"));
            Assert.Null(Transitions.Find("Ring"));
        }

        [Fact]
        public void UserTransition_TravelsWithTheProject_AndTamperedDefinitionIsDropped()
        {
            WithUserFolder(folder =>
            {
                Transitions.LoadFolder(folder);
                var (project, _, b) = TwoClips("Ring");
                Assert.Equal("고리", b.TransitionDef.Name);
                // 기본 효과는 정의를 저장하지 않는다(id만)
                var (plain, _, wipe) = TwoClips("WipeLeft");
                Assert.Null(wipe.TransitionDef);
                Assert.DoesNotContain("TransitionDef", ProjectFile.Snapshot(plain));
                var json = ProjectFile.Snapshot(project);

                // 그 효과 파일이 없는 PC에서 열어도 같은 효과로 그린다
                Transitions.LoadFolder(null);
                Assert.Null(Transitions.Find("Ring"));
                var loaded = ProjectFile.FromSnapshot(json);
                var clip = loaded.MainTrack.Clips[1];
                Assert.Equal("고리", Transitions.Name(clip));
                Assert.Contains("xfade=transition=custom:expr='" + RingExpr + "':duration=0.5:offset=2", Build(loaded, 0, 120).Filter);
                // 다시 저장해도 정의가 남는다
                Assert.Contains("\"Expr\"", ProjectFile.Snapshot(loaded));

                // 함께 저장된 정의를 고쳐 필터 그래프를 벗어나려 하면 정의를 버리고 디졸브로 그린다(id는 남김)
                var node = JsonNode.Parse(json);
                node["VideoTracks"][0]["Clips"][1]["TransitionDef"]["Expr"] = "A',movie='x";
                var tampered = ProjectFile.FromSnapshot(node.ToJsonString());
                Assert.Null(tampered.MainTrack.Clips[1].TransitionDef);
                Assert.Equal("Ring", tampered.MainTrack.Clips[1].Transition);
                var plan = Build(tampered, 0, 120);
                Assert.DoesNotContain("xfade", plan.Filter);
                Assert.DoesNotContain("movie", plan.Filter);
                // 정의 자리에 엉뚱한 값이 있어도 프로젝트는 열린다
                node["VideoTracks"][0]["Clips"][1]["TransitionDef"] = "이상한 값";
                Assert.Null(ProjectFile.FromSnapshot(node.ToJsonString()).MainTrack.Clips[1].TransitionDef);
            },
            ("shapes.json", "{ \"transitions\": [ { \"id\": \"Ring\", \"name\": \"고리\", \"group\": \"모양\", \"expr\": \"" + RingExpr + "\" } ] }"));
        }

        // ---------- 편집 ----------

        [Fact]
        public void SetTransition_ClampsToClipLength_ZeroRemoves_KeepsPositions()
        {
            var (project, a, b) = TwoClips("Pixelize", 999);
            Assert.Equal(60, b.TransitionIn);
            Assert.Equal("Pixelize", b.Transition);
            Assert.Equal((0L, 60L), (a.Start, b.Start));
            TimelineEdits.SetTransition(project, b, "Pixelize", 0);
            Assert.Equal(0, b.TransitionIn);
        }

        [Fact]
        public void SetTransition_RejectsSubtitlesLockedTracksAndUnknownIds()
        {
            var (project, _, b) = TwoClips("WipeLeft");
            Assert.Throws<EditException>(() => TimelineEdits.SetTransition(project, b, "NoSuchEffect", 15));
            Assert.Equal("WipeLeft", b.Transition);
            var subtitles = TimelineEdits.ImportSubtitles(project, new[] { new SubtitleCue { Start = 0, End = 1, Text = "가" } });
            Assert.Throws<EditException>(() => TimelineEdits.SetTransition(project, subtitles.Clips[0], "WipeLeft", 15));
            project.MainTrack.Locked = true;
            Assert.Throws<EditException>(() => TimelineEdits.SetTransition(project, b, "SlideLeft", 15));
        }

        [Fact]
        public void SetTransitionsBetween_OnlyAdjacentClips()
        {
            var project = TestProjects.Empty(magnetic: false);
            var a = TestProjects.Add(project, project.MainTrack, 0, 30, "a.mp4");
            var b = TestProjects.Add(project, project.MainTrack, 30, 30, "b.mp4");
            var c = TestProjects.Add(project, project.MainTrack, 90, 30, "c.mp4");
            var count = TimelineEdits.SetTransitionsBetween(project, project.MainTrack, "FadeBlack", 9);
            Assert.Equal(1, count);
            Assert.Equal(0, a.TransitionIn);
            Assert.Equal(("FadeBlack", 9L), (b.Transition, b.TransitionIn));
            Assert.Equal(0, c.TransitionIn);
        }

        [Fact]
        public void SetTransition_IsUndoable()
        {
            var session = new EditorSession();
            var b = TestProjects.VideoClip(0, 60, "b.mp4");
            session.Execute(p =>
            {
                TimelineEdits.Place(p, p.MainTrack, TestProjects.VideoClip(0, 60, "a.mp4"), 0);
                TimelineEdits.Place(p, p.MainTrack, b, 999);
            });
            Assert.True(session.Execute(p => TimelineEdits.SetTransition(p, p.FindClip(b.Id), "Radial", 20)));
            Assert.Equal("Radial", session.Project.FindClip(b.Id).Transition);
            Assert.True(session.Undo());
            Assert.Equal(0, session.Project.FindClip(b.Id).TransitionIn);
        }

        [Fact]
        public void ProjectFile_KeepsId_AndInvalidBecomesDissolve()
        {
            var (project, _, _) = TwoClips("SmoothRight");
            var json = JsonNode.Parse(ProjectFile.Snapshot(project)).AsObject();
            Assert.Equal("SmoothRight", (string)json["VideoTracks"][0]["Clips"][1]["Transition"]);
            var folder = Path.Combine(Path.GetTempPath(), "fv-transition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "a" + ProjectFile.Extension);
                File.WriteAllText(path, json.ToJsonString());
                Assert.Equal("SmoothRight", ProjectFile.Load(path).MainTrack.Clips[1].Transition);
                json["VideoTracks"][0]["Clips"][1]["Transition"] = 999;
                json["VideoTracks"][0]["Clips"][1]["TransitionIn"] = -5;
                File.WriteAllText(path, json.ToJsonString());
                // 숫자 자리(형식이 다른 값)여도 프로젝트는 열리고 디졸브로
                var loaded = ProjectFile.Load(path).MainTrack.Clips[1];
                Assert.Equal("Fade", loaded.Transition);
                Assert.Equal(0, loaded.TransitionIn);
                json["VideoTracks"][0]["Clips"][1]["Transition"] = "a b;c";
                File.WriteAllText(path, json.ToJsonString());
                Assert.Equal("Fade", ProjectFile.Load(path).MainTrack.Clips[1].Transition);
                // 모르는 효과(다른 PC의 효과 파일)는 id를 남기고 디졸브로 그린다
                json["VideoTracks"][0]["Clips"][1]["Transition"] = "SomeoneElsesEffect";
                File.WriteAllText(path, json.ToJsonString());
                var unknown = ProjectFile.Load(path);
                Assert.Equal("SomeoneElsesEffect", unknown.MainTrack.Clips[1].Transition);
                Assert.Same(Transitions.Dissolve, Transitions.Resolve(unknown.MainTrack.Clips[1]));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    /// <summary>실제 FFmpeg로 전환을 그려 본다(파란 동영상 → 빨간 사진).</summary>
    public class TransitionFfmpegTests
    {
        private readonly ITestOutputHelper _out;

        public TransitionFfmpegTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>파란 동영상 [0, 60) → 빨간 사진 [60, 120), 사진 앞에 kind 전환 30프레임.</summary>
        private static Project BlueToRed(string kind)
        {
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var blue = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.BlueVideo), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, blue, 0);
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            red.Scale = 4;
            TimelineEdits.Place(project, project.MainTrack, red, 999);
            TimelineEdits.SetTransition(project, red, kind, 30);
            return project;
        }

        private static byte[] Frame(Project project, long frame)
        {
            var bytes = PreviewRenderer.Frame(FfmpegMedia.Tool, project, frame, 320, 240, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(bytes);
            return bytes;
        }

        private static (int R, int G, int B) Pixel(byte[] bgra, int x, int y, int offset = 0)
        {
            var i = offset + (y * 320 + x) * 4;
            return (bgra[i + 2], bgra[i + 1], bgra[i]);
        }

        private static bool IsRed((int R, int G, int B) p) { return p.R > 200 && p.B < 60; }

        private static bool IsBlue((int R, int G, int B) p) { return p.B > 200 && p.R < 60; }

        [FfmpegFact]
        public void Ease_MovesTheWipeAlongTheCurve()
        {
            // 64×8, 초당 8장, 0.5초부터 1초 동안 "X > W·P면 새 화면" 닦아내기 — 진행 1/4(4+2번째 장)·1/2(4+4번째 장)에서 새 화면 열 수
            const string expr = "if(gt(X,W*P),B,A)";
            // 새 화면 열 수 = X > 64·P인 열(경계 열은 빼므로 63 − 64·P): 일정 P 0.75 → 15, inout 0.84375 → 9, in 0.9375 → 3, out 0.5625 → 27
            foreach (var (ease, quarter) in new[] { ((string)null, 15), ("inout", 9), ("in", 3), ("out", 27) })
            {
                var def = Transitions.Check(new TransitionDef { Id = "EaseTest", Name = "시험", Expr = expr, Ease = ease }, out var error);
                Assert.NotNull(def);
                var args = new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error",
                    "-f", "lavfi", "-i", "color=c=blue:s=64x8:r=8:d=2", "-f", "lavfi", "-i", "color=c=red:s=64x8:r=8:d=2",
                    "-filter_complex", "[0]format=gbrap[a];[1]format=gbrap[b];[a][b]xfade=" + Transitions.XfadeOption(def) + ":duration=1:offset=0.5,format=rgb24",
                    "-frames:v", "9", "-f", "rawvideo", "pipe:1"
                };
                var bytes = FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath, args, TimeSpan.FromSeconds(60), CancellationToken.None).GetAwaiter().GetResult();
                int Red(int frame) => Enumerable.Range(0, 64).Count(x => bytes[(frame * 64 * 8 + x) * 3] > 200);
                _out.WriteLine((ease ?? "일정") + ": 1/4 " + Red(6) + "열 · 1/2 " + Red(8) + "열");
                Assert.InRange(Red(6), quarter - 1, quarter + 1);
                if (ease != "in" && ease != "out")
                    Assert.InRange(Red(8), 30, 32);
            }
        }

        [FfmpegFact]
        public void Wipe_MidwayShowsBothSides_ThenRed()
        {
            var project = BlueToRed("WipeLeft");
            Assert.Equal(60, project.MainTrack.Clips[1].Start);
            var before = Frame(project, 50);
            var mid = Frame(project, 75);
            var after = Frame(project, 100);
            _out.WriteLine("전 " + Pixel(before, 160, 120) + " · 중간 왼쪽 " + Pixel(mid, 20, 120) + " 오른쪽 " + Pixel(mid, 300, 120) + " · 뒤 " + Pixel(after, 160, 120));
            Assert.True(IsBlue(Pixel(before, 160, 120)));
            // 왼쪽으로 닦아내기: 새 화면(빨강)이 오른쪽부터 드러난다
            Assert.True(IsBlue(Pixel(mid, 20, 120)), "중간 왼쪽은 아직 파랑");
            Assert.True(IsRed(Pixel(mid, 300, 120)), "중간 오른쪽은 이미 빨강");
            Assert.True(IsRed(Pixel(after, 160, 120)));
        }

        [FfmpegFact]
        public async Task PreviewFrame_EqualsContinuousRender_InsideTransition()
        {
            // 한 장만 그린 미리 보기와 재생처럼 이어 그린 같은 프레임이 같아야 한다(전환 도중부터 그려도 진행이 맞음)
            // 61·64·70처럼 시작 초를 TB로 나눈 값이 0.99999가 되는 프레임도 본다(잘리면 한 프레임 앞이 나온다)
            var project = BlueToRed("CircleOpen");
            const int size = 320 * 240 * 4;
            var pixels = new byte[size * 90];
            List<string> files;
            using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 0, 90, 320, 240, out files))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var read = 0;
                while (read < pixels.Length)
                {
                    var n = await process.StandardOutput.BaseStream.ReadAsync(pixels, read, pixels.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                await process.WaitForExitAsync();
                Assert.True(read == pixels.Length, "읽은 바이트 " + read + " · " + await errors);
            }
            foreach (var file in files)
                RenderCommand.TryDelete(file);
            foreach (var frame in new long[] { 61, 64, 66, 70, 75, 89 })
            {
                var single = Frame(project, frame);
                long diff = 0;
                for (var i = 0; i < size; i++)
                {
                    if (i % 4 != 3)
                        diff += Math.Abs(single[i] - pixels[frame * size + i]);
                }
                var mean = diff / (size * 0.75);
                _out.WriteLine(frame + "프레임: 가운데 " + Pixel(single, 160, 120) + " 모서리 " + Pixel(single, 2, 2) + " · 평균 차이 " + mean);
                Assert.True(mean < 1, frame + "프레임 평균 차이 " + mean);
                if (frame == 75)
                {
                    // 원 열기: 가운데는 새 화면(빨강), 모서리는 아직 앞 화면(파랑)
                    Assert.True(IsRed(Pixel(single, 160, 120)));
                    Assert.True(IsBlue(Pixel(single, 2, 2)));
                }
            }
        }

        [FfmpegFact]
        public void TransitionFromTransparent_IsNotDarkened()
        {
            // 투명에서 흐리며 바뀌는 첫 클립: 절반 진행이면 검은 바탕 위 빨강이 절반 밝기(알파를 곱하지 않고 섞으면 1/4로 어두워진다)
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            red.Scale = 4;
            TimelineEdits.Place(project, project.MainTrack, red, 0);
            TimelineEdits.SetTransition(project, red, "Blur", 30);
            var center = Pixel(Frame(project, 15), 160, 120);
            _out.WriteLine("가운데 " + center);
            Assert.InRange(center.R, 100, 160);
        }

        [FfmpegFact]
        public void FirstClip_OpensFromBlack()
        {
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            red.Scale = 4;
            TimelineEdits.Place(project, project.MainTrack, red, 0);
            TimelineEdits.SetTransition(project, red, "CircleOpen", 30);
            var mid = Frame(project, 15);
            _out.WriteLine("가운데 " + Pixel(mid, 160, 120) + " 모서리 " + Pixel(mid, 2, 2));
            Assert.True(IsRed(Pixel(mid, 160, 120)));
            var corner = Pixel(mid, 2, 2);
            Assert.True(corner.R < 30 && corner.G < 30 && corner.B < 30, "모서리는 아직 검정 " + corner);
        }

        [FfmpegFact]
        public async Task Transition_PreviousSoundFadesOutUnderTheNextClip()
        {
            // 소리 있는 동영상(앞 1초만 씀) [0, 30) → 사진 [30, 90), 사진 앞에 1초 전환: 1~2초에도 동영상 소리가 줄어들며 이어지고 그 뒤로는 조용하다
            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var video = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.TestVideo), project.Rate, 0, 0);
            video.SourceOut = video.SourceIn + 30;
            TimelineEdits.Place(project, project.MainTrack, video, 0);
            var red = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 60);
            TimelineEdits.Place(project, project.MainTrack, red, 999);
            TimelineEdits.SetTransition(project, red, "WipeLeft", 30);
            Assert.Equal(30, red.Start);
            string script;
            byte[] pcm;
            using (var process = PreviewRenderer.StartAudio(FfmpegMedia.Tool, project, 0, 90, out script))
            {
                var errors = process.StandardError.ReadToEndAsync();
                using (var memory = new MemoryStream())
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(memory);
                    pcm = memory.ToArray();
                }
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await errors);
            }
            RenderCommand.TryDelete(script);
            // 48kHz 16비트 스테레오: 구간의 왼쪽 채널 크기(RMS)
            double Rms(double from, double to)
            {
                double sum = 0;
                var count = 0;
                for (var i = (int)(from * 48000); i < (int)(to * 48000); i++, count++)
                {
                    var value = BitConverter.ToInt16(pcm, i * 4) / 32768.0;
                    sum += value * value;
                }
                return Math.Sqrt(sum / count);
            }
            var full = Rms(0.25, 0.75);
            var fading = Rms(1.25, 1.5);
            var after = Rms(2.2, 2.8);
            _out.WriteLine("앞 " + full.ToString("0.000") + " · 전환 중 " + fading.ToString("0.000") + " · 뒤 " + after.ToString("0.000"));
            // 시험 소리(sine)는 크기 1/8 — RMS 약 0.088
            Assert.True(full > 0.05, "앞 소리 " + full);
            // cos(π/8~π/4) ≈ 0.92~0.71
            Assert.InRange(fading / full, 0.6, 0.97);
            Assert.True(after < full * 0.02, "전환 뒤 소리 " + after);
        }

        [FfmpegFact]
        public async Task Playback_ExpressionTransition_StreamsEveryFrame_WithoutHanging()
        {
            // 재생 그래프로 전환 끝을 지나 이어 그린다 — 크기 바꾸기를 두 번 이으면 FFmpeg가 전환 끝에서 멈추던 일(6/10)이 없어야 한다
            var project = BlueToRed("Heart");
            const int size = 320 * 240 * 4;
            for (var run = 0; run < 4; run++)
            {
                List<string> files;
                long read = 0;
                using (var process = PreviewRenderer.StartVideo(FfmpegMedia.Tool, project, 40, 120, 320, 240, out files))
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
                }
                foreach (var file in files)
                    RenderCommand.TryDelete(file);
                Assert.Equal(80 * (long)size, read);
            }
        }

        [FfmpegFact]
        public void ExpressionTransition_Heart_OpensFromTheMiddle()
        {
            // 하트가 커지며 새 화면(빨강)이 드러난다 — 가운데는 이미 빨강, 아래 모서리는 아직 파랑
            var project = BlueToRed("Heart");
            var frame = Frame(project, 82);
            _out.WriteLine("가운데 " + Pixel(frame, 160, 120) + " 아래 모서리 " + Pixel(frame, 2, 237));
            Assert.True(IsRed(Pixel(frame, 160, 120)));
            Assert.True(IsBlue(Pixel(frame, 2, 237)));
        }

        [FfmpegFact]
        public async Task EveryBuiltIn_RendersAPreviewStrip()
        {
            // 데이터에 적은 효과가 실제 FFmpeg에서 그려지는지(이 FFmpeg보다 새 버전이 필요한 효과는 건너뜀)
            var tool = FfmpegMedia.Tool;
            var defs = Transitions.All.Where(d => d.BuiltIn && d.Ffmpeg <= tool.MajorVersion).ToList();
            Assert.NotEmpty(defs);
            var gate = new SemaphoreSlim(4);
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Task.WhenAll(defs.Select(async def =>
            {
                await gate.WaitAsync();
                try
                {
                    var png = await MediaPictures.TransitionStrip(tool, def, CancellationToken.None);
                    // PNG 머리(IHDR)의 너비·높이: 칸 15개를 가로로
                    var width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
                    var height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
                    if (width != Transitions.PreviewWidth * Transitions.PreviewFrames || height != Transitions.PreviewHeight)
                        failures.Add(def.Id + " " + width + "x" + height);
                }
                catch (Exception ex)
                {
                    failures.Add(def.Id + ": " + ex.Message);
                }
                finally
                {
                    gate.Release();
                }
            }));
            _out.WriteLine(defs.Count + "개 효과(FFmpeg " + tool.MajorVersion + ")");
            Assert.Empty(failures);
        }
    }
}
