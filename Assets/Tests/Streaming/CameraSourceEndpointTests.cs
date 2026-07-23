#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    /// <summary>
    /// CameraSource の接続エンドポイント階層（discovery &gt; runtime &gt; baked）・変化検知キー・
    /// Basic 認証トークン・discovery 照合 URL を固定する純ロジックテスト（ネットワーク非依存）。
    /// private SerializeField は SerializedObject を使わず reflection で設定する（CameraSourceTests.MakeSource 先例）。
    /// 接続先の階層は connection-robustness の核で、誤ると現場で古い / 誤 IP に繋ぎ黒画面になる。
    /// </summary>
    public sealed class CameraSourceEndpointTests
    {
        private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly List<UnityEngine.Object> _spawned = new();

        [TearDown]
        public void Cleanup()
        {
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _spawned.Clear();
        }

        private static void Set(CameraSource s, string field, object value)
            => typeof(CameraSource).GetField(field, BF)!.SetValue(s, value);

        private CameraSource MakeSource(
            string host = "192.168.1.10", int port = 8080,
            string videoPath = "/video", string infoPath = "/info", string healthPath = "/health",
            string username = "", string password = "")
        {
            var so = ScriptableObject.CreateInstance<CameraSource>();
            _spawned.Add(so);
            Set(so, "host", host);
            Set(so, "port", port);
            Set(so, "videoPath", videoPath);
            Set(so, "infoPath", infoPath);
            Set(so, "healthPath", healthPath);
            Set(so, "username", username);
            Set(so, "password", password);
            return so;
        }

        // ---- 階層解決（baked / runtime / discovery）----

        [Test]
        public void Baked_EffectiveHostPortAndLayer()
        {
            var s = MakeSource("10.0.0.5", 8080);
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.5"));
            Assert.That(s.EffectivePortPublic, Is.EqualTo(8080));
            Assert.That(s.ActiveLayer, Is.EqualTo(CameraSource.EndpointLayer.Baked));
        }

        [Test]
        public void RuntimeOverride_OverridesBaked()
        {
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 9090, "u", "p");
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.9"));
            Assert.That(s.EffectivePortPublic, Is.EqualTo(9090));
            Assert.That(s.ActiveLayer, Is.EqualTo(CameraSource.EndpointLayer.Runtime));
        }

        [Test]
        public void RuntimeOverride_NonPositivePortFallsBackToBaked()
        {
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 0, "", "");
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.9"), "host は上書きされる");
            Assert.That(s.EffectivePortPublic, Is.EqualTo(8080), "port<=0 は焼き込み値へフォールバック");
        }

        [Test]
        public void RuntimeOverride_EmptyHostReleasesToBaked()
        {
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 9090, "u", "p");
            s.ApplyRuntimeEndpoint("", 1234, "x", "y"); // 空 host = override 解除
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.5"));
            Assert.That(s.EffectivePortPublic, Is.EqualTo(8080));
            Assert.That(s.ActiveLayer, Is.EqualTo(CameraSource.EndpointLayer.Baked));
        }

        [Test]
        public void DiscoveryOverride_TakesPrecedenceOverRuntime()
        {
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 9090, "u", "p");
            s.ApplyDiscoveryEndpoint("10.0.0.50", 7000);
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.50"));
            Assert.That(s.EffectivePortPublic, Is.EqualTo(7000));
            Assert.That(s.ActiveLayer, Is.EqualTo(CameraSource.EndpointLayer.Discovery));
        }

        [Test]
        public void DiscoveryOverride_EmptyHostReleasesToRuntime()
        {
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 9090, "u", "p");
            s.ApplyDiscoveryEndpoint("10.0.0.50", 7000);
            s.ApplyDiscoveryEndpoint("", 7000); // 空 host = discovery 層解除
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.9"));
            Assert.That(s.EffectivePortPublic, Is.EqualTo(9090));
            Assert.That(s.ActiveLayer, Is.EqualTo(CameraSource.EndpointLayer.Runtime));
        }

        [Test]
        public void DiscoveryOverride_NonPositivePortReleasesToRuntime()
        {
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 9090, "u", "p");
            s.ApplyDiscoveryEndpoint("10.0.0.50", 7000);
            s.ApplyDiscoveryEndpoint("10.0.0.50", 0); // port<=0 = discovery 層解除
            Assert.That(s.EffectiveHostPublic, Is.EqualTo("10.0.0.9"));
            Assert.That(s.EffectivePortPublic, Is.EqualTo(9090));
            Assert.That(s.ActiveLayer, Is.EqualTo(CameraSource.EndpointLayer.Runtime));
        }

        [Test]
        public void DiscoveryOverride_DoesNotChangeAuth()
        {
            // discovery は host/port のみ差し替え、認証は runtime/baked を維持する（発見対象は認証なし前提）。
            var s = MakeSource("10.0.0.5", 8080);
            s.ApplyRuntimeEndpoint("10.0.0.9", 9090, "user", "pass");
            s.ApplyDiscoveryEndpoint("10.0.0.50", 7000);
            string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
            Assert.That(s.BasicAuthToken, Is.EqualTo(expected), "discovery 適用後も runtime の認証が生きる");
        }

        // ---- ConnectionKey（再接続要否判定の基盤）----
        // 形式は並行変更中（host|port|user|pass 指紋）のため、意味論だけで固定する。

        [Test]
        public void ConnectionKey_PasswordChangeChangesKey()
        {
            var s = MakeSource();
            s.ApplyRuntimeEndpoint("h", 1000, "u", "p1");
            string k1 = s.ConnectionKey;
            s.ApplyRuntimeEndpoint("h", 1000, "u", "p2");
            string k2 = s.ConnectionKey;
            Assert.That(k2, Is.Not.EqualTo(k1), "パスワードだけ変えてもキーは変わる");
        }

        [Test]
        public void ConnectionKey_DoesNotLeakRawPassword()
        {
            var s = MakeSource();
            s.ApplyRuntimeEndpoint("h", 1000, "u", "SECRETPW123");
            Assert.That(s.ConnectionKey, Does.Not.Contain("SECRETPW123"), "キーに生パスワードを含めない");
        }

        [Test]
        public void ConnectionKey_SameInputSameKey()
        {
            var s = MakeSource();
            s.ApplyRuntimeEndpoint("h", 1000, "u", "p");
            string a = s.ConnectionKey;
            s.ApplyRuntimeEndpoint("h", 1000, "u", "p");
            string b = s.ConnectionKey;
            Assert.That(b, Is.EqualTo(a), "同一入力は同一キー（無変化 no-op 判定の基盤）");
        }

        [Test]
        public void ConnectionKey_HostPortUserChangesChangeKey()
        {
            var s = MakeSource();
            s.ApplyRuntimeEndpoint("h", 1000, "u", "p");
            string baseline = s.ConnectionKey;

            s.ApplyRuntimeEndpoint("h2", 1000, "u", "p");
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(baseline), "host 変更でキーが変わる");

            s.ApplyRuntimeEndpoint("h", 2000, "u", "p");
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(baseline), "port 変更でキーが変わる");

            s.ApplyRuntimeEndpoint("h", 1000, "u2", "p");
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(baseline), "user 変更でキーが変わる");
        }

        [Test]
        public void ConnectionKey_DiscoveryApplyChangesKey()
        {
            var s = MakeSource("10.0.0.5", 8080);
            string before = s.ConnectionKey;
            s.ApplyDiscoveryEndpoint("10.0.0.50", 7000);
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(before), "discovery 適用でキーが変わる");
        }

        // ---- BasicAuthToken ----

        [Test]
        public void BasicAuthToken_NullWhenUserEmpty()
        {
            var s = MakeSource(username: "", password: "");
            Assert.That(s.BasicAuthToken, Is.Null);
        }

        [Test]
        public void BasicAuthToken_MatchesBase64OfUserColonPass()
        {
            var s = MakeSource(username: "admin", password: "admin");
            string expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:admin"));
            Assert.That(s.BasicAuthToken, Is.EqualTo(expected));
        }

        // ---- discovery 照合 URL / メタ URL ----

        [Test]
        public void BuildInfoUrlFor_ArbitraryHostPort()
        {
            var s = MakeSource(infoPath: "/info");
            Assert.That(s.BuildInfoUrlFor("1.2.3.4", 9), Is.EqualTo("http://1.2.3.4:9/info"));
        }

        [Test]
        public void BuildInfoUrlFor_EmptyInfoPathReturnsEmpty()
        {
            // infoPath 空（IP Camera Lite 相当）は照合不能 = 発見対象外。
            var s = MakeSource(infoPath: "");
            Assert.That(s.BuildInfoUrlFor("1.2.3.4", 9), Is.EqualTo(""));
        }

        [Test]
        public void BuildInfoAndHealthUrl_EmptyPathReturnsEmpty()
        {
            var s = MakeSource(infoPath: "", healthPath: "");
            Assert.That(s.BuildInfoUrl(), Is.EqualTo(""));
            Assert.That(s.BuildHealthUrl(), Is.EqualTo(""));
        }
    }
}
