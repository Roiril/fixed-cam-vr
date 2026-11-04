# デジタルツインの見る面

固定カメラの動画（Cycles で焼いたもの）を、Unity の仮想体験の記録どおりにカットして流す。

```powershell
py -3.11 tools/twin-game/serve.py 8133   # → http://localhost:8133/
```

| キー | 動き |
|---|---|
| Space | 再生・停止 |
| ← → | コマ送り |
| 1 2 3 / 0 | カメラ A B C / 自動 |
| L / M / H / D | 線 / 地図 / HMD の画 / 検証 |

## HMD なしの動作検証の面（verify.html）

`http://localhost:8133/verify.html`。`tools/twin-verify.py` の走行を選び、目線の動画と判定と出来事を並べて見る。
出来事を押すとその時刻へ飛ぶ。時間軸の赤は報告。走行は Drive の `renders/twin-verify/` をサーバが `/runs/` で配る。

`index.html` の「HMD の画」は仮想体験の記録（時間軸が `routes/` と同じ）で、verify.html の走行とは別物。

## シュビーが検証するとき

動画が無い経路でも、線の表示（壁・区画・経路・体の円）だけで確かめられる。経路を変えたら再レンダなしで見える。

- URL: `?route=forward&t=12&cam=B&overlay=1&panel=1&skiptitle=1`
- `window.twin.seek(t)` `.state()` `.setCam("B"|"auto")` `.setRoute(name)` `.setOverlay(bool)`
- `state()` は位置・速さ・担当カメラ・暗転・全身の余白・表示中の動画の時刻を返す

カットは `active` と `dim`（`pick` ではない）。`data/` は `tools/twin/export_game.py` と `check_rig.py` が書く。
動画は Drive の `renders/twin-cycles/game/<route>/`、HMD の画は `renders/virtualwalk/` の `walk.mp4` をサーバが配る。
