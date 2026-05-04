#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// fixed-cam-streamer の <c>/info</c> / <c>/health</c> を非同期取得するヘルパ。
    ///
    /// 設計メモ: 当初 UnityWebRequest を使っていたが、Unity の Player Settings
    /// "Allow downloads over HTTP" が既定 "Not Allowed" のため http:// 接続が
    /// 黙って失敗する事象が実機検証で発覚（Console に "Non-secure network connections
    /// disabled in Player Settings" が出る）。.NET の HttpClient はこの設定の影響を
    /// 受けないため、こちらに統一して MJPEG 受信側 (MjpegStreamReceiver) と整合させた。
    ///
    /// 失敗（404 / DroidCam 等の非対応サーバ含む）は null 返しでフェイルオープン。
    /// </summary>
    public static class StreamMetadataFetcher
    {
        // 共有 HttpClient（推奨パターン: ソケットリーク防止のため都度 new しない）
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        public static async Task<StreamMetadata?> FetchInfoAsync(string url, int timeoutSec = 3)
        {
            string? json = await GetTextAsync(url, timeoutSec);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<StreamMetadata>(json); }
            catch (Exception ex) { Debug.LogWarning($"[StreamMetadataFetcher] /info parse failed: {ex.Message}"); return null; }
        }

        public static async Task<StreamHealth?> FetchHealthAsync(string url, int timeoutSec = 2)
        {
            string? json = await GetTextAsync(url, timeoutSec);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<StreamHealth>(json); }
            catch (Exception ex) { Debug.LogWarning($"[StreamMetadataFetcher] /health parse failed: {ex.Message}"); return null; }
        }

        private static async Task<string?> GetTextAsync(string url, int timeoutSec)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException) { return null; }
            catch (HttpRequestException) { return null; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StreamMetadataFetcher] GET {url} failed: {ex.Message}");
                return null;
            }
        }
    }
}
