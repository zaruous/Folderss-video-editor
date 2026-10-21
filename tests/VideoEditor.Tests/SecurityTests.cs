using System.IO;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>보안 검토에서 고친 것: 조작된 프로젝트 파일, 임시 파일 이름, 내려받기 주소.</summary>
    public class SecurityTests
    {
        [Fact]
        public void Load_ReplacesMaliciousOrDuplicateIds()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "{\"Version\":1,\"VideoTracks\":[{\"Id\":\"..\\\\..\\\\x\",\"Clips\":["
                                        + "{\"Id\":\"..\\\\..\\\\..\\\\Documents\\\\notes\",\"Kind\":\"Text\",\"Text\":\"hi\",\"HasVideo\":true,\"SourceOut\":30},"
                                        + "{\"Id\":\"abc\",\"Kind\":\"Text\",\"Text\":\"a\",\"HasVideo\":true,\"Start\":30,\"SourceOut\":30},"
                                        + "{\"Id\":\"abc\",\"Kind\":\"Text\",\"Text\":\"b\",\"HasVideo\":true,\"Start\":60,\"SourceOut\":30}]}],"
                                        + "\"Width\":100000,\"Height\":100000,\"SampleRate\":5,\"RateNumerator\":100000,\"RateDenominator\":1}");
                var project = ProjectFile.Load(file);
                var ids = project.AllClips.Select(c => c.Id).Concat(project.AllTracks.Select(t => t.Id)).ToList();
                Assert.All(ids, id => Assert.True(id.Length <= 32 && id.All(System.Uri.IsHexDigit), id));
                Assert.Equal(ids.Count, ids.Distinct().Count());
                Assert.Contains(project.AllClips, c => c.Id == "abc");
                Assert.Equal(8192, project.Width);
                Assert.Equal(48000, project.SampleRate);
                Assert.Equal(FrameRate.Default, project.Rate);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void TextTempFile_DoesNotUseClipId()
        {
            var project = TestProjects.Empty();
            var text = TimelineEdits.CreateText("x", 0, 30);
            text.Id = "..\\..\\evil";
            TimelineEdits.Place(project, project.MainTrack, text, 0);
            var plan = RenderGraph.Build(project, new RenderOptions { From = 0, To = 30, Width = 64, Height = 36, Audio = false, TempFolder = Path.GetTempPath() });
            var file = Assert.Single(plan.TextFiles).Key;
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(Path.GetDirectoryName(file) + Path.DirectorySeparatorChar));
            Assert.DoesNotContain("evil", file);
        }

        [Theory]
        [InlineData("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/x.zip", true)]
        [InlineData("https://objects.githubusercontent.com/x.zip", true)]
        [InlineData("https://evil.example.com/x.zip", false)]
        [InlineData("https://github.com.evil.example/x.zip", false)]
        [InlineData("http://github.com/x.zip", false)]
        public void ChooseBuild_OnlyFromGitHubOverHttps(string url, bool accepted)
        {
            var json = "{\"assets\":[{\"name\":\"checksums.sha256\",\"browser_download_url\":\"https://github.com/c\",\"size\":1},"
                       + "{\"name\":\"ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip\",\"browser_download_url\":\"" + url + "\",\"size\":5}]}";
            Assert.Equal(accepted, FfmpegText.ChooseBuild(json) != null);
        }
    }
}
