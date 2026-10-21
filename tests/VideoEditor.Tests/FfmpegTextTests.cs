using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    public class FfmpegTextTests
    {
        [Fact]
        public void ParseProbe_Video()
        {
            const string json = @"{""streams"":[
                {""codec_type"":""video"",""codec_name"":""h264"",""width"":1920,""height"":1080,""avg_frame_rate"":""30000/1001"",""r_frame_rate"":""30000/1001""},
                {""codec_type"":""audio"",""codec_name"":""aac"",""channels"":1,""duration"":""12.5""}],
              ""format"":{""format_name"":""mov,mp4,m4a,3gp,3g2,mj2"",""duration"":""12.512""}}";
            var info = FfmpegText.ParseProbe("v.mp4", json);
            Assert.Equal(MediaKind.Video, info.Kind);
            Assert.Equal(12.512, info.DurationSeconds, 3);
            Assert.Equal("30000/1001", info.FrameRate);
            Assert.True(info.HasAudio);
            Assert.Equal(1, info.AudioChannels);
            Assert.Equal(1920, info.Width);
        }

        [Fact]
        public void ParseProbe_PhoneVideo_RotatedSwapsSize()
        {
            const string json = @"{""streams"":[{""codec_type"":""video"",""codec_name"":""hevc"",""width"":1920,""height"":1080,""avg_frame_rate"":""30/1"",
                ""side_data_list"":[{""side_data_type"":""Display Matrix"",""rotation"":-90}]}],""format"":{""format_name"":""mov,mp4"",""duration"":""3""}}";
            var info = FfmpegText.ParseProbe("p.mov", json);
            Assert.Equal(1080, info.Width);
            Assert.Equal(1920, info.Height);
        }

        [Fact]
        public void ParseProbe_Mp3WithCover_IsAudio()
        {
            const string json = @"{""streams"":[
                {""codec_type"":""audio"",""codec_name"":""mp3""},
                {""codec_type"":""video"",""codec_name"":""mjpeg"",""width"":500,""height"":500,""disposition"":{""attached_pic"":1}}],
              ""format"":{""format_name"":""mp3"",""duration"":""200.1""}}";
            var info = FfmpegText.ParseProbe("song.mp3", json);
            Assert.Equal(MediaKind.Audio, info.Kind);
            Assert.False(info.HasVideo);
            Assert.Equal(200.1, info.DurationSeconds, 3);
        }

        [Theory]
        [InlineData("png_pipe", "png")]
        [InlineData("image2", "mjpeg")]
        [InlineData("webp_pipe", "webp")]
        public void ParseProbe_Image(string format, string codec)
        {
            var json = @"{""streams"":[{""codec_type"":""video"",""codec_name"":""" + codec + @""",""width"":800,""height"":600,""r_frame_rate"":""25/1""}],""format"":{""format_name"":""" + format + @"""}}";
            var info = FfmpegText.ParseProbe("x", json);
            Assert.Equal(MediaKind.Image, info.Kind);
            Assert.Equal(0, info.DurationSeconds);
            Assert.Null(info.FrameRate);
            Assert.Equal(800, info.Width);
        }

        [Fact]
        public void ParseProbe_NothingUseful_ReturnsNull()
        {
            Assert.Null(FfmpegText.ParseProbe("x", @"{""streams"":[{""codec_type"":""data""}],""format"":{""format_name"":""bin""}}"));
        }

        [Theory]
        [InlineData("ffmpeg version 7.1-full_build-www.gyan.dev Copyright", 7)]
        [InlineData("ffmpeg version n9.0.2-31-gc89d6ee1ca-20261021 Copyright", 9)]
        [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright", 6)]
        [InlineData("ffmpeg version N-118000-g1234abcd Copyright", 99)]
        [InlineData("garbage", 0)]
        public void MajorVersion(string line, int expected)
        {
            Assert.Equal(expected, FfmpegText.MajorVersion(line));
        }

        [Fact]
        public void Encoders()
        {
            const string text = "Encoders:\n V..... = Video\n ------\n V....D libx264              libx264 H.264\n V....D h264_mf              H264 via MediaFoundation\n A....D aac                  AAC\n";
            var set = FfmpegText.Encoders(text);
            Assert.Contains("libx264", set);
            Assert.Contains("h264_mf", set);
            Assert.Contains("aac", set);
            Assert.DoesNotContain("=", set);
        }

        [Fact]
        public void ProgressSeconds()
        {
            Assert.Equal(1.5, FfmpegText.ProgressSeconds("out_time_us=1500000"));
            Assert.Equal(2.0, FfmpegText.ProgressSeconds("out_time_ms=2000000"));
            Assert.Null(FfmpegText.ProgressSeconds("frame=12"));
            Assert.Null(FfmpegText.ProgressSeconds("out_time_us=N/A"));
        }

        [Fact]
        public void ChooseBuild_PrefersHighestReleaseSharedGpl()
        {
            const string json = @"{""assets"":[
                {""name"":""checksums.sha256"",""browser_download_url"":""https://github.com/x/checksums.sha256"",""size"":1},
                {""name"":""ffmpeg-master-latest-win64-gpl-shared.zip"",""browser_download_url"":""https://github.com/x/m.zip"",""size"":9},
                {""name"":""ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip"",""browser_download_url"":""https://github.com/x/81.zip"",""size"":8},
                {""name"":""ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip"",""browser_download_url"":""https://github.com/x/90.zip"",""size"":90},
                {""name"":""ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip"",""browser_download_url"":""https://github.com/x/l90.zip"",""size"":7},
                {""name"":""ffmpeg-n10.0-latest-linux64-gpl-shared-10.0.tar.xz"",""browser_download_url"":""https://github.com/x/linux.tar.xz"",""size"":7}]}";
            var build = FfmpegText.ChooseBuild(json);
            Assert.Equal("ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip", build.Name);
            Assert.Equal(90, build.Size);
            Assert.Equal("https://github.com/x/checksums.sha256", build.ChecksumsUrl);
        }

        [Fact]
        public void ChooseBuild_FallsBackToMaster_AndRejectsHttp()
        {
            const string master = @"{""assets"":[
                {""name"":""checksums.sha256"",""browser_download_url"":""https://github.com/g/c"",""size"":1},
                {""name"":""ffmpeg-master-latest-win64-gpl-shared.zip"",""browser_download_url"":""https://github.com/g/m.zip"",""size"":9}]}";
            Assert.Equal("ffmpeg-master-latest-win64-gpl-shared.zip", FfmpegText.ChooseBuild(master).Name);
            Assert.Null(FfmpegText.ChooseBuild(master.Replace("https://github.com/g/m.zip", "http://github.com/g/m.zip")));
            Assert.Null(FfmpegText.ChooseBuild(@"{""assets"":[]}"));
        }

        [Fact]
        public void FindChecksum()
        {
            var text = "2516ae4aa3a7f4c8143d8e3d7254ec8ebc3657a476d89840039face9c8d0bf44  ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip\n"
                       + new string('A', 64) + " *other.zip\n";
            Assert.Equal("2516ae4aa3a7f4c8143d8e3d7254ec8ebc3657a476d89840039face9c8d0bf44", FfmpegText.FindChecksum(text, "ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip"));
            Assert.Equal(new string('a', 64), FfmpegText.FindChecksum(text, "other.zip"));
            Assert.Null(FfmpegText.FindChecksum(text, "missing.zip"));
        }
    }
}
