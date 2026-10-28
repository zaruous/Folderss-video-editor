using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Media
{
    /// <summary>DPAPI(Windows 사용자 범위) 암호화 — 이 PC의 같은 Windows 사용자로만 풀린다.</summary>
    internal static class UserSecret
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Folderss-video-editor/youtube");

        public static byte[] Protect(byte[] data)
        {
            return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        }

        public static byte[] Unprotect(byte[] data)
        {
            return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        }
    }

    /// <summary>
    /// 새로 고침 토큰을 DPAPI(이 Windows 사용자)로 암호화해 데이터 폴더\youtube\token.dat에 둔다. 어느 클라이언트 ID로 받은 것인지도 함께 —
    /// 설정의 클라이언트 ID가 바뀌면 그 토큰은 쓰지 않는다. 접근 토큰은 저장하지 않는다(메모리에만, 1시간).
    /// 설정에 둘 클라이언트 비밀도 같은 방식으로 암호화한다(ProtectText).
    /// </summary>
    internal sealed class YouTubeTokenStore
    {
        private readonly Func<byte[], byte[]> _protect;
        private readonly Func<byte[], byte[]> _unprotect;

        public YouTubeTokenStore(string folder, Func<byte[], byte[]> protect = null, Func<byte[], byte[]> unprotect = null)
        {
            Folder = folder;
            _protect = protect ?? UserSecret.Protect;
            _unprotect = unprotect ?? UserSecret.Unprotect;
        }

        public string Folder { get; }

        public string FilePath { get { return Path.Combine(Folder, "token.dat"); } }

        public sealed class Entry
        {
            public string ClientId { get; set; }
            public string RefreshToken { get; set; }
            public DateTimeOffset SavedAt { get; set; }
        }

        /// <summary>clientId로 받아 둔 새로 고침 토큰(없거나, 다른 클라이언트 것이거나, 풀지 못하면 null).</summary>
        public Entry Load(string clientId)
        {
            try
            {
                if (string.IsNullOrEmpty(clientId) || !File.Exists(FilePath))
                    return null;
                var entry = JsonSerializer.Deserialize<Entry>(_unprotect(File.ReadAllBytes(FilePath)));
                return entry != null && !string.IsNullOrEmpty(entry.RefreshToken) && entry.ClientId == clientId ? entry : null;
            }
            catch (Exception)
            {
                // 깨진 파일, 다른 사용자·PC에서 옮겨 온 파일 — 로그인 안 한 것으로
                return null;
            }
        }

        public void Save(string clientId, string refreshToken, DateTimeOffset savedAt)
        {
            Directory.CreateDirectory(Folder);
            var data = _protect(JsonSerializer.SerializeToUtf8Bytes(new Entry { ClientId = clientId, RefreshToken = refreshToken, SavedAt = savedAt }));
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, FilePath, true);
        }

        public void Delete()
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }

        /// <summary>설정에 둘 값: 암호화한 base64(빈 글이면 null).</summary>
        public string ProtectText(string text)
        {
            return string.IsNullOrEmpty(text) ? null : Convert.ToBase64String(_protect(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>ProtectText로 만든 값을 푼다. 풀지 못하면(다른 사용자·PC, 손으로 고친 값) null.</summary>
        public string UnprotectText(string value)
        {
            try
            {
                return string.IsNullOrEmpty(value) ? null : Encoding.UTF8.GetString(_unprotect(Convert.FromBase64String(value)));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 유튜브 로그인 상태와 접근 토큰. 클라이언트는 부를 때마다 읽는다(설정 탭에서 바꾸면 바로 따름).
    /// 접근 토큰은 만료 2분 전까지 메모리에서 다시 쓰고, 그 뒤에는 새로 고침 토큰으로 새로 받는다 — 1시간 넘게 걸리는 올리기 중에도.
    /// </summary>
    internal sealed class YouTubeAccount
    {
        private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);
        private readonly Func<YouTubeClient> _client;
        private readonly Func<DateTimeOffset> _now;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private string _accessToken;
        private string _accessClientId;
        private DateTimeOffset _expiresAt;

        public YouTubeAccount(Func<YouTubeClient> client, YouTubeTokenStore store, HttpMessageHandler handler = null, Func<DateTimeOffset> now = null)
        {
            _client = client;
            Store = store;
            Http = YouTubeAuth.NewHttp(handler);
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public YouTubeTokenStore Store { get; }

        /// <summary>로그인·올리기가 함께 쓰는 HttpClient(시스템 프록시 + Windows 자격 증명).</summary>
        public HttpClient Http { get; }

        public YouTubeClient Client { get { return _client() ?? new YouTubeClient(null, null); } }

        /// <summary>지금 클라이언트로 로그인해 둔 때(새로 고침 토큰을 저장한 때). 로그인하지 않았으면 null.</summary>
        public DateTimeOffset? SignedInAt
        {
            get
            {
                var client = Client;
                return client.IsComplete ? Store.Load(client.Id)?.SavedAt : null;
            }
        }

        /// <summary>시스템 브라우저로 로그인해 새로 고침 토큰을 저장한다.</summary>
        public async Task SignIn(Action<Uri> openBrowser, CancellationToken cancellation, TimeSpan? timeout = null)
        {
            var client = Client;
            var problem = client.Problem();
            if (problem != null)
                throw new YouTubeException(problem);
            var tokens = await YouTubeAuth.SignIn(Http, client, openBrowser, cancellation, timeout).ConfigureAwait(false);
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                Store.Save(client.Id, tokens.RefreshToken, _now());
                Remember(client.Id, tokens);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 쓸 수 있는 접근 토큰. refresh면 남은 시간과 상관없이 새로 받는다(서버가 401로 거절했을 때).
        /// 로그인하지 않았거나 새로 고침 토큰이 만료·취소됐으면 NeedsSignIn인 YouTubeException(만료된 것은 지움).
        /// </summary>
        public async Task<string> AccessToken(bool refresh, CancellationToken cancellation)
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var client = Client;
                if (!refresh && _accessToken != null && _accessClientId == client.Id && _now() < _expiresAt - Margin)
                    return _accessToken;
                var saved = client.IsComplete ? Store.Load(client.Id) : null;
                if (saved == null)
                    throw new YouTubeException("유튜브에 로그인하지 않았습니다.", true);
                OAuthTokens tokens;
                try
                {
                    tokens = await YouTubeAuth.Refresh(Http, client, saved.RefreshToken, cancellation).ConfigureAwait(false);
                }
                catch (YouTubeException ex) when (ex.NeedsSignIn)
                {
                    Forget();
                    try
                    {
                        Store.Delete();
                    }
                    catch (Exception)
                    {
                        // 지우지 못해도 다음에 다시 로그인하면 덮어쓴다
                    }
                    throw;
                }
                // 구글은 보통 새로 고침 토큰을 그대로 두지만, 새것을 주면 바꿔 둔다
                if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != saved.RefreshToken)
                    Store.Save(client.Id, tokens.RefreshToken, saved.SavedAt);
                Remember(client.Id, tokens);
                return tokens.AccessToken;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>로그아웃: 구글에 토큰 취소를 보내고(실패해도) 저장한 토큰을 지운다. 구글에서도 취소됐으면 true.</summary>
        public async Task<bool> SignOut(CancellationToken cancellation)
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var client = Client;
                var saved = client.IsComplete ? Store.Load(client.Id) : null;
                var revoked = saved == null;
                if (saved != null)
                {
                    try
                    {
                        revoked = await YouTubeAuth.Revoke(Http, saved.RefreshToken, cancellation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // 연결 안 됨 등 — 이 PC의 토큰은 그래도 지운다
                    }
                }
                Forget();
                Store.Delete();
                return revoked;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>메모리의 접근 토큰을 버린다(클라이언트를 바꿨을 때).</summary>
        public void Forget()
        {
            _accessToken = null;
            _accessClientId = null;
        }

        private void Remember(string clientId, OAuthTokens tokens)
        {
            _accessToken = tokens.AccessToken;
            _accessClientId = clientId;
            _expiresAt = _now() + tokens.ExpiresIn;
        }
    }
}
