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

        // 401 はフェイルオープン（null 返し）だが、原因が分からず黙って /info /health が
        // 効かない状態になりやすい。最初の 1 回だけ認証ヒント付きで警告する。
        private static bool _logged401;

        public static async Task<StreamMetadata?> FetchInfoAsync(string url, int timeoutSec = 3, string? basicAuthToken = null)
        {
            string? json = await GetTextAsync(url, timeoutSec, basicAuthToken);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<StreamMetadata>(json); }
            catch (Exception ex) { Debug.LogWarning($"[StreamMetadataFetcher] /info parse failed: {ex.Message}"); return null; }
        }

        public static async Task<StreamHealth?> FetchHealthAsync(string url, int timeoutSec = 2, string? basicAuthToken = null)
        {
            string? json = await GetTextAsync(url, timeoutSec, basicAuthToken);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<StreamHealth>(json); }
            catch (Exception ex) { Debug.LogWarning($"[StreamMetadataFetcher] /health parse failed: {ex.Message}"); return null; }
        }

        private static async Task<string?> GetTextAsync(string url, int timeoutSec, string? basicAuthToken = null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (basicAuthToken != null)
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basicAuthToken);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && !_logged401)
                    {
                        _logged401 = true;
                        Debug.LogWarning($"[StreamMetadataFetcher] GET {url} -> 401 Unauthorized。" +
                                         "CameraSource の username/password を確認（IP Camera Lite 既定 admin/admin）。/info /health は無効化して続行。");
                    }
                    return null;
                }
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
