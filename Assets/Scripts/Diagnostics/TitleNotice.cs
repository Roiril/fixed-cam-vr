#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// **体験前の注意書き。周回リセット直後の真っ暗な待ちの中だけに出る。**
    ///
    /// 立ち位置は <see cref="TitleStage.Wait"/>（黒だけが立っていて A を待っている段）で、
    /// 題字と同じ場所に注意事項を置く。A が押されて題字が立ち上がったら消える
    /// （<see cref="TitleStage.In"/> 以降は <see cref="ShouldShow"/> が false になる）。
    ///
    /// ⚠ **これはホラー体験の入口に置く安全のための掲示**で、
    /// 「体験者の視界には文字を 1 つも出さない」（rules/show-design.md）の対象外。
    /// 世界観に混ざらないよう、出るのは<b>まだ何も始まっていない黒の中だけ</b>に閉じてある。
    ///
    /// ⚠ **失敗したら黙って出さない側へ倒す**。日本語フォントが解決できない・実体を組めない
    /// ときは 1 文字も出さずに黙る（<see cref="TitleScreen"/> の「タイトルが組めない現場で
    /// 体験が二度と始まらない」と同じ流儀で、この面が体験を止めることは無い）。
    ///
    /// ⚠ 文言に新しい漢字・記号を足したら `Tools/FixedCamVr/Setup/Generate Japanese HUD Font` を
    /// 再実行する（静的ベイクなので忘れると実機で豆腐になる）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TitleNotice : MonoBehaviour
    {
        [Tooltip("段の供給元。null ならシーンから探す。居なければ何も出さない。")]
        [SerializeField] private TitleScreen? titleScreen;

        [Tooltip("頭からの距離 (m)。題字（TitleScreen.distanceM）と同じ所に立てる。")]
        [SerializeField, Min(0.5f)] private float distanceM = 2.6f;

        [Tooltip("視線中心からどれだけ下に置くか (度)。題字と同じ据わりにする。")]
        [SerializeField, Range(-20f, 20f)] private float pitchOffsetDeg = 2.0f;

        [Tooltip("大きさの倍率。**実機の画で StatusHud と 1 文字の px を比べて決める。**")]
        // ⚠⚠ **0.07 は「1 文字 7cm」ではなかった**（2026-08-14 に実機の画で判明）。
        //    3D の TextMeshPro の fontSize は世界 m ではないので、
        //    「24 文字 × 0.07 = 1.68m ＝ 2.6m 先で 36°」という見積もりが丸ごと外れていて、
        //    実機では **StatusHud の 1/8.5** ＝ 読めない大きさの白い点線に見えていた
        //    （ユーザー報告「すごく奥に小さい白い文字」・`canon/LEDGER.md` 0035）。
        //    ⇒ 実機の画で **StatusHud の 1 文字 28px に対して 3.3px** と測り、**8.5 倍**した。
        //      **単位を理屈で決めず、同じ画の中で比べて決める。**
        //    ⚠ 倍率は **transform の scale** で掛ける（fontSize を上げない）。字を大きくすると
        //      メッシュの座標そのものが 10 倍以上に広がり、視錐台の外へ大きくはみ出す。
        [SerializeField, Min(0.1f)] private float sizeScale = 8.5f;

        [Tooltip("出るまでの秒。黒の中にすっと現れる。")]
        [SerializeField, Min(0.01f)] private float fadeInSec = 0.3f;

        [Tooltip("消えるまでの秒。ぱっと消すと題字の立ち上がりと喧嘩する。")]
        [SerializeField, Min(0.01f)] private float fadeOutSec = 0.2f;

        /// <summary>
        /// 注意書きの本文。<b>改行の位置まで含めてここが唯一の供給元</b>。
        /// 空行は「体験の性質」と「気分が悪くなったら」を切る間で、詰めると 1 つの文に読める。
        /// </summary>
        private const string NoticeText =
            "この体験にはホラー表現が含まれます\n" +
            "気分が悪くなった場合は、すぐにヘッドセットを外し\n" +
            "スタッフへお声がけください";

        /// <summary>
        /// タイトルの黒（<c>FixedCamVr/TitleVeil</c> = 4950）と題字（<c>TitleGlyph</c> = 4960）より
        /// 後に描くための Queue。
        ///
        /// ⚠ **5000 を超えてはいけない**（2026-07-31 実害）。URP の透明パスが描くのは
        /// <c>RenderQueueRange.transparent</c> = [2501, 5000] だけで、超えた値はどの描画パスにも
        /// 入らず 1 ピクセルも出ない。しかも `Shader.Find` も配置も成功するので警告が 1 件も出ない。
        /// </summary>
        private const int RenderQueue = 5000;

        /// <summary>版の中の字の大きさ。<b>倍率は <see cref="sizeScale"/> が transform で掛ける。</b></summary>
        private const float FontSize = 0.07f;

        /// <summary>TMP の Overlay 版（<c>ZTest Always</c>）。<b>Always Included に入っている。</b></summary>
        private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";


        /// <summary>黒が実際に立っているとみなす不透明度（<see cref="TitleScreen.AppliedVeil"/>）。</summary>
        private const float VeilUpMin = 0.9f;

        /// <summary>供給元が見つからないときの探し直しの間隔 (s)。毎フレーム探すと只では済まない。</summary>
        private const float ResolveRetrySec = 1f;

        private TMP_Text? _text;
        private float _alpha;
        private float _resolveWait;

        private void Awake()
        {
            ResolveRefs();
            Build();
            SetAlpha(0f);
        }

        private void OnDisable()
        {
            // 次に有効化されたときは黒の中へ改めて現れる（消えかけの途中から再開しない）。
            _alpha = 0f;
            SetAlpha(0f);
        }

        private void ResolveRefs()
        {
            if (titleScreen == null) titleScreen = FindObjectOfType<TitleScreen>();
        }

        private void Build()
        {
            // ⚠ 日本語が出せないなら何も出さない。豆腐（□）が並ぶ方が、注意書きが無いより悪い。
            var jp = JapaneseHudFont.TryGet();
            if (jp == null)
            {
                Debug.LogWarning("[TitleNotice] 日本語フォントを解決できないので注意書きは出しません（体験はそのまま始まります）");
                return;
            }

            GameObject? go = null;
            try
            {
                // ⚠ 3D の TextMeshPro のまま（世界空間 Canvas へ移す利点は無かった）。
                //    2026-08-14 に Canvas 方式も試したが、絵は同じで実機ログの
                //    `Screen position out of view frustum` も減らなかった（`canon/OPEN.md`）。
                go = new GameObject("Label");
                go.transform.SetParent(transform, worldPositionStays: false);
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.font = jp;
                tmp.text = NoticeText;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = FontSize;
                // 折り返しは残す（文言を足した誰かが枠の外へ流れ出さないための安全網）。
                tmp.enableWordWrapping = true;
                tmp.richText = false;
                // 題字の朱と競合させない、抑えた白。純白だと黒の中で浮いて掲示物に見える。
                tmp.color = new Color(0.80f, 0.77f, 0.73f, 1f);

                var rt = (RectTransform)go.transform;
                // 最長行は 2 行目の 24 文字 ＝ 24 × 0.07 = 1.68（この枠に収まる）。
                rt.sizeDelta = new Vector2(2.0f, 1.0f);
                // ⚠ **大きさは scale で掛ける。** fontSize を上げるとメッシュの座標が広がるだけで、
                //    見かけの大きさは同じ。小さい字を拡大する方が、頂点の座標が素直に収まる。
                go.transform.localScale = Vector3.one * Mathf.Max(sizeScale, 0.1f);

                // 頭の正面やや下。head-lock（CenterEyeAnchor 直下に置かれる前提）なので、
                // ここでは局所の置き場所だけを決める。題字は yaw だけ追うが、この面は
                // 黒と同じく頭に貼り付いたままでよい — 読み終わるまで視界から外れない方が正しい。
                float rad = pitchOffsetDeg * Mathf.Deg2Rad;
                float d = Mathf.Max(distanceM, 0.5f);
                go.transform.localPosition = new Vector3(0f, -Mathf.Sin(rad) * d, Mathf.Cos(rad) * d);
                // 傾けない（傾けると台形に見えて、行の揃いが崩れる）。
                go.transform.localRotation = Quaternion.identity;

                // タイトルの黒に潰されないように、黒と題字より後に描く。fontMaterial の getter が
                // インスタンスを作るので、共有マテリアルを汚さない。
                // ⚠⚠ **描画順だけでは足りない。深度でも弾かれていた**（2026-08-13・LEDGER 0027）。
                //    TMP の既定シェーダ（`TextMeshPro/Distance Field`）は `ZTest [unity_GUIZTestMode]`
                //    ＝ 既定で LEqual。この面は **2.6m** に立つのに、本編のスクリーン（不透明・
                //    ZWrite On）が **2.0m** に居るので、**注意書きはスクリーンの深度に隠れて
                //    1 文字も出ていなかった**。しかも警告は 1 件も出ない。
                UseOverlayShader(tmp);
                tmp.fontMaterial.renderQueue = RenderQueue;
                _text = tmp;
            }
            catch (System.Exception e)
            {
                // 組めなかった側は必ず「出さない」で終わらせる（半端な面を残さない）。
                Debug.LogWarning($"[TitleNotice] 実体を組めません — 注意書きは出しません: {e.Message}");
                if (go != null) Destroy(go);
                _text = null;
            }
        }

        /// <summary>
        /// TMP の <b>Overlay 版</b>（<c>ZTest Always</c>）へ差し替える。
        ///
        /// 既定の <c>TextMeshPro/Distance Field</c> は ZTest を
        /// <c>unity_GUIZTestMode</c>（グローバル・既定 LEqual）で引くので、<b>マテリアルからは
        /// 上書きできない</b>。グローバルを書き換える手もあるが、それは他の全 TMP へ効く。
        ///
        /// ⚠ <b>剥がれ対策で Always Included に入れてある</b>（実行時 <c>Shader.Find</c> だけの
        /// シェーダはビルドから外される・2026-07-31 実害）。それでも見つからないときは
        /// <b>差し替えずに続ける</b> — 深度に負けて見えないかもしれないが、
        /// マテリアルを壊して字が化けるよりはよい。
        /// </summary>
        private static void UseOverlayShader(TMP_Text tmp)
        {
            var overlay = Shader.Find(OverlayShaderName);
            if (overlay == null)
            {
                Debug.LogWarning($"[TitleNotice] {OverlayShaderName} が見つかりません。" +
                                 "注意書きが本編スクリーンの深度に隠れる可能性があります");
                return;
            }
            tmp.fontMaterial.shader = overlay;
        }

        /// <summary>
        /// いま注意書きを出す段か。<b>解決できない環境では false ＝ 出さない側へ倒す。</b>
        ///
        /// 段が <see cref="TitleStage.Wait"/> なのは「タイトルが画面を持っていて、まだ A を
        /// 待っている」ときだけ（実体を組めなければ <c>TitleLogic.Disable</c> が
        /// <see cref="TitleStage.Off"/> へ落とす）。
        ///
        /// ⚠ 黒が<b>実際に立っている</b>ことも見る（「段が進んだ」ではなく「画に出た」の側）。
        /// 位置合わせ中はタイトルが時計を止めたまま面だけ畳むので、段は Wait のまま
        /// <see cref="TitleScreen.AppliedVeil"/> が 0 になる。ここを見ないと、
        /// <b>作業中のスタッフの視界に注意書きだけが浮く</b>。
        /// </summary>
        private bool ShouldShow()
        {
            if (titleScreen == null) return false;
            if (titleScreen.Stage != TitleStage.Wait) return false;
            return titleScreen.AppliedVeil >= VeilUpMin;
        }

        private void LateUpdate()
        {
            if (_text == null) return;

            if (titleScreen == null)
            {
                // 供給元がまだ居ない（プレビュー・生成順）。毎フレーム探さず、間を置いて拾い直す。
                _resolveWait += Time.unscaledDeltaTime;
                if (_resolveWait >= ResolveRetrySec)
                {
                    _resolveWait = 0f;
                    ResolveRefs();
                }
            }

            bool show = ShouldShow();
            float target = show ? 1f : 0f;
            float sec = show ? fadeInSec : fadeOutSec;
            _alpha = Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, sec));
            SetAlpha(_alpha);
        }

        private void SetAlpha(float a)
        {
            if (_text == null) return;
            _text.alpha = a;
            // 完全に消えている間は描画そのものを止める（体験中ずっと 0 の文字を描く理由が無い）。
            if (_text.gameObject.activeSelf != (a > 0.002f)) _text.gameObject.SetActive(a > 0.002f);
        }
    }
}
