#nullable enable
using System;
using System.Globalization;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 体験中の撮影（左グリップ）の純ロジック。ファイル名・受理の判断・有効化の判断を持つ。
    /// 時刻は呼び出し側が渡す（テストで固定できるように）。
    /// </summary>
    public static class ExperienceShotLogic
    {
        // ---- 出力ファイル名（1 回の撮影 = 1 フォルダ）----------------------------------
        /// <summary>① 表示されているスクリーン映像（ライブ・素材・CG・加工をすべて含む最終合成）。</summary>
        public const string ScreenFile = "1_screen.png";

        /// <summary>② 加工前の生映像（いま画面へ出ているカメラの受信フレームそのもの）。</summary>
        public const string RawFile = "2_raw.png";

        /// <summary>③ 合成しているものだけ（ライブを黒に置き換えて、同じ加工で描いたもの）。</summary>
        public const string LayersFile = "3_layers.png";

        /// <summary>③ の CG 層そのもの（透過 PNG。スライドへ貼るとき用）。</summary>
        public const string CgAlphaFile = "3_cg_alpha.png";

        /// <summary>④ クエストから体験者が見ているもの（アプリが描いた左眼の視界）。</summary>
        public const string HmdFile = "4_hmd.png";

        public const string InfoFile = "info.json";

        /// <summary>撮影セットのフォルダ名。<c>20260930_142315_001</c>（秒まで + 起動後の通し番号）。</summary>
        public static string SetFolderName(DateTime local, int sequence)
            => local.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
               + "_" + Math.Max(0, sequence).ToString("D3", CultureInfo.InvariantCulture);

        // ---- 受理の判断 -----------------------------------------------------------------

        /// <summary>同じ撮影を連続して受けない間隔（秒）。書き出しが終わる前の二重押下を防ぐ。</summary>
        public const float CooldownSec = 2.0f;

        /// <summary>1 回の起動で受ける撮影の上限。押しっぱなしの暴走で保存領域を食い尽くさないため。</summary>
        public const int MaxSetsPerLaunch = 60;

        public enum Verdict
        {
            Accepted,
            Disabled,
            Cooldown,
            CapReached,
        }

        /// <summary>
        /// 撮影の要求を受けるか。<paramref name="lastAcceptedAt"/> は最後に受理した時刻
        /// （まだ無ければ <c>float.NegativeInfinity</c>）。
        /// </summary>
        public static Verdict Decide(bool enabled, float now, float lastAcceptedAt, int acceptedCount)
        {
            if (!enabled) return Verdict.Disabled;
            if (acceptedCount >= MaxSetsPerLaunch) return Verdict.CapReached;
            if (now - lastAcceptedAt < CooldownSec) return Verdict.Cooldown;
            return Verdict.Accepted;
        }

        // ---- 有効化の判断 ---------------------------------------------------------------

        /// <summary>
        /// 撮影機能が有効か。
        /// <list type="bullet">
        /// <item>Development ビルドでは既定で有効（<paramref name="isDebugBuild"/>）</item>
        /// <item>Release ビルドは既定で無効。<paramref name="onMarker"/>（端末の <c>shots/ON</c>）で有効にできる</item>
        /// <item><paramref name="offMarker"/>（端末の <c>shots/OFF</c>）はどちらの場合も無効にする</item>
        /// </list>
        /// ⚠ 左グリップは体験者が握り込むときに普通に押される。展示本番の機には必ず OFF を置く
        /// （<c>py -3.11 tools/quest-shots.py off</c>）。
        /// </summary>
        public static bool IsEnabled(bool isDebugBuild, bool onMarker, bool offMarker)
        {
            if (offMarker) return false;
            return isDebugBuild || onMarker;
        }
    }

    /// <summary>撮影セットの記録（<c>info.json</c>）。どの瞬間の何を撮ったかを後から読めるようにする。</summary>
    [Serializable]
    public sealed class ExperienceShotInfo
    {
        public string setName = "";
        public string capturedAtIso = "";
        public float appTimeSec;

        // ---- 体験の状態 ----
        public string phase = "";
        public int progressLap;
        public int segmentLap;
        public int screenCamera = -1;
        public string screenCameraName = "";
        public string activeTake = "";
        public string activeStepCue = "";

        // ---- 合成の状態 ----
        public string overlayCue = "";
        public float overlayStrength;
        public float overlay2Strength;
        public float cgStrength;
        public bool cgVisible;

        // ---- 撮れたもの / 撮れなかったもの ----
        public string[] files = Array.Empty<string>();
        public string[] skipped = Array.Empty<string>();
        public int screenWidth;
        public int screenHeight;
        public int rawWidth;
        public int rawHeight;
        public int hmdWidth;
        public int hmdHeight;
        public string hmdNote = "";
    }
}
