---
name: camera-fleet
description: 配信スマホ 3 台の実機構成（機種・アプリ・IP・スロット割り当て）と検証状況
metadata: 
  node_type: memory
  type: project
  originSessionId: 6d7a1a02-c926-4301-b3e5-40f96aeedf21
  modified: 2026-08-05T09:14:03.079Z
---

# 配信カメラ実機フリート（2026-08-05 17:10 更新）

**⚠ スロットは serial ではなく端末内の `camera_id` が正**（画面に大書され、beacon と `/info` に載る）。
下表の serial 対応は 2026-08-05 17:10 の実測。**それ以前の記録（Phone01=3C251 等）は誤りだった** —
同日 17:00 に実物の prefs を読んで判明した（下の事故を参照）。

| スロット | 端末 serial | install uuid（IP より確か） | 版 | 無線 adb |
|---|---|---|---|---|
| cam **A** | Pixel 7a（37201JEHN14152） | `b9ffed4c-18e2-4570-a8b8-2012c1e6559e` | **v0.9.0** | :5555 開 |
| cam **B** | Pixel 7a（3C251JEHN03582） | `429a472b-8131-401b-81a3-9cfb91ad2165` | **v0.9.0** | :5555 開 |
| cam **C** | Pixel 7a（37081JEHN03028） | `de2d8bea-7d42-4e6a-8823-c777a8901cae` | **v0.9.0** | :5555 開 |

3 台とも 2026-08-05 17:00〜17:20 に v0.9.0（傾きを配る）を入れ、レンズ 超広角 104.3°・
`tiltState` あり・配信中を実測で確認した。

IP は DHCP で毎回変わる（8/5 17:10 は A=.39 / B=.20 / C=.23）。**IP を覚えず beacon か
`/discovery` で引く。** `adb -s <serial> shell "ip -f inet addr show wlan0"` が serial ↔ IP の唯一確実な対応。

**⚠ 端末の設定は勝手に変わっていることがある**（2026-08-05 実害）。3 台を USB へ集めたあと読んだら、
1 台（3C251）が **cameraId=A・レンズ 2x（画角 37.7°）** になっていた ＝ **A が 2 台・B が 0 台**で、
しかも較正が前提にしている超広角 104.3° と全く違う画角。`install -r` は prefs を消さないので、
画面操作で変わったと思われる。前セッションの beacon 実測（uuid 429a472b が B を名乗っていた）から
**B・超広角へ戻した**。→ **アプリを入れ直したら必ず 3 台の `/info` で `cameraId` と `lensId` を並べて見る。**
直すのは prefs を直接書けば速い（`run-as com.fixedcamvr.streamer` / `shared_prefs/streamer_prefs.xml` /
キー `camera_id` `lens_zoom` `lens_name`。**force-stop してから書き、起動して `/info` で確認**）。
超広角の値は `lens_zoom=0.5304938` / `lens_name=超広角`。

**⚠ AE/AWB は既定でロックされていない。** 露出が自動で動くので、**素材を作った時と本番で色が変わる**
（2026-08-05 実害: 素材工房の試写と卓のライブ列で「加工後の絵が違う」とユーザーが気づいた。
真因は ①工房の背景が既定「種」＝撮った時の静止画 ②3 台とも `aeLock=false` の 2 つ）。
固定視点で露出が動くと差し替えの継ぎ目が出るので、**設営が済んだら本番の照明でロックする**。

ロックは端末画面の `🔓 露出/AF ロック` トグルだが、**adb から押せる**（HTTP API は無い）:

```bash
adb -s <ip>:5555 shell uiautomator dump /sdcard/ui.xml
adb -s <ip>:5555 shell cat /sdcard/ui.xml        # resource-id が lockToggle の bounds を読む
adb -s <ip>:5555 shell input tap <x> <y>
curl.exe -s http://<ip>:8080/health               # aeLock / awbLock が true になったか確認
```

⚠ **座標は端末ごとに違う**（画面の向きが違うため。実測で 2 台が 2232,470 / 1 台が 2252,435）。
必ず dump して読む。ロックは prefs に残るのでアプリを再起動しても復元される。
**照明を変えたら押し直す**（トグルなので 2 回押せば解除）。

**⚠ `/health` の `totalFrames=0` `fps=0` は故障ではない**（v0.7.0〜の需要駆動 encode）。
`clientCount=0` なら encode を止めるのが正常。生死は
`curl.exe -m 3 http://<ip>:8080/video -o NUL -w "%{size_download}"` で見る
（3 秒で 260〜530KB 出れば正常。exit 28 = timeout はエンドレスストリームなので正常）。

⚠ **`-o /dev/null` と `MSYS_NO_PATHCONV=1` を同時に使うと 0 バイトに見える**（2026-08-05 に踏んだ）。
adb 用に立てた `MSYS_NO_PATHCONV=1` が `/dev/null` の変換まで止めるので、Windows の `curl.exe` が
生の `/dev/null` を作ろうとして exit 23（書き込み失敗）。**3 台とも 0 バイトに見えて配信が死んだと誤読した。**
出力先は `NUL` と書く。

**無線 adb（`adb tcpip 5555`）は USB を繋いだついでに開けておく**（A と B は開いた）。
**端末を再起動すると閉じる。** C は USB デバッグ未承認（`unauthorized`）のままなので、
更新には端末画面での許可ダイアログ承認が要る — `adb kill-server && adb start-server` でも出ない場合は
ケーブルを挿し直す。

**入れた後は端末を水平に置いて `tiltPitchDeg` が 0° 付近になるかを見る**
（センサの符号と `targetRotation` の対応は実機でしか確かめられない唯一の箇所）。
机に平置きだと `tiltState="steep"`（pitch 88°）が正常＝カメラが真下を向いている。

**2026-07-17 から現行フリートは Pixel 7a ×3 に統一**（全台 streamer v0.2.0・認証なし・:8080）。iPhone 13 Pro + IP Camera Lite（:8081・Basic admin/admin）は予備構成へ降格 — 使う時は該当カメラの auth を戻す。

**v0.5.0（2026-07-18）**: 決定性キャプチャプロファイル — EIS/OIS/AF を明示 OFF（EIS は端末既定 ON で超広角を不可視クロップしていた・dumpsys 実測）、フリッカー 50Hz・露出補正 0 固定、AE/AWB は AUTO 維持（🔒は AE/AWB のみ）。実効値は `/health` の eisMode/cropRatio/iso 等で観測可。lensFovDeg は物理レンズ intrinsic 由来に是正（超広角 =104.3°、旧 129° は誤算出）。

**v0.4.0（2026-07-18）**: 配信中核を Service へ移管。HOME/画面 OFF/別アプリでも 30fps 維持（旧: 2.5fps に激減）。

**v0.3.0（2026-07-18）**: cameraId（A/B/C）を端末に刻印（画面巨大表示・再起動永続）+ fixedcam-discovery/1（UDP :8830、probe→unicast announce）。IP が変わっても Web 卓が自動追従・Quest が ID で自動張り替え。ID は .26=A / .12=B / .27=C（show.json と一致）。

**v0.2.0（2026-07-17）**: カメラエラー自動復旧 / WifiLock + WakeLock 定期更新 / 録画容量ガード / レンズ・ロック永続化 / `/info` appVersion・`/health` clientCount 追加。版確認は `curl http://<ip>:8080/info` の appVersion が最速。

**⚠ 起動の罠（2026-07-17 実害）**: 画面が Doze/スリープ中に `am start` でアプリを起動すると **Activity が resume されずカメラが bind 直後に閉じ、フレーム 0 のまま**になる（HTTP サーバだけ生きる）。→ 起動手順は `input keyevent KEYCODE_WAKEUP` + `wm dismiss-keyguard` → `am force-stop` → `am start`。起動後は KEEP_SCREEN_ON で維持される。

**Why:** 機種混在（iPhone ×1〜2 + Pixel ×2）が実運用構成。IP は現場の Wi-Fi ルータ次第で変わるので、host 値は参考値（毎現場で `/info` or curl で再確認）。

**How to apply:**
- host 変更は `Assets/Settings/Cameras/Phone0X.asset` を MCP `manage_scriptable_object` で書き換え（ローカル値・コミット禁止 — [[git-workflow]] のユーザー所有ファイル規約）
- 全端末超広角可: Pixel は streamer のレンズ選択（実測 FOV ≈128°）、iPhone は IP Camera Lite の「Back Ultra Wide Camera」選択（13 Pro 実機確認済み）
- iPhone の配信が重い（≈30Mbps）/ ウォーターマーク等の運用注意は [.claude/rules/streaming.md](../rules/streaming.md) の「iPhone（iOS）ソース」参照
- 3 セッション前の旧 IP（Pixel=192.168.11.8 / 11.12 等）がログや asset に残っていても気にしない（DHCP 変動）
