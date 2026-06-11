#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 協調配置課題（Phase 4）の目標配置パネル。**手役のローカルにだけ表示**（ネット非同期）。
    /// 配置: テーブル作業空間の外（手役席の斜め上方）— 参照時の頭・手の向きが
    /// 相手に「よそ見」として読める位置を避ける（監査指摘 9）。
    /// パターンは起動時にランダム選択し、SessionLogger にイベントとして記録する。
    /// </summary>
    public sealed class PatternPanel : MonoBehaviour
    {
        [SerializeField] private Renderer? panelRenderer;
        [SerializeField] private Material[] patterns = System.Array.Empty<Material>();

        private bool _applied;
        private int _patternIndex = -1;

        private void Update()
        {
            // 役割確定（StudyConfig は起動時に決まるが、従来規則のフォールバックは接続後に判明）
            if (_applied) return;
            var role = ResolveLocalRole();
            if (role == null) return;
            _applied = true;

            bool isHand = role == StudyConfig.Role.Hand;
            if (panelRenderer != null)
            {
                panelRenderer.gameObject.SetActive(isHand);
                if (isHand && patterns.Length > 0)
                {
                    _patternIndex = Random.Range(0, patterns.Length);
                    panelRenderer.sharedMaterial = patterns[_patternIndex];
                    FindObjectOfType<SessionLogger>()?.LogEvent("patternSelected", $"index{_patternIndex}");
                    Debug.Log($"[TableDuo] 目標配置パターン: {_patternIndex}");
                }
            }
        }

        private static StudyConfig.Role? ResolveLocalRole()
        {
            if (StudyConfig.ForcedRole != null) return StudyConfig.ForcedRole;
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return null;
            return nm.IsServer ? StudyConfig.Role.Full : StudyConfig.Role.Hand;
        }
    }
}
