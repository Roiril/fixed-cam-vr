#nullable enable
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// カメラ index → 表示色の共有パレット。登録ビューのゾーン床フットプリント
    /// （<see cref="CourseRegistrationController"/>）と grid 生タイル表示（<see cref="ZoneGridFootprint"/>）が
    /// 同じ色でカメラを識別できるよう一元化する。
    /// </summary>
    public static class ZonePalette
    {
        /// <summary>カメラ別 5 色（緑・青・橙・桃・紫）。<c>cam % Length</c> で巡回する。</summary>
        public static readonly Color[] Colors =
        {
            new(0.3f, 1f, 0.5f), new(0.35f, 0.6f, 1f), new(1f, 0.7f, 0.3f),
            new(1f, 0.4f, 0.6f), new(0.7f, 0.5f, 1f),
        };

        /// <summary>カメラ index の表示色（alpha=1）。負値（未割当）は防御的に灰色を返す。</summary>
        public static Color ForCamera(int camIndex)
        {
            if (camIndex < 0) return new Color(0.6f, 0.6f, 0.6f, 1f);
            return Colors[camIndex % Colors.Length];
        }
    }
}
