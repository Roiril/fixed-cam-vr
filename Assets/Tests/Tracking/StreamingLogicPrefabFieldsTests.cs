#nullable enable
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Tracking.Tests
{
    /// <summary>
    /// C2 の根本監査: <see cref="StreamingLogic"/> prefab の OvrControllerBridge serialized ブロックの
    /// 完全性を機械検証する。missing-key（型 default で読まれる = statusButton が None=0 で B 全死）と
    /// stale-key drift（旧 screenAnchor/prevButton/anchorToggleButton 残存）の双方を捕捉する。
    ///
    /// OvrControllerBridge 型は Assembly-CSharp で asmdef テストから型参照できないため、型名文字列 +
    /// SerializedObject（欠落キー→型 default の実効値を観測）+ 生 YAML テキスト（stale キー検出）で検査する。
    /// OVRInput.Button は Flags 列挙: None=0 / One=1(A) / Two=2(B)。列挙値は intValue で読む。
    /// </summary>
    public sealed class StreamingLogicPrefabFieldsTests
    {
        private const string PrefabPath = "Assets/Prefabs/Logic/StreamingLogic.prefab";
        private const string BridgeTypeName = "FixedCamVr.OvrBridge.OvrControllerBridge";

        private static MonoBehaviour LoadBridge()
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(root, Is.Not.Null, $"prefab をロードできない: {PrefabPath}");
            foreach (var mb in root!.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().FullName == BridgeTypeName)
                    return mb;
            Assert.Fail($"{BridgeTypeName} が prefab に見つからない");
            return null!;
        }

        [Test]
        public void StatusButton_IntValue_IsTwo()
        {
            var so = new SerializedObject(LoadBridge());
            SerializedProperty? p = so.FindProperty("statusButton");
            Assert.That(p, Is.Not.Null, "statusButton プロパティが無い（欠落キーで型 default 化）");
            // 現行バグは statusButton キー欠落 → None=0 で読まれ B（確定・トグル）が全死する。
            Assert.That(p!.intValue, Is.EqualTo(2), "statusButton は Button.Two=2（B・右）であるべき");
        }

        [Test]
        public void PrimaryButton_IntValue_IsOne()
        {
            var so = new SerializedObject(LoadBridge());
            SerializedProperty? p = so.FindProperty("primaryButton");
            Assert.That(p, Is.Not.Null, "primaryButton プロパティが無い（欠落キーで型 default 化）");
            Assert.That(p!.intValue, Is.EqualTo(1), "primaryButton は Button.One=1（A・右）であるべき");
        }

        [Test]
        public void PrefabYaml_HasNoStaleKeys_HasStatusButton()
        {
            string text = File.ReadAllText(PrefabPath);
            Assert.That(text, Does.Not.Contain("screenAnchor:"), "旧フィールド screenAnchor が残存している");
            Assert.That(text, Does.Not.Contain("prevButton:"), "旧フィールド prevButton が残存している");
            Assert.That(text, Does.Not.Contain("anchorToggleButton:"), "旧フィールド anchorToggleButton が残存している");
            Assert.That(text, Does.Contain("statusButton:"), "statusButton キーが書かれていない");

            // 2026-08-12: A のカメラ手動送りを撤去した。**ブロックを切り出して見る** —
            // `registry:` は同じ prefab の CameraSwitchInput にもあるので、全文検索だと誤検出する。
            string block = BridgeBlock(text);
            Assert.That(block, Does.Not.Contain("nextButton:"),
                "旧フィールド nextButton が残存している（primaryButton へ改名済み）");
            Assert.That(block, Does.Not.Contain("registry:"),
                "旧フィールド registry が残存している（カメラ手動送り撤去で不要）");
            Assert.That(block, Does.Not.Contain("switchDirector:"),
                "旧フィールド switchDirector が残存している（カメラ手動送り撤去で不要）");
            Assert.That(block, Does.Contain("titleScreen:"), "titleScreen キーが書かれていない（A の行き先）");
        }

        /// <summary>OvrControllerBridge の serialized ブロックだけを切り出す（`statusButton` は同 prefab で一意）。</summary>
        private static string BridgeBlock(string text)
        {
            int at = text.IndexOf("statusButton:", System.StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), "statusButton キーが見つからない");
            int start = text.LastIndexOf("--- !u!", at, System.StringComparison.Ordinal);
            int end = text.IndexOf("--- !u!", at, System.StringComparison.Ordinal);
            if (start < 0) start = 0;
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start);
        }

        [Test]
        public void MainScene_HasNoStatusButtonOverride()
        {
            const string scenePath = "Assets/Scenes/Main.unity";
            if (!File.Exists(scenePath))
            {
                Assert.Ignore("Main.unity が見つからないためスキップ");
                return;
            }
            // シーンに statusButton override が無い＝prefab 修正が本番 Main へ伝播することを回帰で固定する。
            string text = File.ReadAllText(scenePath);
            Assert.That(text, Does.Not.Contain("statusButton"),
                "Main.unity に statusButton override がある（prefab 伝播が崩れる）");
        }
    }
}
