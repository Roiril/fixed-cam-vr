---
description: 博士タブレットのWebViewがRangeを再適用するため開始位置を二重に進めると音声デコードが失敗する。部分データのハッシュと音声付き通し再生で確認する。
---

# 博士タブレットの動画の途中読み出し

2026-10-05。Redmi Pad SEの最初の博士説明が止まった。Questへの通信は200成功のまま。Chromiumのログは `ffmpeg_audio_decoder DecoderStatus::1` と `PIPELINE_ERROR_DECODE`。イントロの1199370バイトからのRangeを繰り返した。

`MainActivity.assetResponse` はRangeの開始位置まで進めたストリームをWebViewへ返していた。WebViewのAndroidStreamReaderもRangeの開始位置まで進める。1048576バイトから100000バイトをfetchしたデータのSHA-256は、元ファイルの2097152バイトから100000バイトのSHA-256と一致した。AssetFileDescriptorだけで進めたデータは正しかった。元動画もPCで最後までデコードできた。

修正はストリームの開始位置をゼロのまま返すこと。Rangeの終端+1までをLimitedInputStreamで制限する。`skip` で残り長さを減らす。`available` も残り長さ以下にする。終端を制限せずに返すと、有限Rangeのfetchでファイル末尾まで返される。先頭だけを読む検査では二重の開始位置を検出できない。

一次ソース: [Chromium AndroidStreamReaderURLLoader](https://chromium.googlesource.com/chromium/src/+/4c509b812ed32980debbc54195ad5bb07f9ff670/android_webview/browser/network_service/android_stream_reader_url_loader.cc) のRange解析とSeek呼び出し。

以前の機能試験は消音で0.35秒へ移動して1フレームだけ確認していた。その条件では音声デコードの失敗を検出できなかった。`TabletInstrumentation` に `playback true` と `briefing true` を追加した。前者は4動画の12か所のデータ照合と音声付きの全長再生。後者は実際の12文を2回通す。表示中のフレーム数と音声デコード量を確認する。2回目にはHOMEと復帰と再開も挟む。再生成やAPK導入時は `tablet/README.md` の試験を使う。

実機の画面とログは `output/tablet-standalone-20261003/doctor-stalled-20261005.png` と `media-failure-before.log` に保存。修正後の記録は同じディレクトリの `media-fixed-tests-20261005.log`。
