using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core;
using VideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace VideoEditor.Tests
{
    /// <summary>화면 맞춤 방식: 맞춤(비율 유지, 빈 곳)·채우기(비율 유지, 넘치는 곳 자름)·늘이기(비율 무시).</summary>
    public class FitTests
    {
        private readonly ITestOutputHelper _out;

        public FitTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static (Project Project, Clip Photo) Photo(ClipFit fit, double scale = 1)
        {
            var project = TestProjects.Empty();
            var photo = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 60);
            photo.Fit = fit;
            photo.Scale = scale;
            TimelineEdits.Place(project, project.MainTrack, photo, 0);
            return (project, photo);
        }

        private static string Filter(Project project)
        {
            return RenderGraph.Build(project, new RenderOptions { From = 0, To = 60, Width = 640, Height = 360, Audio = false }).Filter;
        }

        [Fact]
        public void Graph_UsesTheChosenFit_InsideTheScaledBox()
        {
            Assert.Contains(",scale=w=640:h=360:force_original_aspect_ratio=decrease,format=rgba", Filter(Photo(ClipFit.Fit).Project));
            // 채우기: 틀을 덮게 키운 뒤 틀 크기로 가운데를 자른다(크기 배율 50%면 틀도 반)
            Assert.Contains(",scale=w=640:h=360:force_original_aspect_ratio=increase,crop=640:360,format=rgba", Filter(Photo(ClipFit.Fill).Project));
            Assert.Contains(",scale=w=320:h=180:force_original_aspect_ratio=increase,crop=320:180,format=rgba", Filter(Photo(ClipFit.Fill, 0.5).Project));
            Assert.Contains(",scale=w=640:h=360,setsar=1,format=rgba", Filter(Photo(ClipFit.Stretch).Project));
            // 흐린 배경: 채우기로 키워 작게(1/8) 흐리고 늘인 바탕 위에 맞춤으로
            var blur = Filter(Photo(ClipFit.Blur).Project);
            Assert.Contains(",split[ba0][bb0];[ba0]scale=w=640:h=360:force_original_aspect_ratio=increase,crop=640:360,scale=80:45,boxblur=8:2,scale=640:360,", blur);
            Assert.Contains("[bb0]scale=w=640:h=360:force_original_aspect_ratio=decrease,format=rgba[bf0];[bg0][bf0]overlay=x=(main_w-overlay_w)/2:y=(main_h-overlay_h)/2:format=auto,format=rgba", blur);
        }

        [Fact]
        public void TextClips_IgnoreFit()
        {
            var project = TestProjects.Empty();
            var text = TimelineEdits.CreateText("제목", 0, 60);
            text.Fit = ClipFit.Fill;
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            Assert.DoesNotContain("force_original_aspect_ratio=increase", Filter(project));
        }

        [Fact]
        public void SetFit_ChangesVisualMediaOnly_AndRespectsLocks()
        {
            var (project, photo) = Photo(ClipFit.Fit);
            var text = TimelineEdits.CreateText("제목", 0, 60);
            var overlay = TimelineEdits.AddTrack(project, TrackKind.Video);
            TimelineEdits.Place(project, overlay, text, 0);
            Assert.Equal(1, TimelineEdits.SetFit(project, new[] { photo, text }, ClipFit.Fill));
            Assert.Equal(ClipFit.Fill, photo.Fit);
            Assert.Equal(ClipFit.Fit, text.Fit);
            project.MainTrack.Locked = true;
            Assert.Throws<EditException>(() => TimelineEdits.SetFit(project, new[] { photo }, ClipFit.Stretch));
        }

        [Fact]
        public void ProjectFile_KeepsFit_AndReadsBadValuesAsFit()
        {
            var (project, _) = Photo(ClipFit.Fill);
            var json = JsonNode.Parse(ProjectFile.Snapshot(project));
            var clip = json["VideoTracks"][0]["Clips"][0];
            Assert.Equal("Fill", (string)clip["Fit"]);
            Assert.Equal(ClipFit.Fill, ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0].Fit);
            // 모르는 이름·숫자·형식이 다른 값이어도 프로젝트는 열리고 맞춤으로, 맞는 숫자는 그 값
            foreach (var (value, expected) in new (JsonNode, ClipFit)[] { ("Banana", ClipFit.Fit), (99, ClipFit.Fit), (JsonNode.Parse("{}"), ClipFit.Fit), (2, ClipFit.Stretch), ("1", ClipFit.Fit) })
            {
                clip["Fit"] = value;
                Assert.Equal(expected, ProjectFile.FromSnapshot(json.ToJsonString()).MainTrack.Clips[0].Fit);
            }
            // 예전 프로젝트(값 없음)는 맞춤, 맞춤은 저장하지 않는다
            clip.AsObject().Remove("Fit");
            var old = ProjectFile.FromSnapshot(json.ToJsonString());
            Assert.Equal(ClipFit.Fit, old.MainTrack.Clips[0].Fit);
            Assert.DoesNotContain("\"Fit\"", ProjectFile.Snapshot(old));
        }

        [FfmpegFact]
        public void Render_FillCoversTheScreen_FitLeavesBars()
        {
            // 정사각형 빨간 사진(200×200)을 4:3 화면(320×240)에: 맞춤은 양옆이 비고, 채우기·늘이기는 꽉 찬다
            (int R, int G, int B) Corner(ClipFit fit)
            {
                var project = TestProjects.Empty();
                project.Width = 320;
                project.Height = 240;
                var photo = TimelineEdits.CreateClip(FfmpegMedia.Probe(FfmpegMedia.RedImage), project.Rate, 0, 30);
                photo.Fit = fit;
                TimelineEdits.Place(project, project.MainTrack, photo, 0);
                var bgra = PreviewRenderer.Frame(FfmpegMedia.Tool, project, 10, 320, 240, CancellationToken.None).GetAwaiter().GetResult();
                return (bgra[2], bgra[1], bgra[0]);
            }
            var fit = Corner(ClipFit.Fit);
            var fill = Corner(ClipFit.Fill);
            var stretch = Corner(ClipFit.Stretch);
            var blur = Corner(ClipFit.Blur);
            _out.WriteLine("왼쪽 위 모서리 — 맞춤 " + fit + " · 채우기 " + fill + " · 늘이기 " + stretch + " · 흐린 배경 " + blur);
            Assert.True(fit.R < 30, "맞춤은 빈 곳 " + fit);
            Assert.True(fill.R > 200 && fill.B < 60, "채우기 " + fill);
            Assert.True(stretch.R > 200 && stretch.B < 60, "늘이기 " + stretch);
            // 흐린 배경: 빈 곳이 같은 그림(빨강)을 조금 어둡게(80%) 깐 것
            Assert.True(blur.R > 150 && blur.R < 230 && blur.B < 60, "흐린 배경 " + blur);
        }
    }

    /// <summary>미디어 정보 다시 읽기: 바꿔 넣은 파일 알아보기, 다시 읽은 값 옮기기, 화면과 비율 비교.</summary>
    public class MediaRefreshTests
    {
        [Fact]
        public void AspectDiffers_BeyondThreePercent()
        {
            var landscape = new MediaInfo { Kind = MediaKind.Image, Width = 1376, Height = 768 };
            Assert.True(landscape.AspectDiffers(1080, 1920));
            Assert.False(landscape.AspectDiffers(1920, 1080));
            Assert.False(new MediaInfo { Kind = MediaKind.Image, Width = 768, Height = 1376 }.AspectDiffers(1080, 1920));
            Assert.False(new MediaInfo { Kind = MediaKind.Audio }.AspectDiffers(1080, 1920));
        }

        [Fact]
        public void CopyFrom_KeepsPath()
        {
            var old = new MediaInfo { Path = "a.png", Kind = MediaKind.Image, Width = 768, Height = 1376 };
            old.CopyFrom(new MediaInfo { Path = "b.png", Kind = MediaKind.Image, Width = 1376, Height = 768, FileSize = 10, FileTime = 20 });
            Assert.Equal(("a.png", 1376, 768, 10L, 20L), (old.Path, old.Width, old.Height, old.FileSize, old.FileTime));
        }

        [FfmpegFact]
        public void Probe_RemembersFile_AndNoticesReplacement()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-refresh-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var photo = Path.Combine(folder, "scene.png");
                File.Copy(FfmpegMedia.RedImage, photo);
                var info = FfmpegMedia.Probe(photo);
                Assert.True(info.FileSize > 0 && info.FileTime > 0);
                Assert.False(MediaProbe.IsStale(info));
                // 예전 프로젝트(크기·시각 모름)는 바뀐 것으로 보고 다시 읽는다
                Assert.True(MediaProbe.IsStale(new MediaInfo { Path = photo }));
                // 같은 이름으로 다른 그림을 넣으면 알아본다, 파일이 없으면 바뀐 것이 아니다
                File.WriteAllBytes(photo, File.ReadAllBytes(FfmpegMedia.RedImage).Concat(new byte[16]).ToArray());
                Assert.True(MediaProbe.IsStale(info));
                Assert.False(MediaProbe.IsStale(new MediaInfo { Path = Path.Combine(folder, "없음.png") }));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }

    /// <summary>미리 보기 틀의 자리: 렌더 그래프와 같은 규칙(틀 = 화면 × 크기, 맞춤 방식, 가운데 + X/Y).</summary>
    public class ClipLayoutTests
    {
        private static (Project Project, Clip Photo) Photo(ClipFit fit)
        {
            var project = TestProjects.Empty();
            var media = TestProjects.Image();
            project.Media.Add(media);
            var photo = TimelineEdits.CreateClip(media, project.Rate, 0, 60);
            photo.Fit = fit;
            TimelineEdits.Place(project, project.MainTrack, photo, 0);
            return (project, photo);
        }

        [Fact]
        public void Content_FollowsFitScaleAndOffset()
        {
            // 800×600(4:3) 사진을 640×360 화면에: 맞춤은 높이에 맞춰 480×360, 채우기·늘이기는 화면 크기
            var (project, photo) = Photo(ClipFit.Fit);
            Assert.Equal((320.0, 180.0, 480.0, 360.0, 0.0), ClipLayout.Content(project, photo));
            photo.Fit = ClipFit.Fill;
            Assert.Equal((320.0, 180.0, 640.0, 360.0, 0.0), ClipLayout.Content(project, photo));
            photo.Fit = ClipFit.Fit;
            photo.Scale = 0.5;
            photo.X = 100;
            photo.Y = -50;
            photo.Rotation = 30;
            Assert.Equal((420.0, 130.0, 240.0, 180.0, 30.0), ClipLayout.Content(project, photo));
        }

        [Fact]
        public void Snap_PullsNearCenterToZero()
        {
            Assert.Equal(0, ClipLayout.Snap(5, 8));
            Assert.Equal(-9, ClipLayout.Snap(-9, 8));
        }

        [Fact]
        public async Task ProbeAll_ReadsSeveralAtOnce_KeepsOrder_SkipsFailures()
        {
            // 예전 프로젝트를 열 때 동영상을 하나씩 다시 읽어 8개에 23.6초 걸렸다(프로세스 시작이 느린 PC, #34) — 몇 개씩 함께
            var running = 0;
            var most = 0;
            async Task<MediaInfo> Fake(string path)
            {
                var now = System.Threading.Interlocked.Increment(ref running);
                lock (this)
                    most = System.Math.Max(most, now);
                await Task.Delay(100);
                System.Threading.Interlocked.Decrement(ref running);
                if (path.EndsWith("bad", System.StringComparison.Ordinal))
                    throw new System.InvalidOperationException("못 읽음");
                return new MediaInfo { Path = path };
            }
            var paths = Enumerable.Range(0, 8).Select(i => i == 3 ? "v3.bad" : "v" + i + ".mp4").ToList();
            var read = await MediaProbe.ProbeAll(paths, Fake, 4);
            Assert.Equal(paths.Where(p => !p.EndsWith("bad")), read.Select(m => m.Path));
            // 함께 읽는지는 동시에 돈 수로 본다 — 걸린 시간은 다른 시험(FFmpeg)과 함께 돌면 늘어난다(Windows 전체 실행에서 넷씩인데도 1236ms)
            Assert.Equal(4, most);
            Assert.Empty(await MediaProbe.ProbeAll(new string[0], Fake, 4));
            // 한 번에 하나(1 이하)도 된다
            most = 0;
            Assert.Equal(2, (await MediaProbe.ProbeAll(new[] { "a.mp4", "b.mp4" }, Fake, 0)).Count);
            Assert.Equal(1, most);
        }

        [Fact]
        public void ProbeParallel_IsAFewButNotMoreThanTheCores()
        {
            Assert.InRange(MediaProbe.Parallel, 1, 4);
            Assert.True(MediaProbe.Parallel <= System.Math.Max(1, System.Environment.ProcessorCount));
        }
    }
}
