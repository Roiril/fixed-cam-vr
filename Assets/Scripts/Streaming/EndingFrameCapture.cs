#nullable enable
using System.Collections;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕で使う 2 枚のスクリーン写真を GPU 上に保持する。
    /// <see cref="TakeRunner.EndingShotWindow"/> が開いたら最初の 1 枚をすぐ撮り、開いているあいだは
    /// <see cref="RefreshIntervalSec"/> ごとに撮り直す。終了時（<see cref="Freeze(EndingShotWindow)"/>）は
    /// 窓が開いていればその場で撮り直してから、最後に成立したフレームをそのまま残す。
    ///
    /// 信号処理の器（<see cref="CrtSignalProcessor"/>・Float の RT と材質）は撮るたびに作らず 1 つを使い回し、
    /// 固定・体験者の切り替え・無効化・破棄で捨てる。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public sealed class EndingFrameCapture : MonoBehaviour
    {
        public const int Width = 1280;
        /// <summary>参照の筐体を持たない 16:9 の画面の高さ。写真の高さは <see cref="HeightFor"/> で画面の縦横比から決める。</summary>
        public const int Height = 720;

        /// <summary>
        /// 窓が開いているあいだ写真を撮り直す間隔（秒）。最初の 1 枚は待たない。
        /// 帰還の写真は最初の 1 枚で窓が閉じ、人形 End の写真は終了時の <see cref="Freeze(EndingShotWindow)"/> が
        /// 撮り直すので、撮り直しが効くのは終了の前に窓が閉じたときの代わりの 1 枚だけ。毎フレームは要らない。
        /// 失敗した撮影のやり直しもこの間隔で待つ。
        /// </summary>
        public const float RefreshIntervalSec = 0.5f;

        /// <summary>
        /// LateUpdate で撮るか。まだ 1 枚も無く直前に失敗していなければすぐ撮る。それ以外は <paramref name="nextAt"/> を待つ。
        /// </summary>
        public static bool CaptureDue(bool hasShot, bool lastAttemptFailed, float now, float nextAt)
            => (!hasShot && !lastAttemptFailed) || now >= nextAt;

        /// <summary>
        /// 幅 <see cref="Width"/> の写真の高さを、画面の物理の縦横比（幅/高さ）から決める。
        /// 合成は画面の uv 0〜1 をそのまま描くので、縦横比が違う器へ写すと引き伸ばされる
        /// （参照の筐体つきの画面は 1280:1030。16:9 へ写すと終幕の写真が横長になった）。
        /// 縦横比が読めなければ 16:9。
        /// </summary>
        public static int HeightFor(float physicalAspect)
            => physicalAspect > 0.01f && !float.IsInfinity(physicalAspect)
                ? Mathf.Max(1, Mathf.RoundToInt(Width / physicalAspect)) : Height;

        /// <summary>画面の Transform の縦横比から写真の高さを決める。</summary>
        public static int HeightFor(Transform? screen)
        {
            if (screen == null) return Height;
            Vector3 s = screen.lossyScale;
            return Mathf.Abs(s.y) < 1e-5f ? Height : HeightFor(Mathf.Abs(s.x / s.y));
        }

        private static readonly int CrtSignalRuntimeId = Shader.PropertyToID("_CrtSignalRuntime");
        private static readonly int IntroLiveId = Shader.PropertyToID("_IntroLive");
        private static readonly int HeartVignetteId = Shader.PropertyToID("_HeartVignette");

        [Tooltip("合成結果を描く ScreenComposite の Renderer。null なら同じ GameObject から取得。")]
        [SerializeField] private Renderer? screenRenderer;

        private TakeRunner? _runner;
        private ShowRunDirector? _runDirector;
        private CameraFeelFx? _feel;
        private Material? _material;
        private RenderTexture? _trappedShot;
        private RenderTexture? _releasedShot;
        private Coroutine? _pending;
        /// <summary>写真を撮る信号処理の器。撮るたびに作らない。固定・リセット・無効化・破棄で捨てる。</summary>
        private CrtSignalProcessor? _signal;
        /// <summary>沈みを戻して描くための材質の写し。撮るたびに作らない。</summary>
        private Material? _sinkClone;
        private float _nextCaptureAt;
        private bool _lastAttemptFailed;
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
            ResetCaptureClock();
            ReleaseShots();
            ReleaseCaptureResources();
            UnhookRunDirector();
        }

        private void OnDestroy()
        {
            UnhookRunDirector();
            ReleaseShots();
            ReleaseCaptureResources();
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
            bool hasShot = (window == EndingShotWindow.Trapped ? _trappedShot : _releasedShot) != null;
            if (!CaptureDue(hasShot, _lastAttemptFailed, SimTime.Now, _nextCaptureAt)) return;
            _pending = StartCoroutine(CaptureAtEndOfFrame(window, _generation));
        }

        private IEnumerator CaptureAtEndOfFrame(EndingShotWindow window, int generation)
        {
            // batchmode では WaitForEndOfFrame が再開しない。PC の走行と再生は次のフレームの頭で撮る。
            if (Application.isBatchMode) yield return null;
            else yield return new WaitForEndOfFrame();
            _pending = null;
            // 前の体験者で予約された仕事を、新しい体験者の写真へ混ぜない。
            // 同じフレームの別 LateUpdate がカットを進めた場合も、古い分類へ新しい画を入れない。
            if (_frozen || generation != _generation || _runner == null
                || _runner.EndingShotWindow != window) yield break;

            bool captured = CaptureWindow(window);
            _lastAttemptFailed = !captured;
            _nextCaptureAt = SimTime.Now + RefreshIntervalSec;
            if (!captured) yield break;
            if (window == EndingShotWindow.Trapped) _trappedCount++;
            else if (window == EndingShotWindow.Released)
            {
                _releasedCount++;
                _runner.NotifyEndingFarewellCaptured();
            }
        }

        /// <summary>
        /// スクリーンに使っている material をそのまま描き、ライブ映像、素材、CG、加工を含む
        /// 最終合成結果を GPU の <paramref name="destination"/> へ写す。
        /// Editor プレビューもこの入口を使う。
        /// </summary>
        public bool CaptureComposite(RenderTexture destination)
        {
            ResolveMaterial();
            float sink = _runDirector != null ? _runDirector.EndingSink : 0f;
            if (_material == null || !(sink > 0f)) return CaptureWithOwnedSignal(_material, destination);

            // 時間切れの手前の沈み（ShowRunDirector.TickEndingSink）は体験者の目の前で起きることで、
            // 人形 End のエンドロールの写真には持ち込まない。画面の材質は書き換えず、写しへ沈む前の値を戻して描く。
            // 写しは撮るたびに作らず使い回す（捨てるのは ReleaseCaptureResources）。
            if (_sinkClone == null || _sinkClone.shader != _material.shader)
            {
                DestroyOwned(ref _sinkClone);
                _sinkClone = new Material(_material) { name = "Ending Sink Snapshot", hideFlags = HideFlags.HideAndDontSave };
            }
            else
            {
                _sinkClone.CopyPropertiesFromMaterial(_material);
                _sinkClone.shaderKeywords = _material.shaderKeywords;
            }
            if (_feel == null) _feel = GetComponent<CameraFeelFx>();
            if (_feel == null) _feel = FindObjectOfType<CameraFeelFx>();
            RemoveEndingSink(_sinkClone, sink, _feel != null ? _feel.AppliedHeartVignette : 0f);
            return CaptureWithOwnedSignal(_sinkClone, destination);
        }

        /// <summary>
        /// <see cref="CaptureComposite(Material, RenderTexture)"/> と同じ経路で描くが、CRT の信号処理は
        /// この部品が持ち続ける器（<c>_signal</c>）を使い回す。撮るたびに Float の RT と材質を作らない。
        /// </summary>
        private bool CaptureWithOwnedSignal(Material? material, RenderTexture destination)
        {
            if (material == null || destination == null) return false;
            if (!material.HasProperty(CrtSignalRuntimeId) || material.GetFloat(CrtSignalRuntimeId) <= 0.5f)
                return CaptureComposite(material, destination);
            RenderTexture? previous = RenderTexture.active;
            try
            {
                if (_signal == null || _signal.IsDisposed) _signal = new CrtSignalProcessor();
                return _signal.CaptureSnapshot(material, destination);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CrtSignal] 同期撮影に失敗: " + e);
                // 半端な状態の器を次の撮影へ持ち越さない。
                _signal?.Dispose();
                _signal = null;
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>
        /// 沈みを掛ける前の値を材質へ戻す。映像の出方（<c>_IntroLive</c>）は掛けた係数で割り戻し、
        /// 周辺の落ち込み（<c>_HeartVignette</c>）は心音のぶんだけにする。
        /// </summary>
        public static void RemoveEndingSink(Material material, float sink, float heartVignette)
        {
            float brightness = EndingDecisionLogic.SinkBrightness(sink);
            if (material.HasProperty(IntroLiveId) && brightness > 0f)
                material.SetFloat(IntroLiveId, Mathf.Clamp01(material.GetFloat(IntroLiveId) / brightness));
            if (material.HasProperty(HeartVignetteId))
                material.SetFloat(HeartVignetteId, Mathf.Max(0f, heartVignette));
        }

        /// <summary>指定 material の合成結果を同じ経路で描く Editor プレビュー用入口。</summary>
        public static bool CaptureComposite(Material? material, RenderTexture destination)
        {
            if (material == null || destination == null) return false;
            RenderTexture? previous = RenderTexture.active;
            try
            {
                if (material.HasProperty(CrtSignalRuntimeId) && material.GetFloat(CrtSignalRuntimeId) > 0.5f)
                    return CrtSignalProcessor.Capture(material, destination);
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
            => Freeze(EndingShotWindow.None);

        /// <summary>
        /// 終了フレームを同期撮影してから固定する。
        /// <paramref name="forcedWindow"/> は表示直後に成立した終了条件で、通常の撮影窓が
        /// EndOfFrame まで待てない場合だけ指定する。
        /// </summary>
        public void Freeze(EndingShotWindow forcedWindow)
        {
            if (_frozen) return;
            EndingShotWindow window = forcedWindow != EndingShotWindow.None
                ? forcedWindow
                : _runner != null ? _runner.EndingShotWindow : EndingShotWindow.None;
            if (window != EndingShotWindow.None && CaptureWindow(window))
            {
                if (window == EndingShotWindow.Trapped) _trappedCount++;
                else if (window == EndingShotWindow.Released)
                {
                    _releasedCount++;
                    _runner?.NotifyEndingFarewellCaptured();
                }
            }
            _frozen = true;
            // 固定した後は撮らない。信号処理の器（Float の RT）を次の体験者まで持ち越さない。
            ReleaseCaptureResources();
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
            ResetCaptureClock();
            ReleaseShots();
            ReleaseCaptureResources();
            Debug.Log($"[EndingFrameCapture] リセット generation={_generation} discarded={oldCount}");
        }

        private void ResetCaptureClock()
        {
            _nextCaptureAt = 0f;
            _lastAttemptFailed = false;
        }

        private bool CaptureWindow(EndingShotWindow window)
        {
            RenderTexture? existing = window == EndingShotWindow.Trapped ? _trappedShot : _releasedShot;
            if (existing != null) return CaptureComposite(existing);
            ResolveMaterial();

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

        private RenderTexture CreateTarget(string name)
        {
            int height = HeightFor(screenRenderer != null ? screenRenderer.transform : transform);
            var rt = new RenderTexture(Width, height, 0, RenderTextureFormat.ARGB32)
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

        /// <summary>撮るための器（信号処理と沈みの写し）を捨てる。写真そのもの（<see cref="ReleaseShots"/>）は別。</summary>
        private void ReleaseCaptureResources()
        {
            _signal?.Dispose();
            _signal = null;
            DestroyOwned(ref _sinkClone);
        }

        private static void DestroyOwned(ref Material? material)
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
            material = null;
        }
    }
}
