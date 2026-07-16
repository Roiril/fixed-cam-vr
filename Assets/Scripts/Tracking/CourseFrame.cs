#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// course space（フロア中心原点・+Z 北）→ トラッキング空間ワールドへの**剛体変換 3 DOF**
    /// （XZ 平行移動 + yaw）を保持するコンポーネント。ゾーン群はこの 1 変換を通して配置される。
    ///
    /// identity デフォルト + persistentDataPath/registration.json の永続化を持つ。
    /// HMD 2 点登録は <see cref="CourseRegistrationController"/> が <see cref="SetRegistration"/> を叩く。
    ///
    /// 形状は show.json layout 側（PC で編集）、位置合わせだけをこの 1 変換で持つ設計。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CourseFrame : MonoBehaviour
    {
        [Tooltip("保存ファイル名（Application.persistentDataPath 直下）。")]
        [SerializeField] private string registrationFileName = "registration.json";

        [Tooltip("course 原点のワールド XZ 位置 (m)。identity は (0,0)。")]
        [SerializeField] private Vector2 originXZ = Vector2.zero;

        [Tooltip("course +Z をワールドへ向ける yaw 角 (deg)。identity は 0。")]
        [SerializeField] private float yawDeg = 0f;

        [Tooltip("Awake 時に registration.json があれば読み込んで適用する。")]
        [SerializeField] private bool loadOnAwake = true;

        /// <summary>変換（原点 or yaw）が変わった時に発火。ZoneLayoutApplier が購読して再配置する。</summary>
        public event Action? Changed;

        private bool _needsReRegistration;

        /// <summary>
        /// OS recenter（Oculus ボタン長押し等）でトラッキング原点が変わり、登録が無効になった状態。
        /// <see cref="MarkNeedsReRegistration"/> で立ち、<see cref="SetRegistration"/>/<see cref="ResetRegistration"/>
        /// で降りる。CourseRegistrationController がこれを見て「要再登録」警告を視界に出す。
        /// ゾーン動作自体は継続する（黙ってズレたまま動かない、が目的）。
        /// </summary>
        public bool NeedsReRegistration => _needsReRegistration;

        /// <summary>course 原点のワールド XZ。</summary>
        public Vector2 OriginXZ => originXZ;

        /// <summary>course +Z → ワールドの yaw (deg)。</summary>
        public float YawDeg => yawDeg;

        /// <summary>course → ワールドの回転（yaw のみ）。ゾーン OBB の向きに使う。</summary>
        public Quaternion Rotation => Quaternion.Euler(0f, yawDeg, 0f);

        private string RegistrationPath => Path.Combine(Application.persistentDataPath, registrationFileName);

        [Serializable]
        private struct RegistrationData
        {
            public float originX;
            public float originZ;
            public float yawDeg;
        }

        private void Awake()
        {
            if (loadOnAwake) LoadRegistration();
        }

        /// <summary>course space の XZ 点を、指定 y でワールド座標へ変換する。</summary>
        public Vector3 CourseToWorld(Vector2 courseXZ, float y)
        {
            Vector3 rotated = Rotation * new Vector3(courseXZ.x, 0f, courseXZ.y);
            return new Vector3(originXZ.x + rotated.x, y, originXZ.y + rotated.z);
        }

        /// <summary>ワールド座標を course space の XZ へ逆変換する（heartbeat のプレイヤードット用）。</summary>
        public Vector2 WorldToCourse(Vector3 world)
        {
            Vector3 d = new Vector3(world.x - originXZ.x, 0f, world.z - originXZ.y);
            Vector3 local = Quaternion.Euler(0f, -yawDeg, 0f) * d;
            return new Vector2(local.x, local.z);
        }

        /// <summary>
        /// 登録変換を設定する。HMD 2 点登録フェーズやナッジ微調整から呼ぶ。
        /// save=true なら registration.json へ即保存。常に <see cref="Changed"/> を発火する。
        /// </summary>
        public void SetRegistration(Vector2 newOriginXZ, float newYawDeg, bool save = true)
        {
            originXZ = newOriginXZ;
            yawDeg = newYawDeg;
            _needsReRegistration = false;
            if (save) SaveRegistration();
            Changed?.Invoke();
        }

        /// <summary>
        /// トラッキング原点が変わって登録が無効になったことを記録する（OS recenter 検知時に呼ぶ）。
        /// フラグを立てて警告ログを出すだけ。ゾーン再配置はしない（次の登録まで現状のまま動かす）。
        /// </summary>
        public void MarkNeedsReRegistration()
        {
            if (_needsReRegistration) return;
            _needsReRegistration = true;
            Debug.LogWarning("[CourseFrame] OS recenter 検知 — 登録が無効化されました。両グリップ 3 秒長押しで再登録してください。");
        }

        /// <summary>登録を identity へ戻し、保存ファイルを削除する。</summary>
        public void ResetRegistration(bool deleteFile = true)
        {
            originXZ = Vector2.zero;
            yawDeg = 0f;
            _needsReRegistration = false;
            if (deleteFile)
            {
                try { if (File.Exists(RegistrationPath)) File.Delete(RegistrationPath); }
                catch (Exception e) { Debug.LogWarning($"[CourseFrame] registration 削除失敗: {e.Message}"); }
            }
            Changed?.Invoke();
        }

        /// <summary>registration.json を読み込んで適用する（無ければ identity のまま）。</summary>
        public void LoadRegistration()
        {
            try
            {
                if (!File.Exists(RegistrationPath)) return;
                var data = JsonUtility.FromJson<RegistrationData>(File.ReadAllText(RegistrationPath));
                originXZ = new Vector2(data.originX, data.originZ);
                yawDeg = data.yawDeg;
                Debug.Log($"[CourseFrame] registration 適用: origin=({originXZ.x:F3},{originXZ.y:F3}) yaw={yawDeg:F1}°");
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CourseFrame] registration 読込失敗: {e.Message}");
            }
        }

        /// <summary>現在の登録を registration.json へ保存する。</summary>
        public void SaveRegistration()
        {
            try
            {
                var data = new RegistrationData { originX = originXZ.x, originZ = originXZ.y, yawDeg = yawDeg };
                File.WriteAllText(RegistrationPath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CourseFrame] registration 保存失敗: {e.Message}");
            }
        }
    }
}
