#nullable enable
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 描画パッドへ線を引く/消すツールのデータ保持（ペン 2 本・消しゴム 1）。素の MonoBehaviour。
    /// <see cref="PadPaintCanvas"/> がサーバ側で各ツールの <see cref="ContactPoint"/> をパッドローカルへ
    /// 変換して接触判定に使う。ペン先はローカル +Z（<see cref="ContactPoint"/> = 原点 + forward·tipDistance）。
    /// 消しゴムは tipDistance=0 = transform.position がそのまま接触点（底面が作用面）。
    /// 値は TableDuoSceneSetup が SerializedObject で焼き込む。
    /// </summary>
    public sealed class PadDrawTool : MonoBehaviour
    {
        [Tooltip("0=coral ペン / 1=teal ペン / 2=消しゴム。PadPaintCanvas のスタンプ材質・パスの索引に一致")]
        [SerializeField] private int toolId;
        [Tooltip("true=消しゴム（ペンの線だけ消す）。PadPaintCanvas がスタンプシェーダ pass 1 を選ぶ")]
        [SerializeField] private bool isEraser;
        [Tooltip("スタンプ半径（mm）。ペン 2.2 / 消しゴム 10")]
        [SerializeField] private float radiusMm = 2.2f;
        [Tooltip("原点→作用点の距離（m）。ペン 0.0923（ペン先まで）/ 消しゴム 0（原点=底面）")]
        [SerializeField] private float tipDistance = 0.0923f;

        public int ToolId => toolId;
        public bool IsEraser => isEraser;
        public float RadiusMm => radiusMm;

        /// <summary>ワールドの作用点（ペン先 / 消しゴム底面）。原点 + forward·tipDistance。</summary>
        public Vector3 ContactPoint => transform.position + transform.forward * tipDistance;
    }
}
