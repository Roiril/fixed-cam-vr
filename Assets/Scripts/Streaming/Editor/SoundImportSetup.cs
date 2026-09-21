#nullable enable
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools
{
    /// <summary>
    /// 音の取り込み設定を揃える（冪等）。<c>.\tools\unity.ps1 menu sound-import</c>。
    ///
    /// **取り込み設定は音の品質を直接決めるのに、Inspector を開かないと見えない。**
    /// だからここが単一の正で、規則は <c>.claude/rules/sound-design.md</c> に文章でも書いてある。
    ///
    /// 2026-08-12 に実在した 4 つの誤り（`HorrBGM.mp3` の既定設定）:
    ///
    /// | 設定 | だった値 | 何が起きるか |
    /// |---|---|---|
    /// | <c>loadType</c> | **Streaming** | <see cref="BgmDirector"/> は<b>ループ範囲を自前でシークする</b>。
    ///   streaming のクリップを <c>time</c> で飛ばすと Android で復号が詰まって音が飛ぶ。
    ///   同じコードが URL 経由のクリップには <c>streamAudio = false</c> を明示していた ＝ 作者は知っていて、
    ///   焼き込み側だけ直っていなかった |
    /// | <c>quality</c> | **1.0** | 元が 128kbps の mp3。100% の Vorbis へ再圧縮しても
    ///   元の粗さが保存されるだけで、実行時のメモリと復号だけが増える |
    /// | <c>normalize</c> | **有効** | 取り込み時に音量を作り直す ＝ <b>素材どうしの高さの設計が消える</b>。
    ///   音源が 1 本のうちは無害だが、18 本になった瞬間に混ぜられなくなる |
    /// | <c>preloadAudioData</c> | 無効 | 最初の 1 回だけ読み込みで引っかかる |
    /// </summary>
    public static class SoundImportSetup
    {
        public const string SoundDir = "Assets/Resources/Sound";

        /// <summary>敷く音（長い・ループする）。**Vorbis を丸ごと RAM に置いて必要なぶんだけ復号する。**</summary>
        private const float BedQuality = 0.42f;

        [MenuItem("Tools/FixedCamVr/Setup/Apply Sound Import Settings", priority = 74)]
        public static void ApplySoundImportSettings()
        {
            var changed = new List<string>();
            int seen = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SoundDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                seen++;
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                bool isBed = name.StartsWith("bed_") || name == "bgm_nocturnal_waters";
                // ⚠⚠ **3D で鳴らす音の名簿は `SpatialAudio.MonoRequired` 1 か所**
                //    （2026-09-03・`canon/LEDGER.md` 0130）。ここに条件を書き足すと、
                //    実行時の観測（`SpatialAudio.CountStereo`）と黙って食い違う。
                bool isSpatial = System.Array.IndexOf(SpatialAudio.MonoRequired, name) >= 0;
                if (Apply(path, isBed, isSpatial)) changed.Add(path);
            }

            // 既存の劇伴も同じ規則へ揃える（**ここが 2026-08-12 まで Streaming で、
            // BgmDirector のループ範囲シークと噛み合っていなかった**）。
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Art/Audio" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                seen++;
                if (Apply(path, isBed: true, isSpatial: false)) changed.Add(path);
            }

            if (seen == 0)
            {
                Debug.LogError($"[SoundImport] 音源が 1 本もありません（{SoundDir}）。"
                               + "先に `py -3.11 tools/make-sounds.py` を走らせること。");
                return;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[SoundImport] {seen} 本を確認 / {changed.Count} 本を更新。"
                      + (changed.Count > 0 ? "\n  " + string.Join("\n  ", changed) : ""));
        }

        private static bool Apply(string path, bool isBed, bool isSpatial)
        {
            var imp = AssetImporter.GetAtPath(path) as AudioImporter;
            if (imp == null) return false;

            var s = imp.defaultSampleSettings;
            var before = s;

            // ⚠ **ループする長い音を Streaming にしない。** `BgmDirector` も `ShowSoundDirector` も
            //    再生位置を触るので、disk から読み直す形だと引っかかる。
            //    かといって DecompressOnLoad は 118 秒の劇伴で 40MB を超える。
            //    圧縮したまま RAM に置く（CompressedInMemory）が唯一まともな選択。
            s.loadType = isBed ? AudioClipLoadType.CompressedInMemory
                               : AudioClipLoadType.DecompressOnLoad;

            // 一撃は **ADPCM**。Vorbis は復号の立ち上がりを持つので、0.14 秒の切替音のように
            // 「暗転の中に入る」約束がある音では、その立ち上がりが約束を壊しうる。
            s.compressionFormat = isBed ? AudioCompressionFormat.Vorbis
                                        : AudioCompressionFormat.ADPCM;
            if (isBed) s.quality = BedQuality;

            // 48kHz で焼いてあるので変換させない（`sampleRateSetting = PreserveSampleRate`）。
            s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            s.preloadAudioData = true;        // 最初の 1 回で引っかからない
            imp.defaultSampleSettings = s;

            bool changed = imp.forceToMono != isSpatial
                           || imp.loadInBackground
                           || imp.ambisonic
                           || before.loadType != s.loadType
                           || before.compressionFormat != s.compressionFormat
                           || before.preloadAudioData != s.preloadAudioData
                           || !Mathf.Approximately(before.quality, s.quality)
                           || before.sampleRateSetting != s.sampleRateSetting;

            imp.loadInBackground = false;
            imp.ambisonic = false;
            // 3D で鳴らす音はモノでなければ spatializer が処理しない。
            // ファイル自体がモノでも、将来ステレオへ差し替えられたときの保険として立てておく。
            imp.forceToMono = isSpatial;

            changed |= ClearNormalize(imp);
            if (!changed) return false;
            imp.SaveAndReimport();
            return true;
        }

        /// <summary>
        /// 取り込み時の正規化を切る。
        ///
        /// ⚠ <b><c>AudioImporter</c> に <c>normalize</c> の公開 API は無い</b>（`.meta` には
        /// <c>normalize:</c> と書かれているのに C# からは見えない）。<c>SerializedObject</c> 経由でしか触れない。
        ///
        /// ⚠ <b>効くのは <c>forceToMono</c> が立っているときだけ。</b> Unity の Inspector でも
        /// 「Normalize」は「Force To Mono」の下にぶら下がっている。つまり既存の <c>HorrBGM.mp3</c> は
        /// <c>normalize: 1</c> だが <c>forceToMono: 0</c> なので<b>実際には効いていなかった</b>
        /// （2026-08-12 に「音量が作り直されている」と誤読しかけた。**設定ファイルの値だけを見て
        /// 効いていると決めない**）。ここで切るのは、3D の音に <c>forceToMono</c> を立てる以上、
        /// そのとき初めて牙を剥くから。
        /// </summary>
        private static bool ClearNormalize(AudioImporter imp)
        {
            var so = new SerializedObject(imp);
            var prop = so.FindProperty("m_Normalize") ?? so.FindProperty("normalize");
            if (prop == null || !prop.boolValue) return false;
            prop.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
