using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoEditor.Core
{
    /// <summary>
    /// yt-dlp가 내놓는 글(검색·영상 JSON, 진행률 줄, 오류 출력)과 URL 가져오기에 쓰는 글 처리. 프로세스는 돌리지 않는다.
    /// </summary>
    public static class YtDlpText
    {
        private static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mts", ".m2ts", ".ts", ".flv", ".3gp",
            ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"
        };

        // ---------- URL 판별 ----------

        /// <summary>
        /// 입력한 주소의 종류. https 미디어 파일 주소는 직접 링크, 유튜브 주소는 YouTube, 그 밖의 http(s)는 yt-dlp에 맡긴다(Other).
        /// http(암호화 없음) 미디어 파일 주소와 http(s)가 아닌 주소는 Invalid.
        /// </summary>
        public static UrlKind Classify(string text, out Uri uri)
        {
            uri = null;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
                || string.IsNullOrEmpty(parsed.Host))
                return UrlKind.Invalid;
            uri = parsed;
            if (IsYouTubeHost(parsed.Host))
                return UrlKind.YouTube;
            if (MediaExtensions.Contains(Path.GetExtension(parsed.AbsolutePath)))
                return parsed.Scheme == Uri.UriSchemeHttps ? UrlKind.Direct : UrlKind.Invalid;
            return UrlKind.Other;
        }

        public static bool IsMediaExtension(string extension)
        {
            return !string.IsNullOrEmpty(extension) && MediaExtensions.Contains(extension);
        }

        public static bool IsYouTubeHost(string host)
        {
            host = (host ?? "").ToLowerInvariant();
            return host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                   || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
        }

        // ---------- 검색 ----------

        /// <summary>
        /// 유튜브 검색 결과 페이지 주소. 필터는 결과 페이지의 sp 값(protobuf를 base64로)으로 넘긴다 — 필터 필드 2 안에
        /// 올린 날짜 필드 1(오늘 2 · 이번 주 3 · 이번 달 4 · 올해 5), 길이 필드 3(짧음 1 · 김 2 · 중간 3), 크리에이티브 커먼즈 필드 6(= 1).
        /// 예: 크리에이티브 커먼즈만 EgIwAQ%3D%3D, 이번 주 EgIIAw%3D%3D.
        /// "올린 날짜순" 정렬(sp=CAI%3D, yt-dlp ytsearchdate)은 2026-10 현재 유튜브가 무시해(결과가 날짜순이 아님) 쓰지 않는다.
        /// </summary>
        public static string SearchUrl(string query, YtSearchOptions options)
        {
            options = options ?? new YtSearchOptions();
            var filters = new List<byte>();
            if (options.Uploaded != YtUploaded.Any)
                filters.AddRange(new byte[] { 0x08, (byte)options.Uploaded });
            if (options.Length != YtLength.Any)
                filters.AddRange(new byte[] { 0x18, (byte)options.Length });
            if (options.CreativeCommons)
                filters.AddRange(new byte[] { 0x30, 0x01 });
            var sp = new List<byte>();
            if (filters.Count > 0)
            {
                sp.Add(0x12);
                sp.Add((byte)filters.Count);
                sp.AddRange(filters);
            }
            var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString((query ?? "").Trim());
            return sp.Count == 0 ? url : url + "&sp=" + Uri.EscapeDataString(Convert.ToBase64String(sp.ToArray()));
        }

        /// <summary>
        /// yt-dlp --flat-playlist -J 결과(검색 결과 페이지) → 영상 목록. 채널·재생목록 항목은 뺀다(받을 수 없음).
        /// entryCount는 빼기 전 항목 수([더 보기]가 있는지 — 요청한 수만큼 왔으면 더 있을 수 있음).
        /// </summary>
        public static List<YtSearchResult> ParseSearch(string json, out int entryCount)
        {
            var results = new List<YtSearchResult>();
            entryCount = 0;
            using (var document = JsonDocument.Parse(json))
            {
                if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return results;
                entryCount = entries.GetArrayLength();
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;
                    var id = Text(entry, "id");
                    var ie = Text(entry, "ie_key");
                    if (string.IsNullOrEmpty(id) || (ie.Length > 0 && ie != "Youtube"))
                        continue;
                    var url = Text(entry, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !IsYouTubeHost(parsed.Host))
                        url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
                    results.Add(new YtSearchResult
                    {
                        Id = id,
                        Url = url,
                        Title = Text(entry, "title"),
                        Channel = FirstText(entry, "channel", "uploader"),
                        DurationSeconds = Number(entry, "duration"),
                        ViewCount = entry.TryGetProperty("view_count", out var views) && views.ValueKind == JsonValueKind.Number ? views.GetInt64() : -1,
                        Uploaded = UploadDate(entry),
                        IsLive = Text(entry, "live_status") == "is_live",
                        ThumbnailUrl = Thumbnail(entry)
                    });
                }
            }
            return results;
        }

        /// <summary>올린 날(upload_date "20240131" 또는 timestamp). 검색 결과(--flat-playlist)에는 보통 없다 — 없으면 null.</summary>
        private static DateTime? UploadDate(JsonElement entry)
        {
            if (DateTime.TryParseExact(Text(entry, "upload_date"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
            foreach (var key in new[] { "timestamp", "release_timestamp" })
            {
                if (entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number)
                    return DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()).LocalDateTime.Date;
            }
            return null;
        }

        /// <summary>썸네일 주소: https://i.ytimg.com의 것만, 너비 320 안팎의 작은 것.</summary>
        private static string Thumbnail(JsonElement entry)
        {
            if (!entry.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
                return null;
            var all = thumbnails.EnumerateArray()
                .Select(t => new { Url = Text(t, "url"), Width = Number(t, "width") })
                .Where(t => IsThumbnailUrl(t.Url))
                .ToList();
            return all.OrderBy(t => t.Width >= 240 ? t.Width : 100000 - t.Width).Select(t => t.Url).FirstOrDefault();
        }

        public const string ThumbnailHost = "i.ytimg.com";

        public static bool IsThumbnailUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == ThumbnailHost && uri.IsDefaultPort;
        }

        // ---------- 영상 정보·화질 ----------

        /// <summary>yt-dlp -J --no-playlist 결과 → 제목·길이·화질(높이) 목록. 재생목록이면 IsPlaylist.</summary>
        public static YtVideoInfo ParseVideo(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                var info = new YtVideoInfo
                {
                    Title = Text(root, "title"),
                    DurationSeconds = Number(root, "duration"),
                    WebpageUrl = Text(root, "webpage_url"),
                    Extractor = Text(root, "extractor_key"),
                    IsPlaylist = Text(root, "_type") == "playlist"
                };
                var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
                if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var format in formats.EnumerateArray())
                    {
                        var vcodec = Text(format, "vcodec");
                        var acodec = Text(format, "acodec");
                        var height = (int)Number(format, "height");
                        if (vcodec != "none" && vcodec.Length > 0 && height > 0 && Text(format, "ext") != "mhtml")
                            heights.Add(height);
                        if (acodec != "none" && acodec.Length > 0)
                            info.HasAudio = true;
                    }
                }
                else if (Number(root, "height") > 0)
                {
                    heights.Add((int)Number(root, "height"));
                }
                info.Heights = heights.ToList();
                return info;
            }
        }

        /// <summary>기본 화질: limit(프로젝트 높이) 이하에서 가장 높은 것, 없으면 가장 낮은 것. 목록이 비면 0(가장 좋은 것).</summary>
        public static int ChooseHeight(IList<int> heights, int limit)
        {
            if (heights == null || heights.Count == 0)
                return 0;
            var fit = heights.Where(h => h <= limit).ToList();
            return fit.Count > 0 ? fit.Max() : heights.Min();
        }

        /// <summary>yt-dlp -f 값: 높이 이하의 가장 좋은 영상+소리, 안 되면 하나로 된 것, 그것도 없으면 아무거나 가장 좋은 것. 0이면 제한 없음.</summary>
        public static string FormatSelector(int maxHeight)
        {
            if (maxHeight <= 0)
                return "bv*+ba/b";
            var h = maxHeight.ToString(CultureInfo.InvariantCulture);
            return "bv*[height<=" + h + "]+ba/b[height<=" + h + "]/bv*+ba/b";
        }

        // ---------- 진행률 ----------

        public const string ProgressPrefix = "FVPROG ";
        public const string FilePrefix = "FVFILE ";

        /// <summary>--progress-template에 넘기는 값(한 줄: 받은 바이트, 전체, 추정 전체, 백분율, 상태).</summary>
        public const string ProgressTemplate = "download:" + ProgressPrefix + "%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress._percent)s %(progress.status)s";

        /// <summary>진행률 줄 → 0~100. 해당 줄이 아니면 null.</summary>
        public static double? ParseProgress(string line)
        {
            if (line == null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                return null;
            var parts = line.Substring(ProgressPrefix.Length).Split(' ');
            if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return Math.Max(0, Math.Min(100, percent));
            if (parts.Length >= 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var done))
            {
                var total = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t
                    : double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0;
                if (total > 0)
                    return Math.Max(0, Math.Min(100, 100 * done / total));
            }
            return null;
        }

        // ---------- 오류 ----------

        /// <summary>yt-dlp 오류 출력 → 알기 쉬운 메시지. Outdated면 yt-dlp를 새로 받으면 나아질 수 있는 오류.</summary>
        public static YtDlpError Explain(string errorOutput)
        {
            var text = errorOutput ?? "";
            var errors = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).ToList();
            var focus = errors.Count > 0 ? string.Join("\n", errors) : text;
            bool Has(params string[] words)
            {
                return words.Any(w => focus.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (Has("not a bot"))
                return new YtDlpError("유튜브가 \"로봇이 아닌지 확인\"을 요구해 막았습니다. 잠시 뒤 다시 하거나 다른 네트워크에서 해 보세요. 계속되면 yt-dlp를 업데이트하세요.", true);
            if (Has("confirm your age", "age-restricted", "age restricted", "inappropriate for some users"))
                return new YtDlpError("연령 제한 영상이라 로그인 없이 받을 수 없습니다.", false);
            if (Has("members-only", "members only", "join this channel", "premium", "private video", "logged-in", "logged in", "sign in", "login required", "cookies"))
                return new YtDlpError("로그인이 필요한 영상입니다(비공개·회원 전용·로그인 필요). 이 편집기에서는 받을 수 없습니다.", false);
            if (Has("unsupported url"))
                return new YtDlpError("지원하지 않는 사이트이거나 영상 주소가 아닙니다.", false);
            if (Has("http error 429", "too many requests"))
                return new YtDlpError("요청이 너무 많아 유튜브가 잠시 막았습니다. 잠시 뒤 다시 하세요.", false);
            // "Requested format is not available"이 아래 "is not available"에 걸리지 않게 먼저 본다
            if (Has("requested format is not available", "unable to extract", "signature extraction failed", "nsig extraction failed", "http error 403", "please report this issue", "update to the latest", "yt-dlp -u"))
                return new YtDlpError("yt-dlp가 유튜브 변경을 따라가지 못했습니다. yt-dlp를 업데이트하세요.", true);
            if (Has("video unavailable", "is not available", "has been removed", "no longer available"))
                return new YtDlpError("볼 수 없는 영상입니다(삭제·지역 제한 등).", false);
            if (Has("getaddrinfo", "name or service not known", "no such host", "timed out", "connection", "unable to download webpage", "network"))
                return new YtDlpError("네트워크 오류로 받지 못했습니다. 인터넷 연결을 확인하세요.", false);
            var last = errors.LastOrDefault() ?? text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            return new YtDlpError(last.Length == 0 ? "yt-dlp가 실패했습니다." : "yt-dlp가 실패했습니다: " + (last.StartsWith("ERROR:", StringComparison.Ordinal) ? last.Substring(6).Trim() : last), false);
        }

        // ---------- 파일 이름 ----------

        private static readonly char[] WindowsInvalid = "<>:\"/\\|?*".ToCharArray();
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>제목 → Windows에서 쓸 수 있는 파일 이름(확장자 없이). 쓸 수 없는 글자는 빼고, 비면 fallback.</summary>
        public static string SafeFileName(string title, string fallback = "동영상")
        {
            var builder = new StringBuilder();
            foreach (var c in title ?? "")
            {
                if (char.IsControl(c) || Array.IndexOf(WindowsInvalid, c) >= 0 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                    builder.Append(' ');
                else
                    builder.Append(c);
            }
            var name = Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (name.Length > 120)
                name = name.Substring(0, 120).TrimEnd('.', ' ');
            if (name.Length == 0)
                return fallback;
            return Reserved.Contains(name.Split('.')[0]) ? "_" + name : name;
        }

        /// <summary>folder 안에 없는 이름: "이름.mp4", 있으면 "이름 (2).mp4"…</summary>
        public static string UniquePath(string folder, string name, string extension)
        {
            var path = Path.Combine(folder, name + extension);
            for (var i = 2; File.Exists(path) || Directory.Exists(path); i++)
                path = Path.Combine(folder, name + " (" + i + ")" + extension);
            return path;
        }

        /// <summary>
        /// URL에서 받은 파일을 둘 폴더: 지정한 폴더(절대 경로일 때만) → 프로젝트 파일 옆 media → 저장 안 한 프로젝트면 데이터 폴더\media.
        /// </summary>
        public static string DownloadFolder(string configured, string projectPath, string dataDirectory)
        {
            var custom = (configured ?? "").Trim();
            if (custom.Length > 0 && Path.IsPathFullyQualified(custom))
                return Path.GetFullPath(custom);
            return string.IsNullOrEmpty(projectPath)
                ? Path.Combine(dataDirectory, "media")
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)), "media");
        }

        // ---------- 최근 검색어 ----------

        /// <summary>최근 검색어(설정에 JSON 배열로) 맨 앞에 넣는다. 같은 것은 앞으로 옮기고 max개까지.</summary>
        public static string AddRecent(string stored, string query, int max = 10)
        {
            query = (query ?? "").Trim();
            var list = ReadRecent(stored);
            if (query.Length > 0)
            {
                list.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, query);
            }
            return JsonSerializer.Serialize(list.Take(max).ToList());
        }

        public static List<string> ReadRecent(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return new List<string>();
            try
            {
                return (JsonSerializer.Deserialize<List<string>>(stored) ?? new List<string>()).Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        // ---------- 내려받기(GitHub 릴리스) ----------

        /// <summary>GitHub 릴리스 JSON에서 파일 하나와 그 체크섬 파일을 고른다(둘 다 https GitHub 주소일 때만). 없으면 null.</summary>
        public static GitHubAsset ChooseAsset(string releaseJson, string name, string checksumsName)
        {
            using (var document = JsonDocument.Parse(releaseJson))
            {
                if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return null;
                JsonElement? file = null, sums = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = Text(asset, "name");
                    if (assetName == name)
                        file = asset;
                    else if (assetName == checksumsName)
                        sums = asset;
                }
                if (file == null || sums == null)
                    return null;
                var url = Text(file.Value, "browser_download_url");
                var checksumsUrl = Text(sums.Value, "browser_download_url");
                if (!FfmpegText.FromGitHub(url) || !FfmpegText.FromGitHub(checksumsUrl))
                    return null;
                return new GitHubAsset { Name = name, Url = url, Size = (long)Number(file.Value, "size"), ChecksumsUrl = checksumsUrl };
            }
        }

        /// <summary>
        /// 체크섬 파일에서 SHA-256(소문자). "해시  파일이름" 줄(yt-dlp SHA2-256SUMS)과
        /// PowerShell Get-FileHash 출력("Hash : 해시", Deno의 .sha256sum)을 읽는다. 없으면 null.
        /// </summary>
        public static string FindChecksum(string checksums, string fileName)
        {
            var listed = FfmpegText.FindChecksum(checksums, fileName);
            if (listed != null)
                return listed;
            var match = Regex.Match(checksums ?? "", @"^\s*Hash\s*:\s*([0-9a-fA-F]{64})\s*$", RegexOptions.Multiline);
            var path = Regex.Match(checksums ?? "", @"^\s*Path\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
            if (match.Success && (!path.Success || path.Groups[1].Value.Replace('\\', '/').EndsWith("/" + fileName, StringComparison.Ordinal) || path.Groups[1].Value == fileName))
                return match.Groups[1].Value.ToLowerInvariant();
            return null;
        }

        // ---------- JSON 도우미 ----------

        private static string Text(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return "";
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static string FirstText(JsonElement element, params string[] names)
        {
            return names.Select(n => Text(element, n)).FirstOrDefault(t => t.Length > 0) ?? "";
        }

        private static double Number(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
            return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
        }
    }

    public enum UrlKind
    {
        Invalid,
        Direct,
        YouTube,
        Other
    }

    /// <summary>값은 유튜브 검색 필터(sp)의 올린 날짜 값.</summary>
    public enum YtUploaded
    {
        Any = 0,
        Today = 2,
        Week = 3,
        Month = 4,
        Year = 5
    }

    /// <summary>값은 유튜브 검색 필터(sp)의 길이 값.</summary>
    public enum YtLength
    {
        Any = 0,
        Short = 1,
        Long = 2,
        Medium = 3
    }

    public sealed class YtSearchOptions
    {
        public YtUploaded Uploaded { get; set; }
        public YtLength Length { get; set; }
        public bool CreativeCommons { get; set; }
    }

    public sealed class YtSearchResult
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Channel { get; set; }
        public double DurationSeconds { get; set; }
        public long ViewCount { get; set; }
        public DateTime? Uploaded { get; set; }
        public bool IsLive { get; set; }
        public string ThumbnailUrl { get; set; }
    }

    public sealed class YtVideoInfo
    {
        public string Title { get; set; }
        public double DurationSeconds { get; set; }
        public string WebpageUrl { get; set; }
        public string Extractor { get; set; }
        public bool IsPlaylist { get; set; }
        public bool HasAudio { get; set; }
        /// <summary>받을 수 있는 화면 높이(큰 것부터).</summary>
        public List<int> Heights { get; set; } = new List<int>();
    }

    public sealed class YtDlpError
    {
        public YtDlpError(string message, bool outdated)
        {
            Message = message;
            Outdated = outdated;
        }

        public string Message { get; }
        public bool Outdated { get; }
    }

    public sealed class GitHubAsset
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public string ChecksumsUrl { get; set; }
    }
}
