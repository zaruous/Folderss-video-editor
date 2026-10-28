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
                    var appear = Make(def.Effect, AnimationKind.In, 12, text);
                    appear.Unit = AnimationUnit.All;
                    appear.Ease = ease;
                    var vanish = Make(def.Effect, AnimationKind.Out, 12, text);
                    vanish.Unit = AnimationUnit.All;
                    vanish.Ease = ease;
                    text.Animations = new List<ClipAnimation> { appear };
                    var motion = AnimationMotion.ObjectMotion(text, false);
                    Assert.False(motion.IsEmpty, def.Name);
                    var rest = (1.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
                    Assert.Equal(rest, Pose(motion, 12));
                    Assert.Equal(rest, Pose(motion, 40));
                    var start = Pose(motion, 0);
                    Assert.True(start.Scale == 0 || start.Opacity == 0 || start.Fly == 1 || start.Visible == 0, def.Name + " " + ease + "은(는) 처음에 보이지 않는다");
                    // 구간 앞(지연)도 보이지 않는다
                    appear.Delay = 5;
                    var early = Pose(AnimationMotion.ObjectMotion(text, false), 2);
                    Assert.True(early.Scale == 0 || early.Opacity == 0 || early.Fly == 1 || early.Visible == 0, def.Name + " 지연 동안");
                    Assert.Equal(rest, Pose(AnimationMotion.ObjectMotion(text, false), 17));

                    text.Animations = new List<ClipAnimation> { vanish };
                    motion = AnimationMotion.ObjectMotion(text, false);
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
