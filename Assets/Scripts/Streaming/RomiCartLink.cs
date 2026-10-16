#nullable enable
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 終幕で人形を載せた台車（Romi・ESP32）へ合図を送る。
    ///
    /// 体験者が最後の異常を解除した縁で <c>POST /ending/go</c>、帰還の結末で電源断が始まった縁で
    /// <c>POST /ending/cut</c>。<b>何をどれだけ動かすかは台車の NVS が持つ</b>（博士タブレットのスタッフ設定で
    /// 調整・保存する）。Quest α / β のどちらから送っても同じ動きになるよう、正は台車 1 か所にした。
    ///
    /// ⚠ 合図が届かなくても体験は 1 ビットも変わらない（台車が動かないだけ）。失敗は警告と
    /// テレメトリ（<c>ev=romi</c>・<c>ev=sum romi=</c>）にだけ出す。
    /// ⚠ Editor と自動走行（<c>ShowWalkDebugDriver</c>）は <b>空走</b>（<c>dry:1</c>）で送る。
    /// 台車は合図を記録するだけで動かない ＝ 経路の検証はできて、机の上の台車は落ちない。
    ///
    /// シーンには焼かない。<c>ShowControlClient</c> が起動時に生成する（<c>VisitorPortal</c> と同じ）。
    /// </summary>
    public sealed class RomiCartLink : MonoBehaviour
    {
        /// <summary>台車の固定 IP（展示網の割り当て: PC .10 / カメラ .21-.23 / Quest .31 .32 / タブレット .41 / 台車 .51）。</summary>
        public const string DefaultHost = "192.168.10.51";

        /// <summary>報告から電源断（終幕の頭）までの目安（秒）。タブレットの時刻表と対。正本は show.json の締めのカット。</summary>
        public const float ReleaseToCutSec = 7.1f;

        private const int TimeoutSec = 2;
        private const float RetryDelaySec = 0.3f;

        /// <summary>自動走行が立てる。立っている間は台車を動かさない（空走で合図だけ送る）。</summary>
        public static bool DryRun;

        private readonly RomiCartLinkLogic _logic = new RomiCartLinkLogic();
        private ShowControlClient? _show;
        private ShowRunDirector? _run;
        private int _goKey;

        /// <summary>送った合図の累計（Go と Cut の合計）。テレメトリは増えた縁で <c>ev=romi</c> を出す。</summary>
        public int Sent { get; private set; }
        /// <summary>台車が ok を返した累計。</summary>
        public int Acked { get; private set; }
        /// <summary>直近の合図（<c>go</c> / <c>cut</c>）・結果（<c>ok</c> / 台車の err / 通信の失敗）・往復の ms・空走か。</summary>
        public string LastKind { get; private set; } = "";
        public string LastResult { get; private set; } = "";
        public int LastRoundTripMs { get; private set; } = -1;
        public bool LastDry { get; private set; }
        /// <summary>結果が確定した回数（テレメトリが「結果が出た縁」を見るため）。</summary>
        public int Completed { get; private set; }

        public string Host => DefaultHost;

        private void Update()
        {
            if (_show == null) _show = FindObjectOfType<ShowControlClient>();
            if (_run == null) _run = FindObjectOfType<ShowRunDirector>();
            if (_show == null || _run == null) return;

            RomiCartLinkLogic.Signals s = _logic.Tick(_show.CurseReleasedCount, _run.Phase, _run.EndingOutcome);
            if (s.go) StartCoroutine(Send("go", "/ending/go"));
            if (s.cut) StartCoroutine(Send("cut", "/ending/cut"));
        }

        private bool ShouldDryRun()
        {
#if UNITY_EDITOR
            return true;   // Editor の Play / 仮想体験から本物の台車を動かさない
#else
            return DryRun;
#endif
        }

        private IEnumerator Send(string kind, string path)
        {
            bool dry = ShouldDryRun();
            if (kind == "go") _goKey++;
            // key は再送で台車が頭からやり直さないための名前（起動ごとに変わる時刻を混ぜる）
            string key = $"q{(int)(Time.realtimeSinceStartup * 10f)}-{_goKey}";
            string body = kind == "go"
                ? $"{{\"src\":\"quest\",\"dry\":{(dry ? 1 : 0)},\"key\":\"{key}\"}}"
                : "{\"src\":\"quest\"}";
            Sent++;
            LastKind = kind;
            LastDry = dry;
            LastResult = "pending";
            Debug.Log($"[Romi] 合図 {kind} を送る（{(dry ? "空走" : "本番")}・http://{Host}{path}）");

            // Go だけ 1 回だけ送り直す（key が同じなので台車は二重に始めない）。Cut は遅れて届くと意味が無いので送り直さない。
            int attempts = kind == "go" ? 2 : 1;
            for (int i = 0; i < attempts; i++)
            {
                float t0 = Time.realtimeSinceStartup;
                using var req = new UnityWebRequest($"http://{Host}{path}", "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = TimeoutSec;
                yield return req.SendWebRequest();
                LastRoundTripMs = (int)((Time.realtimeSinceStartup - t0) * 1000f);

                if (req.result == UnityWebRequest.Result.Success)
                {
                    string text = req.downloadHandler.text ?? "";
                    bool ok = text.Contains("\"ok\":1");
                    LastResult = ok ? "ok" : ExtractErr(text);
                    if (ok) Acked++;
                    else Debug.LogWarning($"[Romi] 台車が合図 {kind} を断った: {text}");
                    Completed++;
                    yield break;
                }
                LastResult = "net";
                Debug.LogWarning($"[Romi] 合図 {kind} が台車に届かない（{req.error}・{LastRoundTripMs}ms）");
                if (i + 1 < attempts) yield return new WaitForSecondsRealtime(RetryDelaySec);
            }
            Completed++;
        }

        private static string ExtractErr(string json)
        {
            const string k = "\"err\":\"";
            int i = json.IndexOf(k, System.StringComparison.Ordinal);
            if (i < 0) return "bad";
            int j = json.IndexOf('"', i + k.Length);
            return j < 0 ? "bad" : json.Substring(i + k.Length, j - i - k.Length);
        }
    }
}
