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
        public void TickUsesRealPrefabAndKeepsBothLayersUntilStop()
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

                coordinator.Tick(true, 1f);
                var effect = Object.FindObjectOfType<UnauthorizedAccessEffect>(true);
                Assert.IsNotNull(effect);
                Transform screenRoot = Root(effect, "_screenRoot");
                Transform spatialRoot = Root(effect, "_spatialRoot");
                Assert.IsTrue(screenRoot.gameObject.activeSelf);
                Assert.IsTrue(spatialRoot.gameObject.activeSelf);
                Vector3 spatialPosition = spatialRoot.position;

                screen.transform.position = new Vector3(1f, 2f, 3f);
                coordinator.Tick(true, effect.Duration * 2f);
                Assert.IsTrue(screenRoot.gameObject.activeSelf, "内部の7秒を越えてもCommsが開いている間は表示する");
                Assert.IsTrue(spatialRoot.gameObject.activeSelf);
                Assert.Greater(MaxAlpha(screenRoot), 0f, "長時間後も画面側に見える要素がある");
                Assert.Greater(MaxAlpha(spatialRoot), 0f, "長時間後も空間側に見える要素がある");
                Assert.AreEqual(screen.transform, screenRoot.parent, "画面側は実画面を追う");
                Assert.AreEqual(spatialPosition, spatialRoot.position, "空間側は発火位置へ固定する");

                coordinator.Tick(false, 0f);
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
