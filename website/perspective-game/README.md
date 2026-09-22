# 視点ゲームの静的同梱

元の視点ゲーム `C:/Users/kouga/Documents/ChatGPT/視点ゲーム` の revision `d3f1b3e` のビルドを同梱しています。元の実装は変更していません。

- `index.html` と `favicon.svg`: `dist/` からコピー。HTML のアセット参照のみ相対パスに変更し、`embed.js` と `embed.css` を追加。
- `assets/index-BZnkwpUb.js`、`assets/three-BfIlZwn7.js`、`assets/index-DdqsXLdv.css`: 同 revision の `dist/assets/` から現用の3ファイルのみコピー。
- `assets/three-LICENSE.txt`: `node_modules/three/LICENSE` からコピーした three.js の MIT ライセンス。

通常の URL では元の操作画面を表示します。`?embedded=1` で開くと操作画面を隠し、描画用の canvas を全画面に広げます。親ページとの通信は同一オリジンの `postMessage` に限定します。メッセージの `type` は `mawarimi-handheld` です。

親からの操作は `{ action: 'mode', mode: 'fps' | 'tps' | 'fixed' }`、`{ action: 'key', code: 'KeyW' | 'KeyA' | 'KeyS' | 'KeyD' | 'ArrowUp' | 'ArrowDown' | 'ArrowLeft' | 'ArrowRight' | 'ShiftLeft', pressed: boolean }`、`{ action: 'release' }`、`{ action: 'reset' }` です。いずれも `type` が必要です。ゲームからは初回描画後に `{ event: 'ready' }`、視点の状態に `{ event: 'state', mode }`、エラー表示時に `{ event: 'error' }`、Escape キーで `{ event: 'escape' }` を送ります。各イベントにも同じ `type` が付きます。
