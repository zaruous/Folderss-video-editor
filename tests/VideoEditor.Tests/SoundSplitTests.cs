using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>사운드 분리(이슈 #5): 영상·사운드 링크(옮기기·다듬기·분할·삭제·속도), 링크 해제·어긋남·싱크 맞추기, 합치기, 미디어에서 나눠 놓기, 사운드 파일로 꺼내기.</summary>
    public class SoundSplitTests
    {
        private readonly ITestOutputHelper _out;

        public SoundSplitTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>주 트랙에 frames 길이의 동영상을 놓고 사운드를 분리한다.</summary>
        private static (Project Project, Clip Video, Clip Sound) Detached(bool magnetic = false, long start = 0, long frames = 60)
        {
            var project = TestProjects.Empty(magnetic);
            var video = TestProjects.VideoClip(start, frames);
            TimelineEdits.Place(project, project.MainTrack, video, start);
            var sound = TimelineEdits.DetachAudio(project, video);
            return (project, video, sound);
        }

        private static Clip Music(long frames, string path = "music.mp3")
        {
            var clip = TimelineEdits.CreateClip(TestProjects.Audio(path), FrameRate.Default, 0, 0);
            clip.SourceOut = frames;
            return clip;
        }

        // ---------- 링크 ----------

        [Fact]
        public void DetachAudio_LinksVideoAndSound()
        {
            var (project, video, sound) = Detached();
            Assert.NotNull(video.LinkId);
            Assert.Equal(video.LinkId, sound.LinkId);
            Assert.False(video.Unlinked || sound.Unlinked);
            Assert.Same(sound, ClipLinks.Partner(project, video));
            Assert.Same(video, ClipLinks.LinkedPartner(project, sound));
            Assert.Equal(0, ClipLinks.Offset(sound, video));
            Assert.True(ClipLinks.IsVideoSide(video) && ClipLinks.IsSoundSide(sound));
        }

        [Fact]
        public void Move_LinkedVideo_SoundFollowsOnItsOwnTrack()
        {
            var (project, video, sound) = Detached();
            TimelineEdits.Move(project, video, project.MainTrack, 30);
            Assert.Equal(30, video.Start);
            Assert.Equal(30, sound.Start);
            Assert.Contains(sound, project.AudioTracks[0].Clips);
            // 다른 동영상 트랙으로 옮겨도 사운드는 제 트랙에서 같은 시간으로
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            TimelineEdits.Move(project, video, overlay, 90);
            Assert.Contains(video, overlay.Clips);
            Assert.Equal(90, sound.Start);
            Assert.Contains(sound, project.AudioTracks[0].Clips);
        }

        [Fact]
        public void Move_Independent_MovesOnlyThatClip_AndKeepsTheNewOffset()
        {
            var (project, video, sound) = Detached();
            TimelineEdits.Move(project, sound, project.AudioTracks[0], 10, independent: true);
            Assert.Equal(0, video.Start);
            Assert.Equal(10, sound.Start);
            Assert.Equal(10, ClipLinks.Offset(sound, video));
            // 링크는 그대로 — 다음 옮기기는 새 차이를 지킨다
            Assert.Same(video, ClipLinks.LinkedPartner(project, sound));
            TimelineEdits.Move(project, video, project.MainTrack, 30);
            Assert.Equal(40, sound.Start);
        }

        [Fact]
        public void Move_SoundOfMagneticMainVideo_MovesTheVideo_AndSoundFollows()
        {
            var project = TestProjects.Empty(magnetic: true);
            var other = TestProjects.VideoClip(0, 30, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, other, 0);
            var video = TestProjects.VideoClip(0, 60);
            TimelineEdits.Place(project, project.MainTrack, video, 999);
            var sound = TimelineEdits.DetachAudio(project, video);
            Assert.Equal(30, sound.Start);
            // 사운드를 앞으로 끌면 주 트랙의 영상이 앞 경계로 가고 사운드가 따라간다
            TimelineEdits.Move(project, sound, project.AudioTracks[0], 0);
            Assert.Equal(0, video.Start);
            Assert.Equal(60, other.Start);
            Assert.Equal(0, sound.Start);
            Assert.Contains(sound, project.AudioTracks[0].Clips);
        }

        [Fact]
        public void MagneticInsert_PushesVideo_AndSoundFollows()
        {
            var (project, video, sound) = Detached(magnetic: true);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 30, "b.mp4"), 0);
            Assert.Equal(30, video.Start);
            Assert.Equal(30, sound.Start);
            // 앞 클립을 지우면 당겨지고 사운드도 따라 당겨진다
            TimelineEdits.Delete(project, project.MainTrack.Clips.Where(c => c.Path == "b.mp4").ToList(), false);
            Assert.Equal(0, video.Start);
            Assert.Equal(0, sound.Start);
        }

        [Fact]
        public void Move_SwapOnMagneticMain_SoundsMirrorTheVideos()
        {
            var project = TestProjects.Empty(magnetic: true);
            var first = TestProjects.VideoClip(0, 30);
            TimelineEdits.Place(project, project.MainTrack, first, 0);
            var firstSound = TimelineEdits.DetachAudio(project, first);
            var second = TestProjects.VideoClip(0, 60, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, second, 999);
            var secondSound = TimelineEdits.DetachAudio(project, second);
            Assert.Contains(secondSound, project.AudioTracks[0].Clips);
            TimelineEdits.Move(project, second, project.MainTrack, 0);
            Assert.Equal((0L, 60L), (second.Start, first.Start));
            Assert.Equal((0L, 60L), (secondSound.Start, firstSound.Start));
            Assert.Single(project.AudioTracks);
        }

        [Fact]
        public void Move_ReinsertedAtTheSamePlace_LeavesLinkedSoundAlone()
        {
            var project = TestProjects.Empty(magnetic: true);
            var other = TestProjects.VideoClip(0, 30, "b.mp4");
            TimelineEdits.Place(project, project.MainTrack, other, 0);
            var video = TestProjects.VideoClip(0, 60);
            TimelineEdits.Place(project, project.MainTrack, video, 999);
            var sound = TimelineEdits.DetachAudio(project, video);
            var music = Music(30);
            TimelineEdits.Place(project, project.AudioTracks[0], music, 90);
            // 앞 클립을 빼냈다가 같은 경계에 다시 끼우면(잠깐 당겨졌다 밀림) 사운드는 제자리·제 트랙
            TimelineEdits.Move(project, other, project.MainTrack, 10);
            Assert.Equal((0L, 30L), (other.Start, video.Start));
            Assert.Equal(30, sound.Start);
            Assert.Single(project.AudioTracks);
            Assert.Equal((90L, 120L), (music.Start, music.End));
        }

        [Fact]
        public void Follower_DoesNotOverwriteOtherClips()
        {
            var (project, video, sound) = Detached(magnetic: true);
            var music = Music(60);
            TimelineEdits.Place(project, project.AudioTracks[0], music, 60);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 30, "b.mp4"), 0);
            // 사운드가 갈 자리(30~90)에 음악(60~120)이 있으면 덮지 않고 새 오디오 트랙으로
            Assert.Equal(30, sound.Start);
            Assert.Equal(2, project.AudioTracks.Count);
            Assert.Contains(sound, project.AudioTracks[1].Clips);
            Assert.Equal(60, music.Start);
            Assert.Equal(120, music.End);
        }

        [Fact]
        public void LockedPartner_StaysPut_AndShowsOffset()
        {
            var (project, video, sound) = Detached();
            project.AudioTracks[0].Locked = true;
            TimelineEdits.Move(project, video, project.MainTrack, 15);
            Assert.Equal(0, sound.Start);
            Assert.Equal(-15, ClipLinks.Offset(sound, video));
        }

        [Fact]
        public void Trim_LinkedPair_TrimsBothEdges()
        {
            var (project, video, sound) = Detached();
            TimelineEdits.TrimStart(project, video, 10);
            Assert.Equal((10L, 60L, 10L), (sound.Start, sound.End, sound.SourceIn));
            TimelineEdits.TrimEnd(project, sound, 50);
            Assert.Equal((10L, 50L), (video.Start, video.End));
            // Alt: 이 클립만
            TimelineEdits.TrimEnd(project, sound, 40, independent: true);
            Assert.Equal(50, video.End);
            Assert.Equal(40, sound.End);
        }

        [Fact]
        public void Trim_OnMagneticMain_SoundStaysInSync_AndDoesNotJumpTracks()
        {
            var project = TestProjects.Empty(magnetic: true);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 30, "b.mp4"), 0);
            var video = TestProjects.VideoClip(0, 60);
            TimelineEdits.Place(project, project.MainTrack, video, 999);
            var sound = TimelineEdits.DetachAudio(project, video);
            // 사운드 바로 앞에 다른 소리(0~30)
            var before = Music(30);
            TimelineEdits.Place(project, project.AudioTracks[0], before, 0);
            // 앞을 10 줄이면 주 트랙은 그대로 붙고(30~80) 사운드도 같은 구간으로
            TimelineEdits.TrimStart(project, video, 40);
            Assert.Equal((30L, 80L, 10L), (video.Start, video.End, video.SourceIn));
            Assert.Equal((30L, 80L, 10L), (sound.Start, sound.End, sound.SourceIn));
            // 앞을 5 늘이면 사운드도 늘어난다(앞 소리와 붙은 채)
            TimelineEdits.TrimStart(project, video, 25);
            Assert.Equal((30L, 85L, 5L), (video.Start, video.End, video.SourceIn));
            Assert.Equal((30L, 85L, 5L), (sound.Start, sound.End, sound.SourceIn));
            Assert.Single(project.AudioTracks);
            Assert.Equal((0L, 30L), (before.Start, before.End));
            // 사운드 쪽을 다듬어도 주 트랙의 영상이 다듬어지고 사운드가 따라간다
            TimelineEdits.TrimEnd(project, sound, 75);
            Assert.Equal((30L, 75L), (video.Start, video.End));
            Assert.Equal((30L, 75L), (sound.Start, sound.End));
        }

        [Fact]
        public void Split_LinkedPair_MakesTwoPairsWithDistinctLinkIds()
        {
            var (project, video, sound) = Detached();
            var made = TimelineEdits.Split(project, new[] { video }, 20);
            Assert.Equal(2, made.Count);
            var videoRight = made.Single(c => !c.AudioOnly);
            var soundRight = made.Single(c => c.AudioOnly);
            Assert.Equal(video.LinkId, sound.LinkId);
            Assert.NotNull(videoRight.LinkId);
            Assert.Equal(videoRight.LinkId, soundRight.LinkId);
            Assert.NotEqual(video.LinkId, videoRight.LinkId);
            Assert.Same(soundRight, ClipLinks.Partner(project, videoRight));
            Assert.Equal((20L, 60L), (soundRight.Start, soundRight.End));
            // 저장·실행 취소 상태로 옮겨도 짝 둘이 그대로
            var copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            Assert.Equal(2, copy.AllClips.Where(c => c.LinkId != null).GroupBy(c => c.LinkId).Count(g => g.Count() == 2));
        }

        [Fact]
        public void Delete_LinkedClip_DeletesBoth_LockedPartnerIsUnlinked()
        {
            var (project, video, _) = Detached(magnetic: true);
            Assert.Equal(2, TimelineEdits.Delete(project, new[] { video }, false));
            Assert.Empty(project.AllClips);

            var (locked, lockedVideo, lockedSound) = Detached();
            locked.AudioTracks[0].Locked = true;
            Assert.Equal(1, TimelineEdits.Delete(locked, new[] { lockedVideo }, false));
            Assert.Null(lockedSound.LinkId);
        }

        [Fact]
        public void SetSpeed_LinkedPair_ChangesBoth()
        {
            var (project, video, sound) = Detached();
            TimelineEdits.SetSpeed(project, video, 2);
            Assert.Equal(2, sound.Speed);
            Assert.Equal(30, sound.Duration);
            // 주 트랙(마그네틱)의 영상과 링크된 사운드의 속도를 바꾸면 영상이 바뀌고 사운드가 따라간다
            var (magnetic, magneticVideo, magneticSound) = Detached(magnetic: true);
            TimelineEdits.Place(magnetic, magnetic.MainTrack, TestProjects.VideoClip(0, 30, "b.mp4"), 999);
            TimelineEdits.SetSpeed(magnetic, magneticSound, 0.5);
            Assert.Equal((0.5, 0.5), (magneticVideo.Speed, magneticSound.Speed));
            Assert.Equal((120L, 120L), (magneticVideo.Duration, magneticSound.Duration));
            Assert.Equal(120, magnetic.MainTrack.Clips.Single(c => c.Path == "b.mp4").Start);
        }

        [Fact]
        public void SetSpeed_LongerSound_MovesToAFreeTrack_InsteadOfOverwriting()
        {
            var (project, video, sound) = Detached();
            var music = Music(30);
            TimelineEdits.Place(project, project.AudioTracks[0], music, 60);
            TimelineEdits.SetSpeed(project, video, 0.5);
            Assert.Equal((120L, 120L), (video.Duration, sound.Duration));
            Assert.Equal(2, project.AudioTracks.Count);
            Assert.Contains(sound, project.AudioTracks[1].Clips);
            Assert.Equal(0, sound.Start);
            Assert.Equal((60L, 90L), (music.Start, music.End));
            Assert.Single(project.AllClips, c => c == sound);
        }

        [Fact]
        public void CloseGap_ShiftedVideo_SoundFollows()
        {
            var project = TestProjects.Empty(magnetic: false);
            var video = TestProjects.VideoClip(40, 60);
            TimelineEdits.Place(project, project.MainTrack, video, 40);
            var sound = TimelineEdits.DetachAudio(project, video);
            Assert.True(TimelineEdits.CloseGap(project, project.MainTrack, 10));
            Assert.Equal(0, video.Start);
            Assert.Equal(0, sound.Start);
        }

        [Fact]
        public void ChangeRate_KeepsLinkedPairInSync()
        {
            var project = TestProjects.Empty(magnetic: true);
            TimelineEdits.Place(project, project.MainTrack, TestProjects.VideoClip(0, 31, "b.mp4"), 0);
            var video = TestProjects.VideoClip(0, 61);
            TimelineEdits.Place(project, project.MainTrack, video, 999);
            var sound = TimelineEdits.DetachAudio(project, video);
            ProjectSettings.ChangeRate(project, new FrameRate(24, 1));
            Assert.Equal(video.Start, sound.Start);
            Assert.Equal(0, ClipLinks.Offset(sound, video));
        }

        [Fact]
        public void Session_SelectsLinkedTogether_AndRemembersThePrimary()
        {
            var session = new EditorSession();
            session.Execute(p =>
            {
                var clip = TestProjects.VideoClip(0, 60);
                TimelineEdits.Place(p, p.MainTrack, clip, 0);
                TimelineEdits.DetachAudio(p, clip);
            });
            var video = session.Project.MainTrack.Clips[0];
            var sound = session.Project.AudioTracks[0].Clips[0];
            session.Select(new[] { sound });
            Assert.Equal(2, session.SelectedIds.Count);
            Assert.Equal(sound.Id, session.PrimaryClip.Id);
            session.SetPrimary(video);
            Assert.Equal(video.Id, session.PrimaryClip.Id);
            session.ToggleSelect(video);
            Assert.Empty(session.SelectedIds);
            Assert.Null(session.PrimaryClip);
            // 링크를 풀면 따로 고른다
            Assert.True(session.Execute(p => ClipLinks.Unlink(p, p.AllClips.ToList())));
            session.Select(new[] { session.Project.MainTrack.Clips[0] });
            Assert.Single(session.SelectedIds);
        }

        [Fact]
        public void Unlink_ThenMove_ShowsOffset_SyncRealigns_Relink()
        {
            var (project, video, sound) = Detached();
            Assert.Equal(1, ClipLinks.Unlink(project, new[] { video, sound }));
            Assert.True(video.Unlinked && sound.Unlinked);
            TimelineEdits.Move(project, sound, project.AudioTracks[0], 35);
            Assert.Equal(0, video.Start);
            Assert.Equal(35, ClipLinks.Offset(sound, video));
            Assert.Equal("+0:00:01.05", ClipLinks.FormatOffset(ClipLinks.Offset(sound, video), project.Rate));
            Assert.Equal("\u22120:00:01.05", ClipLinks.FormatOffset(ClipLinks.Offset(video, sound), project.Rate));
            Assert.Equal(1, ClipLinks.Sync(project, new[] { video }));
            Assert.Equal(0, sound.Start);
            Assert.Throws<EditException>(() => ClipLinks.Sync(project, new[] { video }));
            Assert.Equal(1, ClipLinks.Relink(project, new[] { sound }));
            Assert.False(video.Unlinked || sound.Unlinked);
            Assert.Throws<EditException>(() => ClipLinks.Relink(project, new[] { sound }));
        }

        [Fact]
        public void Sync_MovesTheSound_AndCutsWhatWouldGoBeforeZero()
        {
            var (project, video, sound) = Detached();
            ClipLinks.Unlink(project, new[] { video });
            // 영상을 20 뒤로: 사운드가 20 이르다 → 맞추면 사운드가 20으로
            TimelineEdits.Move(project, video, project.MainTrack, 20);
            Assert.Equal(-20, ClipLinks.Offset(sound, video));
            ClipLinks.Sync(project, new[] { sound });
            Assert.Equal(20, sound.Start);
            Assert.Equal(0, ClipLinks.Offset(sound, video));
            // 영상의 앞 10을 잘라 0에 두면(원본 10프레임이 0에) 사운드는 -10에 와야 한다 → 0 앞 10프레임을 잘라 0부터
            var (cut, cutVideo, cutSound) = Detached();
            ClipLinks.Unlink(cut, new[] { cutVideo });
            TimelineEdits.TrimStart(cut, cutVideo, 10);
            TimelineEdits.Move(cut, cutVideo, cut.MainTrack, 0);
            Assert.Equal(10, ClipLinks.Offset(cutSound, cutVideo));
            ClipLinks.Sync(cut, new[] { cutVideo });
            Assert.Equal((0L, 50L, 10L), (cutSound.Start, cutSound.End, cutSound.SourceIn));
            Assert.Equal(0, ClipLinks.Offset(cutSound, cutVideo));
        }

        [Fact]
        public void Rejoin_LinkedPair_RemovesSound_RestoresVideoSound_OneUndo()
        {
            var session = new EditorSession();
            session.Execute(p =>
            {
                var clip = TestProjects.VideoClip(0, 60);
                TimelineEdits.Place(p, p.MainTrack, clip, 0);
                TimelineEdits.DetachAudio(p, clip);
            });
            session.Execute(p => p.AudioTracks[0].Clips[0].Volume = 0.5);
            Assert.True(session.Execute(p => ClipLinks.Rejoin(p, p.AllClips.ToList())));
            var video = session.Project.AllClips.Single();
            Assert.False(video.AudioDetached);
            Assert.True(video.IsAudible);
            Assert.Equal(0.5, video.Volume);
            Assert.Null(video.LinkId);
            session.Undo();
            Assert.Equal(2, session.Project.AllClips.Count());
            Assert.NotNull(session.Project.MainTrack.Clips[0].LinkId);
            Assert.False(session.Execute(p => ClipLinks.Rejoin(p, p.MainTrack.Clips.ToList())));
            Assert.Contains("합칠", session.LastError);
        }

        [Fact]
        public void Rejoin_SoundsTheSameAsBeforeTheSplit()
        {
            var project = TestProjects.Empty();
            var clip = TestProjects.VideoClip(0, 60);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            var options = new RenderOptions { From = 0, To = 60, Width = 64, Height = 36, Video = false };
            var before = RenderGraph.Build(project, options);
            TimelineEdits.DetachAudio(project, clip);
            ClipLinks.Rejoin(project, project.AllClips.ToList());
            var after = RenderGraph.Build(project, options);
            Assert.Equal(before.Filter, after.Filter);
            Assert.Equal(before.Inputs.Select(i => string.Join(" ", i)), after.Inputs.Select(i => string.Join(" ", i)));
        }

        [Fact]
        public void RejoinPairs_SameRangeWithoutLink_ForOldProjects()
        {
            var video = TestProjects.VideoClip(0, 60);
            video.AudioDetached = true;
            var sound = video.CloneWithNewId();
            TimelineEdits.MakeAudioOnly(sound);
            sound.AudioDetached = false;
            Assert.Single(ClipLinks.RejoinPairs(new[] { video, sound }));
            sound.Start = 1;
            Assert.Empty(ClipLinks.RejoinPairs(new[] { video, sound }));
            // 다른 짝과 링크된 영상은 같은 구간의 다른 사운드와 합치지 않는다
            sound.Start = 0;
            video.LinkId = "abc";
            Assert.Empty(ClipLinks.RejoinPairs(new[] { video, sound }));
        }

        [Fact]
        public void PlaceSeparated_RespectsMagneticMain_AndOneUndoRemovesBoth()
        {
            var session = new EditorSession();
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, TestProjects.VideoClip(0, 90, "b.mp4"), 0));
            var first = TestProjects.Video("a.mp4", 2);
            var second = TestProjects.Video("c.mp4", 1);
            Assert.True(session.Execute(p =>
            {
                // 재생 헤드 30은 앞 클립(0~90)의 가운데 앞이라 그 앞 경계에, 두 번째는 첫째 뒤에 이어서
                var video = ClipLinks.PlaceSeparated(p, first, 30);
                ClipLinks.PlaceSeparated(p, second, video.End);
            }));
            var project = session.Project;
            Assert.Equal("a.mp4 0-60 · c.mp4 60-90 · b.mp4 90-180", string.Join(" · ", project.MainTrack.Clips.Select(c => c.Path + " " + c.Start + "-" + c.End)));
            var sounds = project.AudioTracks.SelectMany(t => t.Clips).OrderBy(c => c.Start).ToList();
            Assert.Equal("a.mp4 0-60 · c.mp4 60-90", string.Join(" · ", sounds.Select(c => c.Path + " " + c.Start + "-" + c.End)));
            Assert.All(project.MainTrack.Clips.Where(c => c.Path != "b.mp4"), v => Assert.Same(sounds.Single(s => s.Path == v.Path), ClipLinks.LinkedPartner(project, v)));
            session.Undo();
            Assert.Single(session.Project.AllClips);
            Assert.False(ClipLinks.CanSplit(TestProjects.Audio()));
            Assert.False(ClipLinks.CanSplit(TestProjects.Video(audio: false)));
            Assert.Throws<EditException>(() => ClipLinks.PlaceSeparated(TestProjects.Empty(), TestProjects.Image(), 0));
        }

        [Fact]
        public void CopyLinks_PairsOnlyWhenBothAreCopied()
        {
            var (project, video, sound) = Detached();
            var other = TestProjects.VideoClip(100, 10, "b.mp4");
            var originals = new[] { video, sound, other };
            var copies = originals.Select(c => c.CloneWithNewId()).ToList();
            Assert.All(copies, c => Assert.Null(c.LinkId));
            ClipLinks.CopyLinks(originals, copies);
            Assert.NotNull(copies[0].LinkId);
            Assert.Equal(copies[0].LinkId, copies[1].LinkId);
            Assert.NotEqual(video.LinkId, copies[0].LinkId);
            Assert.Null(copies[2].LinkId);
            var alone = new[] { video.CloneWithNewId() };
            ClipLinks.CopyLinks(new[] { video }, alone);
            Assert.Null(alone[0].LinkId);
        }

        [Fact]
        public void OnePerPair_DropsTheSecondOfAPair()
        {
            var (project, video, sound) = Detached();
            var other = TestProjects.Add(project, TimelineEdits.AddTrack(project, TrackKind.Video), 0, 10, "b.mp4");
            Assert.Equal(new[] { video, other }, ClipLinks.OnePerPair(project, new[] { video, sound, other, video }));
        }

        // ---------- 프로젝트 파일 ----------

        [Fact]
        public void ProjectFile_KeepsLinks_OldFilesHaveNone()
        {
            var (project, video, sound) = Detached();
            ClipLinks.Unlink(project, new[] { video });
            var folder = Path.Combine(Path.GetTempPath(), "fv-link-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder, "a.fvproj");
                ProjectFile.Save(project, path);
                var loaded = ProjectFile.Load(path);
                var v = loaded.MainTrack.Clips.Single();
                var s = loaded.AudioTracks[0].Clips.Single();
                Assert.Equal(video.LinkId, v.LinkId);
                Assert.Equal(v.LinkId, s.LinkId);
                Assert.True(v.Unlinked && s.Unlinked);

                // 예전 형식(LinkId 없음): 분리한 클립이 따로 움직인다
                var old = File.ReadAllText(path).Replace("\"LinkId\"", "\"OldField\"");
                File.WriteAllText(path, old);
                var oldProject = ProjectFile.Load(path);
                Assert.All(oldProject.AllClips, c => Assert.True(c.LinkId == null && !c.Unlinked));
                var oldVideo = oldProject.MainTrack.Clips.Single();
                TimelineEdits.Move(oldProject, oldVideo, oldProject.MainTrack, 30);
                Assert.Equal(0, oldProject.AudioTracks[0].Clips.Single().Start);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
            // 링크가 없으면 JSON에 쓰지 않는다(예전 프로젝트와 같은 모양)
            Assert.DoesNotContain("LinkId", ProjectFile.Snapshot(TestProjects.Empty()));
        }

        [Fact]
        public void ProjectFile_DropsInvalidLinks()
        {
            var (project, video, sound) = Detached();
            // 같은 LinkId가 셋, 원본이 다른 짝, 16진수가 아닌 값, 한쪽만 풀린 짝
            var third = TestProjects.Add(project, TimelineEdits.AddTrack(project, TrackKind.Video), 0, 10);
            third.LinkId = video.LinkId;
            var (other, _, otherSound) = Detached();
            otherSound.Path = "elsewhere.mp4";
            var bad = TestProjects.Add(project, project.MainTrack, 200, 10, "c.mp4");
            bad.LinkId = "not-hex!";
            var copy = ProjectFile.FromSnapshot(ProjectFile.Snapshot(project));
            Assert.All(copy.AllClips, c => Assert.Null(c.LinkId));
            Assert.All(ProjectFile.FromSnapshot(ProjectFile.Snapshot(other)).AllClips, c => Assert.Null(c.LinkId));

            var (half, halfVideo, _) = Detached();
            halfVideo.Unlinked = true;
            Assert.All(ProjectFile.FromSnapshot(ProjectFile.Snapshot(half)).AllClips, c => Assert.True(c.Unlinked && c.LinkId != null));
        }

        // ---------- 어긋남 표시 ----------

        [Theory]
        [InlineData(0, "0f")]
        [InlineData(3, "+3f")]
        [InlineData(-3, "\u22123f")]
        [InlineData(29, "+29f")]
        [InlineData(30, "+0:00:01.00")]
        [InlineData(35, "+0:00:01.05")]
        [InlineData(-35, "\u22120:00:01.05")]
        [InlineData(30 * 3661 + 2, "+1:01:01.02")]
        public void FormatOffset_FramesThenTime(long frames, string expected)
        {
            Assert.Equal(expected, ClipLinks.FormatOffset(frames, FrameRate.Default));
        }

        [Fact]
        public void FormatOffset_NtscRate()
        {
            var rate = new FrameRate(30000, 1001);
            Assert.Equal("+29f", ClipLinks.FormatOffset(29, rate));
            Assert.Equal("+0:00:01.00", ClipLinks.FormatOffset(30, rate));
        }

        // ---------- 사운드 파일로 꺼내기 ----------

        [Theory]
        [InlineData("aac", ".m4a")]
        [InlineData("mp3", ".mp3")]
        [InlineData("opus", ".opus")]
        [InlineData("vorbis", ".ogg")]
        [InlineData("flac", ".flac")]
        [InlineData("pcm_s16le", ".wav")]
        [InlineData("pcm_s24le", ".wav")]
        [InlineData("AAC", ".m4a")]
        [InlineData("ac3", ".mka")]
        [InlineData("", ".mka")]
        [InlineData(null, ".mka")]
        public void ExtensionFor_PicksContainerByCodec(string codec, string expected)
        {
            Assert.Equal(expected, SoundExtract.ExtensionFor(codec));
        }

        [Theory]
        [InlineData(".m4a", "aac", 1.0, true)]
        [InlineData(".mka", null, 1.0, true)]
        [InlineData(".m4a", "aac", 2.0, false)]
        [InlineData(".wav", "aac", 1.0, false)]
        [InlineData(".WAV", "pcm_s16le", 1.0, true)]
        [InlineData(".wav", "pcm_s16le", 0.5, false)]
        public void ShouldCopy_OnlyAtNormalSpeed_AndPcmIntoWav(string extension, string codec, double speed, bool expected)
        {
            Assert.Equal(expected, SoundExtract.ShouldCopy(extension, codec, speed));
        }

        [Fact]
        public void Arguments_WholeFileCopy()
        {
            var args = SoundExtract.Arguments(@"C:\v\a.mp4", @"C:\v\a.m4a", true, 0, 0, 1);
            Assert.Equal(new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-protocol_whitelist", "file", "-i", @"C:\v\a.mp4",
                "-map", "0:a:0", "-vn", "-c:a", "copy", @"C:\v\a.m4a" }, args);
        }

        [Fact]
        public void Arguments_RangeCopy_SeeksBeforeInput()
        {
            var args = SoundExtract.Arguments("a.mp4", "a.m4a", true, 1.5, 2, 1);
            var input = args.IndexOf("-i");
            Assert.Equal("1.5", args[args.IndexOf("-ss") + 1]);
            Assert.Equal("2", args[args.IndexOf("-t") + 1]);
            Assert.True(args.IndexOf("-ss") < input && args.IndexOf("-t") < input);
            Assert.DoesNotContain("-af", args);
        }

        [Fact]
        public void Arguments_Speed_ReencodesToWavWithAtempoChain()
        {
            var fast = SoundExtract.Arguments("a.mp4", "a.wav", false, 0, 3, 2);
            Assert.Equal("atempo=2", fast[fast.IndexOf("-af") + 1]);
            Assert.Equal("pcm_s16le", fast[fast.IndexOf("-c:a") + 1]);
            var slow = SoundExtract.Arguments("a.mp4", "a.wav", false, 0, 3, 0.2);
            Assert.Equal("atempo=0.5,atempo=0.5,atempo=0.8", slow[slow.IndexOf("-af") + 1]);
            var normal = SoundExtract.Arguments("a.mp4", "a.wav", false, 0, 3, 1);
            Assert.DoesNotContain("-af", normal);
        }

        // ---------- FFmpeg ----------

        private static string Output(string extension)
        {
            return Path.Combine(FfmpegMedia.Folder, "sound-" + Guid.NewGuid().ToString("N") + extension);
        }

        [FfmpegFact]
        public async Task Extract_CopiesAacIntoM4a_WithoutReencoding()
        {
            var output = Output(".m4a");
            var saved = await SoundExtractor.Extract(FfmpegMedia.Tool, FfmpegMedia.TestVideo, "aac", output, 0, 0, 1, CancellationToken.None);
            Assert.Equal(output, saved);
            var info = FfmpegMedia.Probe(saved);
            _out.WriteLine(info.Kind + " · " + info.AudioCodec + " · " + info.DurationSeconds + "초");
            Assert.Equal(MediaKind.Audio, info.Kind);
            Assert.Equal("aac", info.AudioCodec);
            Assert.InRange(info.DurationSeconds, 1.9, 2.2);
            Assert.Empty(Directory.GetFiles(FfmpegMedia.Folder, "*.part-*"));
            File.Delete(saved);
        }

        [FfmpegFact]
        public async Task Extract_ContainerRefusesCodec_FallsBackToWav()
        {
            // MP3 그릇에는 AAC를 담을 수 없다 → 같은 이름의 .wav
            var output = Output(".mp3");
            var saved = await SoundExtractor.Extract(FfmpegMedia.Tool, FfmpegMedia.TestVideo, "aac", output, 0, 0, 1, CancellationToken.None);
            Assert.Equal(Path.ChangeExtension(output, ".wav"), saved);
            Assert.False(File.Exists(output));
            Assert.Equal("pcm_s16le", FfmpegMedia.Probe(saved).AudioCodec);
            File.Delete(saved);
        }

        [FfmpegFact]
        public async Task Extract_ClipRangeWithSpeed_WritesWav()
        {
            // 원본 0.5~1.5초(1초)를 2배속으로 → 0.5초
            var output = Output(".wav");
            var saved = await SoundExtractor.Extract(FfmpegMedia.Tool, FfmpegMedia.TestVideo, "aac", output, 0.5, 1, 2, CancellationToken.None);
            Assert.Equal(output, saved);
            var info = FfmpegMedia.Probe(saved);
            _out.WriteLine(info.AudioCodec + " · " + info.DurationSeconds + "초");
            Assert.Equal("pcm_s16le", info.AudioCodec);
            Assert.InRange(info.DurationSeconds, 0.45, 0.56);
            File.Delete(saved);
        }

        private static async Task<byte[]> Pcm(Project project, long from, long to)
        {
            var plan = RenderGraph.Build(project, new RenderOptions { From = from, To = to, Width = 16, Height = 16, Video = false, Preview = true });
            var args = RenderCommand.Arguments(FfmpegMedia.Tool, plan, new[] { "-map", "[aout]", "-f", "s16le", "-ac", "2", "-ar", "48000", "pipe:1" }, out var script);
            try
            {
                return await FfmpegRunner.RunBytes(FfmpegMedia.Tool.FfmpegPath, args, TimeSpan.FromMinutes(1), CancellationToken.None);
            }
            finally
            {
                RenderCommand.TryDelete(script);
            }
        }

        [FfmpegFact]
        public async Task Rejoin_ExportedSoundMatchesBeforeSplit()
        {
            var project = TestProjects.Empty();
            var media = FfmpegMedia.Probe(FfmpegMedia.TestVideo);
            var clip = TimelineEdits.CreateClip(media, project.Rate, 0, 0);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            var original = await Pcm(project, 0, 60);
            TimelineEdits.DetachAudio(project, clip);
            var detached = await Pcm(project, 0, 60);
            ClipLinks.Rejoin(project, project.AllClips.ToList());
            var rejoined = await Pcm(project, 0, 60);
            var peak = MediaPictures.Peaks(original, original.Length / 2).Max();
            _out.WriteLine("길이 " + original.Length + " · " + detached.Length + " · " + rejoined.Length + " · 크기 " + peak);
            Assert.True(peak > 0.05, "시험 소리가 들려야 한다");
            Assert.Equal(original.Length, detached.Length);
            Assert.InRange(MediaPictures.Peaks(detached, detached.Length / 2).Max(), peak * 0.95, peak * 1.05);
            Assert.Equal(original, rejoined);
        }
    }
}
