#nullable enable
using FixedCamVr.Streaming;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// CameraStreamRegistry Inspector に Play モード時の live state 表示と切替ボタンを追加。
    /// </summary>
    [CustomEditor(typeof(CameraStreamRegistry))]
    public sealed class CameraStreamRegistryEditor : Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var reg = (CameraStreamRegistry)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Play モード中に各 stream の接続状態と切替ボタンが表示されます。", MessageType.Info);
                return;
            }

            int count = reg.Count;
            int active = reg.ActiveIndex;

            for (int i = 0; i < count; i++)
            {
                var stream = reg.Get(i);
                if (stream == null) continue;

                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    bool isActive = (i == active);
                    string indicator = stream.IsConnected ? "● Connected" : "○ Disconnected";
                    var color = stream.IsConnected ? Color.green : new Color(1f, 0.6f, 0.4f);

                    GUILayout.Label(isActive ? "▶" : "  ", GUILayout.Width(14));
                    GUILayout.Label($"[{i}] {stream.DisplayName}", GUILayout.MinWidth(120));

                    var prev = GUI.color;
                    GUI.color = color;
                    GUILayout.Label(indicator, GUILayout.Width(110));
                    GUI.color = prev;

                    using (new EditorGUI.DisabledScope(isActive))
                    {
                        if (GUILayout.Button("Activate", GUILayout.Width(70)))
                        {
                            reg.SetActive(i);
                        }
                    }
                }

                if (!stream.IsConnected && !string.IsNullOrEmpty(stream.LastError))
                {
                    EditorGUILayout.HelpBox(stream.LastError, MessageType.Warning);
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀ Prev")) reg.Prev();
                if (GUILayout.Button("Next ▶")) reg.Next();
            }
        }
    }
}
