using System.Collections.Generic;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>타임라인 클립의 동영상 효과 배지·화면 낭독 이름(#34).</summary>
    public class VideoEffectLabelsTests
    {
        private static (Project Project, Clip Clip) Photo()
        {
            var project = TestProjects.Empty();
            var clip = TimelineEdits.CreateClip(TestProjects.Image(), project.Rate, 0, 90);
            TimelineEdits.Place(project, project.MainTrack, clip, 0);
            return (project, clip);
        }

        [Fact]
        public void NoEffects_NoBadgeNorName()
        {
            var (_, clip) = Photo();
            Assert.Null(VideoEffectLabels.Badge(clip));
            Assert.Null(VideoEffectLabels.Spoken(clip));
            clip.Effects = new List<ClipEffect>();
            Assert.Null(VideoEffectLabels.Badge(clip));
            Assert.Null(VideoEffectLabels.Spoken(null));
        }

        [Fact]
        public void Effects_BadgeCounts_NameListsInDrawingOrder_UnknownIncluded()
        {
            var (project, clip) = Photo();
            VideoEffects.Add(project, clip, VideoEffects.Find("scan-line"));
            Assert.Equal("✦", VideoEffectLabels.Badge(clip));
            Assert.Equal("효과 " + VideoEffects.Find("scan-line").Name, VideoEffectLabels.Spoken(clip));
            // 모르는 효과(새 판에서 만든 것)도 센다 — 속성 패널에 "알 수 없는 효과"로 보이는 것과 같게
            clip.Effects.Add(new ClipEffect { Id = "future-effect" });
            VideoEffects.Add(project, clip, VideoEffects.Find("rgb-split"));
            Assert.Equal("✦3", VideoEffectLabels.Badge(clip));
            Assert.Equal("효과 " + VideoEffects.Find("scan-line").Name + ", 알 수 없는 효과, " + VideoEffects.Find("rgb-split").Name, VideoEffectLabels.Spoken(clip));
            // 순서를 바꾸면 이름 차례도(그리는 차례)
            Assert.True(VideoEffects.Move(project, clip, 2, 0));
            Assert.StartsWith("효과 " + VideoEffects.Find("rgb-split").Name + ", ", VideoEffectLabels.Spoken(clip));
        }

        [Fact]
        public void ClipsThatCannotHaveEffects_ShowNothing()
        {
            // 글자 클립에 (남이 만든 파일 등으로) 효과 목록이 있어도 그리지 않으므로 알리지 않는다
            var text = TimelineEdits.CreateText("글자", 0, 30);
            text.Effects = new List<ClipEffect> { new ClipEffect { Id = "scan-line" } };
            Assert.Null(VideoEffectLabels.Badge(text));
            Assert.Null(VideoEffectLabels.Spoken(text));
        }
    }
}
