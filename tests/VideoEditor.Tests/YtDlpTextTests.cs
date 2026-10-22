using System;
using System.IO;
using System.Linq;
using VideoEditor.Core;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>
    /// yt-dlp 출력 해석과 URL 가져오기 글 처리. Samples의 검색 결과·진행률·오류 출력은 yt-dlp 2026.10.21을 실제로 돌려 저장한 것이고,
    /// ytdlp-video.json(화질 목록)만 손으로 만든 것이다(시험 환경에서 유튜브가 "로봇 확인"으로 막아 받을 수 없었음).
    /// </summary>
    public class YtDlpTextTests
    {
        private static string Sample(string name)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));
        }

        [Fact]
        public void DownloadFolder_ConfiguredThenProjectThenData()
        {
            var root = Path.Combine(Path.GetTempPath(), "fv-dl");
            var data = Path.Combine(root, "data");
            var project = Path.Combine(root, "projects", "a.fvproj");
            var custom = Path.Combine(root, "내 다운로드");
            // 지정한 폴더(절대 경로)가 먼저 — 프로젝트를 저장했어도
            Assert.Equal(custom, YtDlpText.DownloadFolder(custom, project, data));
            Assert.Equal(custom, YtDlpText.DownloadFolder("  " + custom + "  ", null, data));
            // 지정하지 않았거나 상대 경로면 프로젝트 옆 media, 저장 안 한 프로젝트면 데이터 폴더\media
            Assert.Equal(Path.Combine(root, "projects", "media"), YtDlpText.DownloadFolder(null, project, data));
            Assert.Equal(Path.Combine(root, "projects", "media"), YtDlpText.DownloadFolder("relative\\dl", project, data));
            Assert.Equal(Path.Combine(data, "media"), YtDlpText.DownloadFolder("", null, data));
        }

        [Fact]
        public void ParseSearch_RealOutput()
        {
            var results = YtDlpText.ParseSearch(Sample("ytdlp-search.json"), out _);
            Assert.Equal(3, results.Count);
            var first = results[0];
            Assert.Equal("aqz-KE-bpKQ", first.Id);
            Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ", first.Url);
            Assert.StartsWith("Big Buck Bunny 60fps 4K", first.Title);
            Assert.Equal("Blender", first.Channel);
            Assert.Equal(635, first.DurationSeconds);
            Assert.Equal(23455228, first.ViewCount);
            // --flat-playlist 검색 결과에는 올린 날이 없다(timestamp: null)
            Assert.Null(first.Uploaded);
            Assert.StartsWith("https://i.ytimg.com/vi/aqz-KE-bpKQ/", first.ThumbnailUrl);
            Assert.All(results, r => Assert.True(YtDlpText.IsThumbnailUrl(r.ThumbnailUrl), r.ThumbnailUrl));
        }

        [Fact]
        public void ParseSearch_FilteredRealOutput_IsShort()
        {
            // 짧음(4분 미만) + 크리에이티브 커먼즈(+ 지금은 유튜브가 무시하는 날짜순 정렬)로 받은 결과
            var results = YtDlpText.ParseSearch(Sample("ytdlp-search-filtered.json"), out _);
            Assert.Equal(5, results.Count);
            Assert.All(results, r => Assert.InRange(r.DurationSeconds, 1, 239));
        }

        [Fact]
        public void ParseSearch_SkipsChannelsAndForeignThumbnails()
        {
            var json = "{\"entries\":[{\"_type\":\"url\",\"ie_key\":\"YoutubeTab\",\"id\":\"UCx\",\"url\":\"https://www.youtube.com/channel/UCx\"},"
                       + "{\"ie_key\":\"Youtube\",\"id\":\"abc\",\"url\":\"javascript:alert(1)\",\"title\":\"t\",\"live_status\":\"is_live\","
                       + "\"thumbnails\":[{\"url\":\"https://evil.example/x.jpg\",\"width\":320},{\"url\":\"http://i.ytimg.com/x.jpg\",\"width\":320}],"
                       + "\"upload_date\":\"20240131\"},null]}";
            var result = Assert.Single(YtDlpText.ParseSearch(json, out var entries));
            Assert.Equal(3, entries);
            Assert.Equal("https://www.youtube.com/watch?v=abc", result.Url);
            Assert.Null(result.ThumbnailUrl);
            Assert.True(result.IsLive);
            Assert.Equal(-1, result.ViewCount);
            Assert.Equal(new DateTime(2024, 1, 31), result.Uploaded);
        }

        [Fact]
        public void ParseSearch_RealOutputWithPlaylists_CountsAllEntries()
        {
            // 20개를 요청한 실제 결과에 재생목록 3개가 섞여 있다 — 영상만 남기되, [더 보기]는 20개 전부로 판단
            var results = YtDlpText.ParseSearch(Sample("ytdlp-search-playlists.json"), out var entries);
            Assert.Equal(20, entries);
            Assert.Equal(17, results.Count);
            Assert.All(results, r => Assert.StartsWith("https://www.youtube.com/watch?v=", r.Url));
        }

        [Theory]
        // 이 값들로 실제 검색해 결과를 확인했다(검색 페이지의 올린 시각·길이 글: 오늘 → 24시간 안, 이번 주+짧음 → 7일 안·4분 미만 등)
        [InlineData(YtUploaded.Any, YtLength.Any, false, "")]
        [InlineData(YtUploaded.Any, YtLength.Any, true, "&sp=EgIwAQ%3D%3D")]
        [InlineData(YtUploaded.Today, YtLength.Any, false, "&sp=EgIIAg%3D%3D")]
        [InlineData(YtUploaded.Week, YtLength.Short, false, "&sp=EgQIAxgB")]
        [InlineData(YtUploaded.Month, YtLength.Any, false, "&sp=EgIIBA%3D%3D")]
        [InlineData(YtUploaded.Year, YtLength.Short, true, "&sp=EgYIBRgBMAE%3D")]
        [InlineData(YtUploaded.Any, YtLength.Long, false, "&sp=EgIYAg%3D%3D")]
        [InlineData(YtUploaded.Any, YtLength.Medium, false, "&sp=EgIYAw%3D%3D")]
        public void SearchUrl_EncodesFilters(YtUploaded uploaded, YtLength length, bool cc, string sp)
        {
            var url = YtDlpText.SearchUrl(" 고양이 & dog ", new YtSearchOptions { Uploaded = uploaded, Length = length, CreativeCommons = cc });
            Assert.Equal("https://www.youtube.com/results?search_query=%EA%B3%A0%EC%96%91%EC%9D%B4%20%26%20dog" + sp, url);
        }

        [Fact]
        public void ParseVideo_HeightsWithoutStoryboard()
        {
            var info = YtDlpText.ParseVideo(Sample("ytdlp-video.json"));
            Assert.StartsWith("Big Buck Bunny", info.Title);
            Assert.Equal(635, info.DurationSeconds);
            Assert.Equal(new[] { 2160, 1440, 1080, 720, 360 }, info.Heights);
            Assert.True(info.HasAudio);
            Assert.False(info.IsPlaylist);
            Assert.True(YtDlpText.ParseVideo("{\"_type\":\"playlist\",\"title\":\"p\",\"entries\":[]}").IsPlaylist);
        }

        [Fact]
        public void ChooseHeight_AtOrBelowProject()
        {
            var heights = new[] { 2160, 1440, 1080, 720, 360 };
            Assert.Equal(1080, YtDlpText.ChooseHeight(heights, 1080));
            Assert.Equal(1440, YtDlpText.ChooseHeight(heights, 1920));
            Assert.Equal(360, YtDlpText.ChooseHeight(heights, 240));
            Assert.Equal(0, YtDlpText.ChooseHeight(new int[0], 1080));
            Assert.Equal("bv*[height<=1080]+ba/b[height<=1080]/bv*+ba/b", YtDlpText.FormatSelector(1080));
            Assert.Equal("bv*+ba/b", YtDlpText.FormatSelector(0));
        }

        [Fact]
        public void ParseProgress_RealLines()
        {
            var lines = Sample("ytdlp-progress.txt").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            Assert.Equal(0.1024, YtDlpText.ParseProgress(lines[1]).Value, 4);
            Assert.Equal(100, YtDlpText.ParseProgress(lines[4]).Value);
            Assert.Null(YtDlpText.ParseProgress(lines[5]));
            Assert.StartsWith(YtDlpText.FilePrefix, lines[5]);
            Assert.Equal(50, YtDlpText.ParseProgress("FVPROG 50 NA 100 NA downloading"));
            Assert.Null(YtDlpText.ParseProgress("FVPROG NA NA NA NA downloading"));
        }

        [Fact]
        public void Explain_RealErrors()
        {
            var bot = YtDlpText.Explain(Sample("ytdlp-bot-check.txt"));
            Assert.Contains("로봇이 아닌지 확인", bot.Message);
            Assert.True(bot.Outdated);
            var login = YtDlpText.Explain(Sample("ytdlp-login.txt"));
            Assert.Contains("로그인", login.Message);
            Assert.False(login.Outdated);
        }

        [Theory]
        [InlineData("ERROR: Unsupported URL: https://example.com/", "지원하지 않는", false)]
        [InlineData("ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.", "연령 제한", false)]
        [InlineData("ERROR: [youtube] x: Requested format is not available. Use --list-formats", "업데이트", true)]
        [InlineData("ERROR: [youtube] x: Video unavailable", "볼 수 없는", false)]
        [InlineData("ERROR: [youtube] x: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", "네트워크", false)]
        [InlineData("ERROR: something new", "something new", false)]
        public void Explain_KnownErrors(string error, string expected, bool outdated)
        {
            var result = YtDlpText.Explain("WARNING: ignore me\n" + error + "\n");
            Assert.Contains(expected, result.Message);
            Assert.Equal(outdated, result.Outdated);
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", UrlKind.YouTube)]
        [InlineData("https://youtu.be/aqz-KE-bpKQ", UrlKind.YouTube)]
        [InlineData("https://m.youtube.com/shorts/abc", UrlKind.YouTube)]
        [InlineData("https://example.com/videos/clip.MP4?token=1", UrlKind.Direct)]
        [InlineData("https://intranet.local/a/song.mp3", UrlKind.Direct)]
        [InlineData("http://example.com/clip.mp4", UrlKind.Invalid)]
        [InlineData("https://vimeo.com/76979871", UrlKind.Other)]
        [InlineData("https://youtube.com.evil.example/watch?v=x", UrlKind.Other)]
        [InlineData("file:///C:/x.mp4", UrlKind.Invalid)]
        [InlineData("ftp://example.com/x.mp4", UrlKind.Invalid)]
        [InlineData("not a url", UrlKind.Invalid)]
        public void Classify(string text, UrlKind kind)
        {
            Assert.Equal(kind, YtDlpText.Classify(text, out _));
        }

        [Theory]
        [InlineData("고양이: \"집사\" <브이로그> / 1편?", "고양이 집사 브이로그 1편")]
        [InlineData("CON", "_CON")]
        [InlineData("nul.txt", "_nul.txt")]
        [InlineData("끝에 점...", "끝에 점")]
        [InlineData("a\tb\nc", "a b c")]
        [InlineData("???", "동영상")]
        public void SafeFileName_Windows(string title, string expected)
        {
            Assert.Equal(expected, YtDlpText.SafeFileName(title));
        }

        [Fact]
        public void SafeFileName_LimitsLength()
        {
            Assert.Equal(120, YtDlpText.SafeFileName(new string('가', 300)).Length);
        }

        [Fact]
        public void UniquePath_AddsNumber()
        {
            var folder = Path.Combine(Path.GetTempPath(), "fv-unique-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                Assert.Equal(Path.Combine(folder, "a.mp4"), YtDlpText.UniquePath(folder, "a", ".mp4"));
                File.WriteAllText(Path.Combine(folder, "a.mp4"), "");
                File.WriteAllText(Path.Combine(folder, "a (2).mp4"), "");
                Assert.Equal(Path.Combine(folder, "a (3).mp4"), YtDlpText.UniquePath(folder, "a", ".mp4"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Recent_KeepsTenNewestFirst()
        {
            string stored = null;
            for (var i = 0; i < 12; i++)
                stored = YtDlpText.AddRecent(stored, "q" + i);
            stored = YtDlpText.AddRecent(stored, " Q5 ");
            var list = YtDlpText.ReadRecent(stored);
            Assert.Equal(10, list.Count);
            Assert.Equal("Q5", list[0]);
            Assert.Equal("q11", list[1]);
            Assert.DoesNotContain("q5", list);
            Assert.DoesNotContain("q1", list);
            Assert.Empty(YtDlpText.ReadRecent("not json"));
        }

        [Fact]
        public void FindChecksum_YtDlpSumsAndPowerShellFormat()
        {
            var sums = "b7af77f686e95f7845ce8d09d21957718febb8b48b30555adb9dd2292ad7bf14  yt-dlp\n"
                       + "33121d192d706e62f684a06f80918e3f3a8ba52ac3a65213b6951c39c7d9f555  yt-dlp.exe\n";
            Assert.Equal("33121d192d706e62f684a06f80918e3f3a8ba52ac3a65213b6951c39c7d9f555", YtDlpText.FindChecksum(sums, "yt-dlp.exe"));
            // Deno 릴리스의 .sha256sum은 PowerShell Get-FileHash 출력(실제 파일)
            Assert.Equal("cba84b26e0df5ae7f74fc66b86a95b866872a86292ebd84fa1e959927edd93bf",
                YtDlpText.FindChecksum(Sample("deno-sha256sum.txt"), "deno-x86_64-pc-windows-msvc.zip"));
            Assert.Null(YtDlpText.FindChecksum(Sample("deno-sha256sum.txt"), "deno-aarch64-apple-darwin.zip"));
        }

        [Theory]
        [InlineData("https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.21/yt-dlp.exe", true)]
        [InlineData("https://evil.example/yt-dlp.exe", false)]
        [InlineData("http://github.com/yt-dlp.exe", false)]
        public void ChooseAsset_OnlyFromGitHubOverHttps(string url, bool accepted)
        {
            var json = "{\"assets\":[{\"name\":\"SHA2-256SUMS\",\"browser_download_url\":\"https://github.com/s\",\"size\":1},"
                       + "{\"name\":\"yt-dlp.exe\",\"browser_download_url\":\"" + url + "\",\"size\":5}]}";
            Assert.Equal(accepted, YtDlpText.ChooseAsset(json, "yt-dlp.exe", "SHA2-256SUMS") != null);
        }
    }
}
