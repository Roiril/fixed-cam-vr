---
description: Redmi Pad SEのUSBインストール確認が制限時間で自動拒否する。USBインストール設定がオンでもUSER_RESTRICTEDになる。
---

# Redmi Pad SEへのAPK導入

2026-10-03。端末番号 `122d7871`。モデル `23073RPBFG`。画面はAwakeでADBはdeviceだった。
`adb install -r` と `adb install --no-incremental -r` がどちらも `INSTALL_FAILED_USER_RESTRICTED: Install canceled by user` で終了した。
`pm path com.roiril.mawarimi.tablet` はパッケージを返さなかった。署名とAPK内資産の照合は成功していた。

その後のスクリーンショットで「USB経由でインストール」の確認ダイアログを観測した。「拒否 (3)」の制限時間で自動拒否していた。利用者はUSBインストール設定を許可済みと述べた。従ってこのエラーだけでUSBインストール設定がオフとは判断しない。

利用者がUSBでの画面取得と端末操作を明示した後、uiautomatorの現在の画面でダイアログの見出しとアプリ名を照合した。有効な「インストール」の位置を読み取ってADBで押し、通常APKと試験APKの両方がSuccessになった。確認は15秒以内に行う。「この選択を記憶」は変更しない。ADBから端末の設定保護を解除しない。

博士アプリの成果物は `Builds/doctor-tablet.apk`。機能試験は `Builds/doctor-tablet-test.apk`。導入はpm pathでも確認する。画面の実描画と機能試験が通るまでは動作確認済みと報告しない。
