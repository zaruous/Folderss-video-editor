using System.IO;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class SessionAndFileTests
    {
        [Fact]
        public void Execute_Undo_Redo()
        {
            var session = new EditorSession();
            var changed = 0;
            session.Changed += (s, e) => changed++;
            Assert.True(session.Execute(p => TimelineEdits.Place(p, p.MainTrack, TestProjects.VideoClip(0, 30), 0)));
            Assert.True(session.Execute(p => TimelineEdits.Place(p, p.MainTrack, TestProjects.VideoClip(0, 30), 999)));
            Assert.True(session.Dirty);
            Assert.Equal(60, session.Project.Duration);
            Assert.True(session.Undo());
            Assert.Equal(30, session.Project.Duration);
            Assert.True(session.Redo());
            Assert.Equal(60, session.Project.Duration);
            Assert.Equal(4, changed);
        }

        [Fact]
        public void Execute_SameMergeKey_IsOneUndoStep()
        {
            var session = new EditorSession();
            var clip = TestProjects.VideoClip(0, 30);
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, clip, 0));
            for (var i = 1; i <= 5; i++)
            {
                var value = i * 0.1;
                Assert.True(session.Execute(p => p.FindClip(clip.Id).Opacity = value, "opacity:" + clip.Id));
            }
            session.Execute(p => p.FindClip(clip.Id).Volume = 0.5, "volume:" + clip.Id);
            Assert.True(session.Undo());
            Assert.Equal(1, session.Project.FindClip(clip.Id).Volume);
            Assert.Equal(0.5, session.Project.FindClip(clip.Id).Opacity, 6);
            Assert.True(session.Undo());
            Assert.Equal(1, session.Project.FindClip(clip.Id).Opacity);
            Assert.True(session.Undo());
            Assert.Empty(session.Project.AllClips);
        }

        [Fact]
        public void Execute_NoChange_IsNotRecorded()
        {
            var session = new EditorSession();
            Assert.False(session.Execute(p => { }));
            Assert.False(session.CanUndo);
            Assert.False(session.Dirty);
        }

        [Fact]
        public void Selection_IsPrunedWhenClipsDisappear()
        {
            var session = new EditorSession();
            var clip = TestProjects.VideoClip(0, 30);
            session.Execute(p => TimelineEdits.Place(p, p.MainTrack, clip, 0));
            session.Select(new[] { session.Project.FindClip(clip.Id) });
            Assert.Single(session.SelectedClips);
            session.Execute(p => TimelineEdits.Delete(p, p.AllClips.ToList(), false));
            Assert.Empty(session.SelectedClips);
            session.Undo();
            Assert.Empty(session.SelectedIds);
        }

        [Fact]
        public void Playhead_IsNeverNegative()
        {
            var session = new EditorSession();
            session.Playhead = -10;
            Assert.Equal(0, session.Playhead);
        }

        [Fact]
        public void SaveAndOpen_UsesRelativePaths()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-tests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "media"));
            try
            {
                var media = Path.Combine(folder, "media", "a.mp4");
                var session = new EditorSession();
                session.Execute(p =>
                {
                    p.Media.Add(TestProjects.Video(media));
                    var clip = TimelineEdits.CreateClip(p.Media[0], p.Rate, 0, 0);
                    TimelineEdits.Place(p, p.MainTrack, clip, 0);
                    p.Markers.Add(new Marker { Frame = 12, Name = "여기" });
                    p.Rate = new FrameRate(30000, 1001);
                });
                var file = Path.Combine(folder, "p" + ProjectFile.Extension);
                session.Save(file);
                Assert.False(session.Dirty);
                var text = File.ReadAllText(file);
                Assert.Contains("media", text);
                Assert.DoesNotContain(folder.Replace("\\", "\\\\"), text);
                Assert.Contains("\"Kind\": \"Video\"", text);

                var other = new EditorSession();
                other.Open(file);
                Assert.Equal(media, other.Project.Media[0].Path);
                Assert.Equal(media, other.Project.MainTrack.Clips[0].Path);
                Assert.Equal(new FrameRate(30000, 1001), other.Project.Rate);
                Assert.Equal("여기", other.Project.Markers[0].Name);
                Assert.Equal(file, other.FilePath);
                Assert.False(other.CanUndo);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Open_RejectsGarbageAndNewerVersions()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "not json");
                Assert.Throws<InvalidDataException>(() => ProjectFile.Load(file));
                File.WriteAllText(file, "{\"Version\": 99}");
                Assert.Throws<InvalidDataException>(() => ProjectFile.Load(file));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void Load_RepairsBrokenValues()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "{\"Version\":1,\"Width\":1281,\"RateNumerator\":0,\"VideoTracks\":[],\"AudioTracks\":[{\"Clips\":[{\"Speed\":0,\"SourceIn\":5,\"SourceOut\":2}]}]}");
                var project = ProjectFile.Load(file);
                Assert.Equal(1280, project.Width);
                Assert.Equal(FrameRate.Default, project.Rate);
                Assert.Single(project.VideoTracks);
                Assert.Equal("동영상 1", project.MainTrack.Name);
                var clip = project.AudioTracks[0].Clips[0];
                Assert.Equal(1, clip.Speed);
                Assert.Equal(6, clip.SourceOut);
                Assert.Equal(TrackKind.Audio, project.AudioTracks[0].Kind);
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
