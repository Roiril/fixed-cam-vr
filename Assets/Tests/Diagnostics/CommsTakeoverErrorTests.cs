#nullable enable

using System.IO;
using System.Reflection;
using FixedCamVr.Streaming;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Diagnostics.Tests
{
    public sealed class CommsTakeoverErrorTests
    {
        private const string PrefabPath = "Assets/Prefabs/Effects/UnauthorizedAccess.prefab";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void TickStartsRealPrefabWhilePanelIsHiddenAndStopsBothLayers()
        {
            GameObject host = new GameObject("Comms takeover test");
            GameObject screen = new GameObject("Screen anchor");
            try
            {
                var panel = host.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(panel, null);
                var coordinator = host.GetComponent<CommsTakeoverError>();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                Assert.IsNotNull(prefab);
                Assert.IsNotNull(coordinator);
                coordinator.Configure(panel, prefab);
                coordinator.ConfigureScreen(screen.transform, new Vector2(2.3704f, 1.3333f));

                panel.Deliver(CommsNotice.Takeover);
                Advance(panel, .2f);
                Assert.IsTrue(panel.TakeoverVisible);
                Assert.AreEqual(0f, panel.AppliedPanelAlpha, "lead中はCommsの面がまだ見えない");
                coordinator.Tick(panel.TakeoverVisible, 0f);
                var effect = Object.FindObjectOfType<UnauthorizedAccessEffect>(true);
                Assert.IsNotNull(effect);
                Transform screenRoot = Root(effect, "_screenRoot");
                Transform spatialRoot = Root(effect, "_spatialRoot");
                Assert.IsTrue(screenRoot.gameObject.activeSelf, "lead中はエラーだけが見える");
                Assert.IsTrue(spatialRoot.gameObject.activeSelf, "lead中は空間側も見える");
                var statusMaterial = (Material)typeof(UnauthorizedAccessEffect)
                    .GetField("_statusMaterial", Private)!.GetValue(effect)!;
                var attempt = (Texture2D)typeof(UnauthorizedAccessEffect)
                    .GetField("attemptGraphic", Private)!.GetValue(effect)!;
                var failed = (Texture2D)typeof(UnauthorizedAccessEffect)
                    .GetField("failedGraphic", Private)!.GetValue(effect)!;
                Assert.AreSame(attempt, statusMaterial.GetTexture("_MainTex"));
                Vector3 spatialPosition = spatialRoot.position;

                screen.transform.position = new Vector3(1f, 2f, 3f);
                Advance(panel, .1f);
                coordinator.Tick(panel.TakeoverVisible, 0f);
                Assert.AreEqual(screen.transform, screenRoot.parent, "画面側は実画面を追う");
                Assert.AreEqual(spatialPosition, spatialRoot.position, "空間側は発火位置へ固定する");

                var logic = Logic(panel);
                var apply = typeof(CommsPanel).GetMethod("Apply", Private)!;
                bool sawOut = false;
                bool sawFailed = false;
                int blockFailedLogs = 0;
                void CountBlockFailed(string condition, string stackTrace, LogType type)
                {
                    if (condition.Contains("ev=commsError phase=BlockFailed")) blockFailedLogs++;
                }
                Application.logMessageReceived += CountBlockFailed;
                try
                {
                    for (int frame = 0; frame < 300 && panel.TakeoverVisible; frame++)
                    {
                        logic.Tick(1f / 30f);
                        apply.Invoke(panel, new object[] { logic.Weights });
                        float opacity = panel.TakeoverErrorOpacity;
                        coordinator.Tick(panel.TakeoverVisible, 0f);
                        if (panel.TakeoverBlockFailed)
                        {
                            sawFailed = true;
                            Assert.AreSame(failed, statusMaterial.GetTexture("_MainTex"));
                        }
                        if (opacity > 0f && opacity < .35f)
                        {
                            sawOut = true;
                            Assert.Greater(MaxAlpha(screenRoot), 0f);
                            Assert.Greater(MaxAlpha(spatialRoot), 0f);
                        }
                    }
                }
                finally { Application.logMessageReceived -= CountBlockFailed; }
                Assert.IsTrue(sawOut, "Outでは主画面と空間を同じopacityで薄くする");
                Assert.IsTrue(sawFailed, "塗り替わり前に実prefabの遮断失敗表示へ切り替える");
                Assert.AreEqual(1, blockFailedLogs, "BlockFailedの縁は実機ログへ1回だけ出す");
                Assert.IsFalse(effect.IsPlaying);
                Assert.IsFalse(screenRoot.gameObject.activeSelf);
                Assert.IsFalse(spatialRoot.gameObject.activeSelf);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(screen);
            }
        }

        [Test]
        public void DisableAndReconfigureLeaveNoPreviousEffect()
        {
            GameObject host = new GameObject("Comms takeover reset test");
            GameObject screen = new GameObject("Screen anchor");
            try
            {
                var panel = host.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(panel, null);
                var coordinator = host.GetComponent<CommsTakeoverError>();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                coordinator.Configure(panel, prefab);
                coordinator.ConfigureScreen(screen.transform, Vector2.one);
                panel.Deliver(CommsNotice.Takeover);
                coordinator.Tick(panel.TakeoverVisible, 0f);
                var first = Object.FindObjectOfType<UnauthorizedAccessEffect>(true);
                Assert.IsNotNull(first);

                typeof(CommsTakeoverError).GetMethod("OnDisable", Private)!.Invoke(coordinator, null);
                Assert.AreEqual(0, Object.FindObjectsOfType<UnauthorizedAccessEffect>(true).Length);

                coordinator.Configure(panel, prefab);
                coordinator.Tick(true, 0f);
                var second = Object.FindObjectOfType<UnauthorizedAccessEffect>(true);
                Assert.IsNotNull(second);
                Assert.AreNotSame(first, second);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(screen);
            }
        }

        [Test]
        public void RuntimeScreenSizeIsResolvedWhenTakeoverStarts()
        {
            GameObject host = new GameObject("Comms takeover screen resolve test");
            GameObject screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            try
            {
                screen.AddComponent<MjpegScreen>();
                var panel = host.AddComponent<CommsPanel>();
                typeof(CommsPanel).GetMethod("Awake", Private)!.Invoke(panel, null);
                var coordinator = host.GetComponent<CommsTakeoverError>();
                coordinator.Configure(panel, AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));

                screen.transform.localScale = new Vector3(2.4f, 1.35f, 1f);
                panel.Deliver(CommsNotice.Takeover);
                coordinator.Tick(panel.TakeoverVisible, 0f);
                var effect = Object.FindObjectOfType<UnauthorizedAccessEffect>(true);
                var size = (Vector2)typeof(UnauthorizedAccessEffect)
                    .GetField("_screenSize", Private)!.GetValue(effect)!;
                Assert.AreEqual(2.4f, size.x, .0001f);
                Assert.AreEqual(1.35f, size.y, .0001f);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(screen);
            }
        }

        [Test]
        public void MainSceneReferencesUnauthorizedAccessPrefab()
        {
            string prefabGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
            Assert.AreEqual("b9aa0ec021fba704b872270123baf6e1", prefabGuid);
            string yaml = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes/Main.unity"));
            StringAssert.Contains(
                "typeSfx: {fileID: 118864515}\r\n  takeoverErrorPrefab: {fileID: 9221488599788193716, guid: "
                + prefabGuid + ", type: 3}", yaml.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        }

        private static Transform Root(UnauthorizedAccessEffect effect, string field) =>
            (Transform)typeof(UnauthorizedAccessEffect).GetField(field, Private)!.GetValue(effect)!;

        private static void Advance(CommsPanel panel, float seconds)
        {
            var logic = Logic(panel);
            var apply = typeof(CommsPanel).GetMethod("Apply", Private)!;
            for (int i = 0; i < Mathf.CeilToInt(seconds * 30f); i++)
            {
                logic.Tick(1f / 30f);
                apply.Invoke(panel, new object[] { logic.Weights });
            }
        }

        private static CommsPanelLogic Logic(CommsPanel panel) =>
            (CommsPanelLogic)typeof(CommsPanel).GetField("_logic", Private)!.GetValue(panel)!;

        private static float MaxAlpha(Transform root)
        {
            float max = 0f;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Color[] colors = filter.sharedMesh.colors;
                for (int i = 0; i < colors.Length; i++) max = Mathf.Max(max, colors[i].a);
            }
            return max;
        }

    }
}
