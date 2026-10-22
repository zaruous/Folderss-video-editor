using System;
using System.Linq;
using System.Net;
using VideoEditor.Media;
using Xunit;

namespace VideoEditor.Tests
{
    /// <summary>프록시는 Folderss 본체와 같은 방식: 시스템 프록시 + Windows 자격 증명, yt-dlp에는 시스템 프록시 주소를 --proxy로.</summary>
    public class HostNetworkTests
    {
        private sealed class FakeProxy : IWebProxy
        {
            public Func<Uri, Uri> Route { get; set; } = uri => uri;
            public Func<Uri, bool> Bypass { get; set; } = uri => false;
            public ICredentials Credentials { get; set; }
            public Uri GetProxy(Uri destination) => Route(destination);
            public bool IsBypassed(Uri host) => Bypass(host);
        }

        private static readonly Uri YouTube = new Uri("https://www.youtube.com/watch?v=abc");

        [Fact]
        public void Handler_UsesWindowsCredentialsForProxy()
        {
            using (var handler = HostNetwork.Handler(false))
            {
                Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
                Assert.True(handler.UseProxy);
                Assert.False(handler.AllowAutoRedirect);
            }
        }

        [Fact]
        public void ProxyFor_SystemProxy_IsPassedToYtDlp()
        {
            var proxy = new FakeProxy { Route = uri => new Uri("http://proxy.corp.example:8080/") };
            Assert.Equal("http://proxy.corp.example:8080", HostNetwork.ProxyFor(YouTube, proxy));
            Assert.Equal(new[] { "--proxy", "http://proxy.corp.example:8080" }, HostNetwork.YtDlpProxyArgs(YouTube, proxy).ToArray());
        }

        [Fact]
        public void ProxyFor_DirectBypassedOrBroken_IsNull()
        {
            // 프록시 없이 바로 가면 GetProxy가 주소를 그대로 돌려준다
            Assert.Null(HostNetwork.ProxyFor(YouTube, new FakeProxy()));
            Assert.Null(HostNetwork.ProxyFor(YouTube, new FakeProxy { Route = uri => null }));
            Assert.Null(HostNetwork.ProxyFor(YouTube, new FakeProxy { Route = uri => new Uri("http://proxy:8080/"), Bypass = uri => true }));
            // 자동 구성 스크립트를 못 읽는 등 예외는 프록시 없음으로
            Assert.Null(HostNetwork.ProxyFor(YouTube, new FakeProxy { Route = uri => throw new InvalidOperationException("pac") }));
            Assert.Empty(HostNetwork.YtDlpProxyArgs(YouTube, new FakeProxy()));
        }
    }
}
