#nullable enable
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEngine;

namespace FixedCamVr.Streaming.Tests
{
    public sealed class CameraSourceTests
    {
        private static CameraSource MakeSource(string host, int port, string videoPath)
        {
            var so = ScriptableObject.CreateInstance<CameraSource>();
            // private SerializeField を SerializedObject なしで触るため reflection
            typeof(CameraSource).GetField("host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(so, host);
            typeof(CameraSource).GetField("port", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(so, port);
            typeof(CameraSource).GetField("videoPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(so, videoPath);
            return so;
        }

        [Test]
        public void BuildUrl_StandardPath()
        {
            var s = MakeSource("192.168.1.10", 4747, "/video");
            Assert.That(s.BuildUrl(), Is.EqualTo("http://192.168.1.10:4747/video"));
        }

        [Test]
        public void BuildUrl_PathWithoutLeadingSlashGetsOne()
        {
            var s = MakeSource("phone.local", 8080, "video");
            Assert.That(s.BuildUrl(), Is.EqualTo("http://phone.local:8080/video"));
        }

        [Test]
        public void BuildUrl_EmptyPathDefaultsToRoot()
        {
            var s = MakeSource("10.0.0.1", 80, "");
            Assert.That(s.BuildUrl(), Is.EqualTo("http://10.0.0.1:80/"));
        }

        [Test]
        public void BuildUrl_QueryStringPreserved()
        {
            var s = MakeSource("192.168.1.10", 4747, "/mjpegfeed?640x480");
            Assert.That(s.BuildUrl(), Is.EqualTo("http://192.168.1.10:4747/mjpegfeed?640x480"));
        }

        // ---- A3: ConnectionKey がパスワード変化を検知し、生パスワードを露出しない -----------

        [Test]
        public void ConnectionKey_ChangesWhenOnlyPasswordChanges()
        {
            var s = MakeSource("192.168.1.10", 8080, "/video");
            s.ApplyRuntimeEndpoint("192.168.1.10", 8080, "admin", "pass1");
            string before = s.ConnectionKey;
            // show.json で pass だけ差し替えるシナリオ（host/port/user は不変）。
            s.ApplyRuntimeEndpoint("192.168.1.10", 8080, "admin", "pass2");
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(before), "pass 変化で再接続判定が真になる");
        }

        [Test]
        public void ConnectionKey_DoesNotExposeRawPassword()
        {
            var s = MakeSource("192.168.1.10", 8080, "/video");
            s.ApplyRuntimeEndpoint("192.168.1.10", 8080, "admin", "supersecret123");
            Assert.That(s.ConnectionKey, Does.Not.Contain("supersecret123"),
                "生パスワードはキー文字列に載せない（FNV-1a フィンガープリント）");
        }

        [Test]
        public void ConnectionKey_EqualWhenAllParamsEqual()
        {
            var a = MakeSource("192.168.1.10", 8080, "/video");
            var b = MakeSource("192.168.1.10", 8080, "/video");
            a.ApplyRuntimeEndpoint("10.0.0.5", 8081, "admin", "secret");
            b.ApplyRuntimeEndpoint("10.0.0.5", 8081, "admin", "secret");
            Assert.That(a.ConnectionKey, Is.EqualTo(b.ConnectionKey));
        }

        [Test]
        public void ConnectionKey_EqualForEmptyPasswords()
        {
            var a = MakeSource("192.168.1.10", 8080, "/video");
            var b = MakeSource("192.168.1.10", 8080, "/video");
            // 認証なしカメラの後方互換（pass 空同士）。
            a.ApplyRuntimeEndpoint("10.0.0.5", 8081, "", "");
            b.ApplyRuntimeEndpoint("10.0.0.5", 8081, "", "");
            Assert.That(a.ConnectionKey, Is.EqualTo(b.ConnectionKey));
        }

        [Test]
        public void ConnectionKey_ChangesForUserHostPort_NonRegression()
        {
            var s = MakeSource("192.168.1.10", 8080, "/video");
            s.ApplyRuntimeEndpoint("10.0.0.5", 8081, "admin", "secret");
            string baseline = s.ConnectionKey;

            s.ApplyRuntimeEndpoint("10.0.0.5", 8081, "root", "secret");   // user 変更
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(baseline));

            s.ApplyRuntimeEndpoint("10.0.0.99", 8081, "admin", "secret"); // host 変更
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(baseline));

            s.ApplyRuntimeEndpoint("10.0.0.5", 9999, "admin", "secret");  // port 変更
            Assert.That(s.ConnectionKey, Is.Not.EqualTo(baseline));
        }
    }
}
