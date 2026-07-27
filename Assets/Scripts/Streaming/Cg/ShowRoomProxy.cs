#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// show.json の <c>layout.room</c> を、course 空間に置いた**不可視のプロキシ幾何**として生成する層。
    ///
    /// **1 つの幾何で 3 用途を兼ねる**のが設計の芯（<c>.claude/plans/2026-07-27_cg-compositing-rebuild.md</c> §2.5）:
    ///   ① CG 人形のオクルーダ（壁の裏へ回れる） ② 影の落ち先（床平面の定義） ③ 較正の参照
    /// 用途ごとに別の幾何を持つと必ずズレるので、**分けるのはレンダリング属性だけ**にする。
    ///
    /// 壁・箱は <c>FixedCamVr/ShowOccluder</c>（色を書かず深度だけ書く）で描く。
    /// 床は**描かない** — 影は受け皿へ落とすのではなく <see cref="ShowShadowProjector"/> 方式で
    /// 人形を平面へ潰して描くので、床に要るのは高さ（<see cref="FloorCourseY"/>）だけ。
    /// 描くと投影シャドウと z-fight する側にリスクが移るだけで得が無い。
    ///
    /// course→world は <see cref="ShowControlClient.CourseToWorldProvider"/>（CourseFrame）を通す。
    /// **親子付けでは駄目**（CourseFrame は transform を動かさず originXZ/yawDeg を数値で持つ）ので、
    /// 各ボックスのワールド姿勢は毎フレーム置き直す（登録のやり直し・OS recenter に追従させるため。
    /// 数個の Transform 書き込みなので測って困る量ではない）。
    ///
    /// SerializeField は持たない — このコンポーネントは <see cref="ShowCgLayer"/> が実行時に
    /// GameObject ごと作るので、シーン / prefab YAML に焼かれる値が存在しない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowRoomProxy : MonoBehaviour
    {
        /// <summary>オクルーダ用マテリアル（Resources 経由）。**Resources に置くのは build で剥がされないため** —
        /// 実行時 <c>Shader.Find</c> だけに頼ると、どのアセットからも参照されないシェーダはビルドから除去される。</summary>
        public const string OccluderMaterialResource = "ShowCg/ShowOccluder";
        private const string OccluderShaderName = "FixedCamVr/ShowOccluder";

        private ShowControlClient? _showControl;
        private int _layer = -1;
        private Material? _occluderMat;
        private readonly List<Transform> _boxes = new();
        private List<ShowRoomProxyLogic.Box> _spec = new();
        private bool _subscribed;
        private bool _dirty = true;
        private bool _warnedNoMaterial;

        /// <summary>影の落ち先になる床の高さ（**course 空間**）。部屋が未著作なら 0（＝course の床）。</summary>
        public float FloorCourseY { get; private set; }

        /// <summary><c>layout.room</c> が著作されているか（HUD / 診断用）。</summary>
        public bool HasRoom { get; private set; }

        /// <summary>生成済みのオクルーダ数（テスト・診断用）。</summary>
        public int OccluderCount => _boxes.Count;

        /// <summary>
        /// 供給元を注入して購読を始める。再入も安全（前回購読は解除してから張り直す）。
        /// <paramref name="layer"/> は ShowCg レイヤ（仮想カメラだけが描く）。
        /// </summary>
        public void Initialize(ShowControlClient? showControl, int layer)
        {
            Unsubscribe();
            _showControl = showControl;
            _layer = layer;
            Subscribe();
            _dirty = true;
        }

        private void Subscribe()
        {
            if (_subscribed || _showControl == null) return;
            _showControl.LayoutChanged += MarkDirty;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            if (_showControl != null) _showControl.LayoutChanged -= MarkDirty;
            _subscribed = false;
        }

        private void MarkDirty() => _dirty = true;

        private void OnDestroy()
        {
            Unsubscribe();
            if (_occluderMat != null) DestroyObject(_occluderMat);
            _occluderMat = null;
        }

        /// <summary>
        /// 1 フレーム分の追従。<see cref="ShowCgLayer"/> が人形を出しているあいだ毎フレーム呼ぶ。
        /// レイアウト変化時だけ幾何を組み直し、姿勢は毎フレーム置き直す。
        /// </summary>
        public void Sync()
        {
            if (_dirty) Rebuild();
            Place();
        }

        private void Rebuild()
        {
            _dirty = false;
            ShowRoomDef? room = _showControl != null ? _showControl.Room : null;
            HasRoom = room != null;
            // 部屋が未著作でも人形と影は出す（床は course y=0 の無限平面とみなす）。
            // ここで諦めるとオクルージョンだけでなく接地影まで消え、フェイルソフトが壊れる。
            FloorCourseY = room != null ? room.floorY : 0f;
            _spec = ShowRoomProxyLogic.Build(room);

            EnsureBoxCount(_spec.Count);
        }

        private void EnsureBoxCount(int need)
        {
            if (need > 0 && !EnsureMaterial()) need = 0;   // マテリアルが用意できないならオクルーダは出さない

            while (_boxes.Count > need)
            {
                int last = _boxes.Count - 1;
                Transform t = _boxes[last];
                _boxes.RemoveAt(last);
                if (t != null) DestroyObject(t.gameObject);
            }
            while (_boxes.Count < need)
                _boxes.Add(CreateBox(_boxes.Count));
        }

        private Transform CreateBox(int index)
        {
            // Cube プリミティブを使う（自前で 24 頂点を組むより読みやすく、Unity 側の共有メッシュに乗る）。
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"[CgRoomBox{index}]";
            go.transform.SetParent(transform, false);
            if (_layer >= 0) go.layer = _layer;

            // 物理は一切要らない。付いたままだと体験者のコライダや raycast に引っかかる。
            Collider? col = go.GetComponent<Collider>();
            if (col != null) DestroyObject(col);

            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _occluderMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go.transform;
        }

        private bool EnsureMaterial()
        {
            if (_occluderMat != null) return true;

            var loaded = Resources.Load<Material>(OccluderMaterialResource);
            Shader? shader = loaded != null ? loaded.shader : Shader.Find(OccluderShaderName);
            if (shader == null)
            {
                if (!_warnedNoMaterial)
                {
                    _warnedNoMaterial = true;
                    Debug.LogWarning($"[ShowRoomProxy] '{OccluderMaterialResource}' も '{OccluderShaderName}' も" +
                                     " 見つからない → オクルージョンなしで続行（人形は壁の裏でも見える）");
                }
                return false;
            }
            // 資産そのものを実行時に汚さないよう複製して持つ（Editor で .mat が dirty になるのを防ぐ）。
            _occluderMat = loaded != null ? new Material(loaded) : new Material(shader);
            _occluderMat.name = "ShowOccluder (runtime)";
            return true;
        }

        private void Place()
        {
            if (_boxes.Count == 0) return;
            float courseYaw = CourseYawDeg();
            int n = Mathf.Min(_boxes.Count, _spec.Count);
            for (int i = 0; i < n; i++)
            {
                Transform t = _boxes[i];
                if (t == null) continue;
                ShowRoomProxyLogic.Box b = _spec[i];
                t.position = CourseToWorld(new Vector2(b.center.x, b.center.z), b.center.y);
                t.rotation = Quaternion.Euler(0f, courseYaw + b.yawDeg, 0f);
                t.localScale = b.size;
            }
        }

        private Vector3 CourseToWorld(Vector2 xz, float y)
        {
            var f = _showControl?.CourseToWorldProvider;
            return f != null ? f(xz, y) : new Vector3(xz.x, y, xz.y);
        }

        private float CourseYawDeg()
        {
            var f = _showControl?.CourseYawProvider;
            return f != null ? f() : 0f;
        }

        // Edit Mode（Editor プレビューツール）から破棄される経路があるため分岐する。
        private static void DestroyObject(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }

    /// <summary>
    /// <see cref="ShowRoomProxy"/> の幾何計算を Unity のシーン API から分離した純ロジック（EditMode テスト用）。
    /// course 空間の <c>layout.room</c> 定義 → 「中心・寸法・yaw」のボックス列へ落とすところまでを担う。
    /// course→world 変換と GameObject 生成は呼び出し側に残す。
    /// </summary>
    public static class ShowRoomProxyLogic
    {
        /// <summary>プロキシのボックス 1 個。<c>center</c> は course 空間（x,z は XZ・y は高さ）。</summary>
        public struct Box
        {
            /// <summary>course 空間の中心（x, y, z）。y は床からではなく course の絶対高さ。</summary>
            public Vector3 center;
            /// <summary>ローカル寸法。x = 長さ（ローカル +X 方向）、y = 高さ、z = 厚み / 奥行き。</summary>
            public Vector3 size;
            /// <summary>course 空間の yaw（度）。ローカル +X をどちらへ向けるか。</summary>
            public float yawDeg;
        }

        /// <summary>
        /// 部屋定義からプロキシのボックス列を作る。壁 → 箱の順（安定した順序＝生成物の再利用が効く）。
        /// 退化した壁・箱（長さ 0・高さ 0・厚み 0）は**捨てる** — 潰れた箱は深度を書かないので
        /// オクルーダとして無意味なうえ、Unity が scale 0 で警告を出す。
        /// </summary>
        public static List<Box> Build(ShowRoomDef? room)
        {
            var list = new List<Box>();
            if (room == null) return list;

            if (room.walls != null)
                foreach (ShowRoomWallDef w in room.walls)
                {
                    if (w == null || !w.IsUsable()) continue;
                    list.Add(WallBox(w, room.floorY));
                }

            if (room.props != null)
                foreach (ShowRoomBoxDef b in room.props)
                {
                    if (b == null || !b.IsUsable()) continue;
                    list.Add(PropBox(b, room.floorY));
                }

            return list;
        }

        /// <summary>
        /// 壁の線分 → ボックス。ローカル +X が壁の長さ方向・+Z が厚み方向になるよう yaw を決める。
        /// <c>Quaternion.Euler(0, yaw, 0) * Vector3.right == (cos yaw, 0, -sin yaw)</c> なので、
        /// 線分方向 (dx, dz) に合わせるには <c>yaw = atan2(-dz, dx)</c>（符号を落とすと壁が 90° 転ぶ）。
        /// </summary>
        public static Box WallBox(ShowRoomWallDef w, float floorY)
        {
            float dx = w.x2 - w.x1;
            float dz = w.z2 - w.z1;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            return new Box
            {
                center = new Vector3((w.x1 + w.x2) * 0.5f, floorY + w.h * 0.5f, (w.z1 + w.z2) * 0.5f),
                size = new Vector3(len, w.h, Mathf.Max(0.005f, w.thick)),
                yawDeg = Mathf.Atan2(-dz, dx) * Mathf.Rad2Deg,
            };
        }

        /// <summary>
        /// 箱（机・柱）→ ボックス。<c>y</c> は**床からの底面高さ**（浮かせたい時だけ使う）なので、
        /// 中心は <c>floorY + y + h/2</c>。ここを底面のまま渡すと箱が床に半分埋まる。
        /// </summary>
        public static Box PropBox(ShowRoomBoxDef b, float floorY)
            => new Box
            {
                center = new Vector3(b.x, floorY + b.y + b.h * 0.5f, b.z),
                size = new Vector3(b.w, b.h, b.d),
                yawDeg = b.yawDeg,
            };

        /// <summary>
        /// 床の矩形（course 空間）。**course 原点中心**で、<c>layout.grid</c> の
        /// <c>ZoneLayoutSolver.CellRect</c>（col0 = 西 x=-w/2 / row0 = 北 z=+d/2）と同じ置き方にする。
        /// 揃えないと「塗ったタイルの上に部屋が乗らない」状態になる。
        /// </summary>
        public static void FloorRect(ShowRoomDef room,
                                     out float xLo, out float xHi, out float zLo, out float zHi)
        {
            float hw = Mathf.Max(0f, room.floorW) * 0.5f;
            float hd = Mathf.Max(0f, room.floorD) * 0.5f;
            xLo = -hw; xHi = hw;
            zLo = -hd; zHi = hd;
        }
    }
}
