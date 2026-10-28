using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Media;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>
    /// 유튜브 로그인(#9): PKCE·state·로그인 주소, 127.0.0.1 리디렉션 받기, 토큰 교환·새로 고침·만료, 로그아웃, 토큰 저장(DPAPI), 오류 메시지에 비밀 없음.
    /// 구글 서버는 가짜 HttpMessageHandler, 브라우저는 127.0.0.1로 직접 GET을 보내는 것으로 대신한다(실제 네트워크·계정은 쓰지 않음).
    /// </summary>
    public class YouTubeAuthTests : IDisposable
    {
        private const string ClientId = "1234-test.apps.googleusercontent.com";
        private const string ClientSecret = "GOCSPX-test-client-secret";
        private const string RefreshToken = "1//refresh-token-secret";
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "fv-yt-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        /// <summary>시험용 암호화: 바이트를 뒤집고 XOR(평문이 파일에 그대로 남지 않는지 볼 수 있게). Linux CI에는 DPAPI가 없다.</summary>
        private static byte[] Scramble(byte[] data)
        {
            return data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();
        }

        private YouTubeTokenStore Store()
        {
            return new YouTubeTokenStore(_folder, Scramble, Scramble);
        }

        private static readonly YouTubeClient Client = new YouTubeClient(ClientId, ClientSecret);

        private static HttpResponseMessage Json(HttpStatusCode status, string json)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static string TokenJson(string access, string refresh = null, int expires = 3599, string scope = YouTubeAuth.UploadScope)
        {
            return "{\"access_token\":\"" + access + "\",\"expires_in\":" + expires + ",\"scope\":\"" + scope + "\",\"token_type\":\"Bearer\""
                   + (refresh != null ? ",\"refresh_token\":\"" + refresh + "\"" : "") + "}";
        }

        /// <summary>브라우저 대신: 프록시 없이 127.0.0.1로 GET.</summary>
        private static async Task<(HttpStatusCode Status, string Body)> Browse(Uri url)
        {
            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }))
            using (var response = await http.GetAsync(url))
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static Dictionary<string, string> Query(Uri url)
        {
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            return query.AllKeys.ToDictionary(k => k, k => query[k]);
        }

        // ---------- PKCE · state · 로그인 주소 ----------

        [Fact]
        public void Pkce_Challenge_MatchesRfc7636Example()
        {
            // RFC 7636 부록 B
            Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", YouTubeAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        }

        [Fact]
        public void Pkce_VerifierAndState_AreRandomAndUrlSafe()
        {
            var verifiers = Enumerable.Range(0, 20).Select(_ => YouTubeAuth.NewVerifier()).ToList();
            Assert.All(verifiers, v => Assert.Matches(new Regex("^[A-Za-z0-9._~-]{43,128}$"), v));
            Assert.Equal(verifiers.Count, verifiers.Distinct().Count());
            var states = Enumerable.Range(0, 20).Select(_ => YouTubeAuth.NewState()).ToList();
            Assert.All(states, s => Assert.Matches(new Regex("^[A-Za-z0-9_-]{22,}$"), s));
            Assert.Equal(states.Count, states.Distinct().Count());
        }

        [Fact]
        public void AuthorizationUrl_UsesLoopbackPkceStateAndOnlyUploadScope()
        {
            var url = YouTubeAuth.AuthorizationUrl(ClientId, new Uri("http://127.0.0.1:50123/"), "challenge-value", "state-value");
            Assert.Equal("https", url.Scheme);
            Assert.Equal("accounts.google.com", url.Host);
            Assert.Equal("/o/oauth2/v2/auth", url.AbsolutePath);
            var query = Query(url);
            Assert.Equal(ClientId, query["client_id"]);
            Assert.Equal("http://127.0.0.1:50123/", query["redirect_uri"]);
            Assert.Equal("code", query["response_type"]);
            Assert.Equal("https://www.googleapis.com/auth/youtube.upload", query["scope"]);
            Assert.Equal("challenge-value", query["code_challenge"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("state-value", query["state"]);
            Assert.Equal("offline", query["access_type"]);
            Assert.Equal("consent", query["prompt"]);
            // 비밀은 주소에 싣지 않는다
            Assert.DoesNotContain(ClientSecret, url.AbsoluteUri);
            Assert.DoesNotContain("client_secret", url.AbsoluteUri);
        }

        // ---------- 127.0.0.1 리디렉션 ----------

        [Fact]
        public async Task Loopback_ListensOn127001_IgnoresWrongState_ReturnsCode()
        {
            using (var receiver = new LoopbackReceiver())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                Assert.Equal("127.0.0.1", receiver.RedirectUri.Host);
                Assert.True(receiver.RedirectUri.Port > 0);
                Assert.Equal("/", receiver.RedirectUri.AbsolutePath);
                var waiting = receiver.WaitForCode("good-state", timeout.Token);

                var favicon = await Browse(new Uri(receiver.RedirectUri, "/favicon.ico"));
                Assert.Equal(HttpStatusCode.NotFound, favicon.Status);
                // state가 다르면(다른 곳이 보낸 응답) 거절하고 계속 기다린다
                var forged = await Browse(new Uri(receiver.RedirectUri, "/?state=bad-state&code=evil"));
                Assert.Equal(HttpStatusCode.BadRequest, forged.Status);
                var missing = await Browse(new Uri(receiver.RedirectUri, "/?code=evil"));
                Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
                Assert.False(waiting.IsCompleted);

                var ok = await Browse(new Uri(receiver.RedirectUri, "/?state=good-state&code=4%2F0Abc-code&scope=" + Uri.EscapeDataString(YouTubeAuth.UploadScope)));
                Assert.Equal(HttpStatusCode.OK, ok.Status);
                Assert.Contains("로그인했습니다", ok.Body);
                Assert.DoesNotContain("4/0Abc-code", ok.Body);
                Assert.Equal("4/0Abc-code", await waiting);
            }
        }

        [Fact]
        public async Task Loopback_IdleConnection_DoesNotBlockRedirect()
        {
            // 브라우저가 미리 열어 두고 아무것도 보내지 않는 연결이 있어도 진짜 리디렉션을 받는다
            using (var receiver = new LoopbackReceiver())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var idle = new TcpClient())
            {
                var waiting = receiver.WaitForCode("s", timeout.Token);
                await idle.ConnectAsync(IPAddress.Loopback, receiver.RedirectUri.Port);
                await Browse(new Uri(receiver.RedirectUri, "/?state=s&code=c1"));
                Assert.Equal("c1", await waiting);
            }
        }

        [Fact]
        public async Task Loopback_Denied_ThrowsWithTestUserHint()
        {
            using (var receiver = new LoopbackReceiver())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var waiting = receiver.WaitForCode("s", timeout.Token);
                var page = await Browse(new Uri(receiver.RedirectUri, "/?state=s&error=access_denied"));
                Assert.Equal(HttpStatusCode.OK, page.Status);
                var error = await Assert.ThrowsAsync<YouTubeException>(() => waiting);
                Assert.Contains("테스트 사용자", error.Message);
            }
        }

        [Fact]
        public async Task SignIn_TimesOut_WhenBrowserNeverReturns()
        {
            using (var http = YouTubeAuth.NewHttp(new FakeHttp((r, b) => throw new InvalidOperationException("토큰 교환까지 가면 안 됨"))))
            {
                var error = await Assert.ThrowsAsync<YouTubeException>(() => YouTubeAuth.SignIn(http, Client, url => { }, CancellationToken.None, TimeSpan.FromMilliseconds(300)));
                Assert.Contains("안에 마치지 않아", error.Message);
            }
        }

        // ---------- 로그인 전체 흐름 ----------

        [Fact]
        public async Task SignIn_EndToEnd_ExchangesCodeWithVerifier_AndStoresRefreshTokenEncrypted()
        {
            Dictionary<string, string> form = null;
            Uri opened = null;
            var google = new FakeHttp((request, body) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(YouTubeAuth.TokenEndpoint, request.RequestUri.AbsoluteUri);
                Assert.Contains("Folderss-video-editor", request.Headers.UserAgent.ToString());
                form = FakeHttp.Form(body);
                return Json(HttpStatusCode.OK, TokenJson("access-1", RefreshToken));
            });
            var account = new YouTubeAccount(() => Client, Store(), google);
            Assert.Null(account.SignedInAt);

            await account.SignIn(url =>
            {
                // 브라우저: 구글이 로그인 뒤 리디렉션 주소로 코드와 state를 돌려준다
                opened = url;
                var query = Query(url);
                _ = Browse(new Uri(query["redirect_uri"] + "?state=" + Uri.EscapeDataString(query["state"]) + "&code=the-code"));
            }, CancellationToken.None, TimeSpan.FromSeconds(20));

            var sent = Query(opened);
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("the-code", form["code"]);
            Assert.Equal(sent["redirect_uri"], form["redirect_uri"]);
            Assert.Equal(ClientId, form["client_id"]);
            Assert.Equal(ClientSecret, form["client_secret"]);
            // 보낸 검증값이 주소에 실은 S256 챌린지와 맞아야 한다
            Assert.Equal(sent["code_challenge"], YouTubeAuth.Challenge(form["code_verifier"]));
            Assert.NotNull(account.SignedInAt);
            Assert.Equal("access-1", await account.AccessToken(false, CancellationToken.None));
            // 새로 고침 토큰은 암호화해 저장 — 파일에 평문으로 남지 않는다
            var file = File.ReadAllBytes(Path.Combine(_folder, "token.dat"));
            Assert.DoesNotContain(RefreshToken, Encoding.UTF8.GetString(file));
            Assert.Equal(RefreshToken, Store().Load(ClientId).RefreshToken);
            Assert.Single(google.Requests);
        }

        [Fact]
        public async Task ExchangeCode_WithoutUploadScope_AsksToSignInAgain()
        {
            // 동의 화면에서 권한 칸을 끄고 허락하면 범위가 빠진 토큰이 온다
            var google = new FakeHttp((r, b) => Json(HttpStatusCode.OK, TokenJson("a", "r", scope: "openid")));
            using (var http = YouTubeAuth.NewHttp(google))
            {
                var error = await Assert.ThrowsAsync<YouTubeException>(() => YouTubeAuth.ExchangeCode(http, Client, "code", "verifier", new Uri("http://127.0.0.1:1/"), CancellationToken.None));
                Assert.True(error.NeedsSignIn);
                Assert.Contains("권한", error.Message);
            }
        }

        // ---------- 새로 고침 · 만료 ----------

        [Fact]
        public async Task AccessToken_IsReusedUntilNearExpiry_ThenRefreshed()
        {
            var now = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
            var issued = 0;
            var google = new FakeHttp((request, body) =>
            {
                var form = FakeHttp.Form(body);
                Assert.Equal("refresh_token", form["grant_type"]);
                Assert.Equal(RefreshToken, form["refresh_token"]);
                Assert.Equal(ClientId, form["client_id"]);
                Assert.Equal(ClientSecret, form["client_secret"]);
                issued++;
                return Json(HttpStatusCode.OK, TokenJson("access-" + issued, expires: 3600));
            });
            var store = Store();
            store.Save(ClientId, RefreshToken, now);
            var account = new YouTubeAccount(() => Client, store, google, () => now);

            Assert.Equal("access-1", await account.AccessToken(false, CancellationToken.None));
            now = now.AddMinutes(30);
            Assert.Equal("access-1", await account.AccessToken(false, CancellationToken.None));
            // 만료 2분 전부터는 새로 받는다
            now = now.AddMinutes(29);
            Assert.Equal("access-2", await account.AccessToken(false, CancellationToken.None));
            // 서버가 401로 거절하면 남은 시간과 상관없이 새로
            Assert.Equal("access-3", await account.AccessToken(true, CancellationToken.None));
            Assert.Equal(3, issued);
            // 새로 고침 토큰은 그대로
            Assert.Equal(RefreshToken, store.Load(ClientId).RefreshToken);
        }

        [Fact]
        public async Task AccessToken_ExpiredRefreshToken_DeletesItAndAsksToSignIn()
        {
            var google = new FakeHttp((r, b) => Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}"));
            var store = Store();
            store.Save(ClientId, RefreshToken, DateTimeOffset.UtcNow);
            var account = new YouTubeAccount(() => Client, store, google);
            var error = await Assert.ThrowsAsync<YouTubeException>(() => account.AccessToken(false, CancellationToken.None));
            Assert.True(error.NeedsSignIn);
            Assert.Contains("다시 로그인", error.Message);
            Assert.False(File.Exists(store.FilePath));
            Assert.Null(account.SignedInAt);
        }

        [Fact]
        public async Task AccessToken_NotSignedIn_OrOtherClient_AsksToSignIn()
        {
            var google = new FakeHttp((r, b) => throw new InvalidOperationException("요청하면 안 됨"));
            var store = Store();
            // 다른 클라이언트 ID로 받은 토큰은 쓰지 않는다(설정에서 클라이언트를 바꾼 경우)
            store.Save("other.apps.googleusercontent.com", RefreshToken, DateTimeOffset.UtcNow);
            var account = new YouTubeAccount(() => Client, store, google);
            Assert.Null(account.SignedInAt);
            var error = await Assert.ThrowsAsync<YouTubeException>(() => account.AccessToken(false, CancellationToken.None));
            Assert.True(error.NeedsSignIn);
            Assert.Empty(google.Requests);
        }

        // ---------- 로그아웃 ----------

        [Fact]
        public async Task SignOut_RevokesAndDeletes_EvenWhenRevokeFails()
        {
            var google = new FakeHttp((request, body) =>
            {
                Assert.Equal(YouTubeAuth.RevokeEndpoint, request.RequestUri.AbsoluteUri);
                Assert.Equal(RefreshToken, FakeHttp.Form(body)["token"]);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var store = Store();
            store.Save(ClientId, RefreshToken, DateTimeOffset.UtcNow);
            Assert.True(await new YouTubeAccount(() => Client, store, google).SignOut(CancellationToken.None));
            Assert.False(File.Exists(store.FilePath));
            Assert.Single(google.Requests);

            // 연결이 안 돼도 이 PC의 토큰은 지운다
            store.Save(ClientId, RefreshToken, DateTimeOffset.UtcNow);
            var offline = new FakeHttp((r, b) => throw new HttpRequestException("연결 안 됨"));
            Assert.False(await new YouTubeAccount(() => Client, store, offline).SignOut(CancellationToken.None));
            Assert.False(File.Exists(store.FilePath));
        }

        // ---------- 오류 메시지에 비밀 없음 ----------

        [Fact]
        public async Task TokenErrors_DoNotLeakSecrets()
        {
            // 서버가 받은 값을 설명에 그대로 돌려줘도 메시지에 남지 않는다
            var echo = new FakeHttp((r, body) => Json(HttpStatusCode.BadRequest,
                "{\"error\":\"invalid_request\",\"error_description\":\"bad " + ClientSecret + " and " + RefreshToken + "\"}"));
            var store = Store();
            store.Save(ClientId, RefreshToken, DateTimeOffset.UtcNow);
            var error = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeAccount(() => Client, store, echo).AccessToken(false, CancellationToken.None));
            Assert.Contains("invalid_request", error.Message);
            Assert.DoesNotContain(ClientSecret, error.Message);
            Assert.DoesNotContain(RefreshToken, error.Message);

            using (var http = YouTubeAuth.NewHttp(new FakeHttp((r, body) => Json(HttpStatusCode.BadRequest, "{\"error\":\"x\",\"error_description\":\"code the-auth-code verifier " + Encoding.UTF8.GetString(body) + "\"}"))))
            {
                var exchange = await Assert.ThrowsAsync<YouTubeException>(() => YouTubeAuth.ExchangeCode(http, Client, "the-auth-code", "verifier-value-1234", new Uri("http://127.0.0.1:1/"), CancellationToken.None));
                Assert.DoesNotContain("the-auth-code", exchange.Message);
                Assert.DoesNotContain("verifier-value-1234", exchange.Message);
                Assert.DoesNotContain(ClientSecret, exchange.Message);
            }

            var wrongClient = new FakeHttp((r, b) => Json(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\",\"error_description\":\"Unauthorized\"}"));
            var invalid = await Assert.ThrowsAsync<YouTubeException>(() => new YouTubeAccount(() => Client, store, wrongClient).AccessToken(true, CancellationToken.None));
            Assert.Contains("클라이언트 ID나 보안 비밀번호", invalid.Message);
            Assert.False(invalid.NeedsSignIn);
            // 클라이언트가 틀린 것은 로그인을 지우지 않는다(설정만 고치면 됨)
            Assert.True(File.Exists(store.FilePath));
        }

        [Fact]
        public void Redact_RemovesEverySecret()
        {
            Assert.Equal("a *** b *** c", YouTubeAuth.Redact("a s3cret-1 b tok-2222 c", "s3cret-1", "tok-2222", null, ""));
            Assert.Equal("", YouTubeAuth.Redact(null, "x"));
        }

        // ---------- 토큰 저장 · 클라이언트 ----------

        [Fact]
        public void TokenStore_RoundTrip_PerClient_AndBrokenFileIsSignedOut()
        {
            var store = Store();
            Assert.Null(store.Load(ClientId));
            var at = new DateTimeOffset(2026, 10, 28, 1, 2, 3, TimeSpan.Zero);
            store.Save(ClientId, RefreshToken, at);
            var entry = store.Load(ClientId);
            Assert.Equal(RefreshToken, entry.RefreshToken);
            Assert.Equal(at, entry.SavedAt);
            Assert.Null(store.Load("other.apps.googleusercontent.com"));
            Assert.False(File.Exists(store.FilePath + ".tmp"));

            File.WriteAllBytes(store.FilePath, new byte[] { 1, 2, 3 });
            Assert.Null(store.Load(ClientId));
            store.Delete();
            store.Delete();
            Assert.False(File.Exists(store.FilePath));

            Assert.Equal(ClientSecret, store.UnprotectText(store.ProtectText(ClientSecret)));
            Assert.DoesNotContain(ClientSecret, Encoding.UTF8.GetString(Convert.FromBase64String(store.ProtectText(ClientSecret))));
            Assert.Null(store.ProtectText(""));
            Assert.Null(store.UnprotectText("not base64 !!"));
        }

        [Fact]
        public void TokenStore_Dpapi_EncryptsForThisWindowsUser()
        {
            if (!OperatingSystem.IsWindows())
                return;
            var store = new YouTubeTokenStore(_folder);
            store.Save(ClientId, RefreshToken, DateTimeOffset.UtcNow);
            var file = File.ReadAllBytes(store.FilePath);
            Assert.DoesNotContain(RefreshToken, Encoding.UTF8.GetString(file));
            Assert.DoesNotContain(RefreshToken, Encoding.Unicode.GetString(file));
            Assert.Equal(RefreshToken, store.Load(ClientId).RefreshToken);
            // 조금이라도 바뀐 파일은 풀리지 않는다
            file[file.Length - 1] ^= 0xFF;
            File.WriteAllBytes(store.FilePath, file);
            Assert.Null(store.Load(ClientId));
            Assert.Equal(ClientSecret, store.UnprotectText(store.ProtectText(ClientSecret)));
        }

        [Fact]
        public void ClientJson_DesktopIsRead_WebAndOtherFilesAreRejected()
        {
            var client = YouTubeClient.FromJson("{\"installed\":{\"client_id\":\"" + ClientId + "\",\"project_id\":\"p\",\"auth_uri\":\"https://accounts.google.com/o/oauth2/auth\","
                                                + "\"client_secret\":\"" + ClientSecret + "\",\"redirect_uris\":[\"http://localhost\"]}}");
            Assert.Equal(ClientId, client.Id);
            Assert.Equal(ClientSecret, client.Secret);
            Assert.Null(client.Problem());
            var web = Assert.Throws<YouTubeException>(() => YouTubeClient.FromJson("{\"web\":{\"client_id\":\"" + ClientId + "\",\"client_secret\":\"s\"}}"));
            Assert.Contains("데스크톱 앱", web.Message);
            Assert.Throws<YouTubeException>(() => YouTubeClient.FromJson("not json"));
            Assert.Throws<YouTubeException>(() => YouTubeClient.FromJson("[1,2]"));
        }

        [Fact]
        public void Client_Problem_ChecksIdAndSecret()
        {
            Assert.Null(new YouTubeClient("  " + ClientId + " ", " s ").Problem());
            Assert.Equal(ClientId, new YouTubeClient("  " + ClientId + " ", "s").Id);
            Assert.NotNull(new YouTubeClient("", "s").Problem());
            Assert.NotNull(new YouTubeClient("1234-test", "s").Problem());
            Assert.NotNull(new YouTubeClient(YouTubeClient.IdSuffix, "s").Problem());
            Assert.NotNull(new YouTubeClient(ClientId, "").Problem());
            Assert.False(new YouTubeClient(ClientId, null).IsComplete);
        }
    }

    /// <summary>가짜 HTTP: (요청, 본문) → 응답. 받은 요청을 기록한다(본문은 미리 읽어 둠 — 파일 조각 내용도 여기서 읽힌다).</summary>
    internal sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, byte[], HttpResponseMessage> _respond;

        public FakeHttp(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (Requests)
                Requests.Add(request);
            return _respond(request, body);
        }

        public static Dictionary<string, string> Form(byte[] body)
        {
            var query = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
            return query.AllKeys.ToDictionary(k => k, k => query[k]);
        }
    }
}
