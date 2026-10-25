using System;
using System.Collections.Generic;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>동영상 효과(스캔 라인): 값 저장·검사·복사, 렌더 그래프에서의 자리·시각·인물 마스크.</summary>
    public class VideoEffectsTests
    {
        /// <summary>렌더 그래프 시험용 인물 마스크 제공자: 정한 원본에만 마스크 경로를 돌려준다.</summary>
        private sealed class FakeMasks : IPersonMaskProvider
        {
            private readonly Dictionary<string, string> _masks;

            public FakeMasks(Dictionary<string, string> masks)
            {
                _masks = masks;
            }

            public int Calls { get; private set; }

            public string Find(string sourcePath)
            {
                Calls++;
                return sourcePath != null && _masks.TryGetValue(sourcePath, out var mask) ? mask : null;
            }
        }

        private static RenderOptions Options(Project project, long from, long to, IPersonMaskProvider masks = null)
        {
            return new RenderOptions { From = from, To = to, Width = project.Width, Height = project.Height, Audio = false, Preview = true, TempFolder = "T:/tmp", PersonMasks = masks };
        }

        private static Clip WithScanLine(Clip clip, Action<ClipEffect> change = null)
        {
            var effect = VideoEffects.Create(ScanLine.Def);
            change?.Invoke(effect);
            clip.Effects ??= new List<ClipEffect>();
            clip.Effects.Add(effect);
            return clip;
        }

        // ---------- 값·저장 ----------

        [Fact]
        public void Catalog_HasScanLine_WithTypedParams()
        {
            var def = VideoEffects.Find(ScanLine.Id);
            Assert.Same(ScanLine.Def, def);
            Assert.Equal("스캔 라인", def.Name);
            Assert.Equal(new[] { "glow", "core", "bend", "jitter", "period", "opacity", "color" }, def.Params.Select(p => p.Key));
            Assert.All(def.Params.Where(p => p.Kind == EffectParamKind.Number), p => Assert.InRange(p.Default, p.Min, p.Max));
            Assert.Equal("#2F7BFF", def.ColorParam.DefaultColor);
            Assert.Equal(0.8, def.Param("period").Min);
            Assert.Equal(6, def.Param("period").Max);
            // id는 모두 형식에 맞고 겹치지 않는다
            Assert.All(VideoEffects.All, d => Assert.True(VideoEffects.IsValidId(d.Id)));
            Assert.Equal(VideoEffects.All.Count, VideoEffects.All.Select(d => d.Id).Distinct().Count());
        }

        [Fact]
        public void Json_EmptyEffectsAreOmitted_AndValuesRoundTrip()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            // 효과가 없으면 예전 형식 그대로(키 자체가 없다), 형식 번호도 그대로
            Assert.DoesNotContain("Effects", ProjectFile.Snapshot(project));
            Assert.Equal(2, Project.CurrentVersion);

            VideoEffects.Add(project, clip, ScanLine.Def);
            clip.Effects[0].PersonOnly = true;
            clip.Effects[0].Color = "#FF0000";
            VideoEffects.SetNumber(clip.Effects[0], "glow", 77);
            var json = ProjectFile.Snapshot(project);
            Assert.Contains("\"Effects\":[{\"Id\":\"scan-line\"", json);
            var back = ProjectFile.FromSnapshot(json).FindClip(clip.Id).Effects.Single();
            Assert.Equal("scan-line", back.Id);
            Assert.True(back.PersonOnly);
            Assert.Equal("#FF0000", back.Color);
            Assert.Equal(77, back.Values["glow"]);
            Assert.Equal(3, back.Values["core"]);

            // 마지막 효과를 빼면 다시 빠진다(빈 목록을 남기지 않음)
            VideoEffects.Remove(clip, 0);
            Assert.Null(clip.Effects);
            Assert.DoesNotContain("Effects", ProjectFile.Snapshot(project));
        }

        /// <summary>스냅샷 JSON에서 첫 "Effects" 값(목록)을 value로 바꾼다.</summary>
        private static string ReplaceEffects(string json, string value)
        {
            var start = json.IndexOf("\"Effects\":", StringComparison.Ordinal) + "\"Effects\":".Length;
            var depth = 0;
            var inString = false;
            for (var i = start; i < json.Length; i++)
            {
                var ch = json[i];
                if (inString)
                {
                    if (ch == '\\')
                        i++;
                    else if (ch == '"')
                        inString = false;
                    continue;
                }
                if (ch == '"')
                    inString = true;
                else if (ch == '[' || ch == '{')
                    depth++;
                else if ((ch == ']' || ch == '}') && --depth == 0)
                    return json.Substring(0, start) + value + json.Substring(i + 1);
            }
            throw new InvalidOperationException("Effects가 없습니다: " + json);
        }

        [Fact]
        public void Json_Lenient_BadListOrBadItemDoesNotBreakTheProject()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip);
            var json = ProjectFile.Snapshot(project);
            var good = "{\"Id\":\"scan-line\",\"Values\":{\"glow\":12}}";
            // 목록이 아닌 값 → 효과 없음
            var notList = ProjectFile.FromSnapshot(ReplaceEffects(json, "\"scan-line\""));
            Assert.Null(notList.FindClip(clip.Id).Effects);
            // 형식이 틀린 항목만 버린다(숫자 자리에 글, 객체가 아닌 항목)
            var mixed = ReplaceEffects(json, "[{\"Id\":\"scan-line\",\"Values\":{\"glow\":\"많이\"}},7," + good + ",null]");
            var effects = ProjectFile.FromSnapshot(mixed).FindClip(clip.Id).Effects;
            Assert.Single(effects);
            Assert.Equal(12, effects[0].Values["glow"]);
        }

        [Fact]
        public void Validate_ClampsValues_NormalizesColor_DropsBadIds_KeepsUnknownEffects()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip);
            var json = ProjectFile.Snapshot(project);
            var effects = "["
                + "{\"Id\":\"scan-line\",\"Values\":{\"glow\":1e9,\"core\":-5,\"period\":0.1,\"opacity\":50,\"evil\":3},\"Color\":\"#2f7bff\"},"
                + "{\"Id\":\"scan-line\",\"Color\":\"#802F7BFF\",\"PersonOnly\":true},"
                + "{\"Id\":\"scan-line\",\"Color\":\"red';[x]\"},"
                + "{\"Id\":\"Bad Id;[x]\"},"
                + "{\"Id\":\"future-glitch\",\"Values\":{\"amount\":3,\"Bad-Key\":1},\"Color\":\"nope\"}]";
            var loaded = ProjectFile.FromSnapshot(ReplaceEffects(json, effects)).FindClip(clip.Id).Effects;
            Assert.Equal(4, loaded.Count);
            // 범위 안으로, 모르는 키는 버림, 색은 대문자 #RRGGBB
            Assert.Equal(150, loaded[0].Values["glow"]);
            Assert.Equal(1, loaded[0].Values["core"]);
            Assert.Equal(0.8, loaded[0].Values["period"]);
            Assert.Equal(50, loaded[0].Values["opacity"]);
            Assert.False(loaded[0].Values.ContainsKey("evil"));
            Assert.Equal("#2F7BFF", loaded[0].Color);
            // 빠진 값은 기본값으로 채우고, 알파가 든 색은 알파를 뺀다
            Assert.Equal(40, loaded[1].Values["glow"]);
            Assert.Equal("#2F7BFF", loaded[1].Color);
            Assert.True(loaded[1].PersonOnly);
            // 잘못된 색 → 기본 색
            Assert.Equal("#2F7BFF", loaded[2].Color);
            // 모르는 효과(새 판에서 만든 것)는 남겨 두되 형식이 맞는 값만, 그리지는 않는다
            Assert.Equal("future-glitch", loaded[3].Id);
            Assert.Equal(new[] { "amount" }, loaded[3].Values.Keys);
            Assert.Null(loaded[3].Color);
            Assert.Equal(3, VideoEffects.Renderable(new Clip { Kind = ClipKind.Media, HasVideo = true, Effects = loaded }).Count);
            Assert.Equal("알 수 없는 효과", VideoEffects.Name(loaded[3]));
        }

        [Fact]
        public void Validate_EffectsOnlyOnVisualMediaClips_AndAtMostEight()
        {
            var project = TestProjects.Empty();
            var video = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, video, 0);
            for (var i = 0; i < 10; i++)
                WithScanLine(video);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var text = TimelineEdits.CreateText("제목", 0, 30);
            TimelineEdits.Place(project, overlay, text, 0);
            WithScanLine(text);
            var music = TimelineEdits.CreateClip(TestProjects.Audio(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.AudioTracks[0], music, 0);
            WithScanLine(music);
            var loaded = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            Assert.Equal(VideoEffects.MaxPerClip, loaded.FindClip(video.Id).Effects.Count);
            Assert.Null(loaded.FindClip(text.Id).Effects);
            Assert.Null(loaded.FindClip(music.Id).Effects);
        }

        [Fact]
        public void Edits_Add_RefusesTextLockedAndFullClips_RemoveEmptiesList()
        {
            var project = TestProjects.Empty();
            var video = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, video, 0);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var text = TimelineEdits.CreateText("제목", 0, 30);
            TimelineEdits.Place(project, overlay, text, 0);
            Assert.Throws<EditException>(() => VideoEffects.Add(project, text, ScanLine.Def));
            for (var i = 0; i < VideoEffects.MaxPerClip; i++)
                VideoEffects.Add(project, video, ScanLine.Def);
            Assert.Throws<EditException>(() => VideoEffects.Add(project, video, ScanLine.Def));
            project.MainTrack.Locked = true;
            var image = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 30);
            TimelineEdits.Place(project, overlay, image, 60);
            overlay.Locked = true;
            Assert.Throws<EditException>(() => VideoEffects.Add(project, image, ScanLine.Def));
            for (var i = VideoEffects.MaxPerClip - 1; i >= 0; i--)
                VideoEffects.Remove(video, i);
            Assert.Null(video.Effects);
            // 값은 범위 안으로만 바뀌고 NaN은 기본값
            var effect = VideoEffects.Create(ScanLine.Def);
            VideoEffects.SetNumber(effect, "bend", 500);
            VideoEffects.SetNumber(effect, "core", double.NaN);
            VideoEffects.SetNumber(effect, "nothing", 1);
            Assert.Equal(100, effect.Values["bend"]);
            Assert.Equal(3, effect.Values["core"]);
            Assert.False(effect.Values.ContainsKey("nothing"));
        }

        // ---------- 고르기 매개변수·순서 바꾸기(#28) ----------

        [Fact]
        public void Choice_RayDirection_IsAChoiceOfFour()
        {
            var dir = RayScan.Def.Param("dir");
            Assert.Equal(EffectParamKind.Choice, dir.Kind);
            Assert.Equal(new[] { "왼쪽 → 오른쪽", "오른쪽 → 왼쪽", "위 → 아래", "아래 → 위" }, dir.Options);
            Assert.Equal(0, dir.Min);
            Assert.Equal(3, dir.Max);
            Assert.Equal(0, dir.Default);
            Assert.Equal(0, VideoEffects.Create(RayScan.Def).Values["dir"]);
        }

        [Fact]
        public void Choice_OldNumberValuesOpenAsTheNearestOption()
        {
            // 예전 판은 방향을 0~3 숫자(슬라이더)로 저장했다 — 그리기는 가까운 정수였으므로 열 때 같은 선택지로
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip);
            var json = ProjectFile.Snapshot(project);
            // 클립 하나에 8개까지
            var olds = new[] { "0", "1", "2", "3", "2.4", "2.5", "-1", "9" };
            var effects = "[" + string.Join(",", olds.Select(v => "{\"Id\":\"ray-scan\",\"Values\":{\"dir\":" + v + "}}")) + "]";
            var loaded = ProjectFile.FromSnapshot(ReplaceEffects(json, effects)).FindClip(clip.Id).Effects;
            Assert.Equal(new double[] { 0, 1, 2, 3, 2, 3, 0, 3 }, loaded.Select(e => e.Values["dir"]));
            // 저장하면 다시 숫자로 — 예전 판에서 열어도 같은 방향
            var again = ProjectFile.Snapshot(ProjectFile.FromSnapshot(ReplaceEffects(json, effects)));
            Assert.Contains("\"dir\":2,", again.Replace(" ", ""));
        }

        [Fact]
        public void Choice_SetAndRead_RoundToAnOption()
        {
            var effect = VideoEffects.Create(RayScan.Def);
            VideoEffects.SetNumber(effect, "dir", 2.6);
            Assert.Equal(3, effect.Values["dir"]);
            VideoEffects.SetNumber(effect, "dir", -4);
            Assert.Equal(0, effect.Values["dir"]);
            // 값 사전에 직접 들어간 소수(남이 만든 파일을 고치기 전)도 읽을 때는 선택지로
            effect.Values["dir"] = 1.2;
            Assert.Equal(1, VideoEffects.Number(effect, RayScan.Def, "dir"));
        }

        [Fact]
        public void Move_ChangesTheDrawingOrder_RefusesLockedTracks_IgnoresBadIndexes()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            foreach (var def in new[] { ScanLine.Def, RgbSplit.Def, ShakeZoom.Heartbeat })
                VideoEffects.Add(project, clip, def);
            string Order() => string.Join(",", VideoEffects.Renderable(clip).Select(r => r.Def.Id));

            Assert.True(VideoEffects.Move(project, clip, 2, 0));
            Assert.Equal(string.Join(",", ShakeZoom.Heartbeat.Id, ScanLine.Id, RgbSplit.Def.Id), Order());
            Assert.True(VideoEffects.Move(project, clip, 0, 1));
            Assert.Equal(string.Join(",", ScanLine.Id, ShakeZoom.Heartbeat.Id, RgbSplit.Def.Id), Order());

            var before = Order();
            Assert.False(VideoEffects.Move(project, clip, 1, 1));
            Assert.False(VideoEffects.Move(project, clip, -1, 0));
            Assert.False(VideoEffects.Move(project, clip, 0, 3));
            Assert.False(VideoEffects.Move(project, new Clip { Kind = ClipKind.Media, HasVideo = true }, 0, 1));
            Assert.Equal(before, Order());

            project.MainTrack.Locked = true;
            Assert.Throws<EditException>(() => VideoEffects.Move(project, clip, 0, 1));
            Assert.Equal(before, Order());
        }

        [Fact]
        public void Split_And_Clone_DeepCopyEffects()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            clip.SourceOut = 90;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip, e => e.PersonOnly = true);
            var right = TimelineEdits.Split(project, new[] { clip }, 45).Single();
            Assert.NotSame(clip.Effects, right.Effects);
            Assert.NotSame(clip.Effects[0], right.Effects[0]);
            Assert.NotSame(clip.Effects[0].Values, right.Effects[0].Values);
            Assert.True(right.Effects[0].PersonOnly);
            VideoEffects.SetNumber(right.Effects[0], "glow", 5);
            Assert.Equal(40, clip.Effects[0].Values["glow"]);

            // 복사·붙여넣기·복제가 쓰는 사본
            var copy = clip.CloneWithNewId();
            VideoEffects.SetNumber(copy.Effects[0], "glow", 9);
            copy.Effects[0].Color = "#00FF00";
            Assert.Equal(40, clip.Effects[0].Values["glow"]);
            Assert.Equal("#2F7BFF", clip.Effects[0].Color);
            // 사운드 분리로 만든 소리 클립에는 효과가 따라가지 않는다
            var sound = TimelineEdits.DetachAudio(project, clip);
            Assert.Null(sound.Effects);
            Assert.NotNull(clip.Effects);
        }

        // ---------- 렌더 그래프 ----------

        [Fact]
        public void Graph_EffectSitsAfterScale_BeforeRotate()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            clip.SourceOut = 60;
            clip.Fit = ClipFit.Fill;
            clip.Rotation = 30;
            clip.Opacity = 0.5;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip);
            var filter = RenderGraph.Build(project, Options(project, 0, 60)).Filter;
            var scale = filter.IndexOf("scale=w=640:h=360:force_original_aspect_ratio=increase,crop=640:360,format=gbrap", StringComparison.Ordinal);
            var effect = filter.IndexOf("[fx0e0a]", StringComparison.Ordinal);
            var blend = filter.IndexOf("[fx0e0v][fx0e0a]blend=c0_mode=screen:c1_mode=screen:c2_mode=screen:c3_mode=multiply", StringComparison.Ordinal);
            var rotate = filter.IndexOf(",rotate=", StringComparison.Ordinal);
            Assert.True(scale >= 0 && scale < effect && effect < blend && blend < rotate, filter);
            Assert.True(rotate < filter.IndexOf("colorchannelmixer=aa=0.5", StringComparison.Ordinal));
            // 효과가 든 체인에는 크기 바꾸기가 하나뿐(scale을 잇지 않음) — 무늬 줄의 scale은 따로 떨어진 체인
            var clipChain = filter.Split(";\n").Single(c => c.Contains("[fx0e0a]"));
            var mainPart = clipChain.Split(';')[0];
            Assert.Equal(1, CountOf(mainPart, "scale="));
            // 효과 없는 클립은 예전 그대로(rgba)
            clip.Effects = null;
            Assert.Contains("crop=640:360,format=rgba,rotate=", RenderGraph.Build(project, Options(project, 0, 60)).Filter);
        }

        private static int CountOf(string text, string part)
        {
            var count = 0;
            for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        [Fact]
        public void Graph_FitIsPaddedToTheBox_OtherFitsAreNot()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 60);
            clip.Scale = 0.5;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip);
            var fit = RenderGraph.Build(project, Options(project, 0, 60)).Filter;
            Assert.Contains("scale=w=320:h=180:force_original_aspect_ratio=decrease,format=gbrap,pad=320:180:(ow-iw)/2:(oh-ih)/2:color=black@0", fit);
            // 효과는 틀 크기(320×180)에서 — 검은 틀과 띠 너비가 틀과 같다
            Assert.Contains("color=c=black:s=320x180:r=30/1:d=2,format=gbrap[fx0e0c]", fit);
            Assert.Contains("color=c=black:s=320x", fit);
            foreach (var other in new[] { ClipFit.Fill, ClipFit.Stretch, ClipFit.Blur })
            {
                clip.Fit = other;
                var filter = RenderGraph.Build(project, Options(project, 0, 60)).Filter;
                Assert.DoesNotContain(",pad=", filter);
                Assert.Contains("[fx0e0a]", filter);
            }
        }

        [Fact]
        public void Graph_TimeIsClipLocal_ForSeekAndMidSegmentStarts()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            clip.SourceOut = 90;
            TimelineEdits.Place(project, project.MainTrack, clip, 30);
            WithScanLine(clip, e => VideoEffects.SetNumber(e, "period", 2));
            // 처음부터: 클립이 1초에서 시작해도 흐름의 0초 = 클립 시작(그리기는 클립 시작부터) → 0
            Assert.Contains("mod(t+0,2)/1.6", RenderGraph.Build(project, Options(project, 0, 120)).Filter);
            // 클립 안 2초(프레임 90)부터 한 장: 흐름의 0초 = 클립 안 2초
            var seek = RenderGraph.Build(project, Options(project, 90, 91)).Filter;
            Assert.Contains("mod(t+2,2)/1.6", seek);
            // 일그러짐의 어둠 자리·무늬 자리도 같은 시각을 쓴다
            Assert.Contains("crop=640:", seek);
            Assert.Contains("mod(floor((t+2)*12+0.001)*397,640)", seek);
            // 재생(프레임 75부터) → 1.5초
            Assert.Contains("mod(t+1.5,2)/1.6", RenderGraph.Build(project, Options(project, 75, 120)).Filter);
        }

        [Fact]
        public void Graph_PersonOnly_MaskTakesTheSourceAspect_AndShiftsByTheVideoStart()
        {
            // 마스크는 모델 크기(448×256 등)라 원본(16:9)과 비율이 다르다 — 마스크 비율이 아니라 원본 크기로 계산한 클립 그림 크기로 늘인다
            Assert.Equal((320, 180), RenderGraph.FitSize(1920, 1080, 320, 240, false));
            Assert.Equal((427, 240), RenderGraph.FitSize(1920, 1080, 320, 240, true));
            Assert.Equal((1440, 1080), RenderGraph.FitSize(1440, 1080, 1920, 1080, false));
            Assert.Equal((1920, 1440), RenderGraph.FitSize(1440, 1080, 1920, 1080, true));
            Assert.Equal((1080, 1920), RenderGraph.FitSize(1080, 1920, 1080, 1920, false));

            var project = TestProjects.Empty();
            project.Width = 320;
            project.Height = 240;
            var media = TestProjects.Video("wide.mp4");
            media.Width = 1920;
            media.Height = 1080;
            media.VideoStart = 0.4;
            project.Media.Add(media);
            var clip = TimelineEdits.CreateClip(media, project.Rate, 0, 0);
            clip.Fit = ClipFit.Fit;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip, e => e.PersonOnly = true);
            var masks = new FakeMasks(new Dictionary<string, string> { ["wide.mp4"] = "C:/masks/wide.mkv" });
            var options = new RenderOptions { From = 0, To = 1, Width = 320, Height = 240, Audio = false, TempFolder = @"C:\Temp", PersonMasks = masks };
            var plan = RenderGraph.Build(project, options);
            Assert.Contains(",scale=320:180,pad=320:240:(ow-iw)/2:(oh-ih)/2,format=gbrap[fx0m0]", plan.Filter);
            // 화면이 0.4초 늦게 시작하는 원본: 원본은 -ss 0이면 첫 화면부터, 마스크(첫 화면부터 만든 것)는 0.4초 당긴 자리 — 처음에선 둘 다 0
            Assert.Equal("0", plan.Inputs[0][1]);
            Assert.Equal("0", plan.Inputs[1][1]);
            // 1초부터 그리면 원본 1초 ↔ 마스크 0.6초
            options.From = 30;
            options.To = 31;
            plan = RenderGraph.Build(project, options);
            Assert.Equal("1", plan.Inputs[0][1]);
            Assert.Equal("0.6", plan.Inputs[1][1]);
            // 채우기는 키워 가운데를 자른다
            clip.Fit = ClipFit.Fill;
            Assert.Contains(",scale=427:240,crop=320:240,format=gbrap[fx0m0]", RenderGraph.Build(project, options).Filter);
        }

        [Fact]
        public void Probe_RecordsHowLateTheVideoStarts_AndOmitsZero()
        {
            const string late = "{\"format\":{\"format_name\":\"mov,mp4\",\"duration\":\"3.0\",\"start_time\":\"0.000000\"},"
                + "\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":2,\"start_time\":\"0.000000\"},"
                + "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"avg_frame_rate\":\"30/1\",\"start_time\":\"0.400000\"}]}";
            Assert.Equal(0.4, FfmpegText.ParseProbe("late.mp4", late).VideoStart, 6);
            // 파일 시작이 0이 아니어도(방송 파일) 화면 시작과의 차이만, 차이가 없으면 0
            var shifted = late.Replace("\"start_time\":\"0.000000\"},\"streams\"", "\"start_time\":\"1.400000\"},\"streams\"").Replace("0.400000", "1.400000");
            Assert.Equal(0, FfmpegText.ParseProbe("ts.mp4", shifted).VideoStart);
            var project = TestProjects.Empty();
            project.Media.Add(TestProjects.Video("plain.mp4"));
            Assert.DoesNotContain("VideoStart", ProjectFile.Snapshot(project));
            project.Media[0].VideoStart = 0.4;
            Assert.Equal(0.4, ProjectFile.FromSnapshot(ProjectFile.Snapshot(project)).Media[0].VideoStart, 6);
        }

        [Fact]
        public void Graph_PersonOnly_UsesTheMaskInput_TrimmedLikeTheSource_OnlyWhenFound()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video("a.mp4"), project.Rate, 0, 0);
            clip.SourceIn = 15;
            clip.SourceOut = 105;
            clip.Speed = 2;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            WithScanLine(clip, e => e.PersonOnly = true);
            var masks = new FakeMasks(new Dictionary<string, string> { ["a.mp4"] = "C:/masks/a-mask.mkv" });

            var plan = RenderGraph.Build(project, Options(project, 10, 11, masks));
            Assert.Equal(2, plan.Inputs.Count);
            // 원본과 같은 자리·길이로 읽는다(경로만 다름)
            Assert.Equal(plan.Inputs[0].Take(plan.Inputs[0].Count - 1), plan.Inputs[1].Take(plan.Inputs[1].Count - 1));
            Assert.Equal("a.mp4", plan.Inputs[0].Last());
            Assert.Equal("C:/masks/a-mask.mkv", plan.Inputs[1].Last());
            Assert.Equal("-ss", plan.Inputs[1][0]);
            // 마스크 체인: 원본처럼 속도·프레임 맞춤 → 크기 바꾸기 한 번(클립과 같은 맞춤) → 효과의 선에 곱한다
            Assert.Contains("[1:v]setpts=PTS-STARTPTS,setpts=PTS/2,fps=30/1,", plan.Filter);
            Assert.Contains("scale=w=640:h=360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,format=gbrap[fx0m0]", plan.Filter);
            Assert.Contains("[fx0e0l][fx0m0]blend=all_mode=multiply", plan.Filter);

            // 사람에게만이 아니면 마스크를 찾지도 읽지도 않는다
            clip.Effects[0].PersonOnly = false;
            var calls = masks.Calls;
            plan = RenderGraph.Build(project, Options(project, 10, 11, masks));
            Assert.Single(plan.Inputs);
            Assert.Equal(calls, masks.Calls);
            Assert.DoesNotContain("[fx0m0]", plan.Filter);

            // 마스크가 아직 없으면 클립 전체에(입력 없음, 곱하기 없음)
            clip.Effects[0].PersonOnly = true;
            plan = RenderGraph.Build(project, Options(project, 10, 11, new FakeMasks(new Dictionary<string, string>())));
            Assert.Single(plan.Inputs);
            Assert.DoesNotContain("[fx0e0l]", plan.Filter);
            Assert.Contains("[fx0e0v][fx0e0a]blend=c0_mode=screen", plan.Filter);

            // 제공자가 실패해도 그리기는 계속(클립 전체에)
            plan = RenderGraph.Build(project, Options(project, 10, 11, new ThrowingMasks()));
            Assert.Single(plan.Inputs);
        }

        private sealed class ThrowingMasks : IPersonMaskProvider
        {
            public string Find(string sourcePath)
            {
                throw new System.IO.IOException("디스크 오류");
            }
        }

        [Fact]
        public void Graph_PersonOnly_ImageMaskLoopsLikeThePhoto_AndSplitsForTwoEffects()
        {
            var project = TestProjects.Empty();
            var photo = TimelineEdits.CreateClip(TestProjects.Image("photo.png"), project.Rate, 0, 60);
            photo.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, photo, 0);
            WithScanLine(photo, e => e.PersonOnly = true);
            WithScanLine(photo, e => e.PersonOnly = true);
            var plan = RenderGraph.Build(project, Options(project, 0, 60, new FakeMasks(new Dictionary<string, string> { ["photo.png"] = "m.png" })));
            Assert.Equal(new[] { "-loop", "1", "-framerate", "30/1", "-t", "2", "-i", "m.png" }, plan.Inputs[1]);
            Assert.Contains("scale=w=640:h=360:force_original_aspect_ratio=increase,crop=640:360,format=gbrap,split=2[fx0m0][fx0m1]", plan.Filter);
            Assert.Contains("[fx0e0l][fx0m0]blend=all_mode=multiply", plan.Filter);
            Assert.Contains("[fx0e1l][fx0m1]blend=all_mode=multiply", plan.Filter);
        }

        [Fact]
        public void Graph_XfadePath_KeepsTheEffectInsideThePiece()
        {
            var project = TestProjects.Empty();
            var a = TimelineEdits.CreateClip(TestProjects.Video("a.mp4"), project.Rate, 0, 0);
            a.SourceOut = 60;
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            var b = TimelineEdits.CreateClip(TestProjects.Image("b.png"), project.Rate, 0, 60);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            TimelineEdits.SetTransition(project, b, "WipeLeft", 30);
            WithScanLine(a);
            WithScanLine(b);
            var filter = RenderGraph.Build(project, Options(project, 0, 120)).Filter;
            Assert.Contains("xfade=transition=wipeleft", filter);
            // 효과는 조각 안(제자리 얹기·xfade 앞)에서 — 클립마다 따로
            Assert.True(filter.IndexOf("[fx0e0v][fx0e0a]blend", StringComparison.Ordinal) < filter.IndexOf("[s0]", StringComparison.Ordinal));
            Assert.True(filter.IndexOf("[fx1e0v][fx1e0a]blend", StringComparison.Ordinal) < filter.IndexOf("[s1]", StringComparison.Ordinal));
            Assert.True(filter.IndexOf("[s1]", StringComparison.Ordinal) < filter.IndexOf("xfade=", StringComparison.Ordinal));
        }

        [Fact]
        public void Graph_EffectSizesFollowTheBox_ClipScaleAndPlaybackShrink()
        {
            var project = TestProjects.Empty();
            project.Width = 1920;
            project.Height = 1080;
            var a = TimelineEdits.CreateClip(TestProjects.Video("a.mp4"), project.Rate, 0, 0);
            a.SourceOut = 60;
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            var b = TimelineEdits.CreateClip(TestProjects.Image("b.png"), project.Rate, 0, 60);
            b.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            TimelineEdits.SetTransition(project, b, "Heart", 30);
            WithScanLine(b, e => VideoEffects.SetNumber(e, "jitter", 0));
            var still = RenderGraph.Build(project, new RenderOptions { From = 70, To = 71, Width = 1920, Height = 1080, Audio = false, Preview = true });
            var playback = RenderGraph.Build(project, new RenderOptions { From = 40, To = 120, Width = 1920, Height = 1080, Audio = false, Preview = true, Playback = true });
            var shrink = RenderGraph.PlaybackShrink(new RenderOptions { Width = 1920, Height = 1080 });
            Assert.True(shrink > 1);
            var full = ScanLine.Measure(b.Effects[0], 1920, 1080, 1);
            var small = ScanLine.Measure(b.Effects[0], 1920 / shrink, 1080 / shrink, 1.0 / shrink);
            Assert.Contains("color=c=black:s=1920x" + full.StripHeight + ":", still.Filter);
            Assert.Contains("color=c=black:s=" + (1920 / shrink) + "x" + small.StripHeight + ":", playback.Filter);
            Assert.True(small.StripHeight * shrink <= full.StripHeight + shrink * 2);
            // 크기 배율 50%면 선도 절반
            b.Scale = 0.5;
            var half = ScanLine.Measure(b.Effects[0], 960, 540, 0.5);
            Assert.Contains("color=c=black:s=960x" + half.StripHeight + ":", RenderGraph.Build(project, new RenderOptions { From = 70, To = 71, Width = 1920, Height = 1080, Audio = false, Preview = true }).Filter);
        }

        [Fact]
        public void Graph_UnknownEffectsAndTextClipsDrawNothing_ColorCannotInject()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Video(), project.Rate, 0, 0);
            clip.SourceOut = 30;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            clip.Effects = new List<ClipEffect> { new ClipEffect { Id = "future-glitch" } };
            Assert.DoesNotContain("[fx", RenderGraph.Build(project, Options(project, 0, 30)).Filter);
            // 메모리에서 바로 고친 색이어도 그래프에는 숫자만 들어간다
            clip.Effects = new List<ClipEffect> { new ClipEffect { Id = ScanLine.Id, Color = "#FFFFFF';[evil]" } };
            var filter = RenderGraph.Build(project, Options(project, 0, 30)).Filter;
            Assert.DoesNotContain("evil", filter);
            Assert.Contains("colorchannelmixer=rr=1:rg=0.184314:rb=0:gr=1:gg=0.482353:gb=0:br=1:bg=1:bb=0", filter);
            // 글자 클립에는 그리지 않는다
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var text = TimelineEdits.CreateText("제목", 0, 30);
            TimelineEdits.Place(project, overlay, text, 0);
            text.Effects = new List<ClipEffect> { VideoEffects.Create(ScanLine.Def) };
            Assert.DoesNotContain("[fx1", RenderGraph.Build(project, Options(project, 0, 30)).Filter);
        }

        // ---------- 스캔 라인 모양·시각 ----------

        [Fact]
        public void ScanLine_BowsDown_GlowFadesBeforeTheStripEdges()
        {
            var effect = VideoEffects.Create(ScanLine.Def);
            var shape = ScanLine.Measure(effect, 1920, 1080, 1);
            // 번짐은 띠 가장자리에 닿기 전에 0이 된다(띠 위아래에 딱딱한 줄이 생기지 않게)
            Assert.True(shape.Margin >= shape.Glow + shape.Jitter);
            Assert.Equal(0.35 * 0.2 * 1080, shape.Bend, 6);
            // 가운데가 양끝보다 아래(y가 큼)
            var center = ScanLine.LineY(effect, 1, 1920, 1080, 1, 959.5);
            var edge = ScanLine.LineY(effect, 1, 1920, 1080, 1, 0);
            Assert.True(center - edge > shape.Bend * 0.9, center + " / " + edge);
            // 휘어짐 0이면 곧은 선
            VideoEffects.SetNumber(effect, "bend", 0);
            Assert.Equal(ScanLine.LineY(effect, 1, 1920, 1080, 1, 0), ScanLine.LineY(effect, 1, 1920, 1080, 1, 960), 6);
            // 떨림 폭은 FFmpeg displace 한도(127px) 안
            VideoEffects.SetNumber(effect, "jitter", 100);
            Assert.InRange(ScanLine.Measure(effect, 3840, 2160, 2).Jitter, 1, 127);
            // 작은 틀에서는 띠가 틀보다 높지 않게 떨림을 줄인다
            var tiny = ScanLine.Measure(effect, 64, 36, 0.05);
            Assert.True(tiny.Jitter == 0 || tiny.StripHeight <= 36);
        }

        [Fact]
        public void ScanLine_Progress_EasedSweepThenGap_Loops()
        {
            Assert.Equal(0, ScanLine.Progress(0, 2), 6);
            // 쉬는 동안(주기의 끝 20%)은 다 지나간 자리
            Assert.Equal(1, ScanLine.Progress(1.7, 2), 6);
            Assert.Equal(1, ScanLine.Progress(1.99, 2), 6);
            // 다시 처음부터
            Assert.Equal(ScanLine.Progress(0.3, 2), ScanLine.Progress(2.3, 2), 9);
            // 늘 아래로만 가고, 가운데서 느리고 양끝에서 빠르다
            var last = -1.0;
            for (var t = 0.0; t < 1.6; t += 0.01)
            {
                var p = ScanLine.Progress(t, 2);
                Assert.True(p >= last);
                last = p;
            }
            var startSpeed = ScanLine.Progress(0.05, 2) - ScanLine.Progress(0, 2);
            var middleSpeed = ScanLine.Progress(0.825, 2) - ScanLine.Progress(0.775, 2);
            Assert.True(startSpeed > middleSpeed * 3, startSpeed + " / " + middleSpeed);
            Assert.Equal(-100, ScanLine.StripTop(0, 2, 1000, 100), 6);
            Assert.Equal(1000, ScanLine.StripTop(1.8, 2, 1000, 100), 6);
        }

        [Fact]
        public void PreviewArguments_UseTheSameRecipe_AndKeyFollowsTheRecipe()
        {
            var args = VideoEffects.PreviewArguments(ScanLine.Def);
            var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
            Assert.StartsWith("[0]format=gbrap,split[pa][pd];", graph);
            Assert.Contains("mod(t+0,1.5)/1.2", graph);
            Assert.EndsWith(",format=rgb24,tile=15x1", graph);
            Assert.Equal(VideoEffects.PreviewKey(ScanLine.Def), VideoEffects.PreviewKey(ScanLine.Def));
            Assert.Equal(24, VideoEffects.PreviewKey(ScanLine.Def).Length);
        }

        [Fact]
        public void PreviewArguments_DrawOnTheTexturedSource_AndTheCacheKeyLeavesOldThumbnails()
        {
            // 바탕은 합성 그림(무늬·해·사람 실루엣을 수식으로 한 장 그려 1.5초로 늘임) — 예전의 밋밋한 색 바탕(drawbox)이 아니다
            var args = VideoEffects.PreviewArguments(ScanLine.Def).ToList();
            Assert.Equal(VideoEffects.PreviewSource, args[args.IndexOf("-i") + 1]);
            Assert.Equal("lavfi", args[args.IndexOf("-i") - 1]);
            var source = VideoEffects.PreviewSource;
            Assert.StartsWith("color=c=black:s=128x72:r=10:d=0.1,format=gbrp,geq=r='", source);
            Assert.EndsWith(",tpad=stop_mode=clone:stop_duration=1.4", source);
            Assert.DoesNotContain("drawbox", source);
            foreach (var banned in new[] { "st(", "ld(", "random", "time(" })
                Assert.DoesNotContain(banned, source);
            // 모든 효과가 같은 바탕에 그린다
            foreach (var def in VideoEffects.All)
                Assert.Contains(source, VideoEffects.PreviewArguments(def));

            // 캐시 이름: 바탕이 바뀌었으니 예전 판(fx1, 바탕이 이름에 없었다)의 이름과 다르다 — 예전 썸네일 파일을 다시 쓰지 않는다
            foreach (var def in VideoEffects.All)
            {
                var old = "fx1|" + VideoEffects.PreviewWidth + "x" + VideoEffects.PreviewHeight + "|" + def.Id + "|" + def.Version + "|"
                    + string.Join(",", def.Params.Select(p => p.Key + "=" + RenderGraph.N(p.Default) + (p.DefaultColor ?? "")))
                    + "|" + string.Join(",", def.PreviewValues.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + RenderGraph.N(v.Value)));
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var oldKey = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(old))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
                    Assert.NotEqual(oldKey, VideoEffects.PreviewKey(def));
                }
            }
            // 효과마다 다른 이름
            Assert.Equal(VideoEffects.All.Count, VideoEffects.All.Select(VideoEffects.PreviewKey).Distinct().Count());
        }

        [Fact]
        public void Groups_FollowTheCatalogOrder()
        {
            // 효과 탭의 묶음 제목·묶음 고르기 차례: 목록에 처음 나오는 차례, 겹치지 않음, 모든 효과의 묶음이 들어 있음
            var groups = VideoEffects.Groups();
            Assert.Equal(groups.Count, groups.Distinct().Count());
            Assert.Equal(ScanLine.Def.Group, groups[0]);
            Assert.All(VideoEffects.All, d => Assert.Contains(d.Group, groups));
            var firstSeen = VideoEffects.All.Select(d => d.Group).Distinct().ToList();
            Assert.Equal(firstSeen, groups);
        }

        // ---------- 새 효과("새로" 표시) ----------

        /// <summary>처음 판 뒤에 더한 13개(스캔 계열 넷·글리치 넷·흔들림·줌 다섯).</summary>
        private static readonly string[] SecondRelease =
        {
            "shockwave-scan", "scifi-scan", "ray-scan", "glitch-scan",
            "rgb-split", "block-glitch", "line-slip", "bad-tv",
            "camera-shake", "earthquake", "heartbeat", "punch-zoom", "zoom-bounce"
        };

        [Fact]
        public void Since_ScanLineIsTheFirstRelease_TheThirteenOthersTheSecond()
        {
            // 모든 효과가 판을 정해 둔다(0 = 정하지 않음 — 효과를 더할 때 잊지 않게)
            Assert.All(VideoEffects.All, d => Assert.True(d.Since > 0, d.Id + "의 Since가 정해지지 않았습니다."));
            Assert.Equal(1, ScanLine.Def.Since);
            Assert.Equal(VideoEffectNews.FirstRelease, ScanLine.Def.Since);
            Assert.Equal(SecondRelease.OrderBy(i => i), VideoEffects.All.Where(d => d.Since == 2).Select(d => d.Id).OrderBy(i => i));
            Assert.Equal(new[] { "scan-line" }.Concat(SecondRelease).OrderBy(i => i), VideoEffects.All.Select(d => d.Id).OrderBy(i => i));
        }

        [Fact]
        public void NewEffects_WithoutASetting_AreAllButTheFirstRelease()
        {
            // 설정이 없거나 비었거나 깨졌으면 처음 판(스캔 라인)만 본 것 — 예전부터 쓰던 사람에게 13개가 새로 보인다
            foreach (var setting in new[] { null, "", "   ", "not json", "{\"a\":1}", "\"scan-line\"", "[\"scan-line\"", "12", "null", "[[\"rgb-split\"]]" })
            {
                var seen = VideoEffectNews.ReadSeen(setting);
                Assert.Equal(new[] { "scan-line" }, seen);
                Assert.Equal(SecondRelease, VideoEffectNews.New(seen).Select(d => d.Id));
            }
            Assert.Equal(SecondRelease, VideoEffectNews.New(null).Select(d => d.Id));
        }

        [Fact]
        public void NewEffects_FollowTheSeenSetting_KeepingOtherReleasesIds_IgnoringJunk()
        {
            // 본 것만 빠진다(목록 차례 그대로). 모르는 id(다른 판의 효과)는 형식이 맞으면 남기고, 글이 아닌 항목·형식이 틀린 글은 버린다
            var seen = VideoEffectNews.ReadSeen("[\"rgb-split\", \"future-effect\", 42, null, {\"id\":\"heartbeat\"}, \"BAD TV\", \"camera-shake\"]");
            Assert.Equal(new[] { "camera-shake", "future-effect", "rgb-split", "scan-line" }, seen.OrderBy(i => i, StringComparer.Ordinal));
            Assert.Equal(SecondRelease.Except(new[] { "rgb-split", "camera-shake" }), VideoEffectNews.New(seen).Select(d => d.Id));
            // 모두 보면 새 효과 없음, 처음 판의 효과는 모음에 없어도 새로가 아니다
            Assert.Empty(VideoEffectNews.New(new HashSet<string>(SecondRelease)));
            Assert.Empty(VideoEffectNews.New(VideoEffectNews.ReadSeen(VideoEffectNews.WriteSeen(VideoEffects.All.Select(d => d.Id)))));
            // 빈 배열 = 처음 판만 본 것
            Assert.Equal(SecondRelease, VideoEffectNews.New(VideoEffectNews.ReadSeen("[]")).Select(d => d.Id));
        }

        [Fact]
        public void SeenSetting_WritesKnownIdsInCatalogOrder_AndRoundTrips()
        {
            // 아는 효과는 목록 차례로, 형식이 맞는 모르는 id(다른 판의 효과)는 그 뒤에, 형식이 틀린 것은 버린다
            var written = VideoEffectNews.WriteSeen(new[] { "zoom-bounce", "unknown", "scan-line", "rgb-split", "rgb-split", null, "BAD TV" });
            Assert.Equal("[\"scan-line\",\"rgb-split\",\"zoom-bounce\",\"unknown\"]", written);
            Assert.Equal(new[] { "rgb-split", "scan-line", "unknown", "zoom-bounce" }, VideoEffectNews.ReadSeen(written).OrderBy(i => i, StringComparer.Ordinal));
            Assert.Equal("[]", VideoEffectNews.WriteSeen(null));
        }

        [Fact]
        public void SeenSetting_IsCapped()
        {
            // 너무 긴 설정은 깨진 것으로(처음 판만), 항목은 앞의 MaxIds개까지만 읽는다
            var huge = "[" + string.Join(",", Enumerable.Repeat("\"rgb-split\"", VideoEffectNews.MaxSettingLength / 10)) + "]";
            Assert.True(huge.Length > VideoEffectNews.MaxSettingLength);
            Assert.Equal(new[] { "scan-line" }, VideoEffectNews.ReadSeen(huge));
            var late = "[" + string.Join(",", Enumerable.Repeat("\"x\"", VideoEffectNews.MaxIds)) + ",\"rgb-split\"]";
            Assert.True(late.Length <= VideoEffectNews.MaxSettingLength);
            Assert.DoesNotContain("rgb-split", VideoEffectNews.ReadSeen(late));
            var early = "[" + string.Join(",", Enumerable.Repeat("\"x\"", VideoEffectNews.MaxIds - 1)) + ",\"rgb-split\"]";
            Assert.Contains("rgb-split", VideoEffectNews.ReadSeen(early));

            // 쓸 때도 MaxIds개까지(효과가 아주 많아져도 설정이 끝없이 늘지 않게)
            var many = Enumerable.Range(0, VideoEffectNews.MaxIds + 20)
                .Select(i => new VideoEffectDef("fx-" + i, "효과 " + i, "묶음", "", 1, Array.Empty<EffectParam>(), r => "") { Since = 2 }).ToList();
            var all = VideoEffectNews.WriteSeen(many.Select(d => d.Id), many);
            Assert.Equal(VideoEffectNews.MaxIds, System.Text.Json.JsonSerializer.Deserialize<List<string>>(all).Count);
            Assert.Equal(VideoEffectNews.MaxIds, VideoEffectNews.ReadSeen(all, many).Count);
            Assert.Equal(20, VideoEffectNews.New(VideoEffectNews.ReadSeen(all, many), many).Count);
        }

        [Fact]
        public void NewEffects_AFutureReleaseShowsAsNewEvenAfterTheUserDismissedEverything()
        {
            // 다음 판에 효과를 더하면(Since = 3): [확인]으로 지금 것을 모두 본 사람에게도, 설정이 없는 사람에게도 새로 보인다
            var future = new VideoEffectDef("future-effect", "다음 효과", "묶음", "", 1, Array.Empty<EffectParam>(), r => "") { Since = 3 };
            var catalog = VideoEffects.All.Concat(new[] { future }).ToList();
            var dismissed = VideoEffectNews.WriteSeen(VideoEffects.All.Select(d => d.Id));
            Assert.Equal(new[] { "future-effect" }, VideoEffectNews.New(VideoEffectNews.ReadSeen(dismissed, catalog), catalog).Select(d => d.Id));
            Assert.Equal(SecondRelease.Concat(new[] { "future-effect" }), VideoEffectNews.New(VideoEffectNews.ReadSeen(null, catalog), catalog).Select(d => d.Id));
            // 다음 판에서 그 효과를 본 기록은 지금 판이 읽고 다시 써도 남는다 — 지금 판으로 돌아왔다 다음 판을 열어도 다시 "새로"가 되지 않게
            var fromNext = VideoEffectNews.WriteSeen(new[] { "future-effect", "rgb-split" }, catalog);
            var rewritten = VideoEffectNews.WriteSeen(VideoEffectNews.ReadSeen(fromNext));
            Assert.Contains("future-effect", VideoEffectNews.ReadSeen(rewritten, catalog));
            Assert.DoesNotContain(VideoEffectNews.New(VideoEffectNews.ReadSeen(rewritten, catalog), catalog), d => d.Id == "future-effect");
        }
    }
}
