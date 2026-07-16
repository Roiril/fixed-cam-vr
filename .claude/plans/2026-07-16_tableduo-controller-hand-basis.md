# TableDuo: コントローラ保持中の手アバター基底修正

**状態**: 実装済み（2026-07-16・コンパイル OK / EditMode 37/37・**実機未検証**）
**症状**: コントローラを握ると手アバターが実際の手の向きと無関係に「コントローラ先端から垂直方向」に生える。

## 実装（Plan A の簡略版を採用）

`HandPoseSampler.TrySampleWristFromHandNode` を追加し、`SampleHand` から呼ぶ。

- **anchor Transform ではなく `OVRInput` から hand node を直接引く**方式に変更。
  SDK の `HandOnControllerAnchor` は `GetLocalControllerPosition/Rotation(LHand/RHand)` を
  格納しているだけ（OVRCameraRig.cs:425-431）なので、**同じ値を直接引けば anchor 配線が不要**
  → SerializeField 追加ゼロ ＝ prefab YAML 反映・シーン再生成が不要（unity-prefab-fields の罠を回避）。
  `updateHandAnchors` 設定にも依存しない。
- **保持中のみ切替**（`GetControllerIsInHandState == ControllerInHand`）。非保持は従来の
  `hand.transform` 経路のまま＝**動作実績のある主経路に回帰リスクゼロ**
  （非保持時の HandAnchor は同じ hand node で駆動されるので値としても同値）。
- `GetControllerPositionValid/OrientationValid` ガード付き。node が無効なら従来経路へフォールバック。
- 診断: `[TDV-DIAG]` に `inHand=` / `nodeValid=` を追加（実機で切替成立を確認できる）。

**遷移スムージングは入れていない**（受信側 `RemoteHandView` に既存の Slerp があり、
まず素の挙動を実機で見てから判断する。pop が目立つなら追加）。

## 真因（2026-07-16 SDK ソース調査で確定）

`HandPoseSampler.SampleHand` は手首 pose を `hand.transform` から採るが、これは
OVRHandPrefab の `_updateRootPose=0` により**親 HandAnchor の姿勢そのもの**。
HandAnchor は OVRCameraRig が毎フレーム
`GetLocalControllerRotation(GetActiveControllerForHand(手))` で駆動しており
（OVRCameraRig.cs:368-409）、**コントローラを握って Touch が active になると
回転基底が「手トラッキング手根基底（Node.HandLeft/Right）」から
「コントローラのグリップ基底（Node.ControllerLeft/Right）」へ丸ごと切り替わる**
（OVRInput.cs:1002-1026, 1204-1252）。

受信側 `RemoteHandView` は wristRot を手根基底として `_root.localRotation` に据え、
指骨をその上に乗せる契約（RemoteHandView.cs:159-211）なので、基底が切り替わると
手全体がコントローラのグリップ姿勢へ剛体回転する。縦持ちした Touch の forward は
手の甲法線とほぼ直交する → 「垂直に生える」見え。

## 設計

### Plan A（本命）: SDK の HandOnControllerAnchor を使う

OVRCameraRig（SDK 201）は multimodal 用に
**`left/rightHandOnControllerAnchor`** を提供しており、コントローラ保持中は
ここに `Node.LHand/RHand`（＝手トラッキング基底の実際の手 pose）が
HandAnchor 相対で格納される（OVRCameraRig.cs:425-431）。

- **保持検出**: `OVRInput.GetControllerIsInHandState(OVRInput.Hand.HandLeft/HandRight)
  == ControllerInHand`（左右独立・OVRInput.cs:968-997）
- **切替**: `SampleHand` で保持中は wrist を `handOnControllerAnchor`（world）から
  ローカル化して採取。非保持は従来どおり `hand.transform`
- **指骨**: 変更なし（controller-driven Natural の合成値が `skeleton.Bones` に
  更新され続ける = OVRSkeleton.cs:848-943。握った手の形がそのまま出る）
- ユーザー案の「オフセット凍結」より単純で、保持中も実手の向きを追従し続ける
  （SDK が手 node を出し続ける限りオフセット固定より正確）

利点: 凍結タイミング問題（握る瞬間は手が遮蔽されて低 confidence）が発生しない。
送信より上流の 1 箇所の修正なので、ネット同期・リモート描画・手バリアント・
WireTap 録画すべて無改修で恩恵。

### Plan B（フォールバック）: 握った瞬間のオフセット凍結（ユーザー原案）

実機で HandOnControllerAnchor が identity のまま／ジッタ等で使い物にならない場合:

1. 遷移検出（NotInHand→InHand）の瞬間、直近 0.5s 以内の高信頼手首 pose を
   コントローラ node 相対で凍結: `C = inv(controllerPose) * lastGoodWrist`
2. 高信頼 pose が古すぎる場合は**事前校正した標準グリップオフセット**
   （定数。実機で一度測って埋め込む）にフォールバック
3. 保持中: `wrist = controllerPose * C`
4. 解除（InHand→NotInHand）: 手トラッキング復帰へ 0.2s 程度 Slerp ブレンド

### 共通の設計点

- **実装層**: `HandPoseSampler.SampleHand` のみ（送信上流）。
  受信側・バリアント・Remy は無改修
- **配線**: サンプラに `leftHandOnControllerAnchor` / `rightHandOnControllerAnchor`
  の参照を追加（`TableDuoSceneSetup` が OVRCameraRig から自動配線。
  unity-prefab-fields の作法に従い SerializeField 追加時はシーン再生成）
- **遷移スムージング**: 基底切替フレームでの pop を防ぐため、切替後 0.1-0.2s は
  前 pose から Slerp（受信側に既存の smooth があるので軽くてよい）
- **ゲートとの関係**: `SampleHand` の `IsTracked && IsDataHighConfidence` ゲートは
  維持（保持中もネイティブが high を返すことが症状の前提から逆算済み）。
  保持中に low になったら従来どおり「ロスト＝フリーズ」に落ちる
- **L0 / FakeHandDriver**: 経路が別（IHandPoseSource 差し替え）なので影響なし
- **NoHand ガード**: `GetControllerIsInHandState` は LHand/RHand 未接続だと
  NoHand を返す → その場合は従来経路

## 実機での事前確認（実装前に 5 分で取る）

`HandPoseSampler.logDiagnostics=true` + 拡張ログで保持中の値を 1Hz 確認:
1. `GetControllerIsInHandState` が InHand になるか（切替検出の成立）
2. `handOnControllerAnchor.localRotation` が identity でない実値を持つか（Plan A 成立）
3. 保持中 `IsTracked && IsDataHighConfidence` が維持されるか（ゲート通過の確認）

→ 2 が×なら Plan B に切替。1 が×なら検出 API を `GetActiveControllerForHand` に変更。

## 検証

- Editor+Link（または実機）でコントローラを持つ/置くを繰り返し、
  手アバターが実手の向きに追従・切替時に pop しないこと
- 手バリアント（Realistic/Robot）でも同様（土台 `_root` の修正なので自動で直る）
- リモート側（相手 HMD / PC ホスト観戦）でも同じ見えになること（送信上流の修正なので理論上一致）
