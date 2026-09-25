#nullable enable
using System.Collections;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕で使う 2 枚のスクリーン写真を GPU 上に保持する。
    /// <see cref="TakeRunner.EndingShotWindow"/> が開いているあいだ合成済みの画を更新し続け、
    /// 終了時には最後に成立したフレームをそのまま残す。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public sealed class EndingFrameCapture : MonoBehaviour
    {
        public const int Width = 1280;
        public const int Height = 720;

        [Tooltip("合成結果を描く ScreenComposite の Renderer。null なら同じ GameObject から取得。")]
        [SerializeField] private Renderer? screenRenderer;

        private TakeRunner? _runner;
        private ShowRunDirector? _runDirector;
        private Material? _material;
        private RenderTexture? _trappedShot;
        private RenderTexture? _releasedShot;
        private Coroutine? _pending;
        private int _generation;
        private int _trappedCount;
        private int _releasedCount;
        private bool _frozen;
        private bool _runRestartHooked;

        /// <summary>報告前に人形へ閉じ込められている画。成立しなければ null。</summary>
        public Texture? TrappedShot => _trappedShot;

        /// <summary>報告後に現実へ戻った画。成立しなければ null。</summary>
        public Texture? ReleasedShot => _releasedShot;

        /// <summary>保持済みの枚数（0〜2）。</summary>
        public int ShotCount => (_trappedShot != null ? 1 : 0) + (_releasedShot != null ? 1 : 0);

        private void Awake()
        {
            ResolveMaterial();
            ResolveRunDirector();
        }

        private void OnEnable() => ResolveRunDirector();

        private void OnDisable()
        {
            // disabled な MonoBehaviour でも開始済み coroutine は止まらないため、世代ごと無効化する。
            _generation++;
            if (_pending != null) StopCoroutine(_pending);
            _pending = null;
            _frozen = false;
            _trappedCount = 0;
            _releasedCount = 0;
            ReleaseShots();
            UnhookRunDirector();
        }

        private void OnDestroy()
        {
            UnhookRunDirector();
            ReleaseShots();
        }

        /// <summary>自動配置した <see cref="TakeRunner"/> と撮影条件を接続する。</summary>
        public void Bind(TakeRunner runner)
        {
            _runner = runner;
            ResolveMaterial();
            ResolveRunDirector();
        }

        private void LateUpdate()
        {
            if (_frozen || _pending != null || _runner == null) return;
            EndingShotWindow window = _runner.EndingShotWindow;
            if (window == EndingShotWindow.None) return;
            _pending = StartCoroutine(CaptureAtEndOfFrame(window, _generation));
        }

        private IEnumerator CaptureAtEndOfFrame(EndingShotWindow window, int generation)
        {
            yield return new WaitForEndOfFrame();
            _pending = null;
            // 前の体験者で予約された仕事を、新しい体験者の写真へ混ぜない。
            // 同じフレームの別 LateUpdate がカットを進めた場合も、古い分類へ新しい画を入れない。
            if (_frozen || generation != _generation || _runner == null
                || _runner.EndingShotWindow != window) yield break;

            if (!CaptureWindow(window)) yield break;
            if (window == EndingShotWindow.Trapped) _trappedCount++;
            else if (window == EndingShotWindow.Released) _releasedCount++;
        }

        /// <summary>
        /// スクリーンに使っている material をそのまま描き、ライブ映像、素材、CG、加工を含む
        /// 最終合成結果を GPU の <paramref name="destination"/> へ写す。
        /// Editor プレビューもこの入口を使う。
        /// </summary>
        public bool CaptureComposite(RenderTexture destination)
        {
            ResolveMaterial();
            return CaptureComposite(_material, destination);
        }

        /// <summary>指定 material の合成結果を同じ経路で描く Editor プレビュー用入口。</summary>
        public static bool CaptureComposite(Material? material, RenderTexture destination)
        {
            if (material == null || destination == null) return false;
            RenderTexture? previous = RenderTexture.active;
            try
            {
                Graphics.Blit(Texture2D.blackTexture, destination, material, 0);
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>終了処理が画を畳む前に、保持済みの最終フレームを固定する。</summary>
        public void Freeze()
        {
            if (_frozen) return;
            _frozen = true;
            Debug.Log($"[EndingFrameCapture] 固定 generation={_generation} shots={ShotCount} " +
                      $"trappedFrames={_trappedCount} releasedFrames={_releasedCount}");
        }

        /// <summary>1 枚でも成立済みなら固定する。演出だけの中止では空の器を固定しない。</summary>
        public void FreezeIfCaptured()
        {
            if (ShotCount > 0) Freeze();
        }

        private void OnRunRestarted()
        {
            int oldCount = ShotCount;
            _generation++;
            if (_pending != null) StopCoroutine(_pending);
            _pending = null;
            _frozen = false;
            _trappedCount = 0;
            _releasedCount = 0;
            ReleaseShots();
            Debug.Log($"[EndingFrameCapture] リセット generation={_generation} discarded={oldCount}");
        }

        private bool CaptureWindow(EndingShotWindow window)
        {
            RenderTexture? existing = window == EndingShotWindow.Trapped ? _trappedShot : _releasedShot;
            if (existing != null) return CaptureComposite(existing);

            RenderTexture created = CreateTarget(window == EndingShotWindow.Trapped
                ? "Ending Trapped Shot"
                : "Ending Released Shot");
            bool captured;
            try
            {
                captured = CaptureComposite(created);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EndingFrameCapture] GPU 合成に失敗: {e}");
                captured = false;
            }
            if (!captured)
            {
                created.Release();
                DestroyTarget(created);
                return false;
            }

            // Capture に成功してから公開する。未描画 RT を「写真あり」と数えない。
            if (window == EndingShotWindow.Trapped) _trappedShot = created;
            else _releasedShot = created;
            return true;
        }

        private static RenderTexture CreateTarget(string name)
        {
            var rt = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            rt.Create();
            return rt;
        }

        private void ResolveMaterial()
        {
            if (_material != null) return;
            if (screenRenderer == null) screenRenderer = GetComponent<Renderer>();
            _material = screenRenderer == null ? null
                : Application.isPlaying ? screenRenderer.material : screenRenderer.sharedMaterial;
        }

        private void ResolveRunDirector()
        {
            if (_runRestartHooked) return;
            if (_runDirector == null) _runDirector = FindObjectOfType<ShowRunDirector>();
            if (_runDirector == null) return;
            _runDirector.RunRestarted += OnRunRestarted;
            _runRestartHooked = true;
        }

        private void UnhookRunDirector()
        {
            if (_runRestartHooked && _runDirector != null)
                _runDirector.RunRestarted -= OnRunRestarted;
            _runRestartHooked = false;
        }

        private void ReleaseShots()
        {
            Release(ref _trappedShot);
            Release(ref _releasedShot);
        }

        private static void Release(ref RenderTexture? target)
        {
            if (target == null) return;
            target.Release();
            DestroyTarget(target);
            target = null;
        }

        private static void DestroyTarget(RenderTexture target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
