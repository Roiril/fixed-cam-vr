---
name: no-internet-autojoin-kill
description: 上流の無い展示網を Android が恒久無効化し、次の電源投入で全機が戻らなくなる。繋がっている間は一切表に出ない
metadata: 
  node_type: memory
  type: project
  originSessionId: aa629b9d-978a-41e5-b9f1-d22062359145
  modified: 2026-09-05T05:20:41.852Z
---

# 上流が無いと、Android が展示網の自動接続を殺す（2026-09-05 実測）

展示のネットワーク（Aterm・`kougaku-lab-exp-a`）は **WAN に何も繋がない**設計。
体験は全部その中で閉じるので正しい設計なのだが、**Android がそれを「使えない網」と判断する。**

```
dumpsys wifi:
  NetworkSelectionStatus          NETWORK_SELECTION_PERMANENTLY_DISABLED
  mNetworkSelectionDisableReason  NETWORK_SELECTION_DISABLED_NO_INTERNET_PERMANENT
  hasEverConnected: true
  hasNeverDetectedCaptivePortal: true
→ 20〜60 秒おきに Ignoring network selection disabled SSID: "kougaku-lab-exp-a"
```

## ⚠⚠ たちの悪さ

**繋がっている間は 1 ビットも表に出ない。** 既に張れている接続はそのまま続くので、
卓も画も音も正常。**次に電源を入れ直した時に、初めて戻ってこない。**

⇒ **展示の朝、全機が同時に踏む形。** しかも「昨日まで動いていた」ので原因に到達しにくい。

**相手は Quest だけではない。配信スマホ 3 台も Android** なので同じ穴を持つ。

## 見つけた経緯（推測ではなく実測）

「電源を入れるところからの流れを作る」という依頼で、**再起動して静的 IP が戻るかを試した**のが発端。

1. Quest β を再起動 → **182 秒たっても Wi-Fi に戻らない**
2. AP は見えている（`-35dBm`）・保存済みも残っている・Wi-Fi は enabled。それでも繋がらない
3. `dumpsys wifi` で `NETWORK_SELECTION_PERMANENTLY_DISABLED` と理由を特定
4. **α を見たら、繋がっているのに同じ状態だった** ＝ 落ちていないだけで既に死んでいた
5. α も再起動 → 同じく戻らない（**2 台とも**）

⭐ 試さなければ**当日の朝まで誰も気づけなかった**。ドキュメントには「静的 IP なので会場が変わっても
戻る」と書いてあり、実際 IP の設定は正しかった。**壊れていたのは IP ではなく「そこへ繋ぎに行くか」。**

## 直す / 予防する

| | 何をする | 誰が |
|---|---|---|
| **直す**（既に付いた恒久無効を消す） | **端末の Wi-Fi 設定で、その網を 1 度手で選び直す**（手で選ぶと `User Selected` が立って解ける） | 人。⚠ **adb からは戻せない** |
| **予防**（二度と付かないようにする） | 接続チェックを切る（下） | `onsite.py wifi-guard` / `adb-open` が自動で入れる |

```bash
adb -s <serial> shell "settings put global captive_portal_mode 0"
adb -s <serial> shell "settings put global captive_portal_detection_enabled 0"
```

⚠ **予防は「これから」にしか効かない。** 既に書き込まれた恒久無効は消えない
（実測: フラグを入れてから再起動しても戻らなかった）。**順番は「手で選び直す → 予防」ではなく
「予防を入れておく → 一度手で選び直す」**で、以後は付かない。

### ⭐ ここまで実証済み（2026-09-05・Quest 2 台）

`tools/wifi-rejoin.ps1 -Verify` を通して確かめた。**推定ではない。**

1. **`cmd wifi connect-network <ssid> wpa2 <pass>` で恒久無効が解ける**
   （＝ 人が端末で選び直すのと同じ効果。ヘルプに出ない隠れコマンドだが動く）
2. **静的 IP は保たれる。** この経路で繋いでも `.31` / `.32` のまま
   （⚠ 「設定が作り直されて DHCP に戻る」と予想したが**外れた**）
3. **予防を入れた状態なら、再起動しても戻ってくる。** 2 台とも `★ 戻りました`。
   直後の `dumpsys wifi` で `NETWORK_SELECTION_DISABLED_NO_INTERNET` の記述が **0 件**

⚠ **パスワードは人が打つ。** スクリプトは `Read-Host -AsSecureString` で受けるので、
画面にも履歴にも残らない。**シュビーは代われない**（何度頼まれても同じ）。

⭐ **根っこから消すなら、Aterm の WAN を上流のある回線へ挿す。** 検証が通れば
この判定自体が起きないので、Quest もスマホも全部まとめて解決する。会場に挿せる線があるなら
それがいちばん強い（LAN 側は NAT の内側のままなので、端末が別の AP へ移る心配は増えない）。

## ⚠⚠ 計器が 2 つとも嘘をついた（2026-09-05 の設営で発覚。どちらも直した）

**直したあとも「殺されている」と出続けた。** `wifi_guard` が `dumpsys wifi` の**全文**に
`"NETWORK_SELECTION_PERMANENTLY_DISABLED" in dump` を掛けていたため。dumpsys には過去の走査の
写しが ring buffer で何時間も残る（実測: カメラ B は現在の設定が `ENABLED` なのに、
11:53〜14:06 の写しが 9 件居座っていた）。⇒ **`WifiConfigManager - Configured networks` の節だけを
読む**（`configured_networks()`）。判定は「いまの設定」から取る。

⭐ **判定するときは、必ず「止まるはず」と「通るはず」の 2 通りを流してから本題を測る。**
今回は「古い写しだけが残る dump」を作って通し、旧実装ならそれが `True` になることまで確かめた。

**adb に出ている＝Quest ではない。** `check` が `adb devices` の全件を Quest として数えていたので、
設営で配信スマホを USB に挿し `:5555` を開けた直後は **7 件**並び、**Pixel 3 台が
「Quest / アプリが入っていません」の NG** になった（本物の NG に紛れる）。
⇒ `adb devices -l` の `model:Quest` で絞り、**同じ機が USB と `:5555` で 2 回出るので
`ro.serialno` で畳む**。無線側は `s[-6:]` が `1:5555` になって名前も壊れていた。

## 道具

```bash
py -3.11 tools/onsite.py wifi-guard   # 殺されていないか見る＋予防を入れる
py -3.11 tools/onsite.py adb-open     # USB のついでに予防を入れる（配信スマホはこれしか経路が無い）
py -3.11 tools/onsite.py check        # Quest ごとに「自動接続」の行が出る
```

⚠ **配信スマホには USB を挿すしか手が無い**（`:5555` は端末を再起動すると閉じ、
2026-09-04・05 とも 3 台とも閉じていた）。設営で USB を挿す一瞬が、予防を入れられる唯一の機会。

## 同時に確かめた、問題なかったこと

- **クライアント間分離は掛かっていない。** Quest → カメラ 3 台と Quest → 卓、TCP が全部通る
  （計器の校正: 閉じたポートと居ない相手は `rc=1` を返すことを確認済み）
- **帯域は問題にならない。** 3 本同時で**合計 4.87 Mbps**・各 28〜29fps（リンクは 866Mbps）
- **5GHz は 5180MHz ＝ W52。** DFS の瞬断は起きない
- **待機中の ping が 95〜123ms なのは端末の省電力。** 引いている最中は 4〜7ms に戻る
  （⚠ 待機中の数字で電波を判定しない）
- ⚠ カメラ B は熱段 2 / `thermalStatus=3` / 40.3℃ で、昨日から下がっていない

関連: [[onsite_day_ops]] / [[camera_fleet]] / [[quest_fleet_two_devices]]
