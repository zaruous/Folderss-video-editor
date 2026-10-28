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
    /// <summary>#4 사용자 키프레임: 사이 값(직선 + 키프레임마다 이징), 고치기 규칙, 저장 형식, 나누기·다듬기·레이트, 렌더 그래프의 기준 값 식.</summary>
    public class KeyframeTests
    {
        private static ClipKeyframe Key(KeyframeProperty property, long frame, double value, double y = 0, AnimationEase ease = AnimationEase.Linear)
        {
            return new ClipKeyframe { Property = property, Frame = frame, Value = value, Y = y, Ease = ease };
        }

        private static (Project Project, Clip Video) VideoProject()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 60);
            return (project, clip);
        }

        private static RenderOptions Options(long from = 0, long to = 60)
        {
            return new RenderOptions { From = from, To = to, Width = 640, Height = 360, Audio = false, TempFolder = Path.Combine(Path.GetTempPath(), "fv-keyframes"), TextRasterizer = null };
        }

        // ---------- 사이 값 ----------

        [Fact]
        public void Value_HoldsBeforeAndAfter_ExactAtKeys_StraightBetween()
        {
            var track = new List<ClipKeyframe> { Key(KeyframeProperty.Scale, 10, 1), Key(KeyframeProperty.Scale, 30, 2), Key(KeyframeProperty.Scale, 40, 0.5) };
            Assert.Equal(1, ClipKeyframes.Value(track, -5));
            Assert.Equal(1, ClipKeyframes.Value(track, 10));
            Assert.Equal(1.5, ClipKeyframes.Value(track, 20), 12);
            Assert.Equal(2, ClipKeyframes.Value(track, 30));
            Assert.Equal(1.25, ClipKeyframes.Value(track, 35), 12);
            Assert.Equal(0.5, ClipKeyframes.Value(track, 40));
            Assert.Equal(0.5, ClipKeyframes.Value(track, 1000));
            Assert.True(double.IsNaN(ClipKeyframes.Value(new List<ClipKeyframe>(), 3)));
            // 위치는 X·Y를 함께
            var position = new List<ClipKeyframe> { Key(KeyframeProperty.Position, 0, -100, 50), Key(KeyframeProperty.Position, 10, 100, -50) };
            Assert.Equal((0.0, 0.0), (ClipKeyframes.Value(position, 5), ClipKeyframes.Value(position, 5, true)));
        }

        [Fact]
        public void Value_EaseBelongsToTheKeyItLeaves()
        {
            // 0 → 10은 서서히 시작(p²), 10 → 20은 일정
            var track = new List<ClipKeyframe> { Key(KeyframeProperty.Rotation, 0, 0, ease: AnimationEase.In), Key(KeyframeProperty.Rotation, 10, 100), Key(KeyframeProperty.Rotation, 20, 0) };
            Assert.Equal(25, ClipKeyframes.Value(track, 5), 12);
            Assert.Equal(50, ClipKeyframes.Value(track, 15), 12);
            track[1].Ease = AnimationEase.Out;
            Assert.Equal(100 - 75, ClipKeyframes.Value(track, 15), 12);
        }

        [Fact]
        public void Expression_EqualsValue_EveryFrame_EveryEase_ExactAtKeys()
        {
            foreach (AnimationEase ease in Enum.GetValues(typeof(AnimationEase)))
            {
                var track = new List<ClipKeyframe>();
                for (var i = 0; i < 9; i++)
                    track.Add(Key(KeyframeProperty.Position, 3 + i * 7, i % 2 == 0 ? -40 - i : 60 + i * 3, i * 11 - 30, ease));
                foreach (var y in new[] { false, true })
                {
                    var expression = ClipKeyframes.Expression(track, y);
                    for (var frame = -3; frame <= 75; frame++)
                        Assert.Equal(ClipKeyframes.Value(track, frame, y), expression.Eval(frame), 12);
                    // 키프레임 시각에서는 정확히 그 값
                    foreach (var key in track)
                        Assert.Equal(y ? key.Y : key.Value, expression.Eval(key.Frame));
                }
            }
        }

        [Fact]
        public void Expression_IsBalancedIf_OneKeyIsConstant()
        {
            var one = new List<ClipKeyframe> { Key(KeyframeProperty.Opacity, 5, 0.25) };
            Assert.Equal("0.25", ClipKeyframes.Expression(one).ToFfmpeg("t"));
            var track = Enumerable.Range(0, 16).Select(i => Key(KeyframeProperty.Scale, i * 10, 1 + i % 3)).ToList();
            var text = ClipKeyframes.Expression(track).ToFfmpeg("T");
            // 열여섯 구간을 반씩 나눠 고른다 — 어느 구간이든 if를 네 번 거친다(나란히 쌓으면 열다섯 번)
            Assert.StartsWith("if(lt(T,80),if(lt(T,40),if(lt(T,20),if(lt(T,10),", text);
            Assert.Equal(15, Regex.Matches(text, "if\\(").Count);
            Assert.Contains("(1+clip((T/10),0,1))", text);
            // 값이 바뀌지 않으면 상수(고를 것이 없음)
            var still = new List<ClipKeyframe> { Key(KeyframeProperty.Position, 0, 5, 7), Key(KeyframeProperty.Position, 10, 9, 7), Key(KeyframeProperty.Position, 20, 1, 7) };
            Assert.Equal("7", ClipKeyframes.Expression(still, true).ToFfmpeg("T"));
        }

        // ---------- 고치기 ----------

        [Fact]
        public void Set_WithoutKeys_ChangesFixedValue_WithKeys_PutsKeyAtFrame()
        {
            var (_, clip) = VideoProject();
            ClipKeyframes.Set(clip, KeyframeProperty.Scale, 12, 0.5);
            ClipKeyframes.SetPosition(clip, 12, 30, -20);
            Assert.Equal((0.5, 30.0, -20.0), (clip.Scale, clip.X, clip.Y));
            Assert.Null(clip.Keyframes);

            // 키프레임을 넣으면(지금 값으로) 그 값은 키프레임으로 고친다 — 고정 값은 그대로
            Assert.True(ClipKeyframes.Toggle(clip, KeyframeProperty.Scale, 0));
            ClipKeyframes.Set(clip, KeyframeProperty.Scale, 20, 1.5);
            Assert.Equal(0.5, clip.Scale);
            Assert.Equal(new[] { (0L, 0.5), (20L, 1.5) }, ClipKeyframes.Track(clip, KeyframeProperty.Scale).Select(k => (k.Frame, k.Value)));
            Assert.Equal(1.0, ClipKeyframes.At(clip, KeyframeProperty.Scale, 10), 12);
            // 같은 시각이면 고친다
            ClipKeyframes.Set(clip, KeyframeProperty.Scale, 20, 2);
            Assert.Equal(2, ClipKeyframes.Find(clip, KeyframeProperty.Scale, 20).Value);
            Assert.Equal(2, ClipKeyframes.Track(clip, KeyframeProperty.Scale).Count);
            // 위치는 키프레임이 없으니 고정 값
            Assert.Equal((30.0, -20.0), (ClipKeyframes.At(clip, KeyframeProperty.Position, 10), ClipKeyframes.At(clip, KeyframeProperty.Position, 10, true)));
            Assert.Throws<ArgumentException>(() => ClipKeyframes.Set(clip, KeyframeProperty.Position, 0, 1));
        }

        [Fact]
        public void Toggle_AddsCurrentValue_RemovingLastLeavesItAsFixed()
        {
            var (_, clip) = VideoProject();
            clip.X = 10;
            Assert.True(ClipKeyframes.Toggle(clip, KeyframeProperty.Position, 0));
            ClipKeyframes.SetPosition(clip, 30, 70, 40);
            // 가운데에 넣으면 그 자리의 사이 값으로
            Assert.True(ClipKeyframes.Toggle(clip, KeyframeProperty.Position, 15));
            var middle = ClipKeyframes.Find(clip, KeyframeProperty.Position, 15);
            Assert.Equal((40.0, 20.0), (middle.Value, middle.Y));
            Assert.False(ClipKeyframes.Toggle(clip, KeyframeProperty.Position, 15));
            Assert.False(ClipKeyframes.Toggle(clip, KeyframeProperty.Position, 0));
            Assert.Equal(10, clip.X);
            // 마지막 키프레임을 빼면 그 값이 고정 값(화면이 그대로)
            Assert.False(ClipKeyframes.Toggle(clip, KeyframeProperty.Position, 30));
            Assert.Null(clip.Keyframes);
            Assert.Equal((70.0, 40.0), (clip.X, clip.Y));
            Assert.True(ClipKeyframes.Toggle(clip, KeyframeProperty.Opacity, 5));
            Assert.False(ClipKeyframes.Toggle(clip, KeyframeProperty.Opacity, 5));
            Assert.Equal(1, clip.Opacity);
        }

        [Fact]
        public void Limit_EaseAndMove_AllPropertiesAtOneFrame()
        {
            var (_, clip) = VideoProject();
            for (var i = 0; i < ClipKeyframes.MaxPerProperty; i++)
                ClipKeyframes.Toggle(clip, KeyframeProperty.Rotation, i);
            Assert.Throws<EditException>(() => ClipKeyframes.Toggle(clip, KeyframeProperty.Rotation, 500));
            // 다른 값은 따로 센다
            ClipKeyframes.Toggle(clip, KeyframeProperty.Opacity, 3);
            ClipKeyframes.Toggle(clip, KeyframeProperty.Opacity, 8);
            Assert.True(ClipKeyframes.SetEase(clip, 3, AnimationEase.Bounce));
            Assert.Equal(AnimationEase.Bounce, ClipKeyframes.Find(clip, KeyframeProperty.Rotation, 3).Ease);
            Assert.Equal(AnimationEase.Bounce, ClipKeyframes.Find(clip, KeyframeProperty.Opacity, 3).Ease);
            Assert.False(ClipKeyframes.SetEase(clip, 999, AnimationEase.In));
            // 3의 키프레임들을 8로 — 8에 있던 불투명도는 옮긴 것이 대신한다
            ClipKeyframes.MoveFrame(clip, 3, 8);
            Assert.Null(ClipKeyframes.Find(clip, KeyframeProperty.Opacity, 3));
            Assert.Equal(AnimationEase.Bounce, ClipKeyframes.Find(clip, KeyframeProperty.Opacity, 8).Ease);
            Assert.Single(ClipKeyframes.Track(clip, KeyframeProperty.Opacity));
            Assert.Equal(ClipKeyframes.MaxPerProperty - 1, ClipKeyframes.Track(clip, KeyframeProperty.Rotation).Count);
            Assert.Equal(Enumerable.Range(0, 100).Where(f => f != 3).Select(f => (long)f), ClipKeyframes.Frames(clip));
        }

        [Fact]
        public void Pose_TakesValuesAtFrame_WithoutKeyframes_SameIdNotInProject()
        {
            var (project, clip) = VideoProject();
            Assert.Same(clip, ClipKeyframes.Pose(clip, 3));
            clip.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Position, 0, 0, 0), Key(KeyframeProperty.Position, 10, 100, 50),
                Key(KeyframeProperty.Scale, 0, 1), Key(KeyframeProperty.Scale, 10, 0.5),
                Key(KeyframeProperty.Rotation, 10, 45), Key(KeyframeProperty.Opacity, 0, 0.2)
            };
            var pose = ClipKeyframes.Pose(clip, 5);
            Assert.Equal((50.0, 25.0, 0.75, 45.0, 0.2), (pose.X, pose.Y, pose.Scale, pose.Rotation, pose.Opacity));
            Assert.Null(pose.Keyframes);
            Assert.Equal(clip.Id, pose.Id);
            Assert.DoesNotContain(pose, project.AllClips);
            Assert.NotNull(clip.Keyframes);
        }

        // ---------- 저장·나누기 ----------

        [Fact]
        public void Read_CleansValues_DropsUnknownProperty_SubtitlesAndSoundHaveNone()
        {
            var (project, clip) = VideoProject();
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Scale, 0, 50), Key(KeyframeProperty.Opacity, 3, -1), Key(KeyframeProperty.Rotation, 4, 30), Key(KeyframeProperty.Position, 9, 1, 2) };
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var keys = json["VideoTracks"][0]["Clips"][0]["Keyframes"];
            Assert.Equal("Scale", (string)keys[0]["Property"]);
            // 일정 이징과 0인 Y는 쓰지 않는다
            Assert.Null(keys[0]["Ease"]);
            Assert.Null(keys[0]["Y"]);
            // 모르는 값·숫자가 아닌 값은 그 항목만 버린다
            keys[3]["Property"] = "Blur";
            keys.AsArray().Add(JsonNode.Parse("{\"Property\":\"Rotation\",\"Frame\":8,\"Value\":\"abc\"}"));
            var read = ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0];
            Assert.Equal(new[] { (KeyframeProperty.Scale, 20.0), (KeyframeProperty.Rotation, 30.0), (KeyframeProperty.Opacity, 0.0) },
                read.Keyframes.Select(k => (k.Property, k.Value)));
            json["VideoTracks"][0]["Clips"][0]["Keyframes"] = "wrong";
            Assert.Null(ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0].Keyframes);
            clip.Keyframes = null;
            Assert.DoesNotContain("Keyframes", ProjectFile.Snapshot(project));

            // 자막 트랙의 큐·소리만 쓰는 클립은 키프레임이 없다
            var cue = TimelineEdits.CreateText("자막", 0, 30);
            cue.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Opacity, 0, 0.5) };
            var subtitles = TimelineEdits.AddTrack(project, TrackKind.Video);
            subtitles.Subtitles = true;
            subtitles.Clips.Add(cue);
            Assert.Null(ProjectFile.FromSnapshot(ProjectFile.Snapshot(project)).VideoTracks.First(t => t.Subtitles).Clips[0].Keyframes);
            clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Opacity, 0, 0.5) };
            TimelineEdits.MakeAudioOnly(clip);
            Assert.Null(clip.Keyframes);
        }

        [Fact]
        public void Save_WritesVersion4_WithKeyframesOnly_AndLoadReadsThem()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-keys-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "p.fvproj");
                int Version() => (int)JsonNode.Parse(File.ReadAllText(path))["Version"];
                var (project, clip) = VideoProject();
                ProjectFile.Save(project, path);
                Assert.Equal(2, Version());
                clip.Keyframes = new List<ClipKeyframe> { Key(KeyframeProperty.Position, 0, -10, 5, AnimationEase.InOut), Key(KeyframeProperty.Position, 20, 10, -5) };
                ProjectFile.Save(project, path);
                Assert.Equal(4, Version());
                var loaded = ProjectFile.Load(path).MainTrack.Clips[0];
                Assert.Equal(new[] { (0L, -10.0, 5.0, AnimationEase.InOut), (20L, 10.0, -5.0, AnimationEase.Linear) },
                    loaded.Keyframes.Select(k => (k.Frame, k.Value, k.Y, k.Ease)));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Split_And_TrimStart_KeepTheSameMotion_PruneHiddenKeys_RateChangeConverts()
        {
            var (project, clip) = VideoProject();
            clip.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Rotation, 0, 0), Key(KeyframeProperty.Rotation, 20, 90, ease: AnimationEase.InOut), Key(KeyframeProperty.Rotation, 40, 0), Key(KeyframeProperty.Rotation, 55, 30)
            };
            var before = Enumerable.Range(0, 60).Select(f => ClipKeyframes.At(clip, KeyframeProperty.Rotation, f)).ToList();
            var copy = clip.CloneWithNewId();
            copy.Keyframes[0].Value = 99;
            Assert.Equal(0, clip.Keyframes[0].Value);

            var right = Assert.Single(TimelineEdits.Split(project, new[] { clip }, 30));
            // 같은 시각(타임라인)에 같은 값
            for (var f = 0; f < 30; f++)
                Assert.Equal(before[f], ClipKeyframes.At(clip, KeyframeProperty.Rotation, f), 12);
            for (var f = 30; f < 60; f++)
                Assert.Equal(before[f], ClipKeyframes.At(right, KeyframeProperty.Rotation, f - 30), 12);
            // 왼쪽(0~29)은 뒤의 첫 키프레임(40)만, 오른쪽은 앞의 마지막(20 → −10)만 밖에 남는다
            Assert.Equal(new long[] { 0, 20, 40 }, ClipKeyframes.Frames(clip));
            Assert.Equal(new long[] { -10, 10, 25 }, ClipKeyframes.Frames(right));

            // 앞을 다듬으면 당긴다(같은 장면에 같은 값) — 왼쪽 조각(0~29)의 앞 5장을 잘라 낸다
            var values = Enumerable.Range(0, 30).Select(f => ClipKeyframes.At(clip, KeyframeProperty.Rotation, f)).ToList();
            TimelineEdits.TrimStart(project, clip, 5);
            Assert.Equal(5, clip.Start);
            Assert.Equal(new long[] { -5, 15, 35 }, ClipKeyframes.Frames(clip));
            for (var f = 5; f < 30; f++)
                Assert.Equal(values[f], ClipKeyframes.At(clip, KeyframeProperty.Rotation, f - 5), 12);

            // 프레임 레이트를 바꾸면 초 기준으로
            ProjectSettings.ConvertClip(right, new FrameRate(30, 1), new FrameRate(60, 1));
            Assert.Equal(new long[] { -20, 20, 50 }, ClipKeyframes.Frames(right));
        }

        // ---------- 렌더 그래프 ----------

        [Fact]
        public void NoKeyframes_GraphUnchanged_OneKeyEqualToFixedValue_SameGraph()
        {
            var (project, video) = VideoProject();
            video.X = -0.0;
            video.Y = 12.5;
            video.Scale = 0.7;
            video.Rotation = 20;
            video.Opacity = 0.6;
            var image = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 60);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), image, 0);
            var before = RenderGraph.Build(project, Options()).Filter;
            video.Keyframes = new List<ClipKeyframe>();
            Assert.Equal(before, RenderGraph.Build(project, Options()).Filter);
            // 키프레임이 하나면 상수 — 고정 값과 같으면 그래프 글도 같다
            video.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Position, 7, -0.0, 12.5), Key(KeyframeProperty.Scale, 3, 0.7), Key(KeyframeProperty.Rotation, 9, 20), Key(KeyframeProperty.Opacity, 1, 0.6)
            };
            Assert.Equal(before, RenderGraph.Build(project, Options()).Filter);
            Assert.DoesNotContain("eval=frame", before);
            Assert.DoesNotContain("if(", before);
        }

        [Fact]
        public void Graph_Keyframes_BaseExpressions_ScaleFromLargestBox_OpacityCommands()
        {
            var (project, video) = VideoProject();
            video.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Position, 0, -100, 0), Key(KeyframeProperty.Position, 20, 100, 30),
                Key(KeyframeProperty.Scale, 0, 0.5), Key(KeyframeProperty.Scale, 20, 0.25),
                Key(KeyframeProperty.Rotation, 0, 0), Key(KeyframeProperty.Rotation, 30, 90),
                Key(KeyframeProperty.Opacity, 10, 1), Key(KeyframeProperty.Opacity, 20, 0.5)
            };
            var filter = RenderGraph.Build(project, Options(5, 60)).Filter;
            // 틀은 가장 큰 크기(0.5 — 640×360의 반), 장마다 기준 ÷ 0.5만큼 더 줄인다
            Assert.Contains("scale=w=320:h=180", filter);
            Assert.Contains(":eval=frame", filter);
            Assert.Contains("/0.5)", filter);
            // 위치: 화면 가운데 + 키프레임 사이 값(출력 픽셀 — 640/640이라 그대로), 시각은 바탕 흐름 + 그리기 시작의 클립 안 장(5)
            var x = ClipKeyframes.Expression(ClipKeyframes.Track(video, KeyframeProperty.Position)).ToFfmpeg("(round(t*30/1)+5)");
            Assert.StartsWith("if(lt((round(t*30/1)+5),20),((-100)+(200*clip(((round(t*30/1)+5)/20),0,1))),100)", x);
            Assert.Contains("overlay=x='(main_w-overlay_w)/2+" + x + "'", filter);
            // 회전: 바뀌는 각도라 대각선 크기 rotate
            Assert.Contains(",rotate=a='", filter);
            Assert.Contains(":c=none:ow='hypot(iw,ih)':oh=ow", filter);
            // 불투명도: 키프레임 시각에 정확히 그 값(구간 시작 5 → 클립 안 15장이 흐름의 10장)
            var commands = Regex.Match(filter, "sendcmd=c='([^']*)'").Groups[1].Value.Split(';');
            Assert.Equal("0.183333 colorchannelmixer@an0 aa 0.95", commands[0]);
            Assert.Contains("colorchannelmixer@an0=aa=1", filter);
            Assert.Equal("0.483333 colorchannelmixer@an0 aa 0.5", commands[^1]);
        }

        [Fact]
        public void Graph_KeyframesAndEffects_Combine_PositionAdds_ScaleAndOpacityMultiply()
        {
            var (project, video) = VideoProject();
            video.Keyframes = new List<ClipKeyframe>
            {
                Key(KeyframeProperty.Position, 0, 50, 0), Key(KeyframeProperty.Position, 30, -50, 0),
                Key(KeyframeProperty.Scale, 0, 1), Key(KeyframeProperty.Scale, 30, 0.5),
                Key(KeyframeProperty.Opacity, 0, 0.5), Key(KeyframeProperty.Opacity, 30, 1)
            };
            var fade = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fade), AnimationKind.In, FrameRate.Default);
            fade.Duration = 10;
            var zoom = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Zoom), AnimationKind.Out, FrameRate.Default);
            zoom.Duration = 10;
            video.Animations = new List<ClipAnimation> { fade, zoom };
            var filter = RenderGraph.Build(project, Options()).Filter;
            // 크기 = 기준 ÷ 틀 × 효과(축소)
            Assert.Contains("scale=w='max(1,round(iw*(if(lt(", filter);
            // 불투명도 = 기준 × 서서히: 5장은 기준 0.5 + 0.5 × 5/30, 효과 0.5
            var commands = Regex.Match(filter, "sendcmd=c='([^']*)'").Groups[1].Value.Split(';');
            var expected = RenderGraph.N((0.5 + 0.5 * 5 / 30.0) * 0.5);
            Assert.Contains(commands, c => c.EndsWith(" aa " + expected, StringComparison.Ordinal) && c.StartsWith("0.15 ", StringComparison.Ordinal));
            // 위치 = 기준 + 효과: 왼쪽에서 날아오기는 기준 위치 식 위에 화면 밖까지의 무게(세로 기준은 바뀌지 않아 숫자)
            var fly = ClipAnimations.Create(ClipAnimations.Def(AnimationEffect.Fly), AnimationKind.In, FrameRate.Default);
            fly.Direction = AnimationDirection.Left;
            video.Animations = new List<ClipAnimation> { fly };
            filter = RenderGraph.Build(project, Options()).Filter;
            Assert.Contains("*(-overlay_w-((main_w-overlay_w)/2+if(lt(round(t*30/1),30),(50+((-100)*clip((round(t*30/1)/30),0,1))),(-50))))", filter);
            Assert.Contains(":y='(main_h-overlay_h)/2+0'", filter);
        }
    }
}
