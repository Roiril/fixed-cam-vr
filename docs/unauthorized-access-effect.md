# 不正アクセスのエラー演出

2026-09-19。用途を決める前に用意した廻リ視用の表示部品。
具体的な登場場面と演出の採否は未判定。

## 表示

スクリーンへ巨大な `WARNING` と「不正アクセス検出」を重ねる。
文字と警告記号が食い込んだ生成画像を使う。閉じたパネル枠は置かない。
警告は急に現れる。欠落を挟んで読める状態へ戻る。2.05 秒と 3.65 秒から大きく横へ裂ける。
4.9 秒から部分ごとに消え始める。最後に空間の信号が残る。

空間には 220 個の 0 と 1 を置く。距離と大きさと濃さをばらつかせる。
遠くには細かい数字がある。手前には大きくぼけた数字がある。
生成した赤い干渉光を断片として重ねる。文字列を左右対称に並べない。

7 秒で出現から消退まで再生する。時間は非スケール時間を使う。
スクリーンの表示は指定した面に追従する。
空間の表示は開始時のスクリーン位置を基準に固定する。

本編の `Main.unity` と `show.json` には接続していない。
登場時刻は後から決める。音と操作制限はこの部品に含まない。

## 呼び出し

`Assets/Prefabs/Effects/UnauthorizedAccess.prefab` を配置する。
表示前は非表示。`UnauthorizedAccessEffect.Play` で再生する。

```csharp
// screenAnchor: 表示面の中心。+X が右。+Y が上。見る側は -Z。
// screenSize: ワールド単位の幅と高さ。回転と位置を持つ単位スケールの Transform を渡す。
effect.Play(screenAnchor, new Vector2(2.7f, 1.51875f));
effect.Stop(); // 途中で直ちに消す
```

既存の映像用マテリアルを変更しない。独立した透明の面を前に重ねる。
カーブしたスクリーンでは平面として重なる。設置時に寸法と距離を調整する。
頭を動かしても空間の数字列は追従しない。スクリーン前方の空間を確保して使う。

## 作り直しとプレビュー

```powershell
.\tools\unity.ps1 menu raw:FixedCamVr.Streaming.EditorTools.UnauthorizedAccessPreview.Build
.\tools\unity.ps1 menu raw:FixedCamVr.Streaming.EditorTools.UnauthorizedAccessPreview.Run -Set sequence=1
```

`Run` は実際の Prefab を Unity で描画する。
`Logs/unauthorized-access/<日時>/` に静止画と 30 fps の連番を出す。
`-Set out=Logs/unauthorized-access/custom` で出力先を指定できる。
連番は `frames/0000.png` から始まる。

通常の背景は暗色。`overlay-check.png` だけ透過を確かめる格子を出す。
カメラ映像や会場を再現した背景ではない。
正面に加えて横へ 22 cm 移動した視点と明るい背景も描画する。
`Sample(seconds)` は実行時と同じ描画処理を任意時刻で適用する。

```powershell
py -3.11 tools/check-error-preview.py Logs/unauthorized-access/<日時>
```

中央と左右の表示画素を別々に数える。時刻 0 と終了後と停止後を比較する。
警告画像は 11 個の帯に分割する。描画順は 3450。映像面から 6 cm 手前に置く。
数字は専用シェーダで描く。フレームごとのフォント生成は行わない。
文字の頂点が存在するだけでは表示を保証しない。実際の PNG も確認する。
Editor では自動再生せず `Sample` でのみ時刻を進める。

## 素材

- `Assets/Art/Textures/UnauthorizedAccess/warning-v2.png`: imagegen で生成した透明な警告文字と記号。1774 × 887 px。
- `signal-fragments-v2.png`: imagegen で生成した非対称の赤い干渉光。1774 × 887 px。
- `ErrorFx.mat`: 専用シェーダを参照する材質。ビルド時にシェーダの参照を保持する。

参考はユーザー指定の `error1.jpg` と `error2.gif`。
参考画像自体は配布資産へコピーしない。
第 2 版では参考の太い文字と不均一な密度に立ち戻った。
警告画像の日本語は生成結果と Unity の描画の両方で確認する。

## 検証の範囲

Unity の実描画で文字と透過と時間変化を確認する。
ライフサイクルは EditMode テストで確認する。
Quest 実機での両眼視差と表示負荷と快適性は未確認。
用途と配置が決まった時点で HMD を装着して確認する。

2026-09-19 の第 2 版は EditMode 6 件が成功。
1280 × 720 px / 30 fps の連番 211 枚を書き出した。異なる画像は 208 枚。
開始前と終了後と Stop 後の表示差分は 0 画素。
正面と横へ 22 cm 移動した描画も比較した。
