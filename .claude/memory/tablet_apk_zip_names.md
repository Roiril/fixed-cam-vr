---
description: Windows aapt2のAPK資産名にbackslashが残りAndroidでFileNotFoundになる。PythonのZipInfo.filenameはWindowsで正規化されるためraw検査が必要。
---

# タブレットAPKの資産名

2026-10-03。Windowsの `aapt2 link -A` が `assets/web\index.html` をZIPの中央記録と各ファイルのヘッダーへ書いた。全27資産で再現した。Androidは `assets/web/index.html` を完全一致で探すためAssetManagerがFileNotFoundになった。

生成APKと実機base.apkのSHA-256は一致していた。署名とzipalignも成功した。WindowsのPython `ZipInfo.filename` はbackslashをslashへ正規化するため、資産の存在と内容ハッシュの検査だけでは見逃した。`orig_filename` とヘッダーの生バイトで確認する。

`tablet/build.py` はAPKを署名する前に全ZIP名をPOSIX形式で書き直す。MP3とMP4の無圧縮格納を保持する。中央記録と各ファイルのヘッダーの名前が一致し、backslashがないことをビルドの検査へ含める。Android側にZIP直接読み込みを追加して不正なAPKを隠さない。
