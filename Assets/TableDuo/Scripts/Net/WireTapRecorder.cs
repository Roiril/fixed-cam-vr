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
    /// 通信ワイヤタップ記録（ソロ実機検証用）。右コントローラ B ボタンで開始/停止をトグルし、
    /// その間に「通信上で見えるアバターの動き」を CSV に記録する:
    ///   - dir=sent: この端末が相手へ実際に送出した pose（<see cref="ConnectionManager.LocalPoseSent"/>
    ///     ＝ワイヤ送出点のタップ。ローカル描画用の手は経由しない — 手側検証の主対象）
    ///   - dir=recv: ネットワーク経由で受信・デコード・Seq フィルタ通過後の相手 pose
    ///     （<see cref="ConnectionManager.RemotePoseReceived"/>）
    /// どちらも「通信経路を通ったデータそのもの」であり、ローカルのトラッキング値を横取りしない。
    /// 出力: persistentDataPath/tdv_wiretap_yyyyMMdd_HHmmss.csv（adb pull で回収して解析）。
    /// </summary>
    public sealed class WireTapRecorder : MonoBehaviour
    {
        private const float FlushIntervalSec = 2f; // 電源断・force-stop でも直近まで残す

        private StreamWriter? _writer;
        private string _path = "";
        private int _sentRows;
        private int _recvRows;
        private float _nextFlush;
        private readonly StringBuilder _sb = new(512);

        private ConnectionManager? _cm;
        private Action<AvatarPose>? _onSent;
        private Action<ulong, AvatarPose>? _onRecv;

        public bool IsRecording => _writer != null;

        private void Update()
        {
            // トグル入力: 右コントローラ B（Quest）/ キーボード F9（PC ホスト＝コントローラ無し）。
            // 左 Y（バリアント切替）とは別ボタン。PC 観戦ホストでは B は来ないので F9 が主。
            bool toggle = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)
                          || Input.GetKeyDown(KeyCode.F9);
            if (toggle)
            {
                if (IsRecording) StopRecording();
                else StartRecording();
            }

            if (IsRecording && Time.unscaledTime >= _nextFlush)
            {
                _nextFlush = Time.unscaledTime + FlushIntervalSec;
                try { _writer!.Flush(); } catch { /* flush 失敗は次周期で再試行 */ }
            }
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
                    "wristLX,wristLY,wristLZ,wristRX,wristRY,wristRZ,indexBendL,indexBendR");
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
            _onSent = pose => WriteRow("sent", LocalClientId(), pose);
            _onRecv = (origin, pose) => WriteRow("recv", origin, pose);
            _cm.LocalPoseSent += _onSent;
            _cm.RemotePoseReceived += _onRecv;
            Debug.Log($"[TableDuo][WireTap] ● 記録開始 → {_path}（B で停止）");
        }

        private void StopRecording()
        {
            Unsubscribe();
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

        private void WriteRow(string dir, ulong origin, AvatarPose pose)
        {
            if (_writer == null) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var he = pose.HeadRot.eulerAngles;
                // 人差し指 Index2（OVR BoneId=7）の bind からの回転角 = 指の曲げ量プロキシ
                float bendL = Quaternion.Angle(Quaternion.identity, pose.BonesL[7]);
                float bendR = Quaternion.Angle(Quaternion.identity, pose.BonesR[7]);

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
                AppendV3(pose.WristPosR, inv);
                _sb.Append(bendL.ToString("F1", inv)).Append(',');
                _sb.Append(bendR.ToString("F1", inv));
                _writer.WriteLine(_sb.ToString());

                if (dir == "sent") _sentRows++;
                else _recvRows++;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TableDuo][WireTap] 行書き込み失敗（記録継続）: {e.Message}");
            }
        }

        private void AppendV3(Vector3 v, CultureInfo inv)
        {
            _sb.Append(v.x.ToString("F4", inv)).Append(',');
            _sb.Append(v.y.ToString("F4", inv)).Append(',');
            _sb.Append(v.z.ToString("F4", inv)).Append(',');
        }
    }
}
