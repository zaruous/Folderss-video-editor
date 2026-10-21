using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class RenderGraphTests
    {
        private static RenderOptions Whole(Project project, bool preview = false)
        {
            return new RenderOptions { From = 0, To = project.Duration, Width = project.Width, Height = project.Height, Preview = preview, TempFolder = "T:/tmp" };
        }

        [Fact]
        public void EmptyRange_Throws()
        {
            var project = TestProjects.Empty();
            Assert.Throws<System.ArgumentException>(() => RenderGraph.Build(project, new RenderOptions { From = 5, To = 5, Width = 10, Height = 10 }));
        }

        [Fact]
        public void TwoClips_OnMain_SeekAndPlace()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 30, "a.mp4");
            a.SourceIn = 15;
            a.SourceOut = 45;
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 60, "b.mp4"), 999);
            var plan = RenderGraph.Build(project, Whole(project));
            Assert.Equal(90, plan.Frames);
            Assert.Equal(3.0, plan.Seconds, 6);
            // 화면 2 + 소리 2 입력
            Assert.Equal(4, plan.Inputs.Count);
            Assert.Equal(new[] { "-ss", "0.5", "-t", "1", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Contains("color=c=black:s=640x360:r=30/1:d=3[base]", plan.Filter);
            Assert.Contains("setpts=PTS-STARTPTS+1/TB[c1]", plan.Filter);
            Assert.Contains("[o0][c1]overlay=", plan.Filter);
            Assert.Contains("[o1]format=yuv420p[vout]", plan.Filter);
            Assert.Contains("amix=inputs=3:duration=first:normalize=0", plan.Filter);
            Assert.Contains("adelay=1000:all=1", plan.Filter);
        }

        [Fact]
        public void Window_StartsInsideClip_SeeksSource()
        {
            var project = TestProjects.Empty();
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 90), 0);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 45, To = 46, Width = 320, Height = 180, Audio = false, Preview = true });
            Assert.Single(plan.Inputs);
            Assert.Equal(new[] { "-ss", "1.5", "-t", "0.033333", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Contains("[o0]format=bgra[vout]", plan.Filter);
            Assert.DoesNotContain("anullsrc", plan.Filter);
        }

        [Fact]
        public void HiddenTrack_And_MutedTrack_AreSkipped()
        {
            var project = TestProjects.Empty();
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 30), 0);
            project.MainTrack.Hidden = true;
            project.MainTrack.Muted = true;
            var plan = RenderGraph.Build(project, Whole(project));
            Assert.Empty(plan.Inputs);
            Assert.Contains("[base]format=yuv420p[vout]", plan.Filter);
            Assert.Contains("[abase]anull[aout]", plan.Filter);
        }

        [Fact]
        public void Overlay_Image_Transform()
        {
            var project = TestProjects.Empty(magnetic: false);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 60), 0);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var image = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 30, 30);
            image.X = 100;
            image.Y = -50;
            image.Scale = 0.5;
            image.Rotation = 90;
            image.Opacity = 0.5;
            TimelineEdits.Place(project, overlay, image, 30);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 60, Width = 320, Height = 180, Audio = false });
            Assert.Equal(new[] { "-loop", "1", "-framerate", "30/1", "-t", "1", "-i", "photo.png" }, plan.Inputs[1]);
            Assert.Contains("scale=w=160:h=90:force_original_aspect_ratio=decrease", plan.Filter);
            Assert.Contains("rotate=1.570796", plan.Filter);
            Assert.Contains("colorchannelmixer=aa=0.5", plan.Filter);
            // 위치는 출력 크기 비율(320/640 = 0.5)로
            Assert.Contains("x='(main_w-overlay_w)/2+50':y='(main_h-overlay_h)/2+-25'", plan.Filter);
            // 주 트랙이 먼저 깔리고 덮어쓰기 트랙이 위
            Assert.True(plan.Filter.IndexOf("[base][c0]") < plan.Filter.IndexOf("[o0][c1]"));
        }

        [Fact]
        public void Text_UsesTextFileAndFont()
        {
            var project = TestProjects.Empty(magnetic: false);
            var text = TimelineEdits.CreateText("안녕 'it':%{pts}", 0, 30);
            text.TextColor = "#ff0000";
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 30, Width = 640, Height = 360, Audio = false, FontFile = @"C:\Windows\Fonts\malgun.ttf", TempFolder = @"C:\Temp" });
            Assert.Empty(plan.Inputs);
            var file = Assert.Single(plan.TextFiles);
            Assert.Equal("안녕 'it':%{pts}", file.Value);
            Assert.Contains(@"fontfile='C\:/Windows/Fonts/malgun.ttf'", plan.Filter);
            Assert.Contains("expansion=none", plan.Filter);
            Assert.Contains("fontcolor='0xFF0000'", plan.Filter);
        }

        [Fact]
        public void Speed_UsesSetptsAndAtempoChain()
        {
            var project = TestProjects.Empty();
            var clip = TestProjects.VideoClip(0, 90);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            TimelineEdits.SetSpeed(project, clip, 0.25);
            var plan = RenderGraph.Build(project, Whole(project));
            Assert.Contains("setpts=PTS/0.25", plan.Filter);
            Assert.Contains("atempo=0.5,atempo=0.5", plan.Filter);
            Assert.Equal(new[] { 0.5, 0.5 }, RenderGraph.Tempo(0.25));
            Assert.Equal(new[] { 2.0 }, RenderGraph.Tempo(2));
            Assert.Empty(RenderGraph.Tempo(1));
        }

        [Fact]
        public void Crossfade_KeepsPreviousClipUnder_AndFadesIn()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60, "a.mp4");
            var b = TestProjects.VideoClip(0, 60, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            b.TransitionIn = 15;
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 120, Width = 640, Height = 360, Audio = false });
            // a는 끝에서 15프레임 더 그린다(원본 여유가 있으므로 원본에서)
            Assert.Equal(new[] { "-ss", "0", "-t", "2.5", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Contains("fade=t=in:st=0:d=0.5:alpha=1", plan.Filter);
            // 크로스페이드 도중에서 시작하면 b를 처음부터 디코드한다
            var mid = RenderGraph.Build(project, new RenderOptions { From = 65, To = 66, Width = 64, Height = 36, Audio = false });
            Assert.Contains(mid.Inputs, i => i.Last() == "b.mp4" && i[1] == "0");
            Assert.Contains("setpts=PTS-STARTPTS+-0.166667/TB", mid.Filter);
        }

        [Fact]
        public void Crossfade_WithoutSourceHandle_FreezesLastFrame()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 30, "a.mp4");
            a.SourceLength = 30;
            var b = TestProjects.VideoClip(0, 30, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            b.TransitionIn = 9;
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 60, Width = 64, Height = 36, Audio = false });
            // 원본 30프레임만 읽고 꼬리 9프레임은 마지막 화면을 늘여 채운다(전체 39프레임 = 1.3초)
            Assert.Equal(new[] { "-ss", "0", "-t", "1", "-i", "a.mp4" }, plan.Inputs[0]);
            Assert.Contains("tpad=stop_mode=clone:stop_duration=1.3,trim=duration=1.3", plan.Filter);
            // 꼬리 안에서부터 그리면 파일 끝의 마지막 화면만 읽는다
            var tail = RenderGraph.Build(project, new RenderOptions { From = 34, To = 35, Width = 64, Height = 36, Audio = false });
            Assert.Equal(new[] { "-sseof", "-0.3", "-i", "a.mp4" }, tail.Inputs[0]);
            Assert.Contains("reverse,trim=end_frame=1", tail.Filter);
        }

        [Fact]
        public void AudioTrack_FadesAndVolume()
        {
            var project = TestProjects.Empty();
            var music = TimelineEdits.CreateClip(TestProjects.Audio(), project.Rate, 30, 0);
            music.Volume = 0.5;
            music.FadeIn = 30;
            music.FadeOut = 60;
            TimelineEdits.Place(project, project.AudioTracks[0], music, 30);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 90, Width = 64, Height = 36, Video = false });
            Assert.Single(plan.Inputs);
            Assert.Contains("volume='0.5*min(1,max(0,(t+0)/1))*min(1,max(0,(20-t-0)/2))':eval=frame", plan.Filter);
            Assert.Contains("adelay=1000:all=1", plan.Filter);
        }

        [Fact]
        public void VolumeExpression_WindowInsideFade_UsesOffset()
        {
            var clip = TestProjects.VideoClip(100, 90);
            clip.FadeIn = 60;
            Assert.Equal("'1*min(1,max(0,(t+1)/2))':eval=frame", RenderGraph.VolumeExpression(FrameRate.Default, clip, 130));
            clip.FadeIn = 0;
            clip.Volume = 3;
            Assert.Equal("2", RenderGraph.VolumeExpression(FrameRate.Default, clip, 130));
        }

        [Fact]
        public void DetachedAudio_PlaysFromAudioTrackOnly()
        {
            var project = TestProjects.Empty();
            var clip = TestProjects.VideoClip(0, 30);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            TimelineEdits.DetachAudio(project, clip);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 30, Width = 64, Height = 36, Video = false });
            Assert.Single(plan.Inputs);
            Assert.Contains("amix=inputs=2", plan.Filter);
        }

        [Theory]
        [InlineData(@"C:\Windows\Fonts\a.ttf", @"'C\:/Windows/Fonts/a.ttf'")]
        [InlineData("it's", @"'it\'\''s'")]
        public void FilterValue_EscapesTwoLevels(string value, string expected)
        {
            Assert.Equal(expected, RenderGraph.FilterValue(value));
        }

        [Theory]
        [InlineData("#00ff7f", "0x00FF7F")]
        [InlineData("red", "white")]
        [InlineData(null, "white")]
        public void TextColor(string input, string expected)
        {
            Assert.Equal(expected, RenderGraph.TextColor(input));
        }
    }
}
