#nullable enable
using NUnit.Framework;
using TableDuoVr.Net;

namespace TableDuoVr.Tests
{
    /// <summary>
    /// AutoModeResolver.Resolve（起動時の host/client/discover 決定表と優先順）。
    /// enum は ConnectionManager ネストのまま参照。
    /// </summary>
    public class AutoModeResolverTests
    {
        [Test]
        public void Mode_Host_ResolvesHost()
        {
            var (mode, _) = AutoModeResolver.Resolve(ConnectionManager.AutoMode.None, "host", null);
            Assert.AreEqual(ConnectionManager.AutoMode.Host, mode);
        }

        [Test]
        public void Mode_ClientWithIp_ResolvesClient()
        {
            var (mode, ip) = AutoModeResolver.Resolve(ConnectionManager.AutoMode.None, "client", "192.168.1.5");
            Assert.AreEqual(ConnectionManager.AutoMode.Client, mode);
            Assert.AreEqual("192.168.1.5", ip);
        }

        [Test]
        public void Mode_ClientNoIp_ResolvesDiscover()
        {
            var (mode, ip) = AutoModeResolver.Resolve(ConnectionManager.AutoMode.None, "client", null);
            Assert.AreEqual(ConnectionManager.AutoMode.Discover, mode);
            Assert.IsNull(ip);
        }

        [Test]
        public void Mode_NullInspectorNone_ResolvesDiscover()
        {
            var (mode, _) = AutoModeResolver.Resolve(ConnectionManager.AutoMode.None, null, null);
            Assert.AreEqual(ConnectionManager.AutoMode.Discover, mode);
        }

        [Test]
        public void Mode_NullInspectorHost_ResolvesHost()
        {
            var (mode, _) = AutoModeResolver.Resolve(ConnectionManager.AutoMode.Host, null, null);
            Assert.AreEqual(ConnectionManager.AutoMode.Host, mode, "Inspector 焼き込みが勝つ");
        }

        [Test]
        public void Ip_EmptyString_NormalizedToNull()
        {
            var (mode, ip) = AutoModeResolver.Resolve(ConnectionManager.AutoMode.None, "client", "");
            Assert.IsNull(ip, "空文字 IP は null 正規化");
            Assert.AreEqual(ConnectionManager.AutoMode.Discover, mode, "IP 無し扱いで発見へ縮退");
        }
    }
}
