#nullable enable
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// fixed-cam-streamer の <c>/info</c> / <c>/health</c> を非同期取得するヘルパ。
    /// UnityWebRequest を直接 await できないので IEnumerator → TaskCompletionSource ブリッジを使う。
    /// 失敗（404 / DroidCam 等の非対応サーバ含む）は null 返しでフェイルオープン。
    /// </summary>
    public static class StreamMetadataFetcher
    {
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

        private static Task<string?> GetTextAsync(string url, int timeoutSec)
        {
            var tcs = new TaskCompletionSource<string?>();
            var req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result == UnityWebRequest.Result.Success)
                        tcs.SetResult(req.downloadHandler.text);
                    else
                        tcs.SetResult(null);
                }
                finally { req.Dispose(); }
            };
            return tcs.Task;
        }
    }
}
