using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class ProjectSettingsTests
    {
        [Fact]
        public void ChangeRate_KeepsSeconds()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60);
            var b = TestProjects.VideoClip(0, 45);
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, b, 999);
            b.FadeIn = 15;
            project.Markers.Add(new Marker { Frame = 30 });
            project.MarkOut = 90;
            ProjectSettings.ChangeRate(project, new FrameRate(60, 1));
            Assert.Equal(new FrameRate(60, 1), project.Rate);
            Assert.Equal(120, a.Duration);
            Assert.Equal(120, b.Start);
            Assert.Equal(90, b.Duration);
            Assert.Equal(30, b.FadeIn);
            Assert.Equal(60, project.Markers[0].Frame);
            Assert.Equal(180, project.MarkOut);
            Assert.Equal(6000, a.SourceLength);
        }

        [Fact]
        public void ChangeRate_Ntsc_NoOverlaps()
        {
            var project = TestProjects.Empty(magnetic: false);
            for (var i = 0; i < 20; i++)
                TestProjects.Add(project, project.MainTrack, i * 7, 7);
            ProjectSettings.ChangeRate(project, new FrameRate(30000, 1001));
            var clips = project.MainTrack.Clips;
            for (var i = 1; i < clips.Count; i++)
                Assert.True(clips[i].Start >= clips[i - 1].End, "겹침 " + i);
        }

        [Fact]
        public void ChangeSize_RoundsToEven_AndRejectsNonsense()
        {
            var project = TestProjects.Empty();
            ProjectSettings.ChangeSize(project, 1081, 1921);
            Assert.Equal(1080, project.Width);
            Assert.Equal(1920, project.Height);
            Assert.Throws<EditException>(() => ProjectSettings.ChangeSize(project, 0, 100));
        }

        [Fact]
        public void AdoptFirstVideo_OnlyOnceAndOnlyForVideo()
        {
            var project = TestProjects.Empty();
            Assert.False(ProjectSettings.AdoptFirstVideo(project, TestProjects.Image()));
            var phone = TestProjects.Video();
            phone.Width = 1081;
            phone.Height = 1920;
            phone.FrameRate = "30000/1001";
            Assert.True(ProjectSettings.AdoptFirstVideo(project, phone));
            Assert.Equal(1080, project.Width);
            Assert.Equal(new FrameRate(30000, 1001), project.Rate);
            Assert.False(ProjectSettings.AdoptFirstVideo(project, TestProjects.Video()));
        }
    }
}
