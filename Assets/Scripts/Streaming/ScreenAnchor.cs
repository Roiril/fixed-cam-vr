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
        [SerializeField] private bool locked = false;
        [SerializeField] private KeyCode toggleKey = KeyCode.Space;

        // 同 GameObject の MjpegScreen が映像補正用に決めた localRotation を取り込んで、
        // 追従モードでも回転補正（rotationDeg 由来の Z 軸回転）を保つ。
        private MjpegScreen? _mjpegScreen;

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
            _mjpegScreen = GetComponent<MjpegScreen>();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) Toggle();
        }

        private void LateUpdate()
        {
            if (!locked || head == null) return;

            transform.position = head.position + head.forward * distance;
            // MjpegScreen の補正（rotationDeg → Z 軸回転）を保ったまま head 向きに合わせる。
            // 直接 head.rotation を代入すると localRotation が上書きされて補正が消え、
            // 縦持ちの映像が横倒し / 上下反転して見える原因になる。
            var correction = _mjpegScreen != null ? _mjpegScreen.DesiredLocalRotation : Quaternion.identity;
            transform.rotation = head.rotation * correction;
        }
    }
}
