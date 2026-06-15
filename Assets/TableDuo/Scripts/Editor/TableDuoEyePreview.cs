#nullable enable
using UnityEditor;
using UnityEngine;

namespace TableDuoVr.EditorTools
{
    /// <summary>
    /// 席＝初期目線アンカーの「見え方」を Scene ビューでプレビューするメニュー。
    /// Scene カメラを席の位置・向きに合わせるので、その席のプレイヤーが起動時に何を見るかを
    /// エディタ上で確認しながら席（目線アンカー）を調整できる。
    /// </summary>
    public static class TableDuoEyePreview
    {
        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Eye - Seat0 (Full)", priority = 212)]
        public static void PreviewSeat0() => AlignSceneViewTo("[TableDuo]/Seats/Seat0");

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Eye - Seat1 (Hand)", priority = 213)]
        public static void PreviewSeat1() => AlignSceneViewTo("[TableDuo]/Seats/Seat1");

        private static void AlignSceneViewTo(string path)
        {
            var go = GameObject.Find(path);
            if (go == null)
            {
                Debug.LogWarning($"[TableDuoEyePreview] {path} が見つかりません（Setup TableDuo Scene 未実行？）");
                return;
            }
            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
            {
                Debug.LogWarning("[TableDuoEyePreview] アクティブな Scene ビューがありません");
                return;
            }
            // Scene カメラを席の目線位置・向きに一致させる（pivot は前方 1m、近接ズーム）
            sv.rotation = go.transform.rotation;
            sv.pivot = go.transform.position + go.transform.forward * 1.0f;
            sv.size = 0.6f;
            sv.Repaint();
            Selection.activeGameObject = go; // Inspector で位置・向きを直接編集できるよう選択
            Debug.Log($"[TableDuoEyePreview] Scene ビューを {path} の目線に合わせました。" +
                      "席を動かして再実行すると調整できます。");
        }
    }
}
