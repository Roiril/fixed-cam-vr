#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 帰還の別れの人形（2026-10-30・<c>canon/LEDGER.md</c> 0291）。
    ///
    /// 最後の異常を報告して体験者が人形から戻った後、締めで使った CG 人形を 1 体だけ、
    /// 画面の中で体験者の左に右向きで立たせる。少し見つめた後で後ろを向き、ゆっくり下がって消える。
    /// 帰還End の写真は人形が体験者の方を向いている間に撮る（右に体験者・左に人形）。
    ///
    /// 時刻表と段の進み、立ち位置の解き方をここに置く。Unity の状態は引数で受ける（EditMode で固定する）。
    /// 実行体は <see cref="EndingFarewellDoll"/>、人形を描くのは <see cref="Cg.ShowCgLayer"/> の別れのモード。
    /// </summary>
    public sealed class EndingFarewellLogic
    {
        public enum Phase
        {
            /// <summary>まだ始まっていない（報告前・入れ替わりの最中）。</summary>
            Idle = 0,
            /// <summary>人形を出した。立ち位置が解けるのを待っている（まだ見えない）。</summary>
            Anchoring = 1,
            /// <summary>体験者の方を向いたまま現れる。</summary>
            Appear = 2,
            /// <summary>体験者の方を向いて立っている。帰還End の写真はこの段で撮る。</summary>
            Face = 3,
            /// <summary>後ろを向く。</summary>
            Turn = 4,
            /// <summary>ゆっくり下がりながら消える。</summary>
            Retreat = 5,
            /// <summary>消えた。同じ体験者では 2 度出さない。</summary>
            Done = 6,
            /// <summary>立てられなかった（較正・位置合わせ・頭の追跡が無い）。写真は人形なしで撮る。</summary>
            Unavailable = 7,
        }

        // ---- 時刻表 -------------------------------------------------------------------
        //
        // 締めのライブのカットは報告から 7.1 秒（show.json）。そのうち入れ替わり（人形 → 人）が 2.6 秒を使うので、
        // 人形に残るのは約 4.5 秒。全体を 4.25 秒に収め、電源断の前に消え切るようにする。

        /// <summary>現れる（不透明度 0 → 1）。</summary>
        public const float AppearSec = 0.35f;
        /// <summary>体験者の方を向いて立つ。写真の窓。</summary>
        public const float FaceSec = 1.15f;
        /// <summary>後ろを向く。</summary>
        public const float TurnSec = 0.75f;
        /// <summary>下がる。最後の <see cref="FadeOutSec"/> で消える。</summary>
        public const float RetreatSec = 2.0f;
        /// <summary>下がりながら消える尺。</summary>
        public const float FadeOutSec = 1.1f;
        /// <summary>下がる距離 (m)。0.22 m/s 前後の「ゆっくり」。</summary>
        public const float RetreatM = 0.45f;
        /// <summary>立ち位置が解けるのを待つ上限。超えたら出さない。</summary>
        public const float AnchorTimeoutSec = 0.5f;

        public const float TotalSec = AppearSec + FaceSec + TurnSec + RetreatSec;

        // ---- 立ち位置 -----------------------------------------------------------------

        /// <summary>画面の中で体験者の足元から左へずらす量（枠 UV の横幅に対する割合）。</summary>
        public const float ScreenGapU = 0.18f;
        /// <summary>人形の足元を寄せてよい左端（枠 UV）。これより左は画の縁に掛かる。</summary>
        public const float MinU = 0.10f;
        /// <summary>体験者との床の上の距離の範囲 (m)。外れたらカメラの左右方向へこの範囲で置き直す。</summary>
        public const float MinGapM = 0.5f;
        public const float MaxGapM = 1.2f;
        /// <summary>下がる向きに混ぜる「カメラから遠ざかる」成分。0 なら体験者から真横へ離れるだけ。</summary>
        public const float AwayFromCameraMix = 0.6f;

        /// <summary>1 フレームの見え方。</summary>
        public struct Sample
        {
            public Phase phase;
            /// <summary>人形の不透明度 0..1（CG 層の強さ）。</summary>
            public float alpha;
            /// <summary>体験者の方 → 後ろ の回り具合 0..1（緩急つき）。</summary>
            public float turn01;
            /// <summary>立ち位置から下がった距離 (m)。</summary>
            public float retreatM;

            /// <summary>写真を撮ってよい（体験者の方を向いて不透明に立っている）。</summary>
            public bool Facing => phase == Phase.Face;
        }

        /// <summary>1 フレームの判定結果。実行体はこの指示どおりに人形を出す・消す。</summary>
        public struct Step
        {
            public Sample sample;
            /// <summary>この縁で人形を出す（<see cref="Cg.ShowCgLayer.BeginFarewell"/>）。</summary>
            public bool begin;
            /// <summary>この縁で人形を消す（<see cref="Cg.ShowCgLayer.Hide"/>）。</summary>
            public bool hide;
        }

        public Phase Current { get; private set; } = Phase.Idle;
        public float ElapsedSec { get; private set; }
        private float _anchorWaitSec;

        /// <summary>写真の窓が別れの人形を待たずに開いてよいか（人形が出る前・出せなかった場合）。</summary>
        public bool AllowsPlainReleasedShot => Current == Phase.Idle || Current == Phase.Unavailable;

        /// <summary>人形が画に出ている段か（Appear〜Retreat）。</summary>
        public bool Showing => Current >= Phase.Appear && Current <= Phase.Retreat;

        /// <summary>体験者の交代・締めの外で呼ぶ。</summary>
        public void Reset()
        {
            Current = Phase.Idle;
            ElapsedSec = 0f;
            _anchorWaitSec = 0f;
        }

        /// <summary>
        /// 1 フレーム進める。
        /// </summary>
        /// <param name="dt">経過秒。</param>
        /// <param name="endingTakeActive">締めの take が走っているか。外れたら畳んで Idle へ戻す。</param>
        /// <param name="releasedLiveReady">報告済み・ライブのカット・入れ替わりと乱れの遷移が終わっている。</param>
        /// <param name="cgFree">CG 層が空いている（入れ替わりの人形が消え切った）。</param>
        /// <param name="beginAccepted">前フレームの <see cref="Step.begin"/> を CG 層が受けたか。</param>
        /// <param name="anchored">CG 層が立ち位置を解けたか。</param>
        /// <param name="cgShowing">CG 層が別れの人形をまだ持っているか（外から畳まれたら false）。</param>
        public Step Tick(float dt, bool endingTakeActive, bool releasedLiveReady, bool cgFree,
                         bool beginAccepted, bool anchored, bool cgShowing)
        {
            var step = new Step();
            dt = Mathf.Max(0f, dt);
            if (!endingTakeActive)
            {
                // 締めが終わった（電源断へ進んだ・中止・体験者の交代）。出ていれば畳む。
                step.hide = Current == Phase.Anchoring || Showing;
                Reset();
                step.sample = SampleAt(-1f);
                return step;
            }

            switch (Current)
            {
                case Phase.Idle:
                    if (releasedLiveReady && cgFree)
                    {
                        step.begin = true;
                        Current = Phase.Anchoring;
                        _anchorWaitSec = 0f;
                    }
                    break;

                case Phase.Anchoring:
                    if (!beginAccepted)
                    {
                        Current = Phase.Unavailable;
                        break;
                    }
                    if (anchored)
                    {
                        Current = Phase.Appear;
                        ElapsedSec = 0f;
                        break;
                    }
                    _anchorWaitSec += dt;
                    if (_anchorWaitSec >= AnchorTimeoutSec)
                    {
                        step.hide = true;
                        Current = Phase.Unavailable;
                    }
                    break;

                case Phase.Appear:
                case Phase.Face:
                case Phase.Turn:
                case Phase.Retreat:
                    if (!cgShowing)
                    {
                        // 誰かが CG 層を畳んだ（カットの終わり・中止）。もう出さない。
                        Current = Phase.Done;
                        break;
                    }
                    ElapsedSec += dt;
                    Current = SampleAt(ElapsedSec).phase;
                    if (Current == Phase.Done) step.hide = true;
                    break;
            }

            step.sample = Current == Phase.Anchoring
                ? new Sample { phase = Phase.Anchoring }
                : Showing ? SampleAt(ElapsedSec) : SampleAt(-1f);
            if (!Showing && Current != Phase.Anchoring) step.sample.phase = Current;
            return step;
        }

        /// <summary>
        /// 時刻表の純関数。<paramref name="t"/> は現れ始めからの秒。負なら何も出ていない。
        /// Editor のプレビューも同じ関数で段を描く。
        /// </summary>
        public static Sample SampleAt(float t)
        {
            if (t < 0f) return new Sample { phase = Phase.Idle };
            if (t >= TotalSec) return new Sample { phase = Phase.Done };

            if (t < AppearSec)
                return new Sample { phase = Phase.Appear, alpha = Smooth(t / AppearSec) };
            t -= AppearSec;
            if (t < FaceSec)
                return new Sample { phase = Phase.Face, alpha = 1f };
            t -= FaceSec;
            if (t < TurnSec)
                return new Sample { phase = Phase.Turn, alpha = 1f, turn01 = Smooth(t / TurnSec) };
            t -= TurnSec;

            // 下がる。歩き出しだけ緩め、あとは一定の速さ（止まりながら消えると「置かれた」に見える）。
            float u = Mathf.Clamp01(t / RetreatSec);
            const float ease = 0.25f;
            float dist = u < ease
                ? u * u / (2f * ease)
                : ease / 2f + (u - ease);
            dist /= 1f - ease / 2f;
            float fadeStart = RetreatSec - FadeOutSec;
            float alpha = t <= fadeStart ? 1f : 1f - Smooth((t - fadeStart) / FadeOutSec);
            return new Sample { phase = Phase.Retreat, alpha = alpha, turn01 = 1f, retreatM = RetreatM * dist };
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        // ---- 立ち位置を解く -------------------------------------------------------------

        /// <summary>解けた立ち位置。</summary>
        public struct Anchor
        {
            /// <summary>人形の足元（ワールド）。</summary>
            public Vector3 foot;
            /// <summary>下がる向き（床の上の単位ベクトル）。</summary>
            public Vector3 retreatDir;
            /// <summary>後ろを向いた後のヨー（度）。</summary>
            public float awayYawDeg;
            /// <summary>人形と体験者の足元の枠 UV（観測用）。</summary>
            public Vector2 dollUv, visitorUv;
            /// <summary>画面のずらしで決まったか（false ならカメラの左右方向で置き直した）。</summary>
            public bool fromScreen;
        }

        /// <summary>
        /// 体験者の足元から、画面の中で左へ <see cref="ScreenGapU"/> ずらした床の点に人形を立てる。
        /// 床は体験者の足元と同じ高さの水平面。
        /// </summary>
        /// <param name="cam">人形を描く仮想カメラ（実カメラの較正済み姿勢）。</param>
        /// <param name="containScale">ライブの contain-fit（<c>MjpegScreen.ContainScale</c>）。</param>
        /// <param name="visitorFoot">体験者の足元（ワールド）。</param>
        public static bool TrySolve(Camera cam, Vector2 containScale, Vector3 visitorFoot, out Anchor anchor)
        {
            anchor = default;
            if (cam == null) return false;
            if (!Cg.ShowCgLayer.TryFrameUv(cam, visitorFoot, containScale, out Vector2 visitorUv)) return false;

            Vector3 camPos = cam.transform.position;
            Vector3 camRight = Flat(cam.transform.right);
            if (camRight.sqrMagnitude < 1e-6f) return false;
            camRight.Normalize();

            bool fromScreen = false;
            Vector3 foot = visitorFoot;
            float u = Mathf.Max(MinU, visitorUv.x - ScreenGapU);
            if (TryFloorAtFrameUv(cam, containScale, new Vector2(u, visitorUv.y), visitorFoot.y, out Vector3 hit))
            {
                float gap = Flat(hit - visitorFoot).magnitude;
                if (gap >= MinGapM && gap <= MaxGapM && Vector3.Dot(hit - visitorFoot, camRight) < 0f)
                {
                    foot = hit;
                    fromScreen = true;
                }
                else if (Vector3.Dot(hit - visitorFoot, camRight) < 0f)
                {
                    foot = visitorFoot - camRight * Mathf.Clamp(gap, MinGapM, MaxGapM);
                }
                else
                {
                    foot = visitorFoot - camRight * MinGapM;
                }
            }
            else
            {
                foot = visitorFoot - camRight * MinGapM;
            }
            foot.y = visitorFoot.y;

            Vector3 awayFromVisitor = Flat(foot - visitorFoot);
            Vector3 awayFromCamera = Flat(foot - camPos);
            if (awayFromVisitor.sqrMagnitude < 1e-6f) awayFromVisitor = -camRight;
            Vector3 dir = awayFromVisitor.normalized
                          + (awayFromCamera.sqrMagnitude > 1e-6f ? awayFromCamera.normalized * AwayFromCameraMix : Vector3.zero);
            if (dir.sqrMagnitude < 1e-6f) dir = -camRight;
            dir.Normalize();

            Cg.ShowCgLayer.TryFrameUv(cam, foot, containScale, out Vector2 dollUv);
            anchor = new Anchor
            {
                foot = foot,
                retreatDir = dir,
                awayYawDeg = YawOf(dir),
                dollUv = dollUv,
                visitorUv = visitorUv,
                fromScreen = fromScreen,
            };
            return true;
        }

        /// <summary>人形から体験者を向くヨー（度）。</summary>
        public static float FaceYawDeg(Vector3 dollFoot, Vector3 visitorFoot)
        {
            Vector3 d = Flat(visitorFoot - dollFoot);
            return d.sqrMagnitude < 1e-6f ? 0f : YawOf(d);
        }

        /// <summary>立っている向きと後ろを向いた向きの間を回る（短い側）。</summary>
        public static float TurnYawDeg(float faceYawDeg, float awayYawDeg, float turn01)
            => Mathf.LerpAngle(faceYawDeg, awayYawDeg, Mathf.Clamp01(turn01));

        /// <summary>
        /// 枠 UV の点を床（高さ <paramref name="floorY"/>）へ戻す。
        /// <see cref="Cg.ShowCgLayer.TryFrameUv"/> の逆（<c>p = (uv - 0.5) / s + 0.5</c>）。
        /// </summary>
        public static bool TryFloorAtFrameUv(Camera cam, Vector2 containScale, Vector2 frameUv, float floorY,
                                             out Vector3 hit)
        {
            hit = Vector3.zero;
            if (cam == null || containScale.x <= 1e-4f || containScale.y <= 1e-4f) return false;
            var vp = new Vector3((frameUv.x - 0.5f) / containScale.x + 0.5f,
                                 (frameUv.y - 0.5f) / containScale.y + 0.5f, 0f);
            Ray ray = cam.ViewportPointToRay(vp);
            if (ray.direction.y > -1e-4f) return false;
            float t = (floorY - ray.origin.y) / ray.direction.y;
            if (t <= 0f) return false;
            hit = ray.origin + ray.direction * t;
            return true;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
        private static float YawOf(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
    }
}
