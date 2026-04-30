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
        [SerializeField] private bool enableKeyboard = true;

        public CameraStreamRegistry? Registry => registry;

        private void Reset()
        {
            registry = GetComponent<CameraStreamRegistry>();
        }

        private void Update()
        {
            if (registry == null || registry.Count == 0) return;

            if (enableKeyboard)
            {
                if (Input.GetKeyDown(KeyCode.Tab))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                        registry.Prev();
                    else
                        registry.Next();
                }

                for (int i = 0; i < 9; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    {
                        registry.SetActive(i);
                        break;
                    }
                }
            }
        }
    }
}
