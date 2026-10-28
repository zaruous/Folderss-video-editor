using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>미리 보기에서 여러 클립 고르기·함께 옮기기(#34): 틀을 그릴 클립만, 쌓는 순서로, 모두 같은 거리만큼 실행 취소 한 단계.</summary>
    public class PreviewSelectionTests
    {
        /// <summary>동영상 1에 사진, 동영상 2에 글자, 동영상 3에 별(모두 0~3초).</summary>
        private static (Clip Photo, Clip Title, Clip Art) Layered(Project project)
        {
            var photo = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 90);
            TimelineEdits.Place(project, project.MainTrack, photo, 0);
            var title = TimelineEdits.CreateText("제목", 0, 90);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), title, 0);
            var art = TimelineEdits.CreateArt(ArtKind.Star, 0, 90);
            TimelineEdits.Place(project, TimelineEdits.AddTrack(project, TrackKind.Video), art, 0);
            return (photo, title, art);
        }

        [Fact]
        public void Movable_FramableClipsOnly_InStackingOrder()
        {
            var project = TestProjects.Empty(magnetic: false);
            var (photo, title, art) = Layered(project);
            // 고른 차례와 상관없이 렌더 그래프의 쌓는 순서(아래 → 위)
            Assert.Equal(new[] { photo, title, art }, PreviewSelection.Movable(project, new[] { art, photo, title }, 10));
            // 재생 헤드에 없는 클립·숨김·잠금·자막 트랙·소리 클립은 빠진다
            title.Start = 100;
            Assert.Equal(new[] { photo, art }, PreviewSelection.Movable(project, new[] { art, photo, title }, 10));
            title.Start = 0;
            project.TrackOf(art).Hidden = true;
            Assert.Equal(new[] { photo, title }, PreviewSelection.Movable(project, new[] { art, photo, title }, 10));
            project.TrackOf(art).Hidden = false;
            project.TrackOf(title).Locked = true;
            Assert.Equal(new[] { photo, art }, PreviewSelection.Movable(project, new[] { art, photo, title }, 10));
            project.TrackOf(title).Locked = false;
            project.TrackOf(title).Subtitles = true;
            Assert.False(PreviewSelection.IsFramable(project, title, 10));
            var sound = TimelineEdits.CreateClip(TestProjects.Audio(), project.Rate, 0, 0);
            TimelineEdits.Place(project, project.AudioTracks[0], sound, 0);
            Assert.False(PreviewSelection.IsFramable(project, sound, 10));
            Assert.Empty(PreviewSelection.Movable(project, null, 10));
            Assert.False(PreviewSelection.IsFramable(project, null, 10));
        }

        [Fact]
        public void Move_AllBySameDistance_OneUndoStep()
        {
            var session = new EditorSession();
            var (photo, title, _) = Layered(session.Project);
            photo.X = 10;
            title.X = -40.4;
            title.Y = 7;
            Assert.True(session.Execute(p => PreviewSelection.Move(p, PreviewSelection.Movable(p, p.AllClips, 10), 25, -12)));
            var moved = PreviewSelection.Movable(session.Project, session.Project.AllClips, 10);
            Assert.Equal(new[] { 35.0, -15, 25 }, moved.Select(c => c.X));
            Assert.Equal(new[] { -12.0, -5, -12 }, moved.Select(c => c.Y));
            // 실행 취소 한 번에 모두 돌아간다
            Assert.True(session.Undo());
            Assert.Equal(new[] { 10.0, -40.4, 0 }, PreviewSelection.Movable(session.Project, session.Project.AllClips, 10).Select(c => c.X));
        }

        [Fact]
        public void Move_LockedTrack_ChangesNothing_BadDistanceIsZero()
        {
            var session = new EditorSession();
            var (_, title, _) = Layered(session.Project);
            session.Project.TrackOf(title).Locked = true;
            // 잠긴 트랙이 하나라도 있으면 편집 전체가 되돌아간다
            Assert.False(session.Execute(p => PreviewSelection.Move(p, p.AllClips.Where(c => c.IsVisual).ToList(), 5, 5)));
            Assert.NotNull(session.LastError);
            Assert.All(session.Project.AllClips.Where(c => c.IsVisual), c => Assert.Equal(0, c.X));
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateText("가", 0, 30);
            clip.X = 3;
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            PreviewSelection.Move(project, new[] { clip }, double.NaN, double.PositiveInfinity);
            Assert.Equal((3.0, 0.0), (clip.X, clip.Y));
        }
    }
}
