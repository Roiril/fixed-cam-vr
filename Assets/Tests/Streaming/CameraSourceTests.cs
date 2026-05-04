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
    }
}
