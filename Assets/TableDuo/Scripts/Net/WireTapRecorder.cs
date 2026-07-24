#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 通信ワイヤタップ記録（ソロ／PC ホスト実機検証用）。開始/停止を
    ///   - キーボード F9（PC ホスト＝コントローラ無し）
    ///   - 画面の GUI ボタン（PC ホスト。マウスで押せる）
    /// のいずれかでトグルし、その間「通信上で見えるアバターの動き」を CSV に記録する:
    /// （コントローラ B バインドは撤去 — コントローラは視点リセット専用化。2026-07-18）
    ///   - dir=sent: この端末が相手へ実際に送出した pose（<see cref="ConnectionManager.LocalPoseSent"/>
    ///     ＝ワイヤ送出点のタップ。ローカル描画用の手は経由しない — 手側検証の主対象）
    ///   - dir=recv: ネットワーク経由で受信・デコード・Seq フィルタ通過後の相手 pose
    ///     （<see cref="ConnectionManager.RemotePoseReceived"/>）
    /// どちらも「通信経路を通ったデータそのもの」で、ローカルのトラッキング値を横取りしない。
    /// 出力: persistentDataPath/tdv_wiretap_yyyyMMdd_HHmmss.csv（adb pull / PC の LocalLow で回収）。
    ///
    /// 記録中は <see cref="DiagnosticsEnabled"/> を立て、受信手 pose の要約（[TDV-WIRE]）と
    /// 描画適用側の要約（[TDV-DRAW]、<see cref="RemoteHandView"/>）を throttle ログする。
    /// 両者を突き合わせると「データが崩れている（wire 側で既に変）」のか
    /// 「描画/リターゲットが崩している（wire は素直だが draw で変）」のかを切り分けられる。
    /// </summary>
    public sealed class WireTapRecorder : MonoBehaviour
    {
        private const float FlushIntervalSec = 2f;   // 電源断・force-stop でも直近まで残す
        private const float DiagIntervalSec = 1f;    // 診断ログの throttle（受信手 pose の要約）

        /// <summary>記録中フラグ。RemoteHandView が描画適用の診断ログを出すかの判定に使う（同 asmdef 参照）。</summary>
        public static bool DiagnosticsEnabled { get; private set; }

        private StreamWriter? _writer;
        private string _path = "";
        private int _sentRows;
        private int _recvRows;
        private float _nextFlush;
        private float _nextDiag;
        private readonly StringBuilder _sb = new(640);

        private ConnectionManager? _cm;
        private Action<AvatarPose>? _onSent;
        private Action<ulong, AvatarPose>? _onRecv;

        // デバッグ専用 GUI（左下）の遅延生成スタイル
        private GUIStyle? _titleStyle;
        private GUIStyle? _hintStyle;

        public bool IsRecording => _writer != null;
        /// <summary>記録済み行数（送信 / 受信）。FacilitatorPanel の記録セクションが表示に使う。</summary>
        public int SentRows => _sentRows;
        public int RecvRows => _recvRows;
        /// <summary>記録の開始/停止トグル（FacilitatorPanel のボタンから呼ぶ。F9 と同じ）。</summary>
        public void ToggleRecording() => Toggle();

        private void Update()
        {
            // コントローラは視点リセット専用化（2026-07-18）。記録は F9 / PC GUI で
            bool toggle = Input.GetKeyDown(KeyCode.F9);
            if (toggle) Toggle();

            if (IsRecording && Time.unscaledTime >= _nextFlush)
            {
                _nextFlush = Time.unscaledTime + FlushIntervalSec;
                try { _writer!.Flush(); } catch { /* flush 失敗は次周期で再試行 */ }
            }
        }

        // 通信記録はデバッグ専用パネルとして画面左下に分離（2026-07-24）。運営パネル（FacilitatorPanel・
        // 右端）は映像記録に置き換わり、WireTap は調査運用では通常使わない。旧: 独自 GUI を画面右上に
        // 描いて FacilitatorPanel と重なり文字が潰れていた → 左下（左上 Spectator / ConnectionManager・
        // 右端 FacilitatorPanel と非重複）へ移設。キーボード F9 トグル（Update）と公開 API
        // （ToggleRecording / IsRecording）は従来どおり。
        private void OnGUI()
        {
            if (Application.platform == RuntimePlatform.Android) return; // PC ホスト専用
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer) return;

            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
                _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true };
                _hintStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            }

            const float w = 250f, h = 118f;
            var area = new Rect(12f, Screen.height - h - 12f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("【デバッグ】通信記録（WireTap）", _titleStyle);
            GUILayout.Label("調査運用では通常使いません", _hintStyle);
            if (IsRecording)
            {
                GUILayout.Label($"● 記録中  送信 {_sentRows} / 受信 {_recvRows}");
                if (GUILayout.Button("■ 記録を停止（F9）")) Toggle();
            }
            else
            {
                GUILayout.Label("停止中");
                if (GUILayout.Button("● 記録を開始（F9）")) Toggle();
            }
            GUILayout.EndArea();
        }

        private void Toggle()
        {
            if (IsRecording) StopRecording();
            else StartRecording();
        }

        private void StartRecording()
        {
            _cm = ConnectionManager.Instance;
            if (_cm == null)
            {
                Debug.LogWarning("[TableDuo][WireTap] ConnectionManager 不在のため記録を開始できません");
                return;
            }
            try
            {
                _path = Path.Combine(Application.persistentDataPath,
                    $"tdv_wiretap_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _writer = new StreamWriter(_path, false, new UTF8Encoding(false)) { NewLine = "\n" };
                _writer.WriteLine("tMs,dir,origin,seq,captureMs,trackedL,trackedR,pinchL,pinchR," +
                    "headX,headY,headZ,headEX,headEY,headEZ," +
                    "wristLX,wristLY,wristLZ,wristLEX,wristLEY,wristLEZ," +
                    "wristRX,wristRY,wristRZ,wristREX,wristREY,wristREZ," +
                    "bendIdxL,bendIdxR,boneMaxL,boneMaxR");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TableDuo][WireTap] 記録ファイルを開けません: {e.Message}");
                _writer = null;
                return;
            }

            _sentRows = 0;
            _recvRows = 0;
            _nextFlush = Time.unscaledTime + FlushIntervalSec;
            _nextDiag = 0f;
            _onSent = pose => WriteRow("sent", LocalClientId(), pose);
            _onRecv = (origin, pose) => { WriteRow("recv", origin, pose); DiagRecv(origin, pose); };
            _cm.LocalPoseSent += _onSent;
            _cm.RemotePoseReceived += _onRecv;
            DiagnosticsEnabled = true; // 描画適用側（RemoteHandView）の診断ログも有効化
            Debug.Log($"[TableDuo][WireTap] ● 記録開始 → {_path}（F9 / GUI で停止）。診断ログ [TDV-WIRE]/[TDV-DRAW] を有効化");
        }

        private void StopRecording()
        {
            Unsubscribe();
            DiagnosticsEnabled = false;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo][WireTap] クローズ時エラー: {e.Message}");
            }
            _writer = null;
            Debug.Log($"[TableDuo][WireTap] ■ 記録終了 sent={_sentRows} recv={_recvRows} → {_path}");
        }

        private void Unsubscribe()
        {
            if (_cm != null)
            {
                if (_onSent != null) _cm.LocalPoseSent -= _onSent;
                if (_onRecv != null) _cm.RemotePoseReceived -= _onRecv;
            }
            _onSent = null;
            _onRecv = null;
        }

        private void OnDestroy()
        {
            if (IsRecording) StopRecording();
        }

        private static ulong LocalClientId()
        {
            var nm = NetworkManager.Singleton;
            return nm != null ? nm.LocalClientId : ulong.MaxValue;
        }

        // 受信手 pose の要約を throttle ログ（データ側）。描画側の [TDV-DRAW] と突き合わせて切り分ける。
        // wristEuler=手首の向き / boneMax=指骨の最大回転角（0 ≒ 全 identity＝指が動いていない/凍結）
        private void DiagRecv(ulong origin, AvatarPose pose)
        {
            if (Time.unscaledTime < _nextDiag) return;
            _nextDiag = Time.unscaledTime + DiagIntervalSec;

            var er = pose.WristRotR.eulerAngles;
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pose.CaptureMs;
            Debug.Log($"[TDV-WIRE] recv client{origin} R tracked={(pose.TrackedR ? 1 : 0)} " +
                      $"wristEuler=({er.x:F0},{er.y:F0},{er.z:F0}) boneMaxR={MaxBoneAngle(pose.BonesR):F0} " +
                      $"bendIdxR={BoneAngle(pose.BonesR, 7):F0} pinchR={(pose.PinchR ? 1 : 0)} " +
                      $"seq={pose.Seq} age={ageMs}ms wristPosR={pose.WristPosR:F2}");
        }

        private void WriteRow(string dir, ulong origin, AvatarPose pose)
        {
            if (_writer == null) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var he = pose.HeadRot.eulerAngles;
                var wl = pose.WristRotL.eulerAngles;
                var wr = pose.WristRotR.eulerAngles;

                _sb.Clear();
                _sb.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append(',');
                _sb.Append(dir).Append(',');
                _sb.Append(origin == ulong.MaxValue ? "local" : origin.ToString(inv)).Append(',');
                _sb.Append(pose.Seq).Append(',');
                _sb.Append(pose.CaptureMs).Append(',');
                _sb.Append(pose.TrackedL ? 1 : 0).Append(',');
                _sb.Append(pose.TrackedR ? 1 : 0).Append(',');
                _sb.Append(pose.PinchL ? 1 : 0).Append(',');
                _sb.Append(pose.PinchR ? 1 : 0).Append(',');
                AppendV3(pose.HeadPos, inv);
                AppendV3(he, inv);
                AppendV3(pose.WristPosL, inv);
                AppendV3(wl, inv);
                AppendV3(pose.WristPosR, inv);
                AppendV3(wr, inv);
                _sb.Append(BoneAngle(pose.BonesL, 7).ToString("F1", inv)).Append(',');
                _sb.Append(BoneAngle(pose.BonesR, 7).ToString("F1", inv)).Append(',');
                _sb.Append(MaxBoneAngle(pose.BonesL).ToString("F1", inv)).Append(',');
                _sb.Append(MaxBoneAngle(pose.BonesR).ToString("F1", inv));
                _writer.WriteLine(_sb.ToString());

                if (dir == "sent") _sentRows++;
                else _recvRows++;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo][WireTap] 行書き込み失敗（記録継続）: {e.Message}");
            }
        }

        private static float BoneAngle(Quaternion[] bones, int i)
            => (bones != null && i < bones.Length) ? Quaternion.Angle(Quaternion.identity, bones[i]) : 0f;

        private static float MaxBoneAngle(Quaternion[] bones)
        {
            if (bones == null) return 0f;
            float max = 0f;
            for (int i = 0; i < bones.Length; i++)
            {
                float a = Quaternion.Angle(Quaternion.identity, bones[i]);
                if (a > max) max = a;
            }
            return max;
        }

        private void AppendV3(Vector3 v, CultureInfo inv)
        {
            _sb.Append(v.x.ToString("F4", inv)).Append(',');
            _sb.Append(v.y.ToString("F4", inv)).Append(',');
            _sb.Append(v.z.ToString("F4", inv)).Append(',');
        }
    }
}
