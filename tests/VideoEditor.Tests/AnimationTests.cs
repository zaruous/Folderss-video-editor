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
    /// <summary>#4 애니메이션 효과: 모델·저장 형식, 시간식(이징·구간·끝 모습), 글자 나누기, 글자별 모습, 렌더 그래프 글.</summary>
    public class AnimationTests
    {
        private static ClipAnimation Make(AnimationEffect effect, AnimationKind kind, long frames = 10, Clip clip = null)
        {
            var animation = ClipAnimations.Create(ClipAnimations.Def(effect), kind, FrameRate.Default, clip);
            animation.Duration = frames;
            return animation;
        }

        private static (Project Project, Clip Video) VideoProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 60);
            return (project, clip);
        }

        private static (Project Project, Clip Text) TextProject(string text = "안녕 ABC")
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateText(text, 0, 60);
            clip.Style = TextStyle.FromLegacy(clip);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        private static RenderOptions Options(ITextRasterizer rasterizer = null, long from = 0, long to = 60)
        {
            return new RenderOptions { From = from, To = to, Width = 640, Height = 360, Audio = false, TempFolder = @"C:\Temp", TextRasterizer = rasterizer };
        }

        // ---------- 모델·저장 ----------

        [Fact]
        public void Clean_KeepsOneInOneOut_AndUpToEightEmphases_InOrder()
        {
            var (_, clip) = VideoProject();
            clip.Animations = new List<ClipAnimation>
            {
                Make(AnimationEffect.Fade, AnimationKind.Out),
                Make(AnimationEffect.Fly, AnimationKind.In),
                Make(AnimationEffect.Zoom, AnimationKind.In),
                Make(AnimationEffect.Wipe, AnimationKind.Out)
            };
            for (var i = 0; i < 10; i++)
                clip.Animations.Add(Make(AnimationEffect.Pulse, AnimationKind.Emphasis));
            var clean = ClipAnimations.Clean(clip);
            Assert.Equal(1 + ClipAnimations.MaxEmphasis + 1, clean.Count);
            Assert.Equal((AnimationKind.In, AnimationEffect.Fly), (clean[0].Kind, clean[0].Effect));
            Assert.Equal((AnimationKind.Out, AnimationEffect.Fade), (clean[^1].Kind, clean[^1].Effect));
            Assert.All(clean.Skip(1).Take(ClipAnimations.MaxEmphasis), a => Assert.Equal(AnimationKind.Emphasis, a.Kind));
        }

        [Fact]
        public void Clean_ClampsValues_FixesDirections_AndKeepsTextOnlyEffectsOnText()
        {
            var (_, video) = VideoProject();
            var wrong = Make(AnimationEffect.Fly, AnimationKind.In);
            wrong.Duration = -5;
            wrong.Delay = long.MaxValue;
            wrong.Unit = AnimationUnit.Character;
            wrong.Stagger = double.NaN;
            wrong.Repeat = 7;
            var spin = Make(AnimationEffect.Spin, AnimationKind.Out);
            spin.Direction = AnimationDirection.Down;
            spin.Amount = double.PositiveInfinity;
            var pulse = Make(AnimationEffect.Pulse, AnimationKind.Emphasis);
            pulse.Repeat = -3;
            pulse.Amount = 1000;
            video.Animations = new List<ClipAnimation>
            {
                wrong, spin, pulse,
                // 분류가 맞지 않는 효과, 글자 전용 효과(동영상), 모르는 열거 값은 빠진다
                new ClipAnimation { Kind = AnimationKind.Emphasis, Effect = AnimationEffect.Fade, Duration = 5 },
                new ClipAnimation { Kind = AnimationKind.In, Effect = AnimationEffect.Typewriter, Duration = 5 },
                new ClipAnimation { Kind = (AnimationKind)9, Effect = AnimationEffect.Fade, Duration = 5 },
                null
            };
            var clean = ClipAnimations.Clean(video);
            Assert.Equal(3, clean.Count);
            var fly = clean[0];
            Assert.Equal((1L, ClipAnimations.MaxFrames, AnimationUnit.All, 1), (fly.Duration, fly.Delay, fly.Unit, fly.Repeat));
            Assert.Equal(ClipAnimations.Def(AnimationEffect.Fly).DefaultStagger, fly.Stagger);
            Assert.Equal((AnimationDirection.Right, 360.0), (clean[2].Direction, clean[2].Amount));
            Assert.Equal((0, 100.0), (clean[1].Repeat, clean[1].Amount));

            // 글자 클립에는 타자기가 들어가고, 한 번에로 저장된 타자기는 글자별로
            var (_, text) = TextProject();
            var typewriter = Make(AnimationEffect.Typewriter, AnimationKind.In, 30, text);
            typewriter.Unit = AnimationUnit.All;
            text.Animations = new List<ClipAnimation> { typewriter };
            Assert.Equal(AnimationUnit.Character, Assert.Single(ClipAnimations.Clean(text)).Unit);
        }

        [Fact]
        public void Read_UnknownEffect_DropsOnlyThatItem_AndOldProjectsHaveNone()
        {
            var (project, clip) = VideoProject();
            clip.Animations = new List<ClipAnimation> { Make(AnimationEffect.Fade, AnimationKind.In), Make(AnimationEffect.Pulse, AnimationKind.Emphasis) };
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var item = json["VideoTracks"][0]["Clips"][0]["Animations"][0];
            Assert.Equal("Fade", (string)item["Effect"]);
            Assert.Equal("In", (string)item["Kind"]);
            item["Effect"] = "Explode";
            var read = ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0];
            Assert.Equal(AnimationEffect.Pulse, Assert.Single(read.Animations).Effect);
            // 목록이 아니면 없음
            json["VideoTracks"][0]["Clips"][0]["Animations"] = "wrong";
            Assert.Null(ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0].Animations);
            // 애니메이션이 없는 클립은 저장 글에 키가 없다(예전 형식 그대로)
            clip.Animations = null;
            Assert.DoesNotContain("Animations", ProjectFile.Snapshot(project));
        }

        [Fact]
        public void Save_WritesVersion4_OnlyWithAnimations_AndLoadReadsThem()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-anim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "p.fvproj");
                int Version() => (int)JsonNode.Parse(File.ReadAllText(path))["Version"];
                var (project, text) = TextProject();
                ProjectFile.Save(project, path);
                Assert.Equal(2, Version());

                text.Animations = new List<ClipAnimation> { Make(AnimationEffect.Typewriter, AnimationKind.In, 30, text), Make(AnimationEffect.Scatter, AnimationKind.Out, 20, text) };
                ProjectFile.Save(project, path);
                Assert.Equal(4, Version());
                Assert.Equal(4, Project.AnimationVersion);
                var loaded = ProjectFile.Load(path).MainTrack.Clips[0];
                Assert.Equal(new[] { AnimationEffect.Typewriter, AnimationEffect.Scatter }, loaded.Animations.Select(a => a.Effect));
                Assert.Equal(30, loaded.Animations[0].Duration);

                // 아트 + 애니메이션도 4, 애니메이션을 빼면 아트 형식 3, 다 빼면 2
                var art = TimelineEdits.CreateArt(ArtKind.Star, 0, 30);
                TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), art, 0);
                ProjectFile.Save(project, path);
                Assert.Equal(4, Version());
                text.Animations = null;
                ProjectFile.Save(project, path);
                Assert.Equal(3, Version());
                project.VideoTracks[1].Clips.Clear();
                ProjectFile.Save(project, path);
                Assert.Equal(2, Version());

                var json = JsonNode.Parse(File.ReadAllText(path));
                json["Version"] = 5;
                File.WriteAllText(path, json.ToJsonString());
                Assert.Contains("더 새 버전", Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path)).Message);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Split_LeftKeepsIn_RightKeepsOut_EmphasisOnBoth_CloneIsDeep()
        {
            var (project, clip) = VideoProject();
            clip.Animations = new List<ClipAnimation>
            {
                Make(AnimationEffect.Fly, AnimationKind.In), Make(AnimationEffect.Pulse, AnimationKind.Emphasis), Make(AnimationEffect.Fade, AnimationKind.Out)
            };
            var copy = clip.CloneWithNewId();
            copy.Animations[0].Duration = 99;
            Assert.Equal(10, clip.Animations[0].Duration);

            var right = Assert.Single(TimelineEdits.Split(project, new[] { clip }, 30));
            Assert.Equal(new[] { AnimationKind.In, AnimationKind.Emphasis }, clip.Animations.Select(a => a.Kind));
            Assert.Equal(new[] { AnimationKind.Emphasis, AnimationKind.Out }, right.Animations.Select(a => a.Kind));
        }

        [Fact]
        public void RateChange_ConvertsFrames_AudioAndSubtitleClipsHaveNone()
        {
            var (project, clip) = VideoProject();
            var fade = Make(AnimationEffect.Fade, AnimationKind.In, 15);
            fade.Delay = 6;
            clip.Animations = new List<ClipAnimation> { fade };
            ProjectSettings.ConvertClip(clip, new FrameRate(30, 1), new FrameRate(60, 1));
            Assert.Equal((30L, 12L), (clip.Animations[0].Duration, clip.Animations[0].Delay));

            TimelineEdits.MakeAudioOnly(clip);
            Assert.Null(clip.Animations);
            clip.HasVideo = true;
            clip.AudioOnly = false;
            clip.Animations = new List<ClipAnimation> { Make(AnimationEffect.Fade, AnimationKind.In) };
            clip.AudioOnly = true;
            Assert.Null(ClipAnimations.Clean(clip));

            var cue = TimelineEdits.CreateText("자막", 0, 30);
            cue.Animations = new List<ClipAnimation> { Make(AnimationEffect.Fade, AnimationKind.In) };
            var subtitles = TimelineEdits.AddTrack(project, TrackKind.Video);
            subtitles.Subtitles = true;
            subtitles.Clips.Add(cue);
            var read = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            Assert.Null(read.VideoTracks.First(t => t.Subtitles).Clips[0].Animations);
        }

        // ---------- 이징·시간식 ----------

        [Fact]
        public void Ease_StartsAtZero_EndsAtOne_AndExpressionMatches()
        {
            foreach (AnimationEase ease in Enum.GetValues(typeof(AnimationEase)))
            {
                Assert.Equal(0, AnimationMotion.Ease(ease, 0), 9);
                Assert.Equal(1, AnimationMotion.Ease(ease, 1), 9);
                var expression = AnimationMotion.Ease(ease, MotionExpr.Clip01(MotionExpr.Time));
                for (var p = -0.2; p <= 1.2; p += 0.01)
                    Assert.Equal(AnimationMotion.Ease(ease, p), expression.Eval(p), 9);
            }
            // 튕김은 끝에 닿기 전에 한 번 1까지 갔다가 내려온다, 탄성은 1을 넘는다
            Assert.Equal(1, AnimationMotion.Ease(AnimationEase.Bounce, 1 / 2.75), 9);
            Assert.True(AnimationMotion.Ease(AnimationEase.Bounce, 0.5) < 1);
            Assert.True(Enumerable.Range(1, 99).Max(i => AnimationMotion.Ease(AnimationEase.Elastic, i / 100.0)) > 1.05);
        }

        [Fact]
        public void Expression_TextIsFfmpegSyntax_ConstantsFold()
        {
            var e = MotionExpr.Clip01((MotionExpr.Time - 0.5) / 2);
            Assert.Equal("clip((((t+1)-0.5)/2),0,1)", e.ToFfmpeg("(t+1)"));
            Assert.Equal(0.75, e.Eval(2), 9);
            Assert.Equal("(-0.5)", (MotionExpr.Num(1) - 1.5).ToFfmpeg("t"));
            Assert.Equal("t", (MotionExpr.Time * 1 + 0).ToFfmpeg("t"));
            Assert.Equal("if(lt(t,1),2,3)", MotionExpr.If(MotionExpr.Lt(MotionExpr.Time, 1), 2, 3).ToFfmpeg("t"));
        }

        [Fact]
        public void Window_InFromStart_OutFromEnd_OutStartsAfterIn()
        {
            var (_, clip) = VideoProject();
            var fade = Make(AnimationEffect.Fade, AnimationKind.In, 20);
            fade.Delay = 5;
            var fly = Make(AnimationEffect.Fly, AnimationKind.Out, 15);
            fly.Delay = 3;
            clip.Animations = new List<ClipAnimation> { fade, fly };
            Assert.Equal((5L, 20L), AnimationMotion.Window(clip, fade));
            Assert.Equal((42L, 15L), AnimationMotion.Window(clip, fly));
            // 짧은 클립: 사라지기는 나타나기가 끝난 뒤부터
            fly.Duration = 50;
            Assert.Equal((25L, 32L), AnimationMotion.Window(clip, fly));
            var pulse = Make(AnimationEffect.Pulse, AnimationKind.Emphasis, 12);
            pulse.Delay = 6;
            pulse.Repeat = 2;
            Assert.Equal((6L, 12L, 30L), AnimationMotion.EmphasisWindow(clip, pulse));
            pulse.Repeat = 0;
            Assert.Equal((6L, 12L, 60L), AnimationMotion.EmphasisWindow(clip, pulse));
        }

        /// <summary>
        /// 시간 τ의 모습: (크기, 회전, 불투명도, 흔들림 x, 날아오기 무게 합, 닦아내기로 보이는 넓이 비율, 흐림, 옮김 y).
        /// 닦아내기는 축마다 보이는 구간(왼쪽·위부터 v = [0, v], 오른쪽·아래부터 v = [1 − v, 1])을 겹친 길이의 곱 — 가운데서 갈라지기는 양쪽 닦아내기가 겹친 곳.
        /// </summary>
        private static (double Scale, double Rotation, double Opacity, double ShiftX, double Fly, double Visible, double Blur, double ShiftY) Pose(ObjectMotion motion, double time)
        {
            double left = 0, right = 1, top = 0, bottom = 1;
            foreach (var (visible, dx, dy) in motion.Wipes)
            {
                var v = visible.Eval(time);
                if (dx < 0)
                    right = Math.Min(right, v);
                if (dx > 0)
                    left = Math.Max(left, 1 - v);
                if (dy < 0)
                    bottom = Math.Min(bottom, v);
                if (dy > 0)
                    top = Math.Max(top, 1 - v);
            }
            return (motion.Scale?.Eval(time) ?? 1, motion.Rotation?.Eval(time) ?? 0, motion.Opacity?.Eval(time) ?? 1, motion.ShiftX?.Eval(time) ?? 0,
                motion.Fly.Sum(f => f.Weight.Eval(time)), Math.Max(0, right - left) * Math.Max(0, bottom - top),
                motion.Blur?.Eval(time) ?? 0, motion.ShiftY?.Eval(time) ?? 0);
        }

        [Fact]
        public void EveryEntrance_IsGoneBefore_AndExactlyAtRestWhenItsWindowEnds()
        {
            foreach (var def in ClipAnimations.All.Where(d => !d.Emphasis))
            {
                foreach (var ease in new[] { AnimationEase.Linear, AnimationEase.Out, AnimationEase.Bounce, AnimationEase.Elastic })
                {
                    var (_, text) = TextProject();
                    var vanish = Make(def.Effect, AnimationKind.Out, 12, text);
                    vanish.Unit = AnimationUnit.All;
                    vanish.Ease = ease;
                    var rest = (1.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
                    // 사라지기 전용(떨어지기)은 나타나기로 만들지 않는다
                    if (def.OutOnly)
                    {
                        Assert.Null(ClipAnimations.Create(def, AnimationKind.In, FrameRate.Default, text));
                    }
                    else
                    {
                        var appear = Make(def.Effect, AnimationKind.In, 12, text);
                        appear.Unit = AnimationUnit.All;
                        appear.Ease = ease;
                        text.Animations = new List<ClipAnimation> { appear };
                        var appearing = AnimationMotion.ObjectMotion(text, false);
                        Assert.False(appearing.IsEmpty, def.Name);
                        Assert.Equal(rest, Pose(appearing, 12));
                        Assert.Equal(rest, Pose(appearing, 40));
                        var start = Pose(appearing, 0);
                        Assert.True(start.Scale == 0 || start.Opacity == 0 || start.Fly == 1 || start.Visible == 0, def.Name + " " + ease + "은(는) 처음에 보이지 않는다");
                        // 구간 앞(지연)도 보이지 않는다
                        appear.Delay = 5;
                        var early = Pose(AnimationMotion.ObjectMotion(text, false), 2);
                        Assert.True(early.Scale == 0 || early.Opacity == 0 || early.Fly == 1 || early.Visible == 0, def.Name + " 지연 동안");
                        Assert.Equal(rest, Pose(AnimationMotion.ObjectMotion(text, false), 17));
                    }

                    text.Animations = new List<ClipAnimation> { vanish };
                    var motion = AnimationMotion.ObjectMotion(text, false);
                    Assert.Equal(rest, Pose(motion, 48));
                    var end = Pose(motion, 60);
                    Assert.True(end.Scale == 0 || end.Opacity == 0 || end.Fly == 1 || end.Visible == 0, def.Name + " " + ease + "은(는) 끝에 보이지 않는다");
                }
            }
        }

        [Fact]
        public void Emphasis_PulseAndShakeReturn_RotateAndGrowHold()
        {
            var (_, clip) = VideoProject();
            ObjectMotion With(AnimationEffect effect, int repeat, double amount)
            {
                var animation = Make(effect, AnimationKind.Emphasis, 12);
                animation.Repeat = repeat;
                animation.Amount = amount;
                animation.Delay = 6;
                clip.Animations = new List<ClipAnimation> { animation };
                return AnimationMotion.ObjectMotion(clip, false);
            }
            var pulse = With(AnimationEffect.Pulse, 2, 20);
            Assert.Equal(1, pulse.Scale.Eval(6), 9);
            Assert.Equal(1.2, pulse.Scale.Eval(12), 6);
            Assert.Equal(1.2, pulse.Scale.Eval(24), 6);
            Assert.Equal(1, pulse.Scale.Eval(40), 9);
            var shake = With(AnimationEffect.Shake, 1, 30);
            Assert.True(Enumerable.Range(6, 12).Max(f => Math.Abs(shake.ShiftX.Eval(f))) > 10);
            Assert.Equal(0, shake.ShiftX.Eval(30), 9);
            var rotate = With(AnimationEffect.Rotate, 2, 90);
            Assert.Equal(90, rotate.Rotation.Eval(18), 6);
            Assert.Equal(180, rotate.Rotation.Eval(50), 6);
            var grow = With(AnimationEffect.Grow, 1, 150);
            Assert.Equal(1.5, grow.Scale.Eval(50), 9);
            var fade = With(AnimationEffect.Transparency, 1, 40);
            Assert.Equal(0.4, fade.Opacity.Eval(18), 9);
        }

        [Fact]
        public void NewEntrances_RiseGrowTurnSplitUnblur_Midway_AndReversedOut()
        {
            var (_, clip) = VideoProject();
            ObjectMotion With(AnimationEffect effect, AnimationKind kind, double amount, AnimationDirection direction = AnimationDirection.Left)
            {
                var animation = Make(effect, kind, 10);
                animation.Ease = AnimationEase.Linear;
                animation.Amount = amount;
                animation.Direction = direction;
                clip.Animations = new List<ClipAnimation> { animation };
                return AnimationMotion.ObjectMotion(clip, false);
            }
            // 떠오르기: 40px 아래에서 서서히 — 반쯤 20px·반투명, 흐려지며 가라앉기는 끝에 40px 아래·투명(옮김은 프로젝트 픽셀)
            var rise = With(AnimationEffect.Rise, AnimationKind.In, 40);
            Assert.Null(rise.ShiftX);
            Assert.Equal(20, rise.ShiftY.Eval(5), 9);
            Assert.Equal(0.5, rise.Opacity.Eval(5), 9);
            var sink = With(AnimationEffect.Rise, AnimationKind.Out, 40);
            Assert.Equal((40.0, 0.0), (sink.ShiftY.Eval(60), sink.Opacity.Eval(60)));
            Assert.Equal("흐려지며 가라앉기", ClipAnimations.Def(AnimationEffect.Rise).DisplayName(AnimationKind.Out));
            // 커지며 돌기(시계 방향): 20% 크기·90° 덜 돈 자리에서
            var turn = With(AnimationEffect.GrowTurn, AnimationKind.In, 90, AnimationDirection.Right);
            Assert.Equal((0.2, -90.0, 0.0), (Math.Round(turn.Scale.Eval(0), 9), turn.Rotation.Eval(0), turn.Opacity.Eval(0)));
            Assert.Equal(0.6, turn.Scale.Eval(5), 9);
            // 가운데서 갈라지기: 처음엔 아무것도(반씩 맞닿은 한 줄도 없이), 반쯤 가운데 절반 — 좌우로는 가로 닦아내기 둘, 위아래로는 세로 둘
            var split = With(AnimationEffect.Split, AnimationKind.In, 0);
            Assert.Equal(new[] { -1, 1 }, split.Wipes.Select(w => w.Dx));
            Assert.Equal(0, Pose(split, 0).Visible);
            Assert.Equal(0, split.Wipes[0].Visible.Eval(0));
            Assert.Equal(0.5, Pose(split, 5).Visible, 9);
            Assert.Equal(0.75, split.Wipes[0].Visible.Eval(5), 9);
            var closing = With(AnimationEffect.Split, AnimationKind.Out, 0, AnimationDirection.Up);
            Assert.Equal(new[] { -1, 1 }, closing.Wipes.Select(w => w.Dy));
            Assert.Equal((1.0, 0.0), (Pose(closing, 50).Visible, Pose(closing, 60).Visible));
            // 흐림에서 선명하게: 16px에서 0으로, 반쯤에 이미 다 보인다
            var unblur = With(AnimationEffect.Unblur, AnimationKind.In, 16);
            Assert.Equal((16.0, 8.0, 0.0), (unblur.Blur.Eval(0), unblur.Blur.Eval(5), unblur.Blur.Eval(10)));
            Assert.Equal((0.0, 0.6, 1.0), (unblur.Opacity.Eval(0), Math.Round(unblur.Opacity.Eval(3), 9), unblur.Opacity.Eval(5)));
        }

        [Fact]
        public void Fall_OutOnly_AcceleratesDownOffscreen_Tilting()
        {
            var (project, video) = VideoProject();
            var def = ClipAnimations.Def(AnimationEffect.Fall);
            Assert.True(def.OutOnly);
            Assert.False(ClipAnimations.Offers(video, def, AnimationKind.In));
            Assert.True(ClipAnimations.Offers(video, def, AnimationKind.Out));
            // 나타나기로 저장된 떨어지기는 읽을 때 빠진다
            video.Animations = new List<ClipAnimation>
            {
                new ClipAnimation { Kind = AnimationKind.In, Effect = AnimationEffect.Fall, Duration = 10 }, Make(AnimationEffect.Fall, AnimationKind.Out)
            };
            Assert.Equal(AnimationKind.Out, Assert.Single(ClipAnimations.Clean(video)).Kind);

            // 사라지기(50~59장): 서서히 끝 이징을 거꾸로 — 반쯤 화면 밖까지의 1/4(점점 빨라짐), 끝에 화면 아래 밖·시계 방향 25° 기울기
            var fall = Make(AnimationEffect.Fall, AnimationKind.Out);
            fall.Direction = AnimationDirection.Right;
            video.Animations = new List<ClipAnimation> { fall };
            var motion = AnimationMotion.ObjectMotion(video, false);
            var (weight, dx, dy) = motion.Fly.Single();
            Assert.Equal((0, 1), (dx, dy));
            Assert.Equal((0.0, 0.25, 1.0), (weight.Eval(50), weight.Eval(55), weight.Eval(60)));
            Assert.Equal((0.0, -25.0), (motion.Rotation.Eval(50), motion.Rotation.Eval(60)));
            Assert.Null(motion.Opacity);

            // 글자별: 글자 크기의 4배 아래로, 글자마다 다르게(시계 방향으로) 기울며 끝에 투명
            var (_, text) = TextProject("가나다");
            var letters = Make(AnimationEffect.Fall, AnimationKind.Out, 10, text);
            letters.Unit = AnimationUnit.Character;
            letters.Stagger = 0;
            text.Animations = new List<ClipAnimation> { letters };
            var end = TextAnimationFrame.Of(text, project.Rate).Poses(text.Text, 30, 60);
            Assert.All(end, p => Assert.Equal((120.0, 0.0), (p.Y, p.Opacity)));
            Assert.True(end.Select(p => Math.Round(p.Rotation, 3)).Distinct().Count() == 3 && end.All(p => p.Rotation < 0));
        }

        [Fact]
        public void Graph_RiseUsesOwnHeight_UnblurSendsSigmaOnlyWhileBlurred()
        {
            var (project, video) = VideoProject();
            var rise = Make(AnimationEffect.Rise, AnimationKind.In);
            var unblur = Make(AnimationEffect.Unblur, AnimationKind.Out);
            unblur.Ease = AnimationEase.Linear;
            unblur.Amount = 16;
            video.Animations = new List<ClipAnimation> { rise, unblur };
            var filter = RenderGraph.Build(project, Options()).Filter;
            // 떠오르기: overlay y에 거리(프로젝트 픽셀 × 출력 배율 1)
            Assert.Contains("y='(main_h-overlay_h)/2+0+(60*(1-", filter);
            // 흐림: 사라지기 구간(50~59장, 시그마 0 → 14.4)에서만 — 번질 자리 14.4 × 3을 짝수로 올린 44, 알파 곱하기는 흐린 장(51~59)에서만
            Assert.Contains(",pad=w=iw+88:h=ih+88:x=44:y=44:color=black@0,format=gbrap,premultiply=inplace=1:enable='between(t,1.683333,1.983333)'", filter);
            var commands = Regex.Match(filter, "sendcmd=c='([^']*)'").Groups[1].Value.Split(';');
            Assert.Equal("1.683333 gblur@bl0 sigma 1.6, gblur@bl0 sigmaV 1.6", commands[0]);
            Assert.Equal("1.95 gblur@bl0 sigma 14.4, gblur@bl0 sigmaV 14.4", commands[^1]);
            Assert.Contains("gblur@bl0=sigma=0:sigmaV=0,unpremultiply=inplace=1:enable='between(t,1.683333,1.983333)',format=rgba", filter);
            // 흐리지 않는 구간만 그리면 넓히기만 한다(재생 헤드 한 장도 내보내기와 같은 자리에)
            var rest = RenderGraph.Build(project, Options(null, 20, 40)).Filter;
            Assert.DoesNotContain("gblur", rest);
            Assert.Contains(",pad=w=iw+88:h=ih+88:x=44:y=44:color=black@0,", rest);
            Assert.Contains("gblur@bl0=sigma=8:sigmaV=8,", RenderGraph.Build(project, Options(null, 55, 56)).Filter);
        }

        [Fact]
        public void Clean_TextOnlyEmphasis_UnitsAndColors()
        {
            var (_, video) = VideoProject();
            var tint = Make(AnimationEffect.ColorChange, AnimationKind.Emphasis);
            tint.Color = "#8022c55e";
            var glow = Make(AnimationEffect.Glow, AnimationKind.Emphasis);
            glow.Color = "red";
            var pulse = Make(AnimationEffect.Pulse, AnimationKind.Emphasis);
            pulse.Unit = AnimationUnit.Character;
            pulse.Color = "#FFFFFF";
            video.Animations = new List<ClipAnimation> { tint, glow, pulse, Make(AnimationEffect.Wave, AnimationKind.Emphasis), Make(AnimationEffect.BoldFlash, AnimationKind.Emphasis) };
            var clean = ClipAnimations.Clean(video);
            // 글자 전용 강조는 동영상에서 빠지고, 색은 #RRGGBB(알파는 뺌·잘못된 글은 기본 색), 색을 쓰지 않는 효과는 색이 없다
            Assert.Equal(new[] { AnimationEffect.ColorChange, AnimationEffect.Glow, AnimationEffect.Pulse }, clean.Select(a => a.Effect));
            Assert.Equal(new[] { "#22C55E", "#FFE066", null }, clean.Select(a => a.Color));
            Assert.Equal(AnimationUnit.All, clean[2].Unit);
            Assert.Equal("#EF4444", Make(AnimationEffect.ColorChange, AnimationKind.Emphasis).Color);

            // 글자 클립: 물결은 한 번에로 저장돼도 글자별, 굵게 깜빡이기는 한 번에 그대로, 맥박은 단위를 나누지 않는다
            var (_, text) = TextProject();
            var wave = Make(AnimationEffect.Wave, AnimationKind.Emphasis, 10, text);
            Assert.Equal((AnimationUnit.Character, 40.0), (wave.Unit, wave.Stagger));
            wave.Unit = AnimationUnit.All;
            var bold = Make(AnimationEffect.BoldFlash, AnimationKind.Emphasis, 10, text);
            var textPulse = Make(AnimationEffect.Pulse, AnimationKind.Emphasis, 10, text);
            textPulse.Unit = AnimationUnit.Word;
            text.Animations = new List<ClipAnimation> { wave, bold, textPulse };
            Assert.Equal(new[] { AnimationUnit.Character, AnimationUnit.All, AnimationUnit.All }, ClipAnimations.Clean(text).Select(a => a.Unit));
            // 저장 글: 색은 색 효과만
            var saved = JsonNode.Parse(ProjectFile.Snapshot(VideoWith(clean)))["VideoTracks"][0]["Clips"][0]["Animations"];
            Assert.Equal("#22C55E", (string)saved[0]["Color"]);
            Assert.Null(saved[2]["Color"]);
        }

        private static Project VideoWith(List<ClipAnimation> animations)
        {
            var (project, video) = VideoProject();
            video.Animations = animations;
            return project;
        }

        [Fact]
        public void Emphasis_ColorChangeHolds_GlowFlashes_WaveAndBoldFallBack()
        {
            var (_, text) = TextProject();
            ObjectMotion With(AnimationEffect effect, double amount, bool drawn = false)
            {
                var animation = Make(effect, AnimationKind.Emphasis, 12, text);
                animation.Ease = AnimationEase.Linear;
                animation.Amount = amount;
                animation.Delay = 6;
                animation.Repeat = 1;
                text.Animations = new List<ClipAnimation> { animation };
                return AnimationMotion.ObjectMotion(text, drawn);
            }
            // 색 바꾸기: 시작 전 0, 끝나면 세기 그대로 남는다
            var tint = With(AnimationEffect.ColorChange, 80);
            var (amount, color) = tint.Tints.Single();
            Assert.Equal("#EF4444", color);
            Assert.Equal((0.0, 0.4, 0.8, 0.8), (amount.Eval(3), amount.Eval(12), amount.Eval(18), amount.Eval(50)));
            // 광선 깜빡임: 한 번의 가운데에서 가장 밝고 끝에 사라진다
            var glow = With(AnimationEffect.Glow, 24);
            var (strength, size, glowColor) = glow.Glows.Single();
            Assert.Equal((24.0, "#FFE066"), (size, glowColor));
            Assert.Equal(0, strength.Eval(6), 9);
            Assert.Equal(1, strength.Eval(12), 9);
            Assert.Equal(0, strength.Eval(18), 9);
            // 물결·굵게 깜빡이기: 그리개가 그리면 시간식에는 없고, 없으면 글 전체가 글자 크기 × 정도만큼 솟거나 조금 커진다
            Assert.True(With(AnimationEffect.Wave, 50, true).IsEmpty);
            Assert.Equal(-text.FontSize * 0.5, With(AnimationEffect.Wave, 50).ShiftY.Eval(12), 9);
            Assert.True(With(AnimationEffect.BoldFlash, 10, true).IsEmpty);
            Assert.Equal(1.1, With(AnimationEffect.BoldFlash, 10).Scale.Eval(12), 9);
            Assert.True(AnimationMotion.HasPerUnit(text));
        }

        [Fact]
        public void Graph_ColorChangeMixesChannels_GlowUnderlaysBlurredColor()
        {
            var (project, video) = VideoProject();
            var tint = Make(AnimationEffect.ColorChange, AnimationKind.Emphasis);
            tint.Ease = AnimationEase.Linear;
            tint.Color = "#00FF00";
            tint.Delay = 20;
            var glow = Make(AnimationEffect.Glow, AnimationKind.Emphasis);
            glow.Ease = AnimationEase.Linear;
            glow.Amount = 20;
            glow.Repeat = 1;
            glow.Delay = 40;
            video.Animations = new List<ClipAnimation> { tint, glow };
            var filter = RenderGraph.Build(project, Options()).Filter;
            // 색: 21장부터 3×3 값을 함께 보낸다(초록 × 밝기) — 끝난 뒤는 a = 1: rr = 0, gr = 0.299, gg = 0.587
            var commands = Regex.Match(filter, "sendcmd=c='([^']*)'").Groups[1].Value.Split(';');
            Assert.Equal("0.683333 colorchannelmixer@ct0x0 rr 0.9, colorchannelmixer@ct0x0 rg 0, colorchannelmixer@ct0x0 rb 0, colorchannelmixer@ct0x0 gr 0.0299, "
                + "colorchannelmixer@ct0x0 gg 0.9587, colorchannelmixer@ct0x0 gb 0.0114, colorchannelmixer@ct0x0 br 0, colorchannelmixer@ct0x0 bg 0, colorchannelmixer@ct0x0 bb 0.9", commands[0]);
            Assert.Contains("colorchannelmixer@ct0x0=rr=1:rg=0:rb=0:gr=0:gg=1:gb=0:br=0:bg=0:bb=1", filter);
            Assert.Contains(",colorchannelmixer=rr=0:rg=0:rb=0:gr=0.299:gg=0.587:gb=0.114:br=0:bg=0:bb=0", RenderGraph.Build(project, Options(null, 35, 36)).Filter);
            // 시작 전만 그리면 색 체인이 없다(정지 그림 그대로)
            Assert.DoesNotContain("colorchannelmixer", RenderGraph.Build(project, Options(null, 0, 20)).Filter);
            // 광선: 번질 자리(시그마 10 × 3 = 30) 넓힘, 그림을 광선 색으로 칠해 흐린 층을 밑에 — 세기는 sendcmd로
            Assert.Contains(",pad=w=iw+60:h=ih+60:x=30:y=30:color=black@0,split[gla0x0][glb0x0];[gla0x0]format=gbrap,lutrgb=r=255:g=224:b=102,gblur=sigma=10,sendcmd=c='", filter);
            Assert.Contains("[glc0x0];[glc0x0][glb0x0]overlay=format=auto,format=rgba", filter);
            // 광선이 없는 구간은 넓히기만(한 장과 내보내기가 같은 자리)
            var rest = RenderGraph.Build(project, Options(null, 52, 53)).Filter;
            Assert.Contains(",pad=w=iw+60:h=ih+60:x=30:y=30:color=black@0,", rest);
            Assert.DoesNotContain("gblur", rest);
        }

        [Fact]
        public void UnitPoses_WaveRipplesInOrder_RepeatsSeamlessly_BoldSwells_SoloSkipsInAndOut()
        {
            var (project, text) = TextProject("가나다라");
            var wave = Make(AnimationEffect.Wave, AnimationKind.Emphasis, 12, text);
            wave.Ease = AnimationEase.Linear;
            wave.Stagger = 50;
            wave.Delay = 6;
            wave.Repeat = 2;
            wave.Amount = 50;
            text.Animations = new List<ClipAnimation> { Make(AnimationEffect.Typewriter, AnimationKind.In, 4, text), wave };
            var frame = TextAnimationFrame.Of(text, project.Rate);
            var emphasis = Assert.Single(frame.Emphases);
            Assert.Equal((6L, 12L, 30L), (emphasis.Start, emphasis.Cycle, emphasis.End));
            Assert.All(frame.Poses(text.Text, 40, 6), p => Assert.True(p.IsRest));
            // 한 번 안에서 앞 글자부터 솟는다(위로 = −y), 높이는 글자 크기 × 50%까지
            var early = frame.Poses(text.Text, 40, 8);
            Assert.True(early[0].Y < -1 && early[3].Y == 0, "앞 글자부터: " + string.Join(", ", early.Select(p => p.Y)));
            Assert.True(Enumerable.Range(6, 12).SelectMany(f => frame.Poses(text.Text, 40, f)).Min(p => p.Y) >= -20 - 1e-9);
            // 되풀이는 이어진다(한 번 뒤 같은 모습), 다 끝나면 제자리
            Assert.Equal(frame.Poses(text.Text, 40, 9).Select(p => p.Y), frame.Poses(text.Text, 40, 21).Select(p => p.Y));
            Assert.All(frame.Poses(text.Text, 40, 30), p => Assert.True(p.IsRest));
            // 강조 하나만 그리는 장은 나타나기를 다 나타난 모습으로 본다(타자기 첫 장인데도 보인다)
            Assert.All(frame.Poses(text.Text, 40, 0), p => Assert.Equal(0, p.Opacity));
            Assert.All(frame.At(0, 0).Poses(text.Text, 40, 0), p => Assert.Equal(1, p.Opacity));
            // 그리개가 그림 크기를 정할 때 보는 장: 움직이는 장 + 강조 한 번(홀로)
            var states = frame.States().ToList();
            Assert.Equal(Enumerable.Range(0, 5).Concat(Enumerable.Range(6, 24)).Select(f => (long)f), states.Where(s => s.Solo < 0).Select(s => s.Frame));
            Assert.Equal(Enumerable.Range(6, 12).Select(f => (long)f), states.Where(s => s.Solo == 0).Select(s => s.Frame));

            // 굵게 깜빡이기(한 번에): 가운데에서 글자 크기 × 정도만큼 굵어진다
            var bold = Make(AnimationEffect.BoldFlash, AnimationKind.Emphasis, 10, text);
            bold.Ease = AnimationEase.Linear;
            bold.Amount = 10;
            text.Animations = new List<ClipAnimation> { bold };
            frame = TextAnimationFrame.Of(text, project.Rate);
            Assert.All(frame.Poses(text.Text, 40, 5), p => Assert.Equal(4, p.Bold, 9));
            Assert.All(frame.Poses(text.Text, 40, 0), p => Assert.True(p.IsRest));
        }

        [Fact]
        public void PerUnit_Emphasis_DrawsOneCycle_AndLoopsIt()
        {
            var (project, text) = TextProject();
            var wave = Make(AnimationEffect.Wave, AnimationKind.Emphasis, 10, text);
            wave.Repeat = 0;
            text.Animations = new List<ClipAnimation> { wave };
            var rasterizer = new FakeTextRasterizer();
            var plan = RenderGraph.Build(project, Options(rasterizer, 0, 60));
            // 한 번(10장)만 그리고 그림 묶음을 되풀이해 읽는다
            var names = plan.TextImages.Keys.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(Enumerable.Range(0, 10).Select(i => "-w0-" + i.ToString("D5") + ".png"), names.Select(n => n.Substring(67)));
            Assert.All(plan.TextImages.Values, r => Assert.Equal(0, r.Animation.Solo));
            Assert.Equal(new[] { "-loop", "1", "-framerate", "30/1", "-t", "2.016667", "-f", "image2", "-pattern_type", "sequence" }, plan.Inputs[0].Take(10));
            Assert.EndsWith("-w0-%05d.png", plan.Inputs[0][^1]);
            Assert.Contains("[0:v]trim=start_frame=0:end_frame=60,setpts=PTS-STARTPTS,format=rgba", plan.Filter);
            // 가운데부터 그리면 한 번 안의 차례부터
            Assert.Contains("trim=start_frame=5:end_frame=20,", RenderGraph.Build(project, Options(rasterizer, 25, 40)).Filter);
            Assert.EndsWith("-w0-00007.png", Assert.Single(RenderGraph.Build(project, Options(rasterizer, 37, 38)).TextImages).Key);

            // 타자기(0~14장) 뒤에 이어서: 나타나기 장들은 물결을 함께 그리고, 그 뒤는 15장부터 한 번 안의 5번째부터 되풀이
            text.Animations = new List<ClipAnimation> { Make(AnimationEffect.Typewriter, AnimationKind.In, 15, text), wave };
            plan = RenderGraph.Build(project, Options(rasterizer, 0, 60));
            Assert.Contains("trim=start_frame=5:end_frame=50,", plan.Filter);
            Assert.All(plan.TextImages.Where(i => i.Key.Contains("-i0")), i => Assert.Single(i.Value.Animation.Emphases));
            // 강조 둘이 겹치면(쉬는 동안) 장마다 그린다
            var bold = Make(AnimationEffect.BoldFlash, AnimationKind.Emphasis, 6, text);
            bold.Delay = 20;
            bold.Repeat = 1;
            text.Animations = new List<ClipAnimation> { wave, bold };
            var overlap = RenderGraph.Build(project, Options(rasterizer, 0, 60));
            var m = overlap.TextImages.Keys.Select(Path.GetFileName).Where(n => n.Contains("-m00020-")).ToList();
            Assert.Equal(6, m.Count);
            Assert.Contains("trim=start_frame=0:end_frame=20,", overlap.Filter);
            Assert.Contains("trim=start_frame=6:end_frame=40,", overlap.Filter);
            // 키: 강조가 없는 묶음은 예전 그대로(이미 그린 장을 다시 쓴다), 홀로 그리는 장은 장 키에만
            var request = new TextImageRequest { Text = "가", Style = new TextStyle(), FontPixelSize = 10, EffectScale = 1, CanvasWidth = 10, CanvasHeight = 10 };
            var single = TextAnimationFrame.Of(text, project.Rate);
            Assert.Equal(request.WithAnimation(single.At(3)).JobKey(1), request.WithAnimation(single.At(3, 0)).JobKey(1));
            Assert.NotEqual(request.WithAnimation(single.At(3)).Key(1), request.WithAnimation(single.At(3, 0)).Key(1));
        }

        [Fact]
        public void ArtOutline_Drawn_CutsLinesAtX_FollowsStrokes_FullIsUnchanged()
        {
            double MaxX(List<List<(double X, double Y)>> outline) => outline.SelectMany(p => p).Max(p => p.X);
            var line = ArtShape.Default(ArtKind.Line);
            Assert.Empty(ArtGeometry.Outline(line, 1, 0));
            Assert.Equal(200, MaxX(ArtGeometry.Outline(line, 1, 0.5)), 9);
            // 다 그리면(1 이상) 예전 윤곽과 같다(이미 그린 그림·캐시 그대로)
            foreach (var kind in new[] { ArtKind.Line, ArtKind.Arrow, ArtKind.CircleMark, ArtKind.Underline, ArtKind.Star })
            {
                var shape = ArtShape.Default(kind);
                Assert.Equal(ArtGeometry.Outline(shape, 1.5), ArtGeometry.Outline(shape, 1.5, 1));
                Assert.Equal(ArtGeometry.Outline(shape, 1.5), ArtGeometry.Outline(shape, 1.5, 3));
            }
            // 화살표: 머리도 그린 만큼만, 점선은 그 앞의 대시만
            var arrow = ArtShape.Default(ArtKind.Arrow);
            Assert.Equal(380, MaxX(ArtGeometry.Outline(arrow, 1, 0.95)), 9);
            var dashed = ArtShape.Default(ArtKind.Line);
            dashed.Dashed = true;
            Assert.True(ArtGeometry.Outline(dashed, 1, 0.3).Count < ArtGeometry.Outline(dashed, 1).Count);
            // 동그라미 치기·밑줄: 붓 선의 앞부분만(밑줄은 왼쪽 끝부터)
            var circle = ArtShape.Default(ArtKind.CircleMark);
            var half = Assert.Single(ArtGeometry.Outline(circle, 1, 0.5));
            Assert.True(half.Count < Assert.Single(ArtGeometry.Outline(circle, 1)).Count);
            Assert.True(MaxX(ArtGeometry.Outline(ArtShape.Default(ArtKind.Underline), 1, 0.3)) < 480 * 0.3 + 20);
            // 그려지지 않는 모양은 drawn과 상관없이 다 그린다
            Assert.Equal(ArtGeometry.Outline(ArtShape.Default(ArtKind.Star), 1), ArtGeometry.Outline(ArtShape.Default(ArtKind.Star), 1, 0));
        }

        [Fact]
        public void Draw_OnlyForStrokeArt_DrawnFraction_AndFramesFromTheRasterizer()
        {
            var project = TestProjects.Empty(magnetic: false);
            var line = TimelineEdits.CreateArt(ArtKind.Line, 0, 60);
            TimelineEdits.Place(project, project.MainTrack, line, 0);
            var box = TimelineEdits.CreateArt(ArtKind.Rectangle, 0, 60);
            var def = ClipAnimations.Def(AnimationEffect.Draw);
            Assert.True(ClipAnimations.Offers(line, def, AnimationKind.In) && ClipAnimations.Offers(line, def, AnimationKind.Out));
            Assert.False(ClipAnimations.Allows(box, def));
            Assert.False(ClipAnimations.Allows(TimelineEdits.CreateText("가", 0, 30), def));
            box.Animations = new List<ClipAnimation> { Make(AnimationEffect.Draw, AnimationKind.In) };
            Assert.Null(ClipAnimations.Clean(box));

            // 그린 비율: 나타나기는 이징한 진행, 지워지기는 거꾸로, 구간 밖은 정확히 1
            var draw = Make(AnimationEffect.Draw, AnimationKind.In, 10, line);
            draw.Ease = AnimationEase.Linear;
            var erase = Make(AnimationEffect.Draw, AnimationKind.Out, 10, line);
            erase.Ease = AnimationEase.Bounce;
            line.Animations = new List<ClipAnimation> { draw, erase };
            Assert.Equal("사라지기 · 지워지기", ClipAnimations.Name(erase));
            var frame = TextAnimationFrame.Of(line, project.Rate);
            Assert.Equal((0.0, 0.5, 1.0, 1.0), (frame.Drawn(0), frame.Drawn(5), frame.Drawn(10), frame.Drawn(50)));
            Assert.Equal(0, frame.Drawn(60));
            Assert.True(frame.Drawn(55) > 0 && frame.Drawn(55) < 1);

            // 그리개가 장마다 그린다(나타나기 10장 + 쉬는 장 + 지워지기 10장 + 지운 장), 그리개가 없으면 시간식에서도 빠지지 않는다(왼쪽부터 닦아내기)
            var rasterizer = new FakeTextRasterizer();
            var plan = RenderGraph.Build(project, Options(rasterizer, 0, 60));
            var names = plan.TextImages.Keys.Select(Path.GetFileName).ToList();
            Assert.All(names, n => Assert.Matches("^sa-[0-9a-f]{64}-(i[0-9]{5}|s|o[0-9]{5})\\.png$", n));
            Assert.Equal(21, names.Count);
            Assert.All(plan.TextImages.Values, r => Assert.NotNull(r.Art));
            Assert.Contains("concat=n=3", plan.Filter);
            Assert.DoesNotContain("crop=w=iw/2", plan.Filter);
            Assert.True(AnimationMotion.HasPerUnit(line));
            Assert.Equal(2, AnimationMotion.ObjectMotion(line, false).Wipes.Count);
            // 그려지기가 없는 요소는 예전처럼 그림 한 장
            line.Animations = new List<ClipAnimation> { Make(AnimationEffect.Fade, AnimationKind.In) };
            Assert.StartsWith("st-", Path.GetFileName(Assert.Single(RenderGraph.Build(project, Options(rasterizer, 0, 60)).TextImages).Key));
        }

        // ---------- 글자 나누기·글자별 모습 ----------

        [Fact]
        public void TextUnits_HangulSyllables_Words_AndClusters()
        {
            Assert.Equal(new[] { "안", "녕", "A", "B", "C" }, TextUnits.Elements("안녕 ABC"));
            void Units(int[] expected, int count, string text, AnimationUnit unit)
            {
                var (units, total) = TextUnits.Split(text, unit);
                Assert.Equal(expected, units);
                Assert.Equal(count, total);
            }
            Units(new[] { 0, 1, 2, 3, 4 }, 5, "안녕 ABC", AnimationUnit.Character);
            Units(new[] { 0, 0, 1, 1, 1 }, 2, "안녕 ABC", AnimationUnit.Word);
            Units(new[] { 0, 0, 0, 0, 0 }, 1, "안녕 ABC", AnimationUnit.All);
            // 풀어 쓴 자모(ᄒ+ᅡ+ᆫ)도 한 음절, 줄 바꿈은 단어 사이, 결합 문자·이모지 이음은 한 글자
            Assert.Single(TextUnits.Elements("\u1112\u1161\u11AB"));
            Units(new[] { 0, 1 }, 2, "가\r\n나", AnimationUnit.Word);
            Assert.Equal(2, TextUnits.Elements("e\u0301 👨‍👩‍👧").Count);
            Assert.Empty(TextUnits.Elements(" \t\n"));
        }

        [Fact]
        public void UnitPoses_TypewriterRevealsInOrder_ErasesFromTheEnd()
        {
            var (project, text) = TextProject("가나다라");
            var appear = Make(AnimationEffect.Typewriter, AnimationKind.In, 20, text);
            var vanish = Make(AnimationEffect.Typewriter, AnimationKind.Out, 20, text);
            text.Animations = new List<ClipAnimation> { appear, vanish };
            var frame = TextAnimationFrame.Of(text, project.Rate);
            Assert.Equal((0L, 20L, 40L, 20L), (frame.InStart, frame.InLength, frame.OutStart, frame.OutLength));
            int Visible(long f) => frame.Poses(text.Text, 40, f).Count(p => p.Opacity > 0);
            Assert.Equal(0, Visible(0));
            Assert.Equal(new[] { 1, 2, 3, 4 }, new long[] { 1, 6, 11, 16 }.Select(Visible));
            Assert.Equal(4, Visible(30));
            Assert.All(frame.Poses(text.Text, 40, 30), p => Assert.True(p.IsRest));
            // 사라지기: 뒤 글자부터
            var erasing = frame.Poses(text.Text, 40, 51);
            Assert.Equal(new[] { true, true, false, false }, erasing.Select(p => p.Opacity > 0));
            Assert.Equal(0, Visible(60));
        }

        [Fact]
        public void UnitPoses_ScatterIsDeterministic_FarAtStart_RestAtEnd()
        {
            var (project, text) = TextProject("흩어지는 글자");
            text.Animations = new List<ClipAnimation> { Make(AnimationEffect.Scatter, AnimationKind.In, 20, text) };
            var frame = TextAnimationFrame.Of(text, project.Rate);
            var start = frame.Poses(text.Text, 50, 0);
            Assert.Equal(6, start.Length);
            Assert.All(start, p => Assert.True(Math.Sqrt(p.X * p.X + p.Y * p.Y) >= 100 - 1e-9 && p.Opacity == 0));
            Assert.Equal(start.Select(p => (p.X, p.Y, p.Rotation)), frame.Poses(text.Text, 50, 0).Select(p => (p.X, p.Y, p.Rotation)));
            // 글자마다 방향이 다르다
            Assert.True(start.Select(p => Math.Round(Math.Atan2(p.Y, p.X), 2)).Distinct().Count() > 3);
            Assert.All(frame.Poses(text.Text, 50, 20), p => Assert.True(p.IsRest));
        }

        [Fact]
        public void UnitPoses_SplitOpensFromTheMiddle_UnblurSharpens_RiseLifts()
        {
            var (project, text) = TextProject("가나");
            ClipAnimation Unit(AnimationEffect effect, AnimationKind kind, AnimationUnit unit, double amount)
            {
                var animation = Make(effect, kind, 10, text);
                animation.Unit = unit;
                animation.Stagger = 0;
                animation.Ease = AnimationEase.Linear;
                animation.Amount = amount;
                text.Animations = new List<ClipAnimation> { animation };
                return animation;
            }
            Unit(AnimationEffect.Split, AnimationKind.In, AnimationUnit.Character, 0);
            var frame = TextAnimationFrame.Of(text, project.Rate);
            Assert.All(frame.Poses(text.Text, 40, 0), p => Assert.Equal(0, p.Visible));
            Assert.All(frame.Poses(text.Text, 40, 5), p => Assert.Equal((true, 0.5, 1, 0), (p.Split, p.Visible, p.WipeX, p.WipeY)));
            Assert.All(frame.Poses(text.Text, 40, 10), p => Assert.True(p.IsRest));

            // 흐릿하게 사라지기(단어별): 사라지기 구간 반쯤(55장)에 시그마 10 — 흐림은 프로젝트 픽셀(그리개가 효과 배율을 곱한다)
            Unit(AnimationEffect.Unblur, AnimationKind.Out, AnimationUnit.Word, 20);
            frame = TextAnimationFrame.Of(text, project.Rate);
            Assert.All(frame.Poses(text.Text, 40, 55), p => Assert.Equal((10.0, 1.0), (p.Blur, p.Opacity)));
            Assert.All(frame.Poses(text.Text, 40, 49), p => Assert.True(p.IsRest));
            Assert.False(new UnitPose { Scale = 1, Opacity = 1, Visible = 1, Blur = 0.5 }.IsRest);

            // 떠오르기: 50px(프로젝트 픽셀) 아래에서 — 출력이 두 배면 100픽셀
            Unit(AnimationEffect.Rise, AnimationKind.In, AnimationUnit.Character, 50);
            frame = TextAnimationFrame.Of(text, project.Rate);
            Assert.All(frame.Poses(text.Text, 40, 0), p => Assert.Equal((50.0, 0.0), (p.Y, p.Opacity)));
            Assert.All(frame.Poses(text.Text, 40, 0, 2), p => Assert.Equal(100.0, p.Y));
        }

        // ---------- 렌더 그래프 ----------

        [Fact]
        public void NoAnimation_GraphUnchanged_AnimatedClipDoesNotTouchOthers()
        {
            var (project, video) = VideoProject();
            var image = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 60);
            image.Rotation = 15;
            image.Opacity = 0.5;
            image.X = 20;
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), image, 0);
            var before = RenderGraph.Build(project, Options()).Filter;
            video.Animations = new List<ClipAnimation>();
            Assert.Equal(before, RenderGraph.Build(project, Options()).Filter);
            Assert.DoesNotContain("eval=frame", before);
            Assert.DoesNotContain("sendcmd", before);
            // 동영상에 애니메이션을 넣어도 사진 체인은 그대로
            video.Animations = new List<ClipAnimation> { Make(AnimationEffect.Zoom, AnimationKind.In) };
            var after = RenderGraph.Build(project, Options()).Filter.Split(";\n");
            Assert.Equal(before.Split(";\n").Skip(3), after.Skip(3));
        }

        [Fact]
        public void Graph_WholeObject_UsesTimeExpressions()
        {
            var (project, video) = VideoProject();
            video.Rotation = 10;
            video.Opacity = 0.8;
            var fly = Make(AnimationEffect.Fly, AnimationKind.In);
            fly.Delay = 15;
            var transparency = Make(AnimationEffect.Transparency, AnimationKind.Emphasis);
            transparency.Delay = 30;
            var rotate = Make(AnimationEffect.Rotate, AnimationKind.Emphasis);
            rotate.Delay = 20;
            video.Animations = new List<ClipAnimation>
            {
                fly, Make(AnimationEffect.Wipe, AnimationKind.Out), Make(AnimationEffect.Pulse, AnimationKind.Emphasis), rotate, transparency
            };
            // 다 돈 뒤(30장부터 370°)만 그리면 각도 하나로
            Assert.Contains(",pad=w=iw+4:h=ih+4:x=2:y=2:color=black@0,rotate=a='370*PI/180':c=black@0:", RenderGraph.Build(project, Options(null, 30, 60)).Filter);
            var filter = RenderGraph.Build(project, Options(null, 15, 60)).Filter;
            // 날아오기(아래에서): 화면 밖은 main_h, 시각은 장 번호 — 그리는 구간 시작이 클립 안 15장
            Assert.Contains("*(main_h-((main_h-overlay_h)/2+0))", filter);
            Assert.Contains("(round(t*30/1)+15)", filter);
            // 닦아내기(왼쪽): 두 배로 넓혀 자른다
            Assert.Contains(",pad=w=iw*2:h=ih:x=iw:y=0:color=black@0,crop=w=iw/2:h=ih:x='round(iw/2*", filter);
            Assert.Contains(",rotate=a='(10+", filter);
            Assert.Contains(":c=black@0:ow='iw+4*ceil((hypot(iw,ih)-iw)/4)':oh='ih+4*ceil((hypot(iw,ih)-ih)/4)'", filter);
            Assert.Contains(",sendcmd=c='", filter);
            Assert.Contains("colorchannelmixer@an0=aa=0.8", filter);
            Assert.Contains(":eval=frame", filter);
            Assert.DoesNotContain("rotw(", filter);
        }

        [Fact]
        public void Opacity_SendsCommandsOnlyWhereTheValueChanges()
        {
            var (project, video) = VideoProject();
            video.Animations = new List<ClipAnimation> { Make(AnimationEffect.Fade, AnimationKind.In, 10) };
            var filter = RenderGraph.Build(project, Options()).Filter;
            var commands = Regex.Match(filter, "sendcmd=c='([^']*)'").Groups[1].Value.Split(';');
            Assert.Equal(10, commands.Length);
            Assert.Equal("0.016667 colorchannelmixer@an0 aa 0.1", commands[0]);
            Assert.Equal("0.316667 colorchannelmixer@an0 aa 1", commands[^1]);
            Assert.Contains("colorchannelmixer@an0=aa=0", filter);
            // 다 나타난 뒤 한 장은 명령도 고정 값도 없다(예전 그래프와 같은 모양)
            Assert.DoesNotContain("colorchannelmixer", RenderGraph.Build(project, Options(null, 20, 21)).Filter);
        }

        [Fact]
        public void PerUnit_Text_JoinsSequenceAndStill_WithHashedNames()
        {
            var (project, text) = TextProject();
            text.Animations = new List<ClipAnimation> { Make(AnimationEffect.Typewriter, AnimationKind.In, 15, text) };
            var rasterizer = new FakeTextRasterizer();
            var plan = RenderGraph.Build(project, Options(rasterizer, 0, 30));
            var names = plan.TextImages.Keys.Select(Path.GetFileName).ToList();
            Assert.All(names, n => Assert.Matches("^sa-[0-9a-f]{64}-(i[0-9]{5}|s)\\.png$", n));
            Assert.Equal(16, names.Count);
            Assert.Single(names.Select(n => n.Substring(0, 67)).Distinct());
            Assert.Equal(new[] { "-framerate", "30/1", "-f", "image2", "-pattern_type", "sequence" }, plan.Inputs[0].Take(6));
            Assert.EndsWith("-i%05d.png", plan.Inputs[0][^1]);
            Assert.Contains("[0:v]trim=start_frame=0:end_frame=15,setpts=PTS-STARTPTS[ua2x0];[1:v]trim=end_frame=15,setpts=PTS-STARTPTS[ua2x1];[ua2x0][ua2x1]concat=n=2:v=1:a=0", plan.Filter);
            // 장마다 요청의 장 번호, 쉬는 장은 나타나기가 끝난 장
            Assert.Equal(Enumerable.Range(0, 15).Select(i => (long)i).Append(15), plan.TextImages.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Value.Animation.Frame).OrderBy(f => f));
            // 한 장(재생 헤드)은 그 장 그림 하나만 그린다
            var one = RenderGraph.Build(project, Options(rasterizer, 7, 8));
            Assert.EndsWith("-i00007.png", Assert.Single(one.TextImages).Key);
            Assert.DoesNotContain("concat", one.Filter);
            // 꾸미지 않은 글자도 글자별이면 그리개로
            text.Style = null;
            Assert.DoesNotContain("drawtext", RenderGraph.Build(project, Options(rasterizer, 0, 30)).Filter);
            Assert.True(RenderGraph.NeedsTextImage(text));
            // 그리개가 없으면 한 번에(타자기 = 왼쪽부터 닦아내기)
            Assert.Contains("crop=w=iw/2", RenderGraph.Build(project, Options(null, 0, 30)).Filter);
        }

        [Fact]
        public void PerUnit_JobKey_ChangesWithSettings_AndComesBackWhenReverted()
        {
            var (project, text) = TextProject();
            var appear = Make(AnimationEffect.Scatter, AnimationKind.In, 15, text);
            text.Animations = new List<ClipAnimation> { appear };
            var rasterizer = new FakeTextRasterizer();
            string Job() => Path.GetFileName(RenderGraph.Build(project, Options(rasterizer, 3, 4)).TextImages.Keys.Single()).Substring(3, 64);
            var first = Job();
            appear.Amount = 150;
            var changed = Job();
            appear.Amount = 100;
            Assert.NotEqual(first, changed);
            Assert.Equal(first, Job());
            // 위치·크기는 그래프에서 바뀌므로 다시 그리지 않는다
            text.X = 40;
            text.Scale = 0.5;
            Assert.Equal(first, Job());
            // 정지 그림의 키는 예전과 같다(애니메이션 값이 없을 때 키 글이 그대로)
            var request = new TextImageRequest { Text = "가", Style = new TextStyle(), FontPixelSize = 10, EffectScale = 1, CanvasWidth = 10, CanvasHeight = 10 };
            Assert.Equal(request.Key(1), request.WithAnimation(null).Key(1));
            Assert.NotEqual(request.Key(1), request.WithAnimation(new TextAnimationFrame()).Key(1));
        }
    }
}
