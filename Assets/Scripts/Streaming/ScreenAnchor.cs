#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ワールド空間に置いた Screen を「頭の前にピタッと追従」⇄「その場で凍結」を動的に切り替える。
    /// Locked=true: 毎 LateUpdate でカメラ前方にスナップ（実質 head-lock）
    /// Locked=false: 直前位置で凍結（ワールド固定）
    /// </summary>
    public sealed class ScreenAnchor : MonoBehaviour
    {
        [SerializeField] private Transform? head;
        [SerializeField] private float distance = 1.5f;
        [SerializeField] private bool locked = true;
        [SerializeField] private KeyCode toggleKey = KeyCode.Space;

        public bool Locked
        {
            get => locked;
            set => locked = value;
        }

        public void Toggle() => locked = !locked;
        public void Lock() => locked = true;
        public void Unlock() => locked = false;

        private void Awake()
        {
            if (head == null && Camera.main != null)
            {
                head = Camera.main.transform;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) Toggle();
        }

        private void LateUpdate()
        {
            if (!locked || head == null) return;

            transform.position = head.position + head.forward * distance;
            transform.rotation = head.rotation;
        }
    }
}
