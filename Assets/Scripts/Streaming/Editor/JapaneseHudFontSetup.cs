#nullable enable
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// StatusHud 用の日本語 TMP フォントアセットを生成する一発メニュー（冪等）。
    ///
    /// 背景: TMP 既定 LiberationSans SDF に日本語グリフが無く、登録ガイダンス等が豆腐になる
    /// （2026-07-22 検証ハーネスで確定）。またこの Unity/TMP 世代ではランタイムの
    /// OS フォント → CreateFontAsset が全滅するため（実測 58 候補失敗）、**プロジェクト同梱の
    /// Noto Sans JP（SIL OFL・Assets/Art/Fonts/）から Editor で動的アトラスのフォントアセットを
    /// 生成して Resources に置き**、ランタイムは Resources.Load で読む。
    ///
    /// 出力: Assets/Resources/Fonts/NotoSansJP SDF.asset（+ 内包 material / atlas）。
    /// 既存があれば削除して作り直す（再実行安全）。
    /// </summary>
    internal static class JapaneseHudFontSetup
    {
        // ⚠ 可変フォント（NotoSansJP-VF 等）は CreateFontAsset は通るがグリフのラスタライズが全滅して
        //   豆腐のままになる（実測）。**静的フォント**を使うこと。Source Han Sans JP は静的 OTF + SIL OFL。
        private const string SourceFontPath = "Assets/Art/Fonts/SourceHanSansJP-Normal.otf";
        private const string OutDir = "Assets/Resources/Fonts";
        private const string OutPath = OutDir + "/JapaneseHud SDF.asset";

        // public なのは CLI（`unity.ps1 menu hud-font`）が -executeMethod で直接呼ぶため。
        [MenuItem("Tools/FixedCamVr/Setup/Generate Japanese HUD Font", priority = 90)]
        public static void Generate()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(SourceFontPath);
            if (font == null)
            {
                Debug.LogError($"[JapaneseHudFontSetup] 元フォントが無い: {SourceFontPath}");
                return;
            }

            // いったん Dynamic で作成 → HUD が使う文字を**この場で全部ベイク** → Static 化して保存する。
            // （保存→ロード跨ぎで sourceFontFile が解決できず TryAddCharacters が全滅する事象を実測。
            //   事前ベイク + Static ならランタイムのフォントフェイス解決に一切依存しない）
            // ⚠ **1 枚に収める**（2026-08-14）。1024² だと 883 文字が **5 枚**へ分かれ、
            //    TMP が atlas ごとにサブメッシュを作る ＝ HMD の文字面の描画が増える。
            //    4096² なら 1 枚（実測 5 → 1）。
            //    ⚠ **`Screen position out of view frustum` の対策ではない。** そのつもりで
            //       広げたが警告は 1 行も減らなかった（`canon/OPEN.md` の未解決項目）。
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                font, 64, 6, GlyphRenderMode.SDFAA, 4096, 4096, AtlasPopulationMode.Dynamic);
            if (asset == null)
            {
                Debug.LogError("[JapaneseHudFontSetup] CreateFontAsset 失敗（フォントファイル・FontEngine を確認）");
                return;
            }
            asset.isMultiAtlasTexturesEnabled = true;

            string charset = CollectHudCharset();
            bool baked = asset.TryAddCharacters(charset, out string missing);
            Debug.Log($"[JapaneseHudFontSetup] グリフベイク: {charset.Length} 文字, ok={baked}" +
                      (string.IsNullOrEmpty(missing) ? "" : $", 欠落 {missing.Length} 文字: '{missing}'"));
            if (!asset.HasCharacter('床'))
            {
                Debug.LogError("[JapaneseHudFontSetup] CJK グリフのベイクに失敗している（'床' 不在）。フォントを変えて再試行を");
                return;
            }
            asset.atlasPopulationMode = AtlasPopulationMode.Static;

            Directory.CreateDirectory(OutDir);
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(OutPath) != null)
                AssetDatabase.DeleteAsset(OutPath);

            // material / atlas texture はサブアセットとして同梱する（TMP フォントアセットの標準構成）。
            asset.name = "JapaneseHud SDF";
            AssetDatabase.CreateAsset(asset, OutPath);
            if (asset.atlasTextures != null)
                foreach (var tex in asset.atlasTextures)
                    if (tex != null) { tex.name = asset.name + " Atlas"; AssetDatabase.AddObjectToAsset(tex, asset); }
            if (asset.material != null)
            {
                asset.material.name = asset.name + " Material";
                AssetDatabase.AddObjectToAsset(asset.material, asset);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[JapaneseHudFontSetup] 生成完了: {OutPath}");
        }

        // HUD（StatusHud / 登録ガイダンス）が表示しうる文字集合を**ソースコードから自動収集**する。
        // 対象 .cs の非 ASCII 文字全部 + ASCII 印字可能全部 + 記号（進捗バー・信号・矢印等）。
        // 文言を変えたらこのメニューを再実行すれば追従する。
        private static string CollectHudCharset()
        {
            string[] sources =
            {
                "Assets/Scripts/Tracking/RegistrationGuidance.cs",
                "Assets/Scripts/Tracking/CourseRegistrationController.cs",
                "Assets/Scripts/Diagnostics/StatusHud.cs",
                // 2026-07-23 追加: 操作ガイドパネル本文（体験者・全角：等）と、グリップ拒否時の
                // 赤メッセージ（演出中は切り替えできません）の供給元。ここに無い文字は実機でも豆腐になる
                // （静的ベイクのため。U+FF1A 欠落を batchmode プレビューで実測）。
                "Assets/Scripts/Diagnostics/ControllerGuidePanel.cs",
                "Assets/Scripts/OvrBridge/OvrControllerBridge.cs",
                // ⚠ `IntroDirector.cs` は 2026-08-13 に外した。導入の合図（IntroPrompt）を
                //    廃止して、あの面の文言が 1 つも残っていないため（canon/LEDGER.md 0033）。
                // 2026-08-06 追加: 異常の「何が起きたか / 何をすれば直るか」の全文言（StatusHud が描く）と、
                // 視界を閉じる黒の上に出す 1 行。**異常の文言はすべて RecoveryGuidance にある**ので、
                // ここに無いと現場で復帰手順が読めない（実機は静的ベイクなので豆腐になる）。
                // ⚠⚠ 2026-08-14 追加: **体験前の注意書き**（TitleNotice）。ここに無かったせいで
                //    「スタッフへお声がけください」の**「声」だけが豆腐**になっていた
                //    （実機ログに 26 回 `声 was not found`。`canon/LEDGER.md` 0035）。
                //    ⚠ 面が深度で隠れていた間は誰も気づけなかった — **出るようにして初めて出た欠陥**。
                "Assets/Scripts/Diagnostics/TitleNotice.cs",
                "Assets/Scripts/Diagnostics/RecoveryGuidance.cs",
                "Assets/Scripts/Streaming/ShowRunDirector.cs",
            };
            var set = new System.Collections.Generic.SortedSet<char>();
            for (char c = ' '; c <= '~'; c++) set.Add(c);                       // ASCII 印字可能
            foreach (char c in "▓░●○→⚠×📍。、・…％℃①②③④⑤") set.Add(c);      // 記号の取りこぼし保険
            foreach (string path in sources)
            {
                if (!File.Exists(path)) { Debug.LogWarning($"[JapaneseHudFontSetup] 収集元が無い: {path}"); continue; }
                foreach (char c in File.ReadAllText(path))
                    if (c > 0x7F && !char.IsControl(c) && !char.IsSurrogate(c)) set.Add(c);
            }
            var sb = new System.Text.StringBuilder(set.Count);
            foreach (char c in set) sb.Append(c);
            return sb.ToString();
        }
    }
}
