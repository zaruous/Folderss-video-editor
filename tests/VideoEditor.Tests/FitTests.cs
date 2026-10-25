using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
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
            _out.WriteLine("왼쪽 위 모서리 — 맞춤 " + fit + " · 채우기 " + fill + " · 늘이기 " + stretch);
            Assert.True(fit.R < 30, "맞춤은 빈 곳 " + fit);
            Assert.True(fill.R > 200 && fill.B < 60, "채우기 " + fill);
            Assert.True(stretch.R > 200 && stretch.B < 60, "늘이기 " + stretch);
        }
    }
}
