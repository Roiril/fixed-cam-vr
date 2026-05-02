#nullable enable
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// Hierarchy 上で `=== Foo ===` パターンの GameObject を見出しバーとして描画する。
    /// 名前のキーワードで色分け（XR=青 / Stage=緑 / Logic=橙 / その他=灰）。
    /// </summary>
    [InitializeOnLoad]
    public static class HierarchySeparator
    {
        private static readonly Regex Pattern = new(@"^=+\s*(.+?)\s*=+$", RegexOptions.Compiled);

        // VS Code 系ダーク UI と相性のいい控えめなアクセント色。
        private static readonly Color RigColor   = new(0.30f, 0.46f, 0.65f, 1f);  // 青 (Camera Rig)
        private static readonly Color StageColor = new(0.32f, 0.56f, 0.40f, 1f);  // 緑 (Light/Screen)
        private static readonly Color LogicColor = new(0.74f, 0.49f, 0.27f, 1f);  // 橙 (Streaming/Controllers)
        private static readonly Color DefaultColor = new(0.35f, 0.35f, 0.35f, 1f);

        static HierarchySeparator()
        {
            EditorApplication.hierarchyWindowItemOnGUI -= OnGUI;
            EditorApplication.hierarchyWindowItemOnGUI += OnGUI;
        }

        private static void OnGUI(int instanceID, Rect rect)
        {
            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (go == null) return;
            var match = Pattern.Match(go.name);
            if (!match.Success) return;

            string label = match.Groups[1].Value.ToUpperInvariant();
            var color = label switch
            {
                "RIG"    => RigColor,
                "XR"     => RigColor,   // 互換: 旧命名
                "STAGE"  => StageColor,
                "LOGIC"  => LogicColor,
                _ => DefaultColor,
            };

            // 行全体を塗る（行番号アイコン部分も含めるため少し左に伸ばす）
            var bar = new Rect(rect.x - 28, rect.y, rect.width + 32, rect.height);
            EditorGUI.DrawRect(bar, color);

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
            };
            EditorGUI.LabelField(rect, label, style);
        }
    }
}
