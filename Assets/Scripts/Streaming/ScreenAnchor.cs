#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ワールド空間に置いた Screen を「頭の前にピタッと追従」⇄「その場で凍結」を動的に切り替える。
    /// Locked=true: 毎 LateUpdate でカメラ前方にスナップ（実質 head-lock）
    /// Locked=false: 直前位置で凍結（ワールド固定）
    /// 映像の向き・アスペクト補正は ScreenComposite シェーダ側（UV 空間）で完結するため、
    /// ここでは Transform の回転補正を一切持たない。
    /// </summary>
    public sealed class ScreenAnchor : MonoBehaviour
    {
        [SerializeField] private Transform? head;
        [SerializeField] private float distance = 1.5f;
        [SerializeField] private bool locked = false;
        [SerializeField] private KeyCode toggleKey = KeyCode.Space;

        [Tooltip("true: 水平方向(ヨー)のみ追従し、頭の上下(ピッチ)/ロールには追従しない。" +
                 "見上げ/見下ろしで画面が上下にズレず一定の高さに留まる（酔い軽減）。")]
        [SerializeField] private bool yawOnly = true;

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

            // yawOnly: 水平面のヨーのみ抽出。head.forward/head.rotation をそのまま使うと
            // ピッチ（見上げ/見下ろし）で画面が上下に動く・傾くため、Y 軸回転だけに落とす。
            Vector3 fwd;
            Quaternion faceRot;
            if (yawOnly)
            {
                faceRot = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
                fwd = faceRot * Vector3.forward;   // 水平前方（fwd.y=0 → 画面は頭の高さに留まる）
            }
            else
            {
                fwd = head.forward;
                faceRot = head.rotation;
            }

            transform.position = head.position + fwd * distance;
            transform.rotation = faceRot;
        }
    }
}
