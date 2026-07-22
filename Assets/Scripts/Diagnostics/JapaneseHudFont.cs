#nullable enable
using System;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// HMD 内テキスト（StatusHud）用の日本語対応 TMP フォントを **OS フォントから動的生成**する。
    ///
    /// 背景: TMP 既定の LiberationSans SDF は日本語グリフを持たず、登録ガイダンス・ステータス行の
    /// 日本語が全て豆腐（□）になる（2026-07-22 検証ハーネスの Editor プレビュー + 実機 screencap で確定）。
    /// フォントファイルの同梱は配布ライセンス・容量の問題があるため、実行環境の OS フォント
    /// （Quest/Android は Noto Sans CJK、Windows Editor は Yu Gothic 等）から
    /// <see cref="TMP_FontAsset.CreateFontAsset(Font)"/> で動的 SDF アセットを作る。
    /// グリフはダイナミックアトラスへ必要時に焼かれるため CJK 全面の事前ベイクは不要。
    ///
    /// 失敗時は null を返し、呼び出し側は既定フォントのまま動く（ASCII のみ表示＝従来動作）。
    /// </summary>
    public static class JapaneseHudFont
    {
        // 優先順に試す OS フォント名。前半 = Android/Quest 系、後半 = Windows（Editor / Standalone）。
        private static readonly string[] PreferredNames =
        {
            "Noto Sans CJK JP", "Noto Sans JP", "NotoSansCJK", "Droid Sans Japanese",
            "Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo", "MS Gothic",
        };

        private static TMP_FontAsset? _cached;
        private static bool _attempted;

        /// <summary>日本語対応の動的 TMP フォントを返す（プロセス内キャッシュ）。生成不能なら null。</summary>
        public static TMP_FontAsset? TryGet()
        {
            if (_attempted) return _cached;
            _attempted = true;

            // 第一選択: プロジェクト同梱の Noto Sans JP（SIL OFL）。
            // Tools/FixedCamVr/Setup/Generate Japanese HUD Font で生成した Resources アセット。
            // ※ この Unity/TMP 世代はランタイムの OS フォント→CreateFontAsset が通らない（実測 58 候補全滅）
            //   ため、同梱アセットが実質唯一の経路。下の OS フォント探索は将来世代・非常用のフォールバック。
            var bundled = Resources.Load<TMP_FontAsset>("Fonts/JapaneseHud SDF");
            if (bundled != null)
            {
                _cached = bundled;
                Debug.Log("[JapaneseHudFont] 同梱 JapaneseHud SDF（Source Han Sans JP）を使用");
                return _cached;
            }

            try
            {
                string[] installed = Font.GetOSInstalledFontNames();

                // 候補リスト（優先名の完全一致 → 部分一致の順）。**生成に成功するまで順に試す** —
                // 可変フォント（例: Google Fonts 版 Noto Sans JP）は CreateFontAsset が null を返すことが
                // あるため、1 候補で諦めると静的フォントが控えているのに豆腐のままになる（実測）。
                var candidates = new System.Collections.Generic.List<string>();
                foreach (string want in PreferredNames)
                    foreach (string have in installed)
                        if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase) && !candidates.Contains(have))
                            candidates.Add(have);
                foreach (string have in installed)
                {
                    string h = have.ToLowerInvariant();
                    if ((h.Contains("cjk") || h.Contains("japan") || h.Contains("gothic") || h.Contains("mincho"))
                        && !candidates.Contains(have))
                        candidates.Add(have);
                }
                if (candidates.Count == 0)
                {
                    Debug.LogWarning("[JapaneseHudFont] 日本語 OS フォントが見つからない — 既定フォントのまま（日本語は豆腐になる）");
                    return null;
                }

                foreach (string pick in candidates)
                {
                    Font os = Font.CreateDynamicFontFromOSFont(pick, 34);
                    if (os == null) continue;
                    TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(os);
                    if (asset == null)
                    {
                        Debug.Log($"[JapaneseHudFont] 生成不可（次候補へ）: {pick}");
                        continue;
                    }
                    asset.name = $"JapaneseHudFont ({pick})";
                    _cached = asset;
                    Debug.Log($"[JapaneseHudFont] 動的フォント生成: {pick}");
                    return _cached;
                }
                Debug.LogWarning($"[JapaneseHudFont] 全候補 {candidates.Count} 件で生成失敗 — 既定フォントのまま");
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[JapaneseHudFont] 生成例外 — 既定フォントのまま: {e.Message}");
                return null;
            }
        }
    }
}
