using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>Shift+누르기 범위 고르기와 속성 패널의 같은 종류 함께 고치기(EditorSession.SelectRange·PropertyGroup).</summary>
    public class SelectionRangeTests
    {
        private static EditorSession Session(out Clip a, out Clip b, out Clip c, out Clip d)
        {
            var session = new EditorSession();
            var track = session.Project.MainTrack;
            a = TestProjects.Add(session.Project, track, 0, 30, "a.mp4");
            b = TestProjects.Add(session.Project, track, 30, 30, "b.mp4");
            c = TestProjects.Add(session.Project, track, 60, 30, "c.mp4");
            d = TestProjects.Add(session.Project, track, 90, 30, "d.mp4");
            return session;
        }

        [Fact]
        public void SelectRange_SameTrack_SelectsClipsBetween_AnyDirection()
        {
            var session = Session(out var a, out var b, out var c, out var d);
            session.Select(new[] { b });
            session.SelectRange(d);
            Assert.Equal(new[] { b.Id, c.Id, d.Id }.OrderBy(x => x), session.SelectedIds.OrderBy(x => x));
            // 기준(b)은 그대로 — 다시 Shift+누르면 b부터 앞으로
            session.SelectRange(a);
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), session.SelectedIds.OrderBy(x => x));
        }

        [Fact]
        public void SelectRange_WithoutAnchor_SelectsTarget_AndAddKeepsSelection()
        {
            var session = Session(out var a, out var b, out var c, out var d);
            session.SelectRange(c);
            Assert.Equal(new[] { c.Id }, session.SelectedIds);
            session.Select(new[] { a });
            session.SelectRange(b, true);
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), session.SelectedIds.OrderBy(x => x));
            // Ctrl+누르기로 더한 것이 새 기준
            session.ToggleSelect(d);
            session.SelectRange(c, true);
            Assert.Equal(4, session.SelectedIds.Count);
        }

        [Fact]
        public void SelectRange_AcrossTracks_TakesTracksBetween()
        {
            var session = new EditorSession();
            var project = session.Project;
            TimelineEdits.AddSubtitleTrack(project);
            var subtitles = project.AllTracks.First(t => t.Subtitles);
            var cues = Enumerable.Range(0, 4).Select(i => TimelineEdits.CreateText("자막 " + i, i * 30, 30)).ToList();
            subtitles.Clips.AddRange(cues);
            var main = TestProjects.Add(project, project.MainTrack, 30, 60);
            session.Select(new[] { cues[1] });
            session.SelectRange(cues[3]);
            Assert.Equal(new[] { cues[1].Id, cues[2].Id, cues[3].Id }.OrderBy(x => x), session.SelectedIds.OrderBy(x => x));
            // 자막 큐 셋은 같은 종류 — 속성 패널은 마지막으로 누른 것을 보이고 함께 고친다
            Assert.Equal(cues[3].Id, session.PropertyClip.Id);
            Assert.Equal(3, session.PropertyGroup.Count);
            // 다른 트랙(주 트랙)까지 넓히면 사이 트랙 모두에서 시작이 범위 안인 클립
            session.SelectRange(main);
            Assert.Contains(main.Id, session.SelectedIds);
            Assert.Contains(cues[1].Id, session.SelectedIds);
            Assert.DoesNotContain(cues[0].Id, session.SelectedIds);
            // 자막과 동영상이 섞이면 함께 고칠 수 없다
            Assert.Empty(session.PropertyGroup);
            Assert.Null(session.PropertyClip);
        }

        [Fact]
        public void PropertyGroup_SameKindVideos_PrimaryFirst()
        {
            var session = Session(out var a, out var b, out var c, out _);
            session.Select(new[] { a, b, c });
            Assert.Null(session.PrimaryClip);
            Assert.Equal(new[] { a.Id, b.Id, c.Id }, session.PropertyGroup.Select(x => x.Id));
            session.SetPrimary(c);
            Assert.Equal(c.Id, session.PropertyGroup[0].Id);
            Assert.Equal(3, session.PropertyGroup.Count);
        }

        [Fact]
        public void PropertyGroup_MixedKinds_IsEmpty()
        {
            var session = new EditorSession();
            var video = TestProjects.Add(session.Project, session.Project.MainTrack, 0, 30);
            var text = TimelineEdits.CreateText("제목", 0, 30);
            var track = TimelineEdits.AddTrack(session.Project, TrackKind.Video);
            track.Clips.Add(text);
            session.Select(new[] { video, text });
            Assert.Empty(session.PropertyGroup);
            // 같은 종류(글자 둘)는 함께
            var text2 = TimelineEdits.CreateText("부제", 30, 30);
            track.Clips.Add(text2);
            session.Select(new[] { text, text2 });
            Assert.Equal(2, session.PropertyGroup.Count);
        }
    }
}
