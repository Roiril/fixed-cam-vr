#nullable enable

using System.IO;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// タイトル画面（<see cref="TitleScreen"/>）の見た目を PNG で出す。<b>Play も HMD も要らない。</b>
    ///
    /// 出すのは実物の <see cref="TitleScreen"/> と <see cref="TitleLogic"/> をそのまま動かした絵
    /// （プレビュー専用の組み立てを別に書くと、卓と実機が食い違うのと同じ事故が起きる）。
    ///
    /// <b>背景には本物の封印の箱を置く。</b> 依頼の要求「一瞬でも壁が見えてはいけない」は
    /// 「黒が開いたとき何が見えているか」の話なので、単色の背景で撮ると判定そのものができない。
    ///
    /// ⚠ <b>これでも実機の判定にはならない。</b> 立体感・大きさ・怖さは被らないと分からない
    /// （<c>.claude/rules/visual-verification.md</c>）。ここは<b>落とすためだけの安い門</b>。
    /// </summary>
    public static class TitlePreview
    {
        private const string OutDir = "Assets/Screenshots/title";

        /// <summary>閉じる演出の連番。<b>Assets の外</b>（毎回 100 枚をインポートさせない）。</summary>
        private const string SeqDir = "logs/title";

        private const int Width = 1280;
        private const int Height = 1024;

        /// <summary>Quest 3 の片眼に近い縦画角。**大きさの判断はここに依存する**ので変えない。</summary>
        private const float FovDeg = 82f;

        /// <summary>両眼の確認に使う眼のずれ (m)。厚みが視差で出るかを見る。</summary>
        private const float HalfIpdM = 0.032f;

        private const int SeqFrames = 56;
        private const float SeqDt = 1f / 20f;

        /// <summary>
        /// 題字が写っているとみなす最小の明るさ（0..255）。
        /// 墨は生成りで sRGB およそ 242。黒だけなら 13・封印の箱と地だけなら 55。
        /// </summary>
        private const int GlyphPeakMin = 120;

        private static readonly Vector3 EyePos = new Vector3(0f, 1.6f, -3.4f);
        private static readonly Vector3 LookAt = new Vector3(0f, 1.15f, 0f);

        [MenuItem("Tools/FixedCamVr/Diagnostics/Preview Title Screen", priority = 237)]
        public static void Run()
        {
            Directory.CreateDirectory(OutDir);

            GameObject? box = BuildSealedBoxStandIn();
            var camGo = new GameObject("[TitlePreviewCam]") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            // パススルーの代わりの地。**明るすぎない灰**（実際の部屋は暗い）。
            cam.backgroundColor = new Color(0.14f, 0.14f, 0.135f, 1f);
            cam.fieldOfView = FovDeg;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.transform.position = EyePos;
            cam.transform.rotation = Quaternion.LookRotation(LookAt - EyePos, Vector3.up);

            var titleGo = new GameObject("[TitlePreview]") { hideFlags = HideFlags.HideAndDontSave };
            titleGo.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
            var title = titleGo.AddComponent<TitleScreen>();
            title.EditorBuild();
            if (!title.IsBuilt)
            {
                Debug.LogError("[TitlePreview] タイトルの実体を組めませんでした。" +
                               "シェーダ（FixedCamVr/TitleVeil・TitleGlyph）と " +
                               "Resources/Title/MawarimiTitle を確認すること。");
                Cleanup(titleGo, camGo, box);
                return;
            }

            // ---- 出現の途中と、出し切り ----
            Shot(cam, title, WeightsAt(0.30f), 0f, "in_030");
            Shot(cam, title, WeightsAt(0.70f), 0f, "in_070");
            TitleWeights hold = WeightsAt(1f);
            int holdPeak = Shot(cam, title, hold, 0f, "hold");
            // 光が走っている別の時刻（走りが止まって見えないかの確認）。
            Shot(cam, title, hold, 2.6f, "hold_t26");

            // ⚠ **厚みは片眼では判定できない**（rules/visual-verification.md §9）。
            //    左右の眼から撮って、側面の出方が逆になっているかを見る。
            EyeShot(cam, title, hold, -HalfIpdM, "hold_L");
            EyeShot(cam, title, hold, +HalfIpdM, "hold_R");

            // ---- A を押してから ----
            TitleLogic outLogic = Summoned();
            Advance(outLogic, TitleLogic.InDelaySec + TitleLogic.InSec + 0.1f);
            outLogic.RequestDismiss();
            // ⚠ <b>StepTo は「そこから何秒進めるか」</b>。閉じる演出は 1.35 秒しか無いので、
            //    累計が超えると段が Done へ抜けて**真っ白な絵を撮ることになる**（前はそうなっていた）。
            //    括弧内は A からの通算。
            Shot(cam, title, StepTo(outLogic, 0.18f), 0f, "out_flash");       // 0.18 閃光
            Shot(cam, title, StepTo(outLogic, 0.32f), 0f, "out_burn_early");  // 0.50 外周に火が回る
            Shot(cam, title, StepTo(outLogic, 0.40f), 0f, "out_burn_mid");    // 0.90 巻き込みと焦げ
            Shot(cam, title, StepTo(outLogic, 0.30f), 0f, "out_ash");         // 1.20 灰だけ
            Shot(cam, title, TitleWeights.Hidden, 0f, "done");

            Sequence(cam, title, box);

            Cleanup(titleGo, camGo, box);
            AssetDatabase.Refresh();

            // ⚠⚠ **「撮れた」ではなく「画に出た」を数える。** 2026-08-13 に、段の追加へ
            //    プレビューが追随できておらず、**10 枚とも真っ黒なのに成功と報告した**。
            //    墨は生成り（sRGB でおよそ 242）なので、題字が立っていれば必ずここを超える。
            //    黒だけなら 13・封印の箱と地だけなら 55 にしかならない。
            if (holdPeak < GlyphPeakMin)
            {
                Debug.LogError(
                    $"[TitlePreview] hold に題字が写っていない（最大 {holdPeak} < {GlyphPeakMin}）。" +
                    "段が Wait のままか、TitleGlyph が 1 画素も描いていない。**絵を信用しないこと**");
            }

            Debug.Log($"[TitlePreview] 静止画 10 枚 → {OutDir} / 閉じる演出 {SeqFrames} 枚 → {SeqDir}" +
                      $"（hold の最大 {holdPeak}）。" +
                      "**done.png に壁（背景の灰）が箱の外にしか無いことを必ず見る**");
        }

        /// <summary>
        /// 背景の封印の箱。**単色の背景で撮ると「黒が開いたとき何が見えるか」を判定できない**ので、
        /// 実際に体験者が見るのと同じものを置く（<see cref="SealedBoxPreview"/> と同じ寸法）。
        /// </summary>
        private static GameObject? BuildSealedBoxStandIn()
        {
            Shader? s = Shader.Find(SealedBox.ShaderName);
            if (s == null)
            {
                Debug.LogWarning($"[TitlePreview] {SealedBox.ShaderName} が無いので背景の箱は出しません。");
                return null;
            }
            var mat = new Material(s) { name = "SealedBox (title preview)", hideFlags = HideFlags.HideAndDontSave };
            mat.SetFloat("_Opacity", 1f);
            mat.SetFloat("_HexSizeM", 0.45f);
            mat.SetFloat("_GlowGain", 1f);
            mat.SetVector("_BoxSize", new Vector4(3f, 2.4f, 3f, 0f));
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "[TitlePreviewBox]";
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = new Vector3(0f, 1.2f, 0f);
            go.transform.localScale = new Vector3(3f, 2.4f, 3f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>
        /// 題字が立っている状態を作る。
        ///
        /// ⚠⚠ <b>`Begin()` だけでは題字は出ない。</b> 2026-08-12 に段
        /// <see cref="TitleStage.Wait"/>（真っ暗・A 待ち）が入ってから、
        /// <see cref="TitleLogic.RequestAdvance"/> を呼ばないと**黒しか出ない**。
        /// このプレビューはそれを直し忘れていて、**10 枚とも真っ黒な絵を出しながら成功と報告していた**
        /// （2026-08-13 に発覚）。ここを通さない経路を新しく作らないこと。
        /// </summary>
        private static TitleLogic Summoned()
        {
            var l = new TitleLogic();
            l.Begin();
            l.RequestAdvance();          // ← A。これが無いと段は Wait のまま
            return l;
        }

        private static TitleWeights WeightsAt(float reveal)
        {
            TitleLogic l = Summoned();
            Advance(l, TitleLogic.InDelaySec + TitleLogic.InSec * Mathf.Clamp01(reveal) + 0.001f);
            return l.Weights;
        }

        private static void Advance(TitleLogic l, float sec)
        {
            const float dt = 1f / 60f;
            var input = new TitleInput { concealReady = true };
            for (float t = 0f; t < sec; t += dt) l.Tick(dt, input);
        }

        private static TitleWeights StepTo(TitleLogic l, float sec)
        {
            Advance(l, sec);
            return l.Weights;
        }

        /// <summary>1 枚撮って、<b>その絵の最大の明るさ</b>（0..255）を返す。</summary>
        private static int Shot(Camera cam, TitleScreen title, in TitleWeights w, float phase, string name)
        {
            title.Apply(w, phase);
            return Capture(cam, Path.Combine(OutDir, $"title_{name}.png"));
        }

        private static void EyeShot(Camera cam, TitleScreen title, in TitleWeights w, float dx, string name)
        {
            Vector3 keep = cam.transform.position;
            cam.transform.position = keep + cam.transform.right * dx;
            // タイトルは頭に付いているので、眼ではなく**頭**の位置に留める（動かすのは眼だけ）。
            title.Apply(w, 0f);
            Capture(cam, Path.Combine(OutDir, $"title_{name}.png"));
            cam.transform.position = keep;
        }

        private static void Sequence(Camera cam, TitleScreen title, GameObject? box)
        {
            Directory.CreateDirectory(SeqDir);
            foreach (string old in Directory.GetFiles(SeqDir, "seq_*.png")) File.Delete(old);

            TitleLogic l = Summoned();
            Advance(l, TitleLogic.InDelaySec + TitleLogic.InSec + 0.2f);
            l.RequestDismiss();
            var input = new TitleInput { concealReady = true };
            for (int f = 0; f < SeqFrames; f++)
            {
                title.Apply(l.Weights, f * SeqDt);
                Capture(cam, Path.Combine(SeqDir, $"seq_{f:D3}.png"));
                l.Tick(SeqDt, input);
            }
        }

        /// <summary>撮って書き出し、<b>最大の明るさ</b>（0..255）を返す。</summary>
        private static int Capture(Camera cam, string path)
        {
            // ⚠ sRGB を明示する。プロジェクトは Linear なので、linear のまま PNG へ書くと
            //    **実際より 2 段暗い絵**になり、明るさの判断を丸ごと誤る。
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                       RenderTextureReadWrite.sRGB)
            {
                antiAliasing = 4,
            };
            cam.targetTexture = rt;
            cam.Render();

            // ⚠ **MSAA の RT から直接 ReadPixels しない。** 解決前の面を読むので斑点が混じる。
            var resolved = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32,
                                             RenderTextureReadWrite.sRGB);
            Graphics.Blit(rt, resolved);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = resolved;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;

            File.WriteAllBytes(path, tex.EncodeToPNG());

            // 最大の明るさ。**縮小した目視では 13 と 242 の区別が付かない**ので数で持つ。
            int peak = 0;
            Color32[] px = tex.GetPixels32();
            for (int k = 0; k < px.Length; k += 7)      // 7 画素おきで足りる（最大値だけ見る）
            {
                Color32 c = px[k];
                int m = c.r > c.g ? c.r : c.g;
                if (c.b > m) m = c.b;
                if (m > peak) peak = m;
            }

            Object.DestroyImmediate(tex);
            resolved.Release();
            Object.DestroyImmediate(resolved);
            rt.Release();
            Object.DestroyImmediate(rt);
            return peak;
        }

        private static void Cleanup(GameObject titleGo, GameObject camGo, GameObject? box)
        {
            Object.DestroyImmediate(titleGo);
            Object.DestroyImmediate(camGo);
            if (box != null) Object.DestroyImmediate(box);
        }
    }
}
