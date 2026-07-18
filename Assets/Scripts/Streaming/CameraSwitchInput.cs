#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// キーボード (Tab / 1-9) でアクティブカメラを切替える。
    /// Quest コントローラ入力は asmdef 外の OvrControllerBridge から Next()/Prev() を呼び出す。
    /// </summary>
    public sealed class CameraSwitchInput : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("切替を一本化する CameraSwitchDirector。割当時はここ経由（時間ガード + dip 演出）。" +
                 "null なら従来どおり registry を直接叩く（後方互換）。")]
        [SerializeField] private CameraSwitchDirector? director;

        [SerializeField] private bool enableKeyboard = true;

        public CameraStreamRegistry? Registry => registry;

        private void Reset()
        {
            registry = GetComponent<CameraStreamRegistry>();
            director = GetComponent<CameraSwitchDirector>();
        }

        private void Update()
        {
            if (registry == null || registry.Count == 0) return;

            if (enableKeyboard)
            {
                if (Input.GetKeyDown(KeyCode.Tab))
                {
                    bool prev = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (director != null) { if (prev) director.Prev(); else director.Next(); }
                    else if (prev) registry.Prev(); else registry.Next();
                }

                for (int i = 0; i < 9; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    {
                        if (director != null) director.RequestManual(i);
                        else registry.SetActive(i);
                        break;
                    }
                }
            }
        }
    }
}
