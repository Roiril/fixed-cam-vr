# 2026-10-18 実機検証の作業記録

証拠の保存先: `G:/マイドライブ/shubie/reports/fixed-cam-verification-20261018/`。
基準コミットは `809d9e03`。開始時から存在したシーン・設定・フォント等の差分は `git-before.txt` と `source-manifest-before.json` に記録。ビルド用フォントの元は `pre-regeneration-backup/` に保存。

- Unity EditMode 2185/2185 成功。修正後も2185/2185成功。Web回帰82件、効果音7件、砂嵐6件成功。
- Redmi Pad SE `122d7871`: staffFinish152、visitorFocus111、equipment9、presentation32、requireQuest27が成功。計331件。実Quest αへのGET/POSTも含む。代役状態による画面試験と実通信は別の試験。
- タブレット本番APKの端末上SHA-256は `5d07fbf0558b014696d63898a49e1c4f3bac6401db23d4f290028506ed53b407`。試験APKは別パッケージ。
- 周辺視野のEditorプローブは6方向、`peripheral-proof.json`のok=true。Sui復帰255コマで電力0.30→1、圧縮0.06→1、裂け0.55→0、残像0.83→0、赤文字最大5→0。
- スタッフ画面の実パネルを18条件で描画。準備完了の指示は2行。CSVのmissingPixels/outsidePanel/outsideColumnは確認対象で0。
- 初回のQuest α走行 `quest-a-full/`: 3周から帰りの区間まで走り、t=142.6でFinished、t=146.2で終幕Done。326件のXPイベントと60秒の録画3本を保存。端末時計はPCより約162秒進むためmetaで補正。
- 初回APK SHA-256 `b698cd39defa0578932e5181d9703d8ddfb5cc8d31230ac4ed3f3ebd84012498`。端末上の一致を確認。

検証中に直した点:
- 自動走行はスタッフ準備だけを題字待機前に解除し、開始許可は題字終了後に開く。通常の来場者フローと登録ファイルは変更しない。
- 参照のないURP UnlitがPlayerで剥がれ、Suiの縦罫線が欠けていた。色面用シェーダをResourcesへ置いて明示ロードする。
- 解析器は同梱時のcue絞り込みと現行Begin通知と復帰電力0.30に合わせた。自己紹介・未交差の録画線・非同期に確定する文字数・端末からの音声は、観測不足を不合格と取り違えず未検証と表示する。

限界:
- 自動走行はRomiへdry=1で送る。台車の実走行、給電・積載条件は検証しない。
- Quest βはUSBに不在、既知の8090番にも応答なし。Romi ESP32もUSB不在、既知IPのinfo/endingに応答なし。Uno COM18は別装置なので操作しない。
- HMD装着時の両眼・奥行き・頭の動き、聞こえ方、実際の来場者が返却前に装着しないかは未検証。
- presentation試験の最終PNGは画面遷移中に撮られていたため、装着案内の証拠にはvisitorFocusの実機PNGを使う。
- ADBの再起動時にinstrumentationが空出力・exit0となることがある。exit0だけで合格にせず、INSTRUMENTATION_RESULTまたはDoctorTabletTestのPASS件数と実画像を照合する。Unity同梱ADBの絶対パスに統一する。

修正後の結果:
- Quest APK SHA-256 `23718ef5a881a8b50301d8c5935406feeb3308ba16578614d293aa0399dc3ec8`。端末上のハッシュ一致。`quest-a-final/`へ333件のXPイベントと60秒の録画3本を保存（回収時に数秒の隙間あり）。
- 題字In→Hold→Out→Doneを実機で確認。題字と導入の黒のステータスは計2回・4秒。SuiはcommsBg=1。t=135.83でFinished、t=139.46で終幕Done。砂嵐0回。解析FAIL=0/WARN=16/OK=84。
- 警告は未横断の録画開始線、自己紹介・コントローラ練習の省略、非同期文字数の計測、Romi未接続、実音声未計測等。最悪フレーム643msと劣化進行の段差も残る。性能・装着時の見え方が合格したとは扱わない。
- 最終走行はAC接続で残量3→2%、温度46→47℃。走行後にスリープ。通常の充電・温度条件での性能再測定が必要。
- タブレットから実Questへ設定送信→説明→スタッフ装着確定→HMD開始を一人の操作として通す最終運用は未検証。代役による331件のうちの画面検証とGET/POSTの実通信をもって代用しない。
- 解析器の成功・失敗両側を含む31件、takeover関連7件が成功。試験用タブレットパッケージを削除し、SHA一致の本番APKを通常起動へ戻した。
- ビルド時に更新された既存の生成フォントとローカル設定類は、開始時からの差分を含むためコミットに含めない。元フォントは上記backupで復元できる。
