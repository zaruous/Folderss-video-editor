using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class TimelineEditsTests
    {
        private static string Layout(Track track)
        {
            return string.Join(" ", track.Clips.OrderBy(c => c.Start).Select(c => c.Start + "-" + c.End));
        }

        // ---------- 만들기 ----------

        [Fact]
        public void CreateClip_Video_UsesSourceLength()
        {
            var clip = TimelineEdits.CreateClip(TestProjects.Video(seconds: 2.5), FrameRate.Default, 10, 90);
            Assert.Equal(75, clip.SourceLength);
            Assert.Equal(75, clip.Duration);
            Assert.Equal(10, clip.Start);
            Assert.True(clip.HasVideo && clip.HasAudio && clip.IsVisual && clip.IsAudible);
        }

        [Fact]
        public void CreateClip_Image_HasNoSourceLimit()
        {
            var clip = TimelineEdits.CreateClip(TestProjects.Image(), FrameRate.Default, 0, 90);
            Assert.Equal(0, clip.SourceLength);
            Assert.False(clip.HasSourceLimit);
            Assert.Equal(90, clip.Duration);
            Assert.True(clip.IsVisual);
            Assert.False(clip.IsAudible);
        }

        [Fact]
        public void CreateClip_Audio_IsNotVisual()
        {
            var clip = TimelineEdits.CreateClip(TestProjects.Audio(seconds: 1), FrameRate.Default, 0, 0);
            Assert.False(clip.IsVisual);
            Assert.True(clip.IsAudible);
            Assert.True(TimelineEdits.IsAudioClip(clip));
        }

        // ---------- 놓기 ----------

        [Fact]
        public void Place_Magnetic_InsertsAtNearestBoundary_AndPushesLater()
        {
            var project = TestProjects.Empty();
            var main = project.MainTrack;
            TimelineEdits.Place(project, main, TestProjects.VideoClip(0, 30), 0);
            TimelineEdits.Place(project, main, TestProjects.VideoClip(0, 30), 999);
            Assert.Equal("0-30 30-60", Layout(main));
            // 20은 첫 클립 가운데(15)를 지나 있으므로 두 클립 사이에 들어간다
            var inserted = TestProjects.VideoClip(0, 10);
            TimelineEdits.Place(project, main, inserted, 20);
            Assert.Equal("0-30 30-40 40-70", Layout(main));
            Assert.Equal(30, inserted.Start);
        }

        [Fact]
        public void Place_NonMagnetic_OverwritesOverlap()
        {
            var project = TestProjects.Empty(magnetic: false);
            var main = project.MainTrack;
            var long1 = TestProjects.Add(project, main, 0, 100);
            TimelineEdits.Place(project, main, TestProjects.VideoClip(0, 20), 40);
            Assert.Equal("0-40 40-60 60-100", Layout(main));
            var right = main.Clips.Last();
            // 오른쪽 조각은 원본 60프레임부터
            Assert.Equal(60, right.SourceIn);
            Assert.Equal(100, right.SourceOut);
            Assert.Equal(40, long1.SourceOut);
        }

        [Fact]
        public void Place_OnOverlayTrack_IsNeverMagnetic()
        {
            var project = TestProjects.Empty();
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            TimelineEdits.Place(project, overlay, TestProjects.VideoClip(0, 30), 50);
            Assert.Equal("50-80", Layout(overlay));
            Assert.Equal("동영상 2", overlay.Name);
        }

        [Fact]
        public void Place_AudioFileOnVideoTrack_IsRefused()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Audio(), FrameRate.Default, 0, 0);
            Assert.Throws<EditException>(() => TimelineEdits.Place(project, project.MainTrack, clip, 0));
            TimelineEdits.Place(project, project.AudioTracks[0], clip, 15);
            Assert.Equal(15, clip.Start);
        }

        [Fact]
        public void Place_LockedTrack_Throws()
        {
            var project = TestProjects.Empty();
            project.MainTrack.Locked = true;
            Assert.Throws<EditException>(() => TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 10), 0));
        }

        [Fact]
        public void Append_PutsAtTrackEnd()
        {
            var project = TestProjects.Empty(magnetic: false);
            TestProjects.Add(project, project.MainTrack, 10, 20);
            var clip = TestProjects.VideoClip(0, 5);
            TimelineEdits.Append(project, project.MainTrack, clip);
            Assert.Equal(30, clip.Start);
        }

        // ---------- 옮기기 ----------

        [Fact]
        public void Move_WithinMagneticMain_Reorders()
        {
            var project = TestProjects.Empty();
            var main = project.MainTrack;
            var a = TestProjects.VideoClip(0, 30, "a.mp4");
            var b = TestProjects.VideoClip(0, 10, "b.mp4");
            TimelineEdits.Place(project, main, a, 0);
            TimelineEdits.Place(project, main, b, 999);
            TimelineEdits.Move(project, b, main, 0);
            Assert.Equal(new[] { "b.mp4", "a.mp4" }, main.Clips.OrderBy(c => c.Start).Select(c => c.Name));
            Assert.Equal("0-10 10-40", Layout(main));
        }

        [Fact]
        public void Move_ToOverlayTrack_ClosesGapOnMain()
        {
            var project = TestProjects.Empty();
            var main = project.MainTrack;
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            var a = TestProjects.VideoClip(0, 30);
            var b = TestProjects.VideoClip(0, 30);
            TimelineEdits.Place(project, main, a, 0);
            TimelineEdits.Place(project, main, b, 999);
            TimelineEdits.Move(project, a, overlay, 45);
            Assert.Equal("0-30", Layout(main));
            Assert.Equal("45-75", Layout(overlay));
        }

        [Fact]
        public void Move_VideoClipToAudioTrack_IsRefused_AndSessionRollsBack()
        {
            var session = new EditorSession();
            var clip = TestProjects.VideoClip(0, 30);
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, clip, 0));
            var ok = session.Execute(p => TimelineEdits.Move(p, p.FindClip(clip.Id), p.AudioTracks[0], 0));
            Assert.False(ok);
            Assert.NotNull(session.LastError);
            Assert.Single(session.Project.MainTrack.Clips);
        }

        // ---------- 다듬기 ----------

        [Fact]
        public void TrimStart_NonMagnetic_ClampsToSourceAndPrevious()
        {
            var project = TestProjects.Empty(magnetic: false);
            var main = project.MainTrack;
            TestProjects.Add(project, main, 0, 20);
            var clip = TestProjects.Add(project, main, 40, 30);
            clip.SourceIn = 10;
            clip.SourceOut = 40;
            // 원본 앞에 10프레임만 남아 있으므로 30까지만 늘어난다
            Assert.Equal(30, TimelineEdits.TrimStart(project, clip, 0));
            Assert.Equal(0, clip.SourceIn);
            Assert.Equal(70, clip.End);
            // 줄이기
            Assert.Equal(50, TimelineEdits.TrimStart(project, clip, 50));
            Assert.Equal(20, clip.SourceIn);
            Assert.Equal(70, clip.End);
        }

        [Fact]
        public void TrimEnd_ClampsToSourceAndNext()
        {
            var project = TestProjects.Empty(magnetic: false);
            var main = project.MainTrack;
            var clip = TestProjects.Add(project, main, 0, 30);
            clip.SourceLength = 40;
            TestProjects.Add(project, main, 60, 10);
            Assert.Equal(40, TimelineEdits.TrimEnd(project, clip, 100));
            clip.SourceLength = 100;
            Assert.Equal(60, TimelineEdits.TrimEnd(project, clip, 100));
            Assert.Equal(1, TimelineEdits.TrimEnd(project, clip, -5) - clip.Start);
        }

        [Fact]
        public void TrimEnd_Magnetic_RipplesLaterClips()
        {
            var project = TestProjects.Empty();
            var main = project.MainTrack;
            var a = TestProjects.VideoClip(0, 30);
            TimelineEdits.Place(project, main, a, 0);
            TimelineEdits.Place(project, main, TestProjects.VideoClip(0, 30), 999);
            TimelineEdits.TrimEnd(project, a, 10);
            Assert.Equal("0-10 10-40", Layout(main));
        }

        [Fact]
        public void TrimStart_Magnetic_KeepsClipInPlace_AndPullsLater()
        {
            var project = TestProjects.Empty();
            var main = project.MainTrack;
            TimelineEdits.Place(project, main, TestProjects.VideoClip(0, 30), 0);
            var b = TestProjects.VideoClip(0, 30);
            TimelineEdits.Place(project, main, b, 999);
            TimelineEdits.Place(project, main, TestProjects.VideoClip(0, 30), 999);
            TimelineEdits.TrimStart(project, b, 40);
            Assert.Equal("0-30 30-50 50-80", Layout(main));
            Assert.Equal(10, b.SourceIn);
        }

        [Fact]
        public void TrimImage_ExtendsFreely()
        {
            var project = TestProjects.Empty(magnetic: false);
            var image = TimelineEdits.CreateClip(TestProjects.Image(), FrameRate.Default, 100, 90);
            TimelineEdits.Place(project, project.MainTrack, image, 100);
            TimelineEdits.TrimEnd(project, image, 1000);
            Assert.Equal(1000, image.End);
            TimelineEdits.TrimStart(project, image, 0);
            Assert.Equal(0, image.Start);
            Assert.Equal(0, image.SourceIn);
            Assert.Equal(1000, image.Duration);
        }

        [Fact]
        public void TrimToPlayhead_OutsideClip_ReturnsFalse()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 30);
            Assert.False(TimelineEdits.TrimToPlayhead(project, clip, 30, true));
            Assert.True(TimelineEdits.TrimToPlayhead(project, clip, 10, false));
            Assert.Equal(10, clip.End);
        }

        // ---------- 분할·삭제 ----------

        [Fact]
        public void Split_KeepsSourceContinuity_AndFades()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 10, 60);
            clip.SourceIn = 5;
            clip.SourceOut = 65;
            clip.FadeIn = 10;
            clip.FadeOut = 15;
            var made = TimelineEdits.Split(project, new[] { clip }, 40);
            var right = Assert.Single(made);
            Assert.Equal("10-40 40-70", Layout(project.MainTrack));
            Assert.Equal(35, clip.SourceOut);
            Assert.Equal(35, right.SourceIn);
            Assert.Equal(65, right.SourceOut);
            Assert.Equal(10, clip.FadeIn);
            Assert.Equal(0, clip.FadeOut);
            Assert.Equal(0, right.FadeIn);
            Assert.Equal(15, right.FadeOut);
            Assert.NotEqual(clip.Id, right.Id);
        }

        [Fact]
        public void Split_WithSpeed_KeepsTimelineLength()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 90);
            clip.Speed = 1.5;
            Assert.Equal(60, clip.Duration);
            var right = TimelineEdits.Split(project, new[] { clip }, 25).Single();
            Assert.Equal(25, clip.Duration);
            Assert.Equal(25, right.Start);
            Assert.Equal(60, right.End);
            Assert.Equal(clip.SourceOut, right.SourceIn);
        }

        [Fact]
        public void Split_AtEdge_OrOnLockedTrack_DoesNothing()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 30);
            Assert.Empty(TimelineEdits.Split(project, new[] { clip }, 0));
            Assert.Empty(TimelineEdits.Split(project, new[] { clip }, 30));
            project.MainTrack.Locked = true;
            Assert.Empty(TimelineEdits.Split(project, new[] { clip }, 15));
        }

        [Fact]
        public void Delete_Plain_LeavesGap_Ripple_ClosesIt()
        {
            var project = TestProjects.Empty(magnetic: false);
            var track = project.MainTrack;
            var a = TestProjects.Add(project, track, 0, 10);
            var b = TestProjects.Add(project, track, 10, 20);
            TestProjects.Add(project, track, 30, 10);
            Assert.Equal(1, TimelineEdits.Delete(project, new[] { b }, ripple: false));
            Assert.Equal("0-10 30-40", Layout(track));
            Assert.True(TimelineEdits.CloseGap(project, track, 15));
            Assert.Equal("0-10 10-20", Layout(track));
            TimelineEdits.Delete(project, new[] { a }, ripple: true);
            Assert.Equal("0-10", Layout(track));
        }

        [Fact]
        public void Delete_Ripple_MultipleClips()
        {
            var project = TestProjects.Empty(magnetic: false);
            var track = project.MainTrack;
            var a = TestProjects.Add(project, track, 0, 10);
            TestProjects.Add(project, track, 10, 10);
            var c = TestProjects.Add(project, track, 20, 10);
            TestProjects.Add(project, track, 30, 10);
            TimelineEdits.Delete(project, new[] { a, c }, ripple: true);
            Assert.Equal("0-10 10-20", Layout(track));
        }

        [Fact]
        public void Delete_SkipsLockedTrack()
        {
            var project = TestProjects.Empty(magnetic: false);
            var clip = TestProjects.Add(project, project.MainTrack, 0, 10);
            project.MainTrack.Locked = true;
            Assert.Equal(0, TimelineEdits.Delete(project, new[] { clip }, false));
        }

        [Fact]
        public void CloseGap_NotAGap_ReturnsFalse()
        {
            var project = TestProjects.Empty(magnetic: false);
            TestProjects.Add(project, project.MainTrack, 0, 10);
            Assert.False(TimelineEdits.CloseGap(project, project.MainTrack, 5));
            Assert.False(TimelineEdits.CloseGap(project, project.MainTrack, 50));
        }

        // ---------- 속도·오디오 분리 ----------

        [Fact]
        public void SetSpeed_ChangesDuration_Magnetic()
        {
            var project = TestProjects.Empty();
            var a = TestProjects.VideoClip(0, 60);
            TimelineEdits.Place(project, project.MainTrack, a, 0);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 10), 999);
            TimelineEdits.SetSpeed(project, a, 2);
            Assert.Equal("0-30 30-40", Layout(project.MainTrack));
        }

        [Fact]
        public void SetSpeed_OverlapNonMagnetic_Throws_AndImageRefused()
        {
            var project = TestProjects.Empty(magnetic: false);
            var a = TestProjects.Add(project, project.MainTrack, 0, 30);
            TestProjects.Add(project, project.MainTrack, 30, 10);
            Assert.Throws<EditException>(() => TimelineEdits.SetSpeed(project, a, 0.5));
            Assert.Equal(1, a.Speed);
            var image = TimelineEdits.CreateClip(TestProjects.Image(), FrameRate.Default, 100, 30);
            TimelineEdits.Place(project, project.MainTrack, image, 100);
            Assert.Throws<EditException>(() => TimelineEdits.SetSpeed(project, image, 2));
        }

        [Fact]
        public void DetachAudio_MakesAudioClip_AndSilencesVideoClip()
        {
            var project = TestProjects.Empty();
            var clip = TestProjects.VideoClip(0, 30);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            var audio = TimelineEdits.DetachAudio(project, clip);
            Assert.True(clip.AudioDetached);
            Assert.False(clip.IsAudible);
            Assert.True(audio.AudioOnly);
            Assert.False(audio.IsVisual);
            Assert.Contains(audio, project.AudioTracks[0].Clips);
            Assert.Equal(clip.Start, audio.Start);
            Assert.Throws<EditException>(() => TimelineEdits.DetachAudio(project, clip));
            // 두 번째 클립: 오디오 1이 겹치면 새 오디오 트랙
            var other = TestProjects.Add(project, TimelineEdits.AddTrack(project, TrackKind.Video), 0, 30);
            TimelineEdits.DetachAudio(project, other);
            Assert.Equal(2, project.AudioTracks.Count);
        }

        // ---------- 트랙·붙기 ----------

        [Fact]
        public void RemoveTrack_MainIsProtected()
        {
            var project = TestProjects.Empty();
            Assert.Throws<EditException>(() => TimelineEdits.RemoveTrack(project, project.MainTrack));
            var extra = TimelineEdits.AddTrack(project, TrackKind.Video);
            TimelineEdits.RemoveTrack(project, extra);
            Assert.Single(project.VideoTracks);
        }

        [Fact]
        public void Snap_PicksNearestWithinThreshold()
        {
            var targets = new long[] { 0, 100, 130 };
            Assert.Equal(100, TimelineEdits.Snap(targets, 104, 5, out var snapped));
            Assert.True(snapped);
            Assert.Equal(110, TimelineEdits.Snap(targets, 110, 5, out snapped));
            Assert.False(snapped);
        }

        [Fact]
        public void SnapMove_SnapsEndWhenCloser()
        {
            var targets = new long[] { 100 };
            // 길이 20 클립을 78에 놓으면 끝(98)이 100에 붙는다
            Assert.Equal(80, TimelineEdits.SnapMove(targets, 78, 20, 5, out var at));
            Assert.Equal(100, at);
        }

        [Fact]
        public void SnapTargets_ExcludeMovingClip()
        {
            var project = TestProjects.Empty(magnetic: false);
            var a = TestProjects.Add(project, project.MainTrack, 10, 10);
            TestProjects.Add(project, project.MainTrack, 50, 10);
            project.Markers.Add(new Marker { Frame = 77 });
            var targets = TimelineEdits.SnapTargets(project, 33, new[] { a });
            Assert.DoesNotContain(10L, targets);
            Assert.Contains(50L, targets);
            Assert.Contains(77L, targets);
            Assert.Contains(33L, targets);
        }

        [Fact]
        public void EditPoints_AreSortedAndDistinct()
        {
            var project = TestProjects.Empty(magnetic: false);
            TestProjects.Add(project, project.MainTrack, 10, 10);
            TestProjects.Add(project, project.MainTrack, 20, 10);
            Assert.Equal(new long[] { 0, 10, 20, 30 }, TimelineEdits.EditPoints(project));
        }
    }
}
