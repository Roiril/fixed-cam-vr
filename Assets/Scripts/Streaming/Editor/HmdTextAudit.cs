#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using FixedCamVr.Diagnostics;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// <b>HMD の中の文字を全部並べて測る。</b> 面ごとに「1 文字の見かけ角」と
    /// 「いちばん長い行が枠に収まっているか」を出し、外れていれば <c>LogError</c> で落とす。
    ///
    /// ⚠⚠ <b>照合する相手を先に作る</b>（<c>~/.claude/rules/work-style.md</c> §2）。
    /// この codebase は文字の大きさを<b>机上で 2 回続けて外している</b>
    /// （体験前の注意書き 8.5 倍・上司からの連絡 10 倍。どちらも実機の画で初めて分かった）。
    /// 目で見ても「小さい気がする」までしか言えないので、**数字にして並べる**。
    ///
    /// ⚠ 測るのは <b>TMP が実際に組んだメッシュ</b>（<c>textBounds</c> と <c>characterInfo</c>）で、
    /// 計算式ではない。透視カメラの 3D TextMeshPro が内部で 0.1 を掛けることも、
    /// Canvas の <c>lossyScale</c> も、ここでは仮定せずに実測へ入る。
    ///
    /// 使い方: <c>.\tools\unity.ps1 menu text-audit</c>（Play 不要・シーンは保存しない）。
    /// </summary>
    public static class HmdTextAudit
    {
        private const string MainScenePath = "Assets/Scenes/Main.unity";

        /// <summary>手元の面までのおよその距離 (m)。腕を自然に下ろした位置から頭まで。</summary>
        private const float HandDistanceM = 0.45f;

        /// <summary>見かけ角の許容（度）。段の値からこれ以上離れたら落とす。</summary>
        private const float TolDeg = 0.06f;

        private sealed class Surface
        {
            public string Name = "";
            public System.Type Type = typeof(MonoBehaviour);
            public string? DistanceField;      // null なら FixedDistanceM を使う
            public float FixedDistanceM;
            public float TierDeg = HmdTextStyle.BodyDeg;
            public string? Probe;              // 実行時にしか入らない面へ流し込む最長の想定文
            public string? Field;              // TMP を持つ private フィールド名（面が 2 つ持つとき）
            public bool BuildsItsOwnText;      // Awake で TMP を組む面（Edit モードでは走っていない）
        }

        private static readonly Surface[] Surfaces =
        {
            new Surface { Name = "体験前の注意書き", Type = typeof(TitleNotice),
                          DistanceField = "distanceM", BuildsItsOwnText = true },
            new Surface { Name = "終幕の報告", Type = typeof(OutroReport),
                          DistanceField = "distanceM", BuildsItsOwnText = true,
                          Probe = OutroReportText.Compose(3) },
            // ⚠ この面は TMP を **2 つ**持つ（上段 = 文面 / 下段 = 報告の押し方）。
            //    フィールドを名指ししないと、先に組んだ方が測られて「狙いと違う」と誤って落ちる。
            new Surface { Name = "上司からの連絡", Type = typeof(CommsPanel),
                          FixedDistanceM = 1.5f, BuildsItsOwnText = true, Field = "_text" },
            new Surface { Name = "上司からの連絡（下段）", Type = typeof(CommsPanel),
                          FixedDistanceM = 1.5f, BuildsItsOwnText = true, Field = "_hint",
                          TierDeg = HmdTextStyle.MinorDeg,
                          // 長押し中の 2 行（ゲージが 10 目盛でいちばん長い）。
                          Probe = VisitorMarkGuidance.Line(0.6f, confirming: false) },
            new Surface { Name = "ステータス", Type = typeof(StatusHud),
                          DistanceField = "distance",
                          // 実行時のいちばん長い行（異常の 1 行目）。ここが枠に収まらないと現場で切れる。
                          // ⚠ この 4 行は `StatusHud.BuildStatus` の写し。**向こうを直したらここも直す**
                          //    （private なうえ registry / tracker が要るので、Edit モードから呼べない）。
                          //    異常の 2 行だけは `RecoveryGuidance` から実物を引いている。
                          Probe = "2周目／全3周　経過 1:05\n場所：C:North　表示：カメラ3\n"
                                + "次の演出：3周目 カメラ1\n受信：1 ○　2 ×　3 ○\n"
                                + RecoveryGuidance.What(ShowAlert.IntroAborted, 2) + "\n"
                                + RecoveryGuidance.How(ShowAlert.IntroAborted) },
            new Surface { Name = "操作早見表", Type = typeof(ControllerGuidePanel),
                          FixedDistanceM = HandDistanceM,
                          // ⚠ `ControllerGuidePanel.NormalBody` の写し（private const なので参照できない）。
                          Probe = "A：タイトルを閉じて始める\nB：ステータス表示を切り替える\n"
                                + "グリップ2秒：新しい体験者にする\nトリガー2秒：位置合わせを開始" },
            // ⚠ 報告の押し方とゲージは **[Comms] の下段**（2026-08-16・canon/LEDGER.md 0058）。
            //    面としては上の「上司からの連絡」と同じ実体なので、ここでは別行を持たない。
            //    下段が枠に収まっているかは `menu comms-preview` の絵で見る（4 文面 × 長押し中）。
            // ⚠ 黒は 0.3m だが**文字は 1.5m の別の面**（`ShowEndingFader.MessageDistanceM`）。
            //    0.3m は輻輳の負担が大きく、両眼で読む文字を置く距離ではない。
            new Surface { Name = "黒の上の 1 行", Type = typeof(ShowEndingFader),
                          FixedDistanceM = 1.5f, TierDeg = HmdTextStyle.AlertDeg,
                          BuildsItsOwnText = true, Probe = "部屋の位置がずれたので止めました" },
        };

        [MenuItem("Tools/FixedCamVr/Diagnostics/Audit HMD Text", priority = 236)]
        public static void Run()
        {
            if (!EditorCliArgs.EnsureScene(MainScenePath)) return;

            var spawned = new List<GameObject>();
            var sb = new StringBuilder(2048);
            int bad = 0;
            int index = 0;
            Directory.CreateDirectory(Path.Combine(Application.dataPath, ShotDirRel));

            sb.Append("[HmdTextAudit] 面 / 距離 / 1文字の見かけ角（狙い） / いちばん長い行 vs 枠\n");
            try
            {
                foreach (Surface s in Surfaces)
                {
                    index++;
                    var comp = Object.FindObjectOfType(s.Type, includeInactive: true) as MonoBehaviour;
                    if (comp == null)
                    {
                        sb.Append($"  {s.Name,-16} — シーンに居ません（Setup Main Demo Scene を先に）\n");
                        bad++;
                        continue;
                    }

                    // Edit モードでは Awake が走っていないので、自分で TMP を組む面は明示的に起こす。
                    if (s.BuildsItsOwnText) Invoke(comp, "Awake", spawned, comp.gameObject);

                    TMP_Text? tmp = FindText(comp, s.Field);
                    if (tmp == null)
                    {
                        sb.Append($"  {s.Name,-16} — TMP を組めていません（実機でも 1 文字も出ません）\n");
                        bad++;
                        continue;
                    }

                    float dist = ResolveDistance(comp, s);
                    string probe = s.Probe ?? tmp.text;
                    if (string.IsNullOrEmpty(probe)) probe = "国";

                    var jp = JapaneseHudFont.TryGet();
                    if (jp != null && tmp.font != jp) tmp.font = jp;
                    // ⚠ 起こしてから測る。実行時は不透明度 0・非活性で待っている面があり、
                    //    寝たままだと TMP がメッシュを組まず**見かけ角が嘘の値で出る**。
                    Wake(comp.transform, tmp);

                    Vector3 ls = tmp.transform.lossyScale;
                    float em = MeasureEm(tmp) * Mathf.Abs(ls.x);
                    float deg = HmdTextStyle.DegreesOf(em, dist);

                    Layout(tmp, probe);
                    float lineW = LongestLine(tmp) * Mathf.Abs(ls.x);
                    float frameW = ((RectTransform)tmp.transform).rect.width * Mathf.Abs(ls.x);

                    // ⚠ 数字が緑でも絵は必ず開く（rules/visual-verification.md §10）。
                    //   全部を**同じ画角**で撮るので、並べれば見かけの大きさがそのまま比べられる。
                    Shoot(comp.transform, tmp, dist, Path.Combine(ShotDir, $"{index:00}_{Ascii(s.Name)}.png"));

                    bool sizeOk = Mathf.Abs(deg - s.TierDeg) <= TolDeg;
                    bool fitOk = lineW <= frameW * 1.001f;
                    if (!sizeOk || !fitOk) bad++;

                    sb.Append($"  {s.Name,-16} {dist:0.00}m  {deg:0.00}°（狙い {s.TierDeg:0.0}°）{(sizeOk ? "" : "  ← 外れ")}"
                            + $"  行 {lineW:0.000}m / 枠 {frameW:0.000}m{(fitOk ? "" : "  ← はみ出し")}\n");
                }
            }
            finally
            {
                foreach (GameObject go in spawned) if (go != null) Object.DestroyImmediate(go);
            }

            sb.Append($"  段: 補助 {HmdTextStyle.MinorDeg:0.0}° / 本文 {HmdTextStyle.BodyDeg:0.0}° / 注目 {HmdTextStyle.AlertDeg:0.0}°\n");
            sb.Append($"  絵: Assets/{ShotDirRel}/（画角 {ShotFovDeg}° 固定 ＝ 並べると見かけの大きさがそのまま比べられる）");
            if (bad > 0) Debug.LogError(sb.ToString() + $"\n  ⚠ {bad} 面が外れています");
            else Debug.Log(sb.ToString() + "\n  すべて狙いどおり");
        }

        // ---- 絵 ------------------------------------------------------------------------

        private const string ShotDirRel = "Screenshots/hud-text";
        private static string ShotDir => Path.Combine(Application.dataPath, ShotDirRel);
        private const float ShotFovDeg = 40f;
        private const int ShotW = 1400, ShotH = 800;

        /// <summary>撮影台の場所。他の scene 幾何が写り込まないよう、誰も居ない高さへ持っていく。</summary>
        private static readonly Vector3 Stage = new Vector3(0f, 1000f, 0f);

        /// <summary>
        /// 面を起こす。実行時は不透明度 0・非活性・Canvas 切りで待っている面があり
        /// （注意書き・終幕・黒の上の 1 行）、寝たままでは TMP がメッシュを組まない
        /// ＝ <b>測っても撮っても嘘の結果が出る</b>。
        /// </summary>
        private static void Wake(Transform root, TMP_Text tmp)
        {
            root.gameObject.SetActive(true);
            foreach (Canvas cv in root.GetComponentsInChildren<Canvas>(includeInactive: true)) cv.enabled = true;
            tmp.gameObject.SetActive(true);
            tmp.enabled = true;
            tmp.color = new Color(tmp.color.r, tmp.color.g, tmp.color.b, 1f);
            tmp.alpha = 1f;
        }

        /// <summary>
        /// 面を「眼から <paramref name="dist"/> の正面」へ置いて撮る。
        /// ⚠ <b>画角は全面で固定</b>。距離ごとに変えると、並べたときに見かけの大きさを比べられない
        /// （＝ この道具の存在理由が消える）。
        /// ⚠ 動かすのは<b>面の根</b>で、字だけを親から抜かない（Canvas の中の TextMeshProUGUI は
        /// 親を失うと描かれず、RectTransform の寸法も変わる）。世界スケールは保つ
        /// （<c>worldPositionStays: true</c>）— 大きさが変わると、測った値と撮った絵が食い違う。
        /// </summary>
        private static void Shoot(Transform root, TMP_Text tmp, float dist, string path)
        {
            root.SetParent(null, worldPositionStays: true);
            root.rotation = Quaternion.identity;
            // 字がちょうど「眼の正面 dist」へ来るように、根と字のずれぶん戻して置く。
            Vector3 offset = tmp.transform.position - root.position;
            root.position = Stage + new Vector3(0f, 0f, dist) - offset;

            Wake(root, tmp);

            var camGo = new GameObject("[HmdTextAudit] Camera");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.transform.SetPositionAndRotation(Stage, Quaternion.identity);
                cam.fieldOfView = ShotFovDeg;
                cam.nearClipPlane = 0.05f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                // 実機の暗い現場に近い無地。純黒だと字の縁の締まり方が現場と違って見える。
                cam.backgroundColor = new Color(0.06f, 0.06f, 0.07f, 1f);

                var rt = new RenderTexture(ShotW, ShotH, 24);
                var tex = new Texture2D(ShotW, ShotH, TextureFormat.RGB24, mipChain: false);
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, ShotW, ShotH), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                }
                finally
                {
                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    Object.DestroyImmediate(rt);
                    Object.DestroyImmediate(tex);
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
                // ⚠ 撮り終えた面は台から降ろす。置いたままにすると**次の 1 枚に前の面が写り込む**
                //    （実際に 2 枚が同じ絵になった）。
                root.gameObject.SetActive(false);
            }
        }

        // ファイル名に使える形へ（日本語のままだと現場のシェルで扱いにくい）。
        private static string Ascii(string name) => name switch
        {
            "体験前の注意書き" => "notice",
            "終幕の報告" => "outro-report",
            "上司からの連絡" => "comms",
            "ステータス" => "status",
            "操作早見表" => "guide",
            "報告ボタンの面" => "visitor-mark",
            "報告ボタンの面（長押し中）" => "visitor-mark-hold",
            "黒の上の 1 行" => "blackout",
            _ => "surface",
        };

        /// <summary>
        /// 1 文字ぶん（em）のローカルサイズ。<b>全角 2 文字を組ませて、字送りの差を測る</b> —
        /// 和文の全角は送りがちょうど 1 em なので、これが定義そのもの。
        ///
        /// ⚠ <c>fontSize</c> から式で出さない。透視カメラの 3D TextMeshPro が内部で掛ける 0.1 も、
        /// Canvas の <c>scaleFactor</c> も、<b>この codebase が 2 回続けて取り違えたもの</b>。
        /// ⚠ 字の当たり判定（<c>isVisible</c>）は使わない — 不透明度 0 の面では全部 false になる。
        /// </summary>
        private static float MeasureEm(TMP_Text tmp)
        {
            Layout(tmp, "国国");
            TMP_TextInfo info = tmp.textInfo;
            if (info == null || info.characterCount < 2) return 0f;
            return info.characterInfo[1].origin - info.characterInfo[0].origin;
        }

        /// <summary>
        /// いちばん長い行の幅（ローカル）。
        /// ⚠ <c>textBounds</c> は使わない — 枠いっぱいに置いた TMP では<b>枠の幅がそのまま返る</b>ので、
        /// はみ出していても気づけない（＝ 落ちない門になる）。行ごとの実測から最大を採る。
        /// </summary>
        private static float LongestLine(TMP_Text tmp)
        {
            TMP_TextInfo info = tmp.textInfo;
            if (info == null || info.lineCount == 0) return 0f;
            float w = 0f;
            for (int i = 0; i < info.lineCount; i++)
            {
                // ⚠ `lineInfo[i].width` は**行箱**（＝枠の幅）で、字の幅ではない。
                //    実際に字が乗っている範囲は lineExtents。
                Extents e = info.lineInfo[i].lineExtents;
                w = Mathf.Max(w, e.max.x - e.min.x);
            }
            return w;
        }

        private static void Layout(TMP_Text tmp, string s)
        {
            tmp.text = s;
            // 2 回呼ぶ（1 回目でグリフの焼き付けを要求し、2 回目で焼けたものを含めて組み直す）。
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
            tmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
        }

        private static float ResolveDistance(MonoBehaviour comp, Surface s)
        {
            if (s.DistanceField == null) return s.FixedDistanceM;
            FieldInfo? f = comp.GetType().GetField(s.DistanceField,
                BindingFlags.Instance | BindingFlags.NonPublic);
            return f?.GetValue(comp) is float v && v > 0.01f ? v : s.FixedDistanceM;
        }

        // 面が持つ TMP。SerializeField の `text` があればそれ、無ければ子から拾う。
        private static TMP_Text? FindText(MonoBehaviour comp, string? fieldName = null)
        {
            FieldInfo? f = comp.GetType().GetField(fieldName ?? "text",
                                                   BindingFlags.Instance | BindingFlags.NonPublic);
            if (fieldName != null) return f?.GetValue(comp) as TMP_Text;
            if (f?.GetValue(comp) is TMP_Text t && t != null) return t;
            return comp.GetComponentInChildren<TMP_Text>(includeInactive: true);
        }

        private static void Invoke(object target, string method, List<GameObject> spawned, GameObject root)
        {
            int before = root.transform.childCount;
            MethodInfo? m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) return;
            m.Invoke(target, null);
            // Awake が作った子は監査が終わったら消す（シーンを汚さない・保存もしない）。
            for (int i = before; i < root.transform.childCount; i++)
                spawned.Add(root.transform.GetChild(i).gameObject);
        }
    }
}
