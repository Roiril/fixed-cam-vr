# エンドロール冒頭

2026-09-25。ユーザーの「最後の画面を、エンドロール的な感じに変更してほしい」に対応。

- 最後の呪いを報告した結末は「帰還End」。報告しない結末は「人形End」。既存の Released / Trapped 判定を使う。スタッフ停止は「中断」。
- `OutroReport` は黒地の左側に廻リ視ロゴとEnd名と報告数を置く。右側に16:9の写真を置く。スタッフロールはまだ追加しない。
- `EndingFrameCapture` は合成済みスクリーンを体験中にGPU上へ保存する。帰還用は人形が消えた後。人形用は最後の報告待ちで左の大量人形と右の体験者位置CGが表示された画面。左右を別に作り直さない。
- 画像はラン内だけ保持する。`RunRestarted` で両方を無効化する。撮れなかった場合に前の体験者の写真や終了後の映像を代用しない。
- 旧結果報告の説明文と分母とタイプ打ちを廃止。既存の終了音楽は継続。`repStyle=roll` のログでは打鍵音を要求しない。
- `tools/unity.ps1 menu raw:FixedCamVr.Streaming.EditorTools.OutroReportPreview.Run` で3言語と3結末を描画する。`Assets/Screenshots/ending/` に画面と文字の画素検査を出す。
- プレビューの写真は部屋素材とUnityのCGによるデザイン確認用。実際の体験者の記録ではない。素材と配置条件は `Logs/ending-roll-preview/sample-source.txt` に出す。

実機での体験者位置とカメラ映像の対応は実機走行で確認する。Editorのデザインプレビューを実機記録として扱わない。
