using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;

namespace VideoEditor.Media
{
    /// <summary>
    /// 네트워크 접속은 Folderss 본체와 같은 방식: Windows 시스템 프록시(자동 구성 스크립트 포함)를 쓰고,
    /// 인증이 필요한 프록시에는 Windows 로그인 자격 증명을 넘긴다(본체의 플러그인 저장소·마크다운 뷰어와 같은 DefaultProxyCredentials).
    /// 플러그인 계약(GetAppSettings)에는 네트워크 설정이 없어서, 본체 설정을 읽는 대신 본체와 같은 기본값을 쓴다.
    /// </summary>
    internal static class HostNetwork
    {
        /// <summary>본체와 같은 HttpClient 처리기(시스템 프록시 + Windows 자격 증명).</summary>
        public static HttpClientHandler Handler(bool allowAutoRedirect = true)
        {
            return new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect, DefaultProxyCredentials = CredentialCache.DefaultCredentials };
        }

        /// <summary>
        /// 별도 프로세스(yt-dlp)에 넘길 프록시 주소: 시스템 프록시로 target에 갈 때 쓰는 프록시. 바로 가거나 알 수 없으면 null.
        /// Windows 로그인 인증은 넘길 수 없다(yt-dlp는 주소 안의 사용자·암호만 지원).
        /// </summary>
        public static string ProxyFor(Uri target, IWebProxy proxy = null)
        {
            try
            {
                proxy = proxy ?? HttpClient.DefaultProxy;
                if (proxy == null || target == null || proxy.IsBypassed(target))
                    return null;
                var address = proxy.GetProxy(target);
                if (address == null || !address.IsAbsoluteUri || address == target)
                    return null;
                return address.AbsoluteUri.TrimEnd('/');
            }
            catch (Exception)
            {
                // 자동 구성 스크립트를 못 읽는 등 — 프록시 없이(yt-dlp 자체 판단으로) 간다
                return null;
            }
        }

        /// <summary>yt-dlp 인자 "--proxy 주소"(프록시를 안 쓰면 빈 목록).</summary>
        public static IEnumerable<string> YtDlpProxyArgs(Uri target, IWebProxy proxy = null)
        {
            var address = ProxyFor(target, proxy);
            return address == null ? Array.Empty<string>() : new[] { "--proxy", address };
        }
    }
}
