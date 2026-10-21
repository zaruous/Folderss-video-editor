using VideoEditor.Core;

namespace VideoEditor.Tests
{
    /// <summary>시험용 프로젝트·미디어를 짧게 만든다(30fps, 640×360).</summary>
    internal static class TestProjects
    {
        public static Project Empty(bool magnetic = true)
        {
            var project = Project.CreateDefault();
            project.Width = 640;
            project.Height = 360;
            project.Magnetic = magnetic;
            return project;
        }

        public static MediaInfo Video(string path = "a.mp4", double seconds = 10, bool audio = true)
        {
            return new MediaInfo { Path = path, Kind = MediaKind.Video, DurationSeconds = seconds, HasVideo = true, HasAudio = audio, Width = 640, Height = 360, FrameRate = "30/1" };
        }

        public static MediaInfo Audio(string path = "music.mp3", double seconds = 20)
        {
            return new MediaInfo { Path = path, Kind = MediaKind.Audio, DurationSeconds = seconds, HasAudio = true };
        }

        public static MediaInfo Image(string path = "photo.png")
        {
            return new MediaInfo { Path = path, Kind = MediaKind.Image, HasVideo = true, Width = 800, Height = 600 };
        }

        /// <summary>frames 길이(원본 frames)의 동영상 클립.</summary>
        public static Clip VideoClip(long start, long frames, string path = "a.mp4")
        {
            var clip = TimelineEdits.CreateClip(Video(path, 100), FrameRate.Default, start, 0);
            clip.SourceOut = frames;
            return clip;
        }

        public static Clip Add(Project project, Track track, long start, long frames, string path = "a.mp4")
        {
            var clip = VideoClip(start, frames, path);
            clip.Start = start;
            track.Clips.Add(clip);
            track.Sort();
            return clip;
        }
    }
}
