using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Media
{
    /// <summary>올릴 동영상의 정보(videos.insert의 snippet·status).</summary>
    internal sealed class YouTubeVideo
    {
        public const int MaxTitle = 100;
        public const int MaxDescriptionBytes = 5000;
        public static readonly string[] Privacies = { "private", "unlisted", "public" };

        public string Title { get; set; }
        public string Description { get; set; }

        /// <summary>private(기본) · unlisted · public. 검증받지 않은 API 프로젝트로 올리면 유튜브가 private으로 묶는다.</summary>
        public string Privacy { get; set; } = "private";

        /// <summary>아동용인지(status.selfDeclaredMadeForKids). 유튜브가 꼭 묻는 값이라 고르기 전(null)에는 올리지 않는다.</summary>
        public bool? MadeForKids { get; set; }

        /// <summary>분류. 22 = People &amp; Blogs(유튜브 스튜디오에서 바꿀 수 있음).</summary>
        public string CategoryId { get; set; } = "22";

        /// <summary>유튜브가 받지 않을 값이면 알릴 글, 괜찮으면 null.</summary>
        public string Problem()
        {
            var title = (Title ?? "").Trim();
            var description = Description ?? "";
            if (title.Length == 0)
                return "제목을 넣으세요.";
            if (title.Length > MaxTitle)
                return "제목은 " + MaxTitle + "자까지입니다(지금 " + title.Length + "자).";
            if (title.IndexOfAny(new[] { '<', '>' }) >= 0 || description.IndexOfAny(new[] { '<', '>' }) >= 0)
                return "제목·설명에는 < > 글자를 쓸 수 없습니다(유튜브가 받지 않음).";
            if (Encoding.UTF8.GetByteCount(description) > MaxDescriptionBytes)
                return "설명은 " + MaxDescriptionBytes + "바이트까지입니다(한글은 한 글자에 3바이트).";
            if (Array.IndexOf(Privacies, Privacy) < 0)
                return "공개 범위를 고르세요.";
            if (MadeForKids == null)
                return "아동용 동영상인지 고르세요(유튜브가 꼭 묻는 값).";
            return null;
        }
    }

    /// <summary>
    /// YouTube Data API v3 재개 가능 업로드(resumable)를 HttpClient로 직접: 세션 시작(POST, 정보 JSON) → 조각을 PUT(Content-Range, 256KiB의 배수)
    /// → 서버는 308과 받은 범위(Range: bytes=0-N)로 답하고 마지막 조각에 200/201과 동영상 정보. 연결이 끊기거나 5xx면 잠시 뒤 받은 위치를 물어
    /// (PUT, Content-Range: bytes */전체) 그 다음부터 이어 보낸다. 401이면 접근 토큰을 새로 받아 이어 간다(1시간 넘는 올리기).
    /// </summary>
    internal sealed class YouTubeUploader
    {
        public const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
        public const int ChunkUnit = 256 * 1024;
        public const long MaxBytes = 256L * 1024 * 1024 * 1024;
        private const long ReportStep = 512 * 1024;

        private readonly HttpClient _http;
        private readonly Func<bool, CancellationToken, Task<string>> _accessToken;

        /// <param name="accessToken">(새로 받을지, 취소) → 접근 토큰. 보통 <see cref="YouTubeAccount.AccessToken"/>.</param>
        public YouTubeUploader(HttpClient http, Func<bool, CancellationToken, Task<string>> accessToken)
        {
            _http = http;
            _accessToken = accessToken;
        }

        /// <summary>조각 크기(ChunkUnit의 배수, 기본 8MiB).</summary>
        public int ChunkSize { get; set; } = 32 * ChunkUnit;

        /// <summary>진전 없이 잇달아 실패해도 다시 해 볼 횟수(넘으면 멈춤).</summary>
        public int MaxRetries { get; set; } = 8;

        /// <summary>요청 하나(조각 하나)의 시간 한도. 넘으면 연결이 끊긴 것으로 보고 이어 보낸다.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>다시 해 보기 전 기다리기(시험에서 바꿈).</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        public sealed class Status
        {
            public long Sent { get; set; }
            public long Total { get; set; }

            /// <summary>다시 해 보는 중이면 그 까닭(아니면 null).</summary>
            public string Note { get; set; }
        }

        public sealed class Result
        {
            public string VideoId { get; set; }
            public string Privacy { get; set; }
            public string ChannelTitle { get; set; }
            public Uri Link { get { return new Uri("https://youtu.be/" + Uri.EscapeDataString(VideoId)); } }
        }

        public static TimeSpan Backoff(int failures)
        {
            return TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures - 1, 6)));
        }

        public async Task<Result> Upload(string path, YouTubeVideo video, IProgress<Status> progress, CancellationToken cancellation)
        {
            var problem = video.Problem();
            if (problem != null)
                throw new YouTubeException(problem);
            if (ChunkSize <= 0 || ChunkSize % ChunkUnit != 0)
                throw new ArgumentException("조각 크기는 256KiB의 배수여야 합니다.", nameof(ChunkSize));
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
            {
                var total = file.Length;
                if (total == 0)
                    throw new YouTubeException("빈 파일은 올릴 수 없습니다.");
                if (total > MaxBytes)
                    throw new YouTubeException("유튜브에는 256GB까지 올릴 수 있습니다.");
                var contentType = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "application/octet-stream";
                Uri session = null;
                long offset = 0, best = 0, reported = -1;
                var ask = false;
                var refresh = false;
                var failures = 0;
                var unauthorized = 0;
                string bearer = null;

                void Report(long sent, string note = null)
                {
                    if (note == null && sent != total && reported >= 0 && Math.Abs(sent - reported) < ReportStep)
                        return;
                    reported = sent;
                    progress?.Report(new Status { Sent = sent, Total = total, Note = note });
                }

                async Task Retry(string what)
                {
                    failures++;
                    if (failures > MaxRetries)
                        throw new YouTubeException("연결이 거듭 끊겨 올리기를 멈췄습니다(" + what + "). 나중에 다시 올리세요.");
                    var wait = Backoff(failures);
                    Report(offset, "연결이 끊겨 " + wait.TotalSeconds + "초 뒤 이어 보냅니다(" + failures + "/" + MaxRetries + " — " + what + ")");
                    await Delay(wait, cancellation).ConfigureAwait(false);
                    ask = session != null;
                }

                Report(0);
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var asking = ask;
                    var from = offset;
                    HttpResponseMessage response;
                    try
                    {
                        bearer = await _accessToken(refresh, cancellation).ConfigureAwait(false);
                        refresh = false;
                        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                        using (var request = session == null ? StartRequest(video, total, contentType)
                                             : ask ? StatusRequest(session, total)
                                             : ChunkRequest(session, file, offset, total, contentType, sent => Report(sent)))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                            timer.CancelAfter(RequestTimeout);
                            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex.InnerException is YouTubeException inner)
                    {
                        throw inner;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is OperationCanceledException)
                    {
                        await Retry(ex is OperationCanceledException ? "응답 없음" : YouTubeAuth.Clip(YouTubeAuth.Redact(ex.Message, bearer), 120)).ConfigureAwait(false);
                        continue;
                    }

                    using (response)
                    {
                        var code = (int)response.StatusCode;
                        if (session == null && code >= 200 && code < 300)
                        {
                            var location = response.Headers.Location;
                            if (location == null)
                                throw new YouTubeException("유튜브가 올리기 세션 주소를 주지 않았습니다.");
                            session = location.IsAbsoluteUri ? location : new Uri(new Uri(UploadEndpoint), location);
                            unauthorized = 0;
                            continue;
                        }
                        if (session != null && (code == 200 || code == 201))
                        {
                            Report(total);
                            return await ReadResult(response, cancellation).ConfigureAwait(false);
                        }
                        if (session != null && code == 308)
                        {
                            offset = Received(response);
                            unauthorized = 0;
                            if (offset > best)
                            {
                                best = offset;
                                failures = 0;
                            }
                            if (offset >= total || (!asking && offset <= from))
                            {
                                // 다 받았다면서 끝나지 않거나, 보낸 조각을 하나도 받지 않음 — 잠시 뒤 다시 묻는다(끝없이 되풀이하지 않게 실패로 셈)
                                await Retry(offset >= total ? "서버가 마무리하지 않음" : "서버가 조각을 받지 않음").ConfigureAwait(false);
                                continue;
                            }
                            ask = false;
                            Report(offset);
                            continue;
                        }
                        if (code == 401 && ++unauthorized <= 2)
                        {
                            // 접근 토큰 만료 — 새로 받고(세션이 있으면 받은 위치부터) 이어 간다
                            refresh = true;
                            ask = session != null;
                            continue;
                        }
                        if (session != null && (code == 404 || code == 410))
                            throw new YouTubeException("올리기 세션이 끝났습니다(오래 멈췄거나 유튜브가 지움). 처음부터 다시 올리세요.");
                        if (code >= 500 || code == 429)
                        {
                            await Retry("HTTP " + code).ConfigureAwait(false);
                            continue;
                        }
                        throw await ApiError(response, cancellation, bearer).ConfigureAwait(false);
                    }
                }
            }
        }

        private static HttpRequestMessage StartRequest(YouTubeVideo video, long total, string contentType)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                snippet = new { title = video.Title.Trim(), description = video.Description ?? "", categoryId = video.CategoryId },
                status = new { privacyStatus = video.Privacy, selfDeclaredMadeForKids = video.MadeForKids == true }
            });
            var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Add("X-Upload-Content-Length", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-Upload-Content-Type", contentType);
            return request;
        }

        /// <summary>받은 위치 묻기: 빈 PUT, Content-Range: bytes */전체.</summary>
        private static HttpRequestMessage StatusRequest(Uri session, long total)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(total);
            return request;
        }

        private HttpRequestMessage ChunkRequest(Uri session, FileStream file, long offset, long total, string contentType, Action<long> sent)
        {
            var length = Math.Min(ChunkSize, total - offset);
            var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new FileChunk(file, offset, length, done => sent(offset + done)) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);
            return request;
        }

        /// <summary>308의 Range(bytes=0-N)로 받은 바이트 수(N+1). Range가 없으면 아직 아무것도 받지 않은 것(0).</summary>
        public static long Received(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Range", out var values))
                return 0;
            var text = values.FirstOrDefault() ?? "";
            var dash = text.LastIndexOf('-');
            return dash >= 0 && long.TryParse(text.Substring(dash + 1).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var last) ? last + 1 : 0;
        }

        private static async Task<Result> ReadResult(HttpResponseMessage response, CancellationToken cancellation)
        {
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            try
            {
                using (var document = JsonDocument.Parse(body))
                {
                    var root = document.RootElement;
                    var id = YouTubeAuth.Text(root, "id");
                    if (string.IsNullOrEmpty(id))
                        throw new YouTubeException("올렸지만 유튜브가 동영상 ID를 주지 않았습니다. 유튜브 스튜디오에서 확인하세요.");
                    return new Result
                    {
                        VideoId = id,
                        Privacy = root.TryGetProperty("status", out var status) ? YouTubeAuth.Text(status, "privacyStatus") : null,
                        ChannelTitle = root.TryGetProperty("snippet", out var snippet) ? YouTubeAuth.Text(snippet, "channelTitle") : null
                    };
                }
            }
            catch (JsonException)
            {
                throw new YouTubeException("올렸지만 유튜브의 답을 읽지 못했습니다. 유튜브 스튜디오에서 확인하세요.");
            }
        }

        /// <summary>API 오류({"error":{"message","errors":[{"reason"}],"details":[{"reason"}]}})를 알릴 글로. 토큰은 지운다.</summary>
        private static async Task<YouTubeException> ApiError(HttpResponseMessage response, CancellationToken cancellation, string bearer)
        {
            var code = (int)response.StatusCode;
            string reason = null, message = null;
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                using (var document = JsonDocument.Parse(body))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        message = YouTubeAuth.Text(error, "message");
                        foreach (var list in new[] { "errors", "details" })
                        {
                            if (reason == null && error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                                reason = items.EnumerateArray().Select(item => YouTubeAuth.Text(item, "reason")).FirstOrDefault(r => r != null);
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
            }
            switch (reason)
            {
                case "quotaExceeded":
                case "dailyLimitExceeded":
                    return new YouTubeException("오늘 쓸 수 있는 YouTube Data API 할당량을 다 썼습니다. 할당량은 태평양 시간 자정에 다시 채워집니다(올리기 한 번에 약 1,600 단위).");
                case "uploadLimitExceeded":
                    return new YouTubeException("이 채널이 지금 올릴 수 있는 동영상 수를 넘었습니다. 나중에 다시 올리세요.");
                case "youtubeSignupRequired":
                    return new YouTubeException("이 구글 계정에는 유튜브 채널이 없습니다. 유튜브에서 채널을 먼저 만드세요.");
                case "insufficientPermissions":
                case "ACCESS_TOKEN_SCOPE_INSUFFICIENT":
                    return new YouTubeException("유튜브 동영상 올리기 권한이 없습니다. 로그아웃한 뒤 다시 로그인해 권한을 허락하세요.", true);
                case "accessNotConfigured":
                case "SERVICE_DISABLED":
                    return new YouTubeException("이 Google Cloud 프로젝트에서 YouTube Data API v3를 켜지 않았습니다(설정 방법 안내 2단계).");
            }
            var detail = YouTubeAuth.Clip(YouTubeAuth.Redact(message, bearer));
            if (code == 401)
                return new YouTubeException("유튜브가 로그인을 받지 않았습니다. 다시 로그인하세요." + (detail.Length > 0 ? " — " + detail : ""), true);
            return new YouTubeException("유튜브가 올리기를 받지 않았습니다: HTTP " + code + (reason != null ? " " + YouTubeAuth.Clip(reason, 60) : "") + (detail.Length > 0 ? " — " + detail : ""));
        }

        /// <summary>파일의 한 구간을 보내는 내용. 다시 보낼 때마다 처음 위치로 돌아가 읽고, 보낸 바이트를 알린다.</summary>
        private sealed class FileChunk : HttpContent
        {
            private readonly FileStream _file;
            private readonly long _offset;
            private readonly long _length;
            private readonly Action<long> _sent;

            public FileChunk(FileStream file, long offset, long length, Action<long> sent)
            {
                _file = file;
                _offset = offset;
                _length = length;
                _sent = sent;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                return SerializeToStreamAsync(stream, context, CancellationToken.None);
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                _file.Position = _offset;
                while (done < _length)
                {
                    var read = await _file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _length - done)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new YouTubeException("올리는 동안 파일이 짧아졌습니다. 파일을 바꾸지 말고 다시 올리세요.");
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    _sent(done);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _length;
                return true;
            }
        }
    }
}
