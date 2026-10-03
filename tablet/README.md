# 廻リ視の博士タブレット

博士UIはAndroidタブレットに入れる独立アプリ。画面と説明素材はこのアプリのAPKに同梱する。PCとインターネットは起動に不要。QuestからHTMLや動画を取得しない。

## 起動と接続

ホーム画面の「廻リ視 博士」を開く。横向きと全画面で起動する。前面にある間は画面を維持する。

タイトルの「スタッフ設定」でクエストαかβを選ぶ。本体のラベルと照合して接続を確認する。選択先はタブレット内へ保存する。IPを入力する欄や別のQuestのページへ移る操作はない。

Questとタブレットは同じ展示Wi-Fiへ接続する。Questでは「廻リ視」を起動する。αは `192.168.10.31:8090`。βは `192.168.10.32:8090`。通信先はアプリ内の固定表で決まる。

画面と素材は未接続でも読み込める。スタッフ設定の「説明を確認する」で説明を確認できる。この確認ではQuestへの設定送信と反映済み表示を行わない。来場者の説明開始はQuestの実値と反映番号を確認してから行う。

## ビルドと導入

Unityに同梱されたAndroid SDKとOpenJDKでビルドする。Gradleや追加ライブラリは使わない。

```powershell
py -3.11 tablet/build.py
adb -s 122d7871 install -r Builds/doctor-tablet.apk
adb -s 122d7871 shell am start -n com.roiril.mawarimi.tablet/.MainActivity
```

出力は `Builds/doctor-tablet.apk`。署名鍵はホーム配下のローカル鍵を使う。鍵とビルド出力はコミットしない。

## 正本と更新

画面の正本は `app/src/main/assets/web/index.html`。説明JSONと動画と画像と音声は `app/src/main/assets/web/asset/`。拡張子に `.bytes` を追加しない。

素材は `tools/visitor-ui/` の再生成ツールで更新する。画面や動画を変更したときはタブレットAPKだけを焼き直して各タブレットへ導入する。Questの演出や設定APIの変更はQuest側のAPK更新が必要。

既存Quest APK内の旧ページは移行前の版として残る。新しいタブレットアプリはそのページを使わない。現在のQuestソースでは旧ページと素材のGETを410にする。次のQuest通常ビルドから博士素材を同梱しない。

## 通信と検証

タブレットはQuestへ `GET /status` と `POST /set` と `POST /clear` と `POST /tablet/pulse` だけを送る。Androidの通信処理が固定した2台へ送る。HTMLの取得と動画の再生はタブレット内で完結する。

設定送信の受理だけで成功にしない。Questの起動IDとタブレットのページIDを照合する。受理番号と反映番号と言語とホラー軽減も一致させる。通信が切れたときは未確認として保持する。オフラインの送信予約は行わない。

机上の確認画面は `py -3.11 tools/visitor-portal-stub.py --port 8093`。このPC上の代役はAPKの実機通信を証明しない。画面の回帰検証は `node tools/visitor-ui/verify-runtime.cjs`。実機では同梱素材の再生と途中シークを確認する。αとβの接続先も別々に確認する。

実機の機能試験用APKも追加依存なしで作れる。

```powershell
py -3.11 tablet/build.py --instrumentation
adb -s 122d7871 install -r Builds/doctor-tablet.apk
adb -s 122d7871 install -r Builds/doctor-tablet-test.apk
adb -s 122d7871 shell am instrument -w -e requireQuest true com.roiril.mawarimi.tablet.test/.TabletInstrumentation
```

試験は同梱素材の読み込みと動画の実描画とシークと背景停止を確認する。`requireQuest true` はQuest αの通信も必須にする。通常APKには試験操作の入口を含めない。USBインストールが端末に拒否された場合は端末側の確認を行う。

2026-10-03にRedmi Pad SEへ導入した。Quest α接続時は29項目が成功。Quest αを停止した状態でも26項目が成功した。動画5本すべてでシーク後の描画フレーム増加を確認した。ホーム画面へ移った間の媒体の進行は0.16秒未満。復帰後も停止を維持した。αへの設定送信と取消を行い、元の言語と軽減値へ戻ったことを確認した。スタッフ設定から未接続の博士説明を開き、端末の画面も撮影した。βの実機接続は未確認。
