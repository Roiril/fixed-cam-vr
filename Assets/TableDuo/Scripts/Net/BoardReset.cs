#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 盤面リセット（ホストのみ）。サーバ起動後に掴めるプロップ（Grabbable）全ての初期姿勢を記憶し、
    /// <see cref="ResetBoard"/> で復元する（保持中のものは強制解放してから）。
    /// ラウンド跨ぎ・条件ブロック跨ぎで散らかった卓上を初期配置に戻すファシリテータ操作:
    ///   curl "http://&lt;hostIP&gt;:7780/mark?label=reset_board"
    /// （FacilitatorMarkServer が label=reset_board を特別扱いして呼ぶ。CSV にも mark 行が残る）
    /// プロップはサーバ権威 NetworkTransform なので、サーバが transform を書けば全員に同期される。
    /// </summary>
    public sealed class BoardReset : MonoBehaviour
    {
        private readonly List<(Transform t, Vector3 pos, Quaternion rot)> _initial = new();
        private bool _captured;

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer)
            {
                // セッション終了で初期姿勢を破棄（次の listen で再キャプチャ。シーン再ロード対応）
                _captured = false;
                _initial.Clear();
                return;
            }
            if (!_captured) Capture();
        }

        private void Capture()
        {
            _captured = true;
            _initial.Clear();
            foreach (var grab in FindObjectsOfType<Grabbable>())
            {
                var t = grab.transform;
                _initial.Add((t, t.position, t.rotation));
            }
            Debug.Log($"[TableDuo] BoardReset: 初期配置を記憶（{_initial.Count} 個）");
        }

        /// <summary>全プロップを初期姿勢へ戻す（server 専用）。保持中は強制解放してから戻す。</summary>
        public void ResetBoard()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !_captured)
            {
                Debug.LogWarning("[TableDuo] BoardReset: server 以外/未キャプチャでは実行不可");
                return;
            }
            int restored = 0;
            foreach (var (t, pos, rot) in _initial)
            {
                if (t == null) continue;
                var grab = t.GetComponent<Grabbable>();
                if (grab != null && grab.IsHeld) grab.ServerForceRelease("boardReset");
                t.SetPositionAndRotation(pos, rot);
                restored++;
            }
            Debug.Log($"[TableDuo] BoardReset: {restored} 個を初期配置へ復元");
        }
    }
}
