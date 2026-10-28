using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Media;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>
    /// 유튜브 재개 가능 업로드(#9): 세션 시작(정보 JSON·머리글), 256KiB 배수 조각과 Content-Range, 308 Range로 이어 보내기,
    /// 5xx·연결 끊김·응답 없음 뒤 받은 위치를 물어 이어 보내기, 401이면 토큰을 새로, 할당량 등 오류 메시지, 취소. 가짜 서버만 쓴다.
    /// </summary>
    public class YouTubeUploadTests : IDisposable
    {
        private const int Unit = YouTubeUploader.ChunkUnit;
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-ytup-" + Guid.NewGuid().ToString("N"));
        private readonly List<TimeSpan> _waits = new List<TimeSpan>();

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private string MakeFile(int length, string name = "내 영상.mp4")
        {
            Directory.CreateDirectory(_folder);
            var data = new byte[length];
            new Random(length).NextBytes(data);
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        private static YouTubeVideo Video()
        {
            return new YouTubeVideo { Title = "  첫 영상  ", Description = "설명 한 줄\n둘째 줄", MadeForKids = false };
        }

        private YouTubeUploader Uploader(FakeYouTube server, Func<bool, CancellationToken, Task<string>> token = null)
        {
            return new YouTubeUploader(YouTubeAuth.NewHttp(server), token ?? ((refresh, c) => Task.FromResult(server.Token)))
            {
                ChunkSize = Unit,
                Delay = (wait, c) =>
                {
                    _waits.Add(wait);
                    return Task.CompletedTask;
                }
            };
        }

        [Fact]
        public async Task Upload_StartsSessionWithMetadata_AndSendsChunksOf256KiB()
        {
            var path = MakeFile(2 * Unit + 7000);
            var server = new FakeYouTube();
            var sent = new List<long>();
            var result = await Uploader(server).Upload(path, Video(), new SyncProgress<YouTubeUploader.Status>(s => sent.Add(s.Sent)), CancellationToken.None);

            Assert.Equal("vid123", result.VideoId);
            Assert.Equal("https://youtu.be/vid123", result.Link.AbsoluteUri);
            Assert.Equal("private", result.Privacy);
            Assert.Equal("내 채널", result.ChannelTitle);
            Assert.Equal(File.ReadAllBytes(path), server.Received.ToArray());

            // 세션 시작: 재개 업로드·snippet,status, 파일 크기·종류 머리글, 정보 JSON
            var start = server.Start;
            Assert.Equal(HttpMethod.Post, start.Method);
            Assert.Equal("www.googleapis.com", start.RequestUri.Host);
            Assert.Equal("/upload/youtube/v3/videos", start.RequestUri.AbsolutePath);
            Assert.Contains("uploadType=resumable", start.RequestUri.Query);
            Assert.Contains("part=snippet,status", start.RequestUri.Query);
            Assert.Equal((2 * Unit + 7000).ToString(), start.Headers.GetValues("X-Upload-Content-Length").Single());
            Assert.Equal("video/mp4", start.Headers.GetValues("X-Upload-Content-Type").Single());
            Assert.Equal("application/json", start.Content.Headers.ContentType.MediaType);
            using (var metadata = JsonDocument.Parse(server.Metadata))
            {
                var root = metadata.RootElement;
                Assert.Equal("첫 영상", root.GetProperty("snippet").GetProperty("title").GetString());
                Assert.Equal("설명 한 줄\n둘째 줄", root.GetProperty("snippet").GetProperty("description").GetString());
                Assert.Equal("22", root.GetProperty("snippet").GetProperty("categoryId").GetString());
                Assert.Equal("private", root.GetProperty("status").GetProperty("privacyStatus").GetString());
                Assert.False(root.GetProperty("status").GetProperty("selfDeclaredMadeForKids").GetBoolean());
            }

            // 조각: 마지막 말고는 256KiB의 배수, 빈틈 없이 이어짐
            Assert.Equal(new[] { "bytes 0-262143/531288", "bytes 262144-524287/531288", "bytes 524288-531287/531288" }, server.Ranges);
            Assert.Equal(0, server.Queries);
            Assert.Empty(_waits);
            Assert.Equal(0, sent.First());
            Assert.Equal(2 * Unit + 7000, sent.Last());
            Assert.True(sent.Zip(sent.Skip(1), (a, b) => b >= a).All(x => x));
        }

        [Fact]
        public async Task Upload_ServerKeepsLess_ResumesFromItsRange()
        {
            // 두 번째 조각을 서버가 1000바이트만 받았다고 하면(308 Range) 그 다음부터 보낸다
            var path = MakeFile(3 * Unit);
            var server = new FakeYouTube { Keep = (i, length) => i == 1 ? 1000 : length };
            await Uploader(server).Upload(path, Video(), null, CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(path), server.Received.ToArray());
            Assert.Equal("bytes " + (Unit + 1000) + "-" + (2 * Unit + 1000 - 1) + "/" + 3 * Unit, server.Ranges[2]);
            Assert.Empty(_waits);
        }

        [Fact]
        public async Task Upload_308WithoutRange_StartsAgainFromZero()
        {
            var path = MakeFile(Unit + 10);
            var server = new FakeYouTube { Keep = (i, length) => i == 0 ? 0 : length };
            await Uploader(server).Upload(path, Video(), null, CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(path), server.Received.ToArray());
            // 받은 것이 없으면 실패로 세고(끝없이 되풀이하지 않게) 받은 위치를 다시 물은 뒤 처음부터
            Assert.Equal(new[] { "bytes 0-262143/262154", "bytes */262154", "bytes 0-262143/262154", "bytes 262144-262153/262154" }, server.Ranges);
            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, _waits);
        }

        [Fact]
        public async Task Upload_ServerError_WaitsAsksPositionAndResumes()
        {
            // 두 번째 조각에서 503(일부는 받음) → 기다렸다가 받은 위치를 묻고(bytes */전체) 그 다음부터
            var path = MakeFile(3 * Unit);
            var server = new FakeYouTube
            {
                Keep = (i, length) => i == 1 ? 5000 : length,
                Fail = i => i == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null
            };
            var notes = new List<string>();
            await Uploader(server).Upload(path, Video(), new SyncProgress<YouTubeUploader.Status>(s =>
            {
                if (s.Note != null)
                    notes.Add(s.Note);
            }), CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(path), server.Received.ToArray());
            Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, _waits);
            Assert.Equal(1, server.Queries);
            Assert.Equal("bytes */" + 3 * Unit, server.Ranges[2]);
            Assert.StartsWith("bytes " + (Unit + 5000) + "-", server.Ranges[3]);
            Assert.Contains(notes, n => n.Contains("HTTP 503"));
        }

        [Fact]
        public async Task Upload_ConnectionDrop_AndNoAnswer_AreResumed()
        {
            var path = MakeFile(3 * Unit);
            var server = new FakeYouTube
            {
                Keep = (i, length) => i == 0 ? 100 : length,
                Drop = i => i == 0 ? new HttpRequestException("현재 연결은 원격 호스트에 의해 강제로 끊겼습니다.") : null,
                // 이어 보낸 첫 조각은 답이 없다(요청 시간 한도를 넘김)
                Stall = i => i == 1
            };
            var uploader = Uploader(server);
            uploader.RequestTimeout = TimeSpan.FromMilliseconds(300);
            await uploader.Upload(path, Video(), null, CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(path), server.Received.ToArray());
            Assert.Equal(2, server.Queries);
            // 끊긴 뒤 물어보니 100바이트를 받았음(진전) → 실패 수를 새로 센다
            Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) }, _waits);
        }

        [Fact]
        public async Task Upload_StartServerError_IsRetried()
        {
            var path = MakeFile(100);
            var server = new FakeYouTube();
            var starts = 0;
            server.StartReply = () => ++starts == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null;
            await Uploader(server).Upload(path, Video(), null, CancellationToken.None);
            Assert.Equal(2, starts);
            Assert.Equal(new[] { "bytes 0-99/100" }, server.Ranges);
        }

        [Fact]
        public async Task Upload_Unauthorized_RefreshesTokenAndContinues()
        {
            // 올리는 동안 접근 토큰이 만료됨(1시간 넘는 올리기) → 새로 받아 받은 위치부터
            var path = MakeFile(3 * Unit);
            var server = new FakeYouTube { Token = "access-1" };
            server.After = i =>
            {
                if (i == 0)
                    server.Token = "access-2";
            };
            var current = "access-1";
            var refreshes = 0;
            var uploader = Uploader(server, (refresh, c) =>
            {
                if (refresh)
                {
                    refreshes++;
                    current = "access-2";
                }
                return Task.FromResult(current);
            });
            await uploader.Upload(path, Video(), null, CancellationToken.None);
            Assert.Equal(1, refreshes);
            Assert.Equal(File.ReadAllBytes(path), server.Received.ToArray());
            Assert.Equal(1, server.Queries);
            Assert.Empty(_waits);
        }

        [Fact]
        public async Task Upload_QuotaExceeded_StopsWithExplanation()
        {
            var path = MakeFile(100);
            var server = new FakeYouTube
            {
                StartReply = () => Error(HttpStatusCode.Forbidden, "quotaExceeded", "The request cannot be completed because you have exceeded your quota.")
            };
            var error = await Assert.ThrowsAsync<YouTubeException>(() => Uploader(server).Upload(path, Video(), null, CancellationToken.None));
            Assert.Contains("할당량", error.Message);
            Assert.Empty(_waits);
            Assert.Empty(server.Ranges);
        }

        [Fact]
        public async Task Upload_ApiNotEnabled_And_NoChannel_AreExplained()
        {
            var path = MakeFile(100);
            var disabled = new FakeYouTube { StartReply = () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"code\":403,\"message\":\"YouTube Data API v3 has not been used in project 1 before or it is disabled.\",\"status\":\"PERMISSION_DENIED\","
                                            + "\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"SERVICE_DISABLED\"}]}}")
            } };
            Assert.Contains("YouTube Data API v3를 켜지 않았습니다", (await Assert.ThrowsAsync<YouTubeException>(() => Uploader(disabled).Upload(path, Video(), null, CancellationToken.None))).Message);
            var noChannel = new FakeYouTube { StartReply = () => Error(HttpStatusCode.Unauthorized, "youtubeSignupRequired", "Unauthorized") };
            Assert.Contains("채널이 없습니다", (await Assert.ThrowsAsync<YouTubeException>(() => Uploader(noChannel).Upload(path, Video(), null, CancellationToken.None))).Message);
        }

        [Fact]
        public async Task Upload_SessionGone_AsksToStartOver()
        {
            var path = MakeFile(2 * Unit);
            var server = new FakeYouTube { Fail = i => i == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : null };
            var error = await Assert.ThrowsAsync<YouTubeException>(() => Uploader(server).Upload(path, Video(), null, CancellationToken.None));
            Assert.Contains("처음부터 다시", error.Message);
        }

        [Fact]
        public async Task Upload_TooManyFailuresWithoutProgress_Stops()
        {
            var path = MakeFile(2 * Unit);
            var server = new FakeYouTube { Keep = (i, length) => 0, Fail = i => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
            var uploader = Uploader(server);
            uploader.MaxRetries = 3;
            var error = await Assert.ThrowsAsync<YouTubeException>(() => uploader.Upload(path, Video(), null, CancellationToken.None));
            Assert.Contains("거듭 끊겨", error.Message);
            Assert.Equal(new[] { 1, 2, 4 }, _waits.Select(w => (int)w.TotalSeconds));
        }

        [Fact]
        public async Task Upload_Cancel_StopsSending()
        {
            var path = MakeFile(4 * Unit);
            var server = new FakeYouTube();
            using (var cancel = new CancellationTokenSource())
            {
                var progress = new SyncProgress<YouTubeUploader.Status>(s =>
                {
                    if (s.Sent > 0)
                        cancel.Cancel();
                });
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Uploader(server).Upload(path, Video(), progress, cancel.Token));
            }
            Assert.True(server.Received.Length < 4 * Unit);
            Assert.Empty(_waits);
        }

        [Fact]
        public async Task Upload_ErrorMessages_DoNotContainAccessToken()
        {
            const string token = "ya29.a0-secret-access-token";
            var path = MakeFile(100);
            var server = new FakeYouTube { Token = token, StartReply = () => Error(HttpStatusCode.BadRequest, "badRequest", "Bad header Authorization: Bearer " + token) };
            var error = await Assert.ThrowsAsync<YouTubeException>(() => Uploader(server).Upload(path, Video(), null, CancellationToken.None));
            Assert.Contains("HTTP 400", error.Message);
            Assert.DoesNotContain(token, error.Message);

            var notes = new List<string>();
            var dropping = new FakeYouTube { Token = token, Keep = (i, length) => 0, Drop = i => new HttpRequestException("proxy said: " + token) };
            var uploader = Uploader(dropping);
            uploader.MaxRetries = 1;
            var dropped = await Assert.ThrowsAsync<YouTubeException>(() => uploader.Upload(path, Video(), new SyncProgress<YouTubeUploader.Status>(s => notes.Add(s.Note ?? "")), CancellationToken.None));
            Assert.DoesNotContain(token, dropped.Message);
            Assert.All(notes, n => Assert.DoesNotContain(token, n));
        }

        [Fact]
        public async Task Upload_InvalidVideo_IsRejectedBeforeAnyRequest()
        {
            var path = MakeFile(100);
            var server = new FakeYouTube();
            var video = Video();
            video.MadeForKids = null;
            var error = await Assert.ThrowsAsync<YouTubeException>(() => Uploader(server).Upload(path, video, null, CancellationToken.None));
            Assert.Contains("아동용", error.Message);
            Assert.Null(server.Start);
            var empty = MakeFile(0, "빈.mp4");
            await Assert.ThrowsAsync<YouTubeException>(() => Uploader(server).Upload(empty, Video(), null, CancellationToken.None));
            Assert.Null(server.Start);
        }

        [Fact]
        public void Video_Problem_FollowsYouTubeLimits()
        {
            Assert.Null(Video().Problem());
            Assert.Contains("제목", new YouTubeVideo { Title = " ", MadeForKids = false }.Problem());
            Assert.Null(new YouTubeVideo { Title = new string('가', 100), MadeForKids = true }.Problem());
            Assert.Contains("100자", new YouTubeVideo { Title = new string('가', 101), MadeForKids = true }.Problem());
            Assert.Contains("< >", new YouTubeVideo { Title = "a<b", MadeForKids = true }.Problem());
            Assert.Contains("< >", new YouTubeVideo { Title = "a", Description = "x>y", MadeForKids = true }.Problem());
            // 설명은 바이트로 센다(한글 3바이트)
            Assert.Null(new YouTubeVideo { Title = "a", Description = new string('가', 1666), MadeForKids = true }.Problem());
            Assert.Contains("5000바이트", new YouTubeVideo { Title = "a", Description = new string('가', 1667), MadeForKids = true }.Problem());
            Assert.Contains("공개 범위", new YouTubeVideo { Title = "a", Privacy = "everyone", MadeForKids = true }.Problem());
            Assert.Equal("private", new YouTubeVideo().Privacy);
        }

        [Fact]
        public void Received_ReadsRangeOfResumeResponse()
        {
            var response = new HttpResponseMessage((HttpStatusCode)308);
            Assert.Equal(0, YouTubeUploader.Received(response));
            response.Headers.Add("Range", "bytes=0-524287");
            Assert.Equal(524288, YouTubeUploader.Received(response));
            var broken = new HttpResponseMessage((HttpStatusCode)308);
            broken.Headers.TryAddWithoutValidation("Range", "bytes=0-x");
            Assert.Equal(0, YouTubeUploader.Received(broken));
        }

        [Fact]
        public async Task Received_RealHttpStack_Reads308RangeWithoutFollowingIt()
        {
            // 가짜 처리기가 아닌 .NET HTTP 스택이 실제 응답을 읽을 때도 308을 리디렉션으로 다루지 않고 Range를 보여 주는지(127.0.0.1의 작은 서버)
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var serving = Task.Run(async () =>
                {
                    using (var client = await listener.AcceptTcpClientAsync())
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var head = "";
                        while (!head.Contains("\r\n\r\n"))
                            head += Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer, 0, buffer.Length));
                        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 308 Resume Incomplete\r\nRange: bytes=0-262143\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(reply, 0, reply.Length);
                    }
                });
                using (var http = YouTubeAuth.NewHttp(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
                using (var request = new HttpRequestMessage(HttpMethod.Put, "http://127.0.0.1:" + port + "/upload") { Content = new ByteArrayContent(Array.Empty<byte>()) })
                {
                    request.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1000);
                    using (var response = await http.SendAsync(request))
                    {
                        Assert.Equal(308, (int)response.StatusCode);
                        Assert.Equal(262144, YouTubeUploader.Received(response));
                    }
                }
                await serving;
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void Backoff_DoublesUpToAMinute()
        {
            Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 60, 60 }, Enumerable.Range(1, 8).Select(n => (int)YouTubeUploader.Backoff(n).TotalSeconds));
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string reason, string message)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\":{\"code\":" + (int)status + ",\"message\":" + JsonSerializer.Serialize(message) + ",\"errors\":[{\"domain\":\"youtube\",\"reason\":\"" + reason + "\"}]}}",
                                            Encoding.UTF8, "application/json")
            };
        }

        /// <summary>
        /// 재개 가능 업로드를 흉내 내는 가짜 유튜브: 세션을 열고 받은 바이트를 모은다. 조각마다(0부터) 끼어들어 일부만 받기(Keep)·
        /// 다른 응답(Fail)·연결 끊김(Drop)·답 없음(Stall)을 만든다. 조각은 서버가 받은 위치부터 와야 한다.
        /// </summary>
        private sealed class FakeYouTube : HttpMessageHandler
        {
            public const string Session = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status&upload_id=xyz";

            public MemoryStream Received { get; } = new MemoryStream();
            public string Token { get; set; } = "access-1";
            public HttpRequestMessage Start { get; private set; }
            public string Metadata { get; private set; }
            public List<string> Ranges { get; } = new List<string>();
            public int Queries { get; private set; }
            public Func<HttpResponseMessage> StartReply { get; set; }
            public Func<int, int, int> Keep { get; set; }
            public Func<int, HttpResponseMessage> Fail { get; set; }
            public Func<int, Exception> Drop { get; set; }
            public Func<int, bool> Stall { get; set; }
            public Action<int> After { get; set; }
            private long _total;
            private int _chunks;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Assert.Contains("Folderss-video-editor", request.Headers.UserAgent.ToString());
                if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != Token)
                    return Error(HttpStatusCode.Unauthorized, "authError", "Invalid Credentials");
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal(YouTubeUploader.UploadEndpoint, request.RequestUri.AbsoluteUri);
                    var reply = StartReply?.Invoke();
                    if (reply != null)
                        return reply;
                    Start = request;
                    Metadata = Encoding.UTF8.GetString(body);
                    _total = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single());
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.Location = new Uri(Session);
                    return response;
                }
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(Session, request.RequestUri.AbsoluteUri);
                var range = request.Content.Headers.ContentRange;
                Ranges.Add(range.ToString());
                Assert.Equal(_total, range.Length);
                if (!range.HasRange)
                {
                    Queries++;
                    Assert.Empty(body);
                    return Progress();
                }
                var index = _chunks++;
                Assert.Equal(Received.Length, range.From);
                Assert.Equal(range.To - range.From + 1, body.Length);
                Assert.True(range.To + 1 == _total || body.Length % Unit == 0, "마지막이 아닌 조각은 256KiB의 배수");
                if (Stall?.Invoke(index) == true)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                Received.Write(body, 0, Keep?.Invoke(index, body.Length) ?? body.Length);
                var drop = Drop?.Invoke(index);
                if (drop != null)
                    throw drop;
                After?.Invoke(index);
                return Fail?.Invoke(index) ?? Progress();
            }

            private HttpResponseMessage Progress()
            {
                if (Received.Length == _total)
                {
                    return new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent("{\"kind\":\"youtube#video\",\"id\":\"vid123\",\"snippet\":{\"title\":\"첫 영상\",\"channelTitle\":\"내 채널\"},"
                                                    + "\"status\":{\"uploadStatus\":\"uploaded\",\"privacyStatus\":\"private\"}}", Encoding.UTF8, "application/json")
                    };
                }
                var response = new HttpResponseMessage((HttpStatusCode)308);
                if (Received.Length > 0)
                    response.Headers.Add("Range", "bytes=0-" + (Received.Length - 1));
                return response;
            }
        }
    }
}
