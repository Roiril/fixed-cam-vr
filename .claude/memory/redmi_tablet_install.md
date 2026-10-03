---
description: Redmi Pad SEへのAPK導入でINSTALL_FAILED_USER_RESTRICTEDが出た実測。端末側のUSBインストール許可が必要。
---

# Redmi Pad SEへのAPK導入

2026-10-03。端末番号 `122d7871`。モデル `23073RPBFG`。画面はAwakeでADBはdeviceだった。
`adb install -r` と `adb install --no-incremental -r` がどちらも `INSTALL_FAILED_USER_RESTRICTED: Install canceled by user` で終了した。
`pm path com.roiril.mawarimi.tablet` はパッケージを返さなかった。署名とAPK内資産の照合は成功していた。

この拒否はAPKのコンパイル失敗と区別する。端末のインストール確認と開発者向けオプションの「USB経由でインストール」を利用者に確認してもらう。ADBから端末の設定保護を解除しない。同じ拒否を再試行で解決したとは扱わない。

博士アプリの成果物は `Builds/doctor-tablet.apk`。機能試験は `Builds/doctor-tablet-test.apk`。再開時は端末側の準備後に導入する。実機試験が通るまでは導入済みと報告しない。
