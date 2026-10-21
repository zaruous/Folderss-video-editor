using System;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>코드 리뷰에서 나온 문제의 재발 방지.</summary>
    public class ReviewRegressionTests
    {
        [Fact]
        public void Execute_UnexpectedException_RollsBackAndReports()
        {
            var session = new EditorSession();
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, TestProjects.VideoClip(0, 30), 0));
            var ok = session.Execute(p =>
            {
                p.MainTrack.Clips[0].Start = 999;
                throw new InvalidOperationException("뜻밖의 오류");
            });
            Assert.False(ok);
            Assert.Contains("뜻밖의 오류", session.LastError);
            Assert.Equal(0, session.Project.MainTrack.Clips[0].Start);
        }

        [Fact]
        public void EditsOnDeletedClip_AreRefusedWithReason()
        {
            var session = new EditorSession();
            var clip = TestProjects.VideoClip(0, 30);
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, clip, 0));
            session.Execute(p => TimelineEdits.Delete(p, p.AllClips.ToList(), false));
            Assert.False(session.Execute(p => TimelineEdits.SetSpeed(p, p.FindClip(clip.Id), 2)));
            Assert.Contains("클립이 없습니다", session.LastError);
            Assert.False(session.Execute(p => TimelineEdits.TrimEnd(p, p.FindClip(clip.Id), 10)));
            Assert.False(session.Execute(p => TimelineEdits.TrimStart(p, p.FindClip(clip.Id), 10)));
            Assert.False(session.Execute(p => TimelineEdits.DetachAudio(p, p.FindClip(clip.Id))));
        }

        [Fact]
        public void ConvertClip_KeepsSecondsAcrossRates()
        {
            // 30fps 프로젝트에서 복사한 클립을 24fps 프로젝트에 붙일 때
            var clip = TestProjects.VideoClip(60, 90);
            clip.SourceIn = 30;
            clip.SourceOut = 120;
            clip.FadeIn = 15;
            ProjectSettings.ConvertClip(clip, new FrameRate(30, 1), new FrameRate(24, 1));
            Assert.Equal(48, clip.Start);
            Assert.Equal(24, clip.SourceIn);
            Assert.Equal(96, clip.SourceOut);
            Assert.Equal(72, clip.Duration);
            Assert.Equal(12, clip.FadeIn);
            Assert.Equal(2400, clip.SourceLength);
        }

        [Fact]
        public void AudioSegment_NeverZeroLength()
        {
            var project = TestProjects.Empty();
            var music = TimelineEdits.CreateClip(TestProjects.Audio(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.AudioTracks[0], music, 0);
            music.Speed = 0.25;
            // 1프레임 구간 × 0.25배 → 원본 0.25프레임이지만 -t는 최소 1프레임
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 1, Width = 16, Height = 16, Video = false });
            var t = plan.Inputs.Single();
            Assert.Equal("0.033333", t[t.IndexOf("-t") + 1]);
        }
    }
}
