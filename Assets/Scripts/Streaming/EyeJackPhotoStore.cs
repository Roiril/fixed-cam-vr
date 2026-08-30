#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>視界ジャックの当日写真を端末に用意する</b>層（<c>canon/LEDGER.md</c> 0099）。
    /// リストは <see cref="ShowControlClient"/>（show.json トップレベル <c>eyejack.photos[]</c>）から、
    /// バイトは URL ごとに <c>persistentDataPath/eyejack/</c> へディスクキャッシュして取る。
    ///
    /// <b>正規化（縮小・EXIF 回転・減光）は PC 側（卓のサーバ）で済んでいる前提</b>。
    /// ここがやるのは 落とす / 貯める / デコードする だけ — 12MP をそのまま食うと
    /// 1 枚 50MB 級の一時確保が走る（2026-08-21 設計批評）。
    ///
    /// 堅牢の要点:
    /// <list type="bullet">
    ///   <item>卓が死んでいても、前回落としたディスクキャッシュから出す（リストは show.json の
    ///     端末キャッシュが持っている）</item>
    ///   <item>1 枚の失敗はその 1 枚だけ飛ばす。全滅しても目は従来どおり（ジャックが出ないだけ）</item>
    ///   <item>デコードは 1 フレーム 1 枚（メインスレッド。まとめて食うとフレームが止まる）</item>
    ///   <item>取りこぼしは 30 秒ごとに黙って再試行（当日、写真がフォルダへ増えるたびに追随する）</item>
    /// </list>
    /// </summary>
    public sealed class EyeJackPhotoStore : IDisposable
    {
        /// <summary>持つ枚数の上限。ジャックが出すのは最大 12 枚（<see cref="EyeJackLogic"/>）+ 余裕。</summary>
        public const int MaxPhotos = 16;

        /// <summary>取りこぼしの再試行間隔 (秒)。</summary>
        public const float RetrySec = 30f;

        private readonly List<Texture2D> _ready = new();
        private readonly CancellationTokenSource _cts = new();
        private string[] _urls = Array.Empty<string>();
        private int _seenRev = -1;
        private bool _syncing;
        private bool _incomplete;
        private float _retryAt;
        private int _generation;   // リストが変わるたびに増える（走行中の Sync の結果を捨てる）

        /// <summary>端末に用意できた（デコード済みの）枚数。ファイル名順。</summary>
        public int ReadyCount => _ready.Count;

        /// <summary>i 枚目（0 始まり）。範囲外は null。</summary>
        public Texture2D? Get(int i) => i >= 0 && i < _ready.Count ? _ready[i] : null;

        /// <summary>
        /// 発火の瞬間の写真一式を写し取る（ジャックの最中にリストが入れ替わっても順序が崩れない）。
        /// </summary>
        public Texture2D[] Snapshot() => _ready.ToArray();

        private static string CacheDir => Path.Combine(Application.persistentDataPath, "eyejack");

        /// <summary>
        /// 毎フレーム呼んでよい（変更が無ければ int 比較 1 回で返る）。
        /// リストの変更・再試行時刻で同期を仕掛ける。
        /// </summary>
        /// <param name="inUse">
        /// いま視界ジャックが走っているか。<b>走っている間は差し替えを仕掛けない。</b>
        ///
        /// ⚠⚠ <see cref="SyncAsync"/> は完了時に <c>Clear()</c> で <c>_ready</c> のテクスチャを
        /// <c>Destroy</c> するが、<see cref="AnomalyEyes"/> は発火の瞬間に <see cref="Snapshot"/> で
        /// **参照の配列**を掴んでいるだけなので、破棄されると次のコマで <c>tex == null</c> になり
        /// <b>残りの写真が 1 枚も出ないまま乗っ取りが終わる</b>（2026-08-30）。
        /// 踏むのは「写真を 1 枚でも落とせていない（30 秒ごとに再試行）状態で 3 周目 C に入り、
        /// ジャックの 2.4 秒に再試行が重なったとき」と「卓が写真リストを編集したとき」。
        /// 延期しても rev も再試行時刻も進めないので、ジャックが終われば次のフレームで仕掛かる。
        /// </param>
        public void Tick(ShowControlClient? show, float now, bool inUse = false)
        {
            if (show == null || _syncing || inUse) return;
            bool revChanged = show.EyeJackPhotosRev != _seenRev;
            bool retryDue = _incomplete && now >= _retryAt;
            if (!revChanged && !retryDue) return;

            _seenRev = show.EyeJackPhotosRev;
            _urls = show.ResolveEyeJackPhotoUrls();
            _retryAt = now + RetrySec;
            _ = SyncAsync(_cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            Clear();
        }

        private void Clear()
        {
            foreach (var t in _ready)
                if (t != null) UnityEngine.Object.Destroy(t);
            _ready.Clear();
        }

        private async Task SyncAsync(CancellationToken ct)
        {
            _syncing = true;
            int gen = ++_generation;
            try
            {
                Directory.CreateDirectory(CacheDir);
                var texes = new List<Texture2D>();
                var keep = new HashSet<string>();
                bool missed = false;
                int taken = 0;

                foreach (string url in _urls)
                {
                    if (taken >= MaxPhotos) break;
                    if (string.IsNullOrEmpty(url)) continue;
                    string path = Path.Combine(CacheDir, FileKey(url));
                    keep.Add(Path.GetFileName(path));

                    if (!File.Exists(path) && !await DownloadAsync(url, path, ct)) { missed = true; continue; }

                    Texture2D? tex = Decode(path, url);
                    if (tex == null)
                    {
                        // 壊れたキャッシュは消して次回の再試行で取り直す。
                        TryDelete(path);
                        missed = true;
                        continue;
                    }
                    texes.Add(tex);
                    taken++;
                    // デコードは 1 フレーム 1 枚（メインスレッドを 1 枚ぶん以上塞がない）。
                    await Task.Yield();
                    ct.ThrowIfCancellationRequested();
                }

                if (gen != _generation)
                {
                    // 走っている間にリストが変わった。この結果は古いので捨てる（新しい Sync が走る）。
                    foreach (var t in texes) if (t != null) UnityEngine.Object.Destroy(t);
                    return;
                }

                Clear();
                _ready.AddRange(texes);
                _incomplete = missed;
                // ⚠⚠ **取りこぼした回は掃除しない**（2026-08-30）。ファイル名は
                //    「卓のホストを含む**絶対 URL** の SHA1」（`ShowAssetResolver` が相対 URL を
                //    卓のホストで解決する）なので、**卓の IP が変わると全キーが変わる**。
                //    そのとき 1 枚も落とせていなくても旧ファイルは全部 `keep` から漏れて消え、
                //    「卓が死んでいても前回のキャッシュから出す」という設計がその場で破れる。
                //    当日の Wi-Fi 再接続・DHCP の更新で普通に起きる。
                if (!missed) Prune(keep);
                if (_urls.Length > 0 || _ready.Count > 0)
                    Debug.Log($"[EyeJack] 写真 {_ready.Count}/{_urls.Length} 枚を用意した" +
                              (missed ? "（取りこぼしあり — 30 秒後に再試行）" : ""));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _incomplete = true;
                Debug.LogWarning($"[EyeJack] 写真の同期に失敗: {e.Message}（30 秒後に再試行）");
            }
            finally { _syncing = false; }
        }

        private static async Task<bool> DownloadAsync(string url, string path, CancellationToken ct)
        {
            string tmp = path + ".part";
            try
            {
                using var req = UnityWebRequest.Get(url);
                req.downloadHandler = new DownloadHandlerFile(tmp);
                req.timeout = 10;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[EyeJack] 写真を落とせない: {url}（{req.error}）");
                    TryDelete(tmp);
                    return false;
                }
                // 書きかけを正の名前にしない（半端なファイルが「ある」と読まれると壊れた絵が出る）。
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (OperationCanceledException) { TryDelete(tmp); throw; }
            catch (Exception e)
            {
                Debug.LogWarning($"[EyeJack] 写真の取得に失敗: {url}（{e.Message}）");
                TryDelete(tmp);
                return false;
            }
        }

        private static Texture2D? Decode(string path, string url)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                // 正規化済み（長辺 1280）なので mip は要らない（全視界表示 ＝ 縮小して見せない）。
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: false)
                {
                    name = "EyeJackPhoto:" + Path.GetFileName(path),
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                if (!tex.LoadImage(bytes, markNonReadable: true))
                {
                    UnityEngine.Object.Destroy(tex);
                    Debug.LogWarning($"[EyeJack] 写真をデコードできない: {url}");
                    return null;
                }
                return tex;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EyeJack] 写真の読込に失敗: {url}（{e.Message}）");
                return null;
            }
        }

        /// <summary>いまのリストに無いキャッシュを消す（当日撮り直した分だけが残るように）。</summary>
        private static void Prune(HashSet<string> keep)
        {
            try
            {
                foreach (string f in Directory.GetFiles(CacheDir))
                    if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
            }
            catch (Exception e) { Debug.LogWarning($"[EyeJack] キャッシュ掃除に失敗: {e.Message}"); }
        }

        // URL → 安定ファイル名（SHA1 hex + 元の拡張子）。GetHashCode は 32bit で衝突しうる。
        private static string FileKey(string url)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            byte[] bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            var sb = new System.Text.StringBuilder(bytes.Length * 2 + 4);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            string ext = ".jpg";
            try
            {
                string e = Path.GetExtension(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                    ? new Uri(url).AbsolutePath : url);
                if (e == ".png" || e == ".jpeg" || e == ".jpg") ext = e;
            }
            catch { /* 拡張子が読めなくても jpg として扱う（LoadImage は中身で判定する） */ }
            sb.Append(ext);
            return sb.ToString();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Debug.LogWarning($"[EyeJack] 一時ファイル削除に失敗: {e.Message}"); }
        }
    }
}
