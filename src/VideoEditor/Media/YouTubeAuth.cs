using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Media
{
    /// <summary>유튜브 올리기에서 사용자에게 보일 오류. 메시지에는 토큰·클라이언트 비밀·인증 코드를 넣지 않는다.</summary>
    internal sealed class YouTubeException : Exception
    {
        public YouTubeException(string message, bool needsSignIn = false) : base(message)
        {
            NeedsSignIn = needsSignIn;
        }

        /// <summary>다시 로그인해야 풀리는 오류(로그인 안 함, 새로 고침 토큰 만료·취소, 권한 부족).</summary>
        public bool NeedsSignIn { get; }
    }

    /// <summary>
    /// 사용자가 자기 Google Cloud 프로젝트에서 만든 OAuth 클라이언트(데스크톱 앱 유형). 플러그인에는 클라이언트 ID·비밀이 들어 있지 않다 —
    /// 할당량과 앱 검증은 그 프로젝트 몫이다. 설치형 앱의 비밀은 구글도 진짜 비밀로 보지 않지만 그래도 기록·오류에 남기지 않는다.
    /// </summary>
    internal sealed class YouTubeClient
    {
        public const string IdSuffix = ".apps.googleusercontent.com";

        public YouTubeClient(string id, string secret)
        {
            Id = (id ?? "").Trim();
            Secret = (secret ?? "").Trim();
        }

        public string Id { get; }
        public string Secret { get; }

        public bool IsComplete { get { return Id.Length > 0 && Secret.Length > 0; } }

        /// <summary>입력이 쓸 만한지 본다. 맞으면 null, 아니면 알릴 글.</summary>
        public string Problem()
        {
            if (Id.Length == 0)
                return "클라이언트 ID를 넣으세요.";
            if (!Id.EndsWith(IdSuffix, StringComparison.Ordinal) || Id.Length == IdSuffix.Length || Id.Any(char.IsWhiteSpace))
                return "클라이언트 ID는 \"…" + IdSuffix + "\"로 끝나는 값입니다.";
            if (Secret.Length == 0)
                return "클라이언트 보안 비밀번호를 넣으세요.";
            return null;
        }

        /// <summary>
        /// Google Cloud 콘솔에서 내려받은 클라이언트 JSON({"installed":{"client_id","client_secret"}})을 읽는다.
        /// 웹 애플리케이션 유형({"web":…})은 127.0.0.1 리디렉션을 쓸 수 없어 거절한다.
        /// </summary>
        public static YouTubeClient FromJson(string json)
        {
            try
            {
                using (var document = JsonDocument.Parse(json ?? ""))
                {
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.Object)
                            return new YouTubeClient(YouTubeAuth.Text(installed, "client_id"), YouTubeAuth.Text(installed, "client_secret"));
                        if (root.TryGetProperty("web", out _))
                            throw new YouTubeException("웹 애플리케이션 유형의 클라이언트입니다. 유형을 \"데스크톱 앱\"으로 새로 만드세요(설정 방법 안내 4단계).");
                    }
                }
            }
            catch (JsonException)
            {
            }
            throw new YouTubeException("Google Cloud 콘솔에서 내려받은 OAuth 클라이언트 JSON 파일이 아닙니다.");
        }
    }

    /// <summary>토큰 응답. 접근 토큰은 메모리에만 두고, 새로 고침 토큰만 암호화해 저장한다.</summary>
    internal sealed class OAuthTokens
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        /// <summary>접근 토큰을 쓸 수 있는 시간(expires_in, 보통 1시간).</summary>
        public TimeSpan ExpiresIn { get; set; }
        public string Scope { get; set; }
    }

    /// <summary>
    /// 설치형 앱 OAuth(RFC 8252): 시스템 브라우저로 구글 로그인 → 127.0.0.1의 빈 포트로 돌아온 인증 코드를 PKCE(S256) 검증값과 함께 토큰으로 바꾼다.
    /// state로 이 창이 보낸 요청의 응답인지 확인한다. 범위는 youtube.upload 하나(올리기만 — 재생목록·채널 정보는 읽지도 바꾸지도 않음).
    /// HTTP는 본체와 같은 프록시 방식(HostNetwork).
    /// </summary>
    internal static class YouTubeAuth
    {
        public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
        public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
        public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        /// <summary>유튜브용 HttpClient. 리디렉션은 따라가지 않는다 — 재개 업로드의 308(이어 보낼 위치)을 리디렉션으로 다루지 않게.</summary>
        public static HttpClient NewHttp(HttpMessageHandler handler = null)
        {
            var http = handler == null ? new HttpClient(HostNetwork.Handler(false)) : new HttpClient(handler, false);
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(GitHubDownload.UserAgent);
            return http;
        }

        /// <summary>PKCE 검증값: 32바이트 난수의 base64url(43자, RFC 7636이 정한 글자만).</summary>
        public static string NewVerifier()
        {
            return Base64Url(RandomNumberGenerator.GetBytes(32));
        }

        public static string NewState()
        {
            return Base64Url(RandomNumberGenerator.GetBytes(16));
        }

        /// <summary>S256 챌린지 = base64url(SHA-256(검증값)).</summary>
        public static string Challenge(string verifier)
        {
            return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        }

        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static Uri AuthorizationUrl(string clientId, Uri redirect, string challenge, string state)
        {
            var query = new[]
            {
                ("client_id", clientId),
                ("redirect_uri", redirect.AbsoluteUri),
                ("response_type", "code"),
                ("scope", UploadScope),
                ("code_challenge", challenge),
                ("code_challenge_method", "S256"),
                ("state", state),
                // 새로 고침 토큰을 받으려면 offline, 다시 로그인할 때도 새로 고침 토큰을 주게 consent
                ("access_type", "offline"),
                ("prompt", "consent")
            };
            return new Uri(AuthorizeEndpoint + "?" + string.Join("&", query.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2))));
        }

        /// <summary>
        /// 로그인: 127.0.0.1의 빈 포트에서 기다리며 openBrowser로 구글 로그인 주소를 연다. 돌아온 코드를 토큰으로 바꿔 돌려준다.
        /// timeout(기본 5분) 안에 돌아오지 않으면 YouTubeException.
        /// </summary>
        public static async Task<OAuthTokens> SignIn(HttpClient http, YouTubeClient client, Action<Uri> openBrowser, CancellationToken cancellation, TimeSpan? timeout = null)
        {
            var verifier = NewVerifier();
            var state = NewState();
            using (var receiver = new LoopbackReceiver())
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var limit = timeout ?? SignInTimeout;
                timer.CancelAfter(limit);
                var waiting = receiver.WaitForCode(state, timer.Token);
                string code;
                try
                {
                    openBrowser(AuthorizationUrl(client.Id, receiver.RedirectUri, Challenge(verifier), state));
                    code = await waiting.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new YouTubeException("로그인을 " + Math.Max(1, (int)Math.Round(limit.TotalMinutes)) + "분 안에 마치지 않아 멈췄습니다. 다시 해 보세요.");
                }
                finally
                {
                    // 브라우저를 열지 못하는 등으로 빠져나가면 기다리기도 끝낸다
                    timer.Cancel();
                }
                var tokens = await ExchangeCode(http, client, code, verifier, receiver.RedirectUri, cancellation).ConfigureAwait(false);
                if (string.IsNullOrEmpty(tokens.RefreshToken))
                    throw new YouTubeException("구글이 새로 고침 토큰을 주지 않았습니다. 다시 로그인하세요.");
                return tokens;
            }
        }

        /// <summary>인증 코드 → 토큰(PKCE 검증값과 같은 리디렉션 주소를 함께 보냄).</summary>
        public static Task<OAuthTokens> ExchangeCode(HttpClient http, YouTubeClient client, string code, string verifier, Uri redirect, CancellationToken cancellation)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirect.AbsoluteUri,
                ["client_id"] = client.Id,
                ["client_secret"] = client.Secret
            };
            return PostToken(http, form, cancellation, client.Secret, code, verifier);
        }

        /// <summary>새로 고침 토큰으로 접근 토큰을 새로 받는다. 만료·취소된 새로 고침 토큰이면 NeedsSignIn인 YouTubeException.</summary>
        public static Task<OAuthTokens> Refresh(HttpClient http, YouTubeClient client, string refreshToken, CancellationToken cancellation)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = client.Id,
                ["client_secret"] = client.Secret
            };
            return PostToken(http, form, cancellation, client.Secret, refreshToken);
        }

        /// <summary>토큰을 구글에서 취소한다(로그아웃). 취소됐거나 이미 쓸 수 없는 토큰(400)이면 true.</summary>
        public static async Task<bool> Revoke(HttpClient http, string token, CancellationToken cancellation)
        {
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }))
            {
                timer.CancelAfter(RequestTimeout);
                using (var response = await http.PostAsync(RevokeEndpoint, content, timer.Token).ConfigureAwait(false))
                    return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest;
            }
        }

        private static async Task<OAuthTokens> PostToken(HttpClient http, Dictionary<string, string> form, CancellationToken cancellation, params string[] secrets)
        {
            string body;
            HttpStatusCode status;
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var content = new FormUrlEncodedContent(form))
            {
                timer.CancelAfter(RequestTimeout);
                try
                {
                    using (var response = await http.PostAsync(TokenEndpoint, content, timer.Token).ConfigureAwait(false))
                    {
                        status = response.StatusCode;
                        body = await response.Content.ReadAsStringAsync(timer.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new HttpRequestException("구글 로그인 서버가 " + RequestTimeout.TotalSeconds + "초 안에 답하지 않았습니다.");
                }
            }
            if ((int)status >= 400)
                throw TokenError((int)status, body, secrets);
            try
            {
                using (var document = JsonDocument.Parse(body))
                {
                    var root = document.RootElement;
                    var tokens = new OAuthTokens
                    {
                        AccessToken = Text(root, "access_token"),
                        RefreshToken = Text(root, "refresh_token"),
                        Scope = Text(root, "scope"),
                        ExpiresIn = TimeSpan.FromSeconds(root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600)
                    };
                    if (string.IsNullOrEmpty(tokens.AccessToken))
                        throw new YouTubeException("구글 로그인 서버의 답에 접근 토큰이 없습니다.");
                    // 구글 동의 화면에서 권한 칸을 끄고 허락하면 범위가 빠진 토큰이 온다
                    if (tokens.Scope != null && !tokens.Scope.Split(' ').Contains(UploadScope))
                        throw new YouTubeException("유튜브 동영상 올리기 권한을 허락하지 않았습니다. 다시 로그인해 권한 칸을 체크하세요.", true);
                    return tokens;
                }
            }
            catch (JsonException)
            {
                throw new YouTubeException("구글 로그인 서버의 답을 읽지 못했습니다(HTTP " + (int)status + ").");
            }
        }

        /// <summary>토큰 엔드포인트 오류({"error","error_description"})를 알릴 글로. 서버가 보낸 설명에 비밀이 섞여 있어도 지운다.</summary>
        private static YouTubeException TokenError(int status, string body, string[] secrets)
        {
            string error = null, description = null;
            try
            {
                using (var document = JsonDocument.Parse(body))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        error = Text(document.RootElement, "error");
                        description = Text(document.RootElement, "error_description");
                    }
                }
            }
            catch (JsonException)
            {
            }
            switch (error)
            {
                case "invalid_grant":
                    return new YouTubeException("로그인이 만료됐거나 취소됐습니다. 다시 로그인하세요(테스트 모드 앱은 7일마다 다시 로그인해야 합니다).", true);
                case "invalid_client":
                case "unauthorized_client":
                    return new YouTubeException("클라이언트 ID나 보안 비밀번호가 맞지 않습니다. 설정의 값을 확인하세요(유형이 \"데스크톱 앱\"인 클라이언트여야 합니다).");
                default:
                    var detail = Clip(Redact(description, secrets));
                    return new YouTubeException("구글 로그인이 실패했습니다: HTTP " + status + (error != null ? " " + Clip(Redact(error, secrets)) : "") + (detail.Length > 0 ? " — " + detail : ""));
            }
        }

        /// <summary>글에서 비밀(토큰·클라이언트 비밀·인증 코드)을 지운다 — 서버가 받은 값을 그대로 돌려줘도 오류 메시지에 남지 않게.</summary>
        public static string Redact(string text, params string[] secrets)
        {
            text = text ?? "";
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                    text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
            return text;
        }

        public static string Clip(string text, int length = 300)
        {
            text = (text ?? "").Trim();
            return text.Length <= length ? text : text.Substring(0, length) + "…";
        }

        public static string Text(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
    }

    /// <summary>
    /// 로그인 리디렉션을 받는 곳: 127.0.0.1(이 PC 안에서만)의 빈 포트. 브라우저가 "GET /?state=…&amp;code=…"로 돌아오면 코드를 넘기고 안내 쪽을 보인다.
    /// state가 다른 요청은 무시하고 계속 기다린다(다른 곳이 보낸 요청으로 로그인을 끝내지 않게). 연결마다 따로 읽어, 브라우저가 미리 열어 둔 빈 연결이 막지 않는다.
    /// </summary>
    internal sealed class LoopbackReceiver : IDisposable
    {
        private const int MaxRequest = 16 * 1024;
        private readonly TcpListener _listener;

        public LoopbackReceiver()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            RedirectUri = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
        }

        /// <summary>http://127.0.0.1:포트/ — 구글 로그인 주소와 토큰 교환에 같은 값을 쓴다.</summary>
        public Uri RedirectUri { get; }

        public void Dispose()
        {
            _listener.Stop();
        }

        /// <summary>state가 맞는 리디렉션의 인증 코드. 사용자가 거부했으면(error=…) YouTubeException.</summary>
        public async Task<string> WaitForCode(string state, CancellationToken cancellation)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellation.Register(() => result.TrySetCanceled(cancellation)))
            {
                _ = Accept(state, result);
                try
                {
                    return await result.Task.ConfigureAwait(false);
                }
                finally
                {
                    _listener.Stop();
                }
            }
        }

        private async Task Accept(string state, TaskCompletionSource<string> result)
        {
            while (!result.Task.IsCompleted)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 멈춘 리스너(끝남·취소)
                    return;
                }
                _ = Handle(client, state, result);
            }
        }

        private static async Task Handle(TcpClient client, string state, TaskCompletionSource<string> result)
        {
            using (client)
            using (var timer = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try
                {
                    var stream = client.GetStream();
                    var target = await ReadTarget(stream, timer.Token).ConfigureAwait(false);
                    if (target == null)
                        return;
                    var question = target.IndexOf('?');
                    var path = question < 0 ? target : target.Substring(0, question);
                    var query = System.Web.HttpUtility.ParseQueryString(question < 0 ? "" : target.Substring(question + 1));
                    if (path != "/")
                    {
                        await Respond(stream, 404, "Not Found", "찾을 수 없습니다.", timer.Token).ConfigureAwait(false);
                        return;
                    }
                    if (!string.Equals(query["state"], state, StringComparison.Ordinal))
                    {
                        await Respond(stream, 400, "Bad Request", "이 로그인 응답은 동영상 편집이 보낸 요청의 것이 아닙니다. 동영상 편집에서 다시 로그인하세요.", timer.Token).ConfigureAwait(false);
                        return;
                    }
                    var error = query["error"];
                    var code = query["code"];
                    if (!string.IsNullOrEmpty(error))
                    {
                        await Respond(stream, 200, "OK", "로그인하지 않았습니다. 동영상 편집으로 돌아가세요.", timer.Token).ConfigureAwait(false);
                        result.TrySetException(new YouTubeException(error == "access_denied"
                            ? "구글 로그인에서 허락하지 않았습니다. 테스트 사용자로 추가하지 않은 계정이면 구글이 막습니다(설정 방법 안내 3단계)."
                            : "구글 로그인이 실패했습니다: " + YouTubeAuth.Clip(error, 80)));
                        return;
                    }
                    if (string.IsNullOrEmpty(code))
                    {
                        await Respond(stream, 400, "Bad Request", "인증 코드가 없습니다.", timer.Token).ConfigureAwait(false);
                        return;
                    }
                    await Respond(stream, 200, "OK", "로그인했습니다. 이 창을 닫고 동영상 편집으로 돌아가세요.", timer.Token).ConfigureAwait(false);
                    result.TrySetResult(code);
                }
                catch (Exception)
                {
                    // 브라우저가 연결을 끊었거나 이상한 요청 — 다음 연결을 기다린다
                }
            }
        }

        /// <summary>요청 줄 "GET 대상 HTTP/1.1"의 대상(GET이 아니거나 너무 길면 null). 머리글 끝(빈 줄)까지 읽는다.</summary>
        private static async Task<string> ReadTarget(NetworkStream stream, CancellationToken cancellation)
        {
            var buffer = new byte[MaxRequest];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellation).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }
            var head = Encoding.ASCII.GetString(buffer, 0, length);
            var end = head.IndexOf("\r\n", StringComparison.Ordinal);
            var parts = (end < 0 ? head : head.Substring(0, end)).Split(' ');
            return parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/", StringComparison.Ordinal) ? parts[1] : null;
        }

        private static async Task Respond(NetworkStream stream, int status, string reason, string message, CancellationToken cancellation)
        {
            var html = "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>Folderss 동영상 편집</title></head>"
                       + "<body style=\"font-family:sans-serif;margin:3em\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length
                                               + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellation).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
    }
}
