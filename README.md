# fixed-cam-vr

1 つの Unity プロジェクトに **2 つの独立した VR アプリ**が同居している：

| アプリ | 一言で | コード | シーン | パッケージ ID |
|---|---|---|---|---|
| **[廻リ視（FixedCam）](#廻リ視fixedcam--固定視点ホラー-vr)** | 固定視点カメラのホラー VR（IVRC2026 出展企画） | `Assets/Scripts/`（`FixedCamVr.*`） | `Main.unity` | `com.roiril.mawarimi` |
| **[TableDuo](#tableduo--手だけアバターの対人調査-vr)** | 「手だけアバター」との対人インタラクション調査 VR | `Assets/TableDuo/`（`TableDuoVr.*`） | `TableDuoMain.unity` | `com.roiril.tableduo` |

2 アプリは asmdef 相互参照禁止で完全分離。ビルドは専用メニューで別 APK として出し、Quest 上に並存できる（→ [ビルド & デプロイ](#ビルド--デプロイ)）。共通スタック：**Unity 2022.3.62f2 LTS / URP / Meta XR All-in-One SDK / Quest 3（Android・IL2CPP・ARM64）**。

## 目次

- [廻リ視（FixedCam）](#廻リ視fixedcam--固定視点ホラー-vr) — 概要 / 実装状況 / 動かし方 / 入力
- [TableDuo](#tableduo--手だけアバターの対人調査-vr) — 概要 / 調査運用 / 記録物 / 状態
- [ビルド & デプロイ](#ビルド--デプロイ)（共通）
- [HMD なし・Editor での検証](#hmd-なしeditor-での検証)
- [ドキュメント一覧](#ドキュメント一覧) / [ディレクトリ](#ディレクトリ) / [開発フロー](#開発フロー)

---

# 廻リ視（FixedCam） — 固定視点ホラー VR

1990 年代サバイバルホラーの「固定視点カメラ」演出を、VR と実空間で現実に持ち込む。体験者は HMD を装着し、実空間に設置した複数の固定カメラ（スマホ）の映像だけを見ながら、自分の体で歩き・しゃがみ・探索する。一人称が主流の VR であえて第三者視点を強制することで、視界外への恐怖・被観測感・身体と視点の乖離による不安を生む。映像には CG 合成とフィルタでホラー演出を重ねる。

## 実装状況

| Phase | 内容 | 状態 |
|---|---|---|
| 1–2.5 | MJPEG 受信（[Streaming/](Assets/Scripts/Streaming/)）・スクリーン描画・複数カメラ切替 | ✅ 実機動作確認済み |
| 2.7 | プレイヤー位置連動カメラ切替（[Tracking/](Assets/Scripts/Tracking/)）+ HMD 内ゾーン校正（ZoneCalibrator） | ✅ 実機でゾーン校正運用中 |
| 3 | 映像加工 4 系統プロトタイプ（[Fx/](Assets/Scripts/Fx/)。本命 = CRT + 薄い埃） | ✅ Editor 検証済み・本実装前 |
| 3.5 | 映像差し替え（OverlayCue）+ Web オペレータ卓遠隔制御（ShowControlClient / [tools/web-compositor/](tools/web-compositor/)） | ✅ 実装済み・運用検証中 |
| 4 | スクリーン外 3D 演出 / CG 合成 | 未着手 |

主要コンポーネントの仕様（エンドポイント・遅延対策・show.json 設定契約・スクリーン合成モデル）は [.claude/rules/streaming.md](.claude/rules/streaming.md) に集約。

## 動かし方（最短）

1. **配信側**: 各スマホで MJPEG 配信を起動（下表）。IP を `Assets/Settings/Cameras/Phone01.asset` 等の `host` に反映（または Web オペレータ卓の show.json から遠隔設定）
2. **Unity**: `Main.unity` を開き（**Ctrl+Shift+M**）、必要なら **Tools > FixedCamVr > Setup > Setup Main Demo Scene**（Zones / Tracker / HUD を冪等再配置）→ Play（Quest Link）or 実機ビルド
3. 疎通確認は **Tools > FixedCamVr > Diagnostics > Ping DroidCams**、または `Phone01.asset` Inspector の **Test Connection**

| 配信アプリ | port | videoPath | 備考 |
|---|---|---|---|
| **[fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)**（自作・Android 標準） | 8080 | `/video` | `/info`（自動回転メタ）・`/health`（品質モニタ）・レンズ切替・露出ロック |
| **IP Camera Lite**（iPhone 標準） | 8081 | `/video` | Basic 認証（既定 admin/admin）。CameraSource の username/password に入力 |
| DroidCam / IP Webcam | 4747 / 8080 | `/mjpegfeed?WxH` / `/video` | フォールバック |

配信側の設置のコツ（露出ロック・auto-IDLE・前面維持）は [fixed-cam-streamer README](https://github.com/Roiril/fixed-cam-streamer)、層別の切り分けは [.claude/rules/troubleshooting.md](.claude/rules/troubleshooting.md)。

## 入力早見表

| 入力 | 機能 |
|---|---|
| 右 **A** / **B** | 次 / 前のカメラに切替（ゾーン自動切替を上書き） |
| 左 **X** | スクリーン head-lock 切替 |
| 左 **Y** | HUD 表示トグル |
| **両グリップ 3 秒長押し** | ゾーン校正モード ON/OFF（右レイで A=選択・トリガ=床ドラッグ・スティック=サイズ、左スティック横=全体回転、X=保存 / Y=リセット） |
| キーボード **Tab / Shift+Tab / 1–9** | カメラ切替（Editor・HMD なし検証） |
| キーボード **Space** / **H** | head-lock / HUD トグル |

---

# TableDuo — 手だけアバターの対人調査 VR

テーブルを挟んだ 2 人非対称マルチプレイ VR。片方は**フルアバター（発話可）**、片方は**手だけ（無言・ジェスチャーのみ）**。「手だけの存在と人はどうコミュニケーションするか」を半構造化観察する研究用アプリ（学会発表前提）。

- **構成**: Meta XR ハンドトラッキング + Netcode for GameObjects（LAN 直結・pose 60Hz Unreliable + Seq 後着棄却・自動再接続）。役割（full/hand）と host/client は起動フラグで独立指定。PC からの**観戦ロール**（第三者視点・席なし）あり
- **卓上タスク**: Deep Sea Adventure（ボードゲーム）一式。駒 ×2・サイコロが掴める（サーバ権威・ピンチグラブ）
- **手の見た目 3 バリアント = 調査条件**（within-pair・ブロック固定・`tdv_hand default|realistic|robot`。セッション中の切替は封印、端末間の不一致は検出して CSV に記録）
- **シーン生成**: `Tools/FixedCamVr/Setup/Setup TableDuo Scene`（冪等。**ビルド直前に再実行してクリーン状態にする**）

## 調査の記録物（すべて自動）

| 記録 | 内容 | 場所 |
|---|---|---|
| SessionLogger CSV | 両者 pose 30Hz + 手役 7 ランドマーク + イベント（grab / recenter / 条件 / layout 受信 / clockOffset / 欠落系） | host の `persistentDataPath` |
| SessionReplayRecorder | 全 bone + 小物 + イベントの一括リプレイ（Editor の ReplayViewer で自由視点再生 = stimulated recall） | 同上 |
| StreamingPoseRecorder | 各端末ローカルの **lossless 手 pose 60Hz**（ネット遅延・量子化なしの完全忠実度バックアップ） | 各端末の `persistentDataPath` |
| FacilitatorMarkServer | `curl http://<host>:7780/mark?label=phase2` でフェーズマーク | CSV へ |

## 実機起動（2 台・同 LAN）

```
adb -s <hostSerial>   shell am start -n com.roiril.tableduo/com.unity3d.player.UnityPlayerActivity -e tdv_mode host   -e tdv_role full -e tdv_hand default
adb -s <clientSerial> shell am start -n com.roiril.tableduo/com.unity3d.player.UnityPlayerActivity -e tdv_mode client -e tdv_ip <hostIP> -e tdv_role hand -e tdv_hand default
```

役割交代・条件ブロック切替は [tools/tableduo-role-swap.ps1](tools/tableduo-role-swap.ps1)（`-HandVariant robot` / `-KeepRoles` / `-DryRun`）。

## 状態（2026-07-02）

実機 2 台で接続〜手メッシュ描画〜掴みまで動作確認済み。手バリアント条件化・lossless 記録・通信堅牢化・三視点整合性改善まで実装済み（EditMode 37/37・直近改善分は実機最終確認待ち）。**パイロット 1 ペア実施 → プロトコル凍結**が次のマイルストーン。

- 設計 [docs/table-duo/study-design.md](docs/table-duo/study-design.md) / 実施手順 [docs/table-duo/study-protocol.md](docs/table-duo/study-protocol.md) / 同意書 [docs/table-duo/consent-template.md](docs/table-duo/consent-template.md)
- 最新の実装状態・既知の罠 → [.claude/memory/table_duo_study_status.md](.claude/memory/table_duo_study_status.md)
- 実機ゼロ検証（L0: Standalone を CLI で host/client/観戦 3 プロセス起動） → [.claude/memory/table_duo_l0_desktop_test.md](.claude/memory/table_duo_l0_desktop_test.md)

---

# ビルド & デプロイ

**手動 Build Settings は使わない**（2 アプリが同名・同 ID になり Quest 上で共存できなくなる）。専用メニュー [BuildVariants.cs](Assets/Editor/BuildVariants.cs) が唯一の正：

| メニュー | 出力 | パッケージ ID | シーン |
|---|---|---|---|
| `Tools/FixedCamVr/Build FixedCam APK（廻リ視）` | `Builds/mawarimi.apk` | `com.roiril.mawarimi` | Main.unity |
| `Tools/FixedCamVr/Build TableDuo APK` | `Builds/tableduo.apk` | `com.roiril.tableduo` | TableDuoMain.unity |
| （各 `…（Release）` 版あり） | `*-release.apk` | 同上 | Development なし |

```
adb -s <serial> install -r --no-streaming Builds\tableduo.apk
```

**2 ビルドの同時実行は厳禁**（productName/ID を一時 swap するため。→ [.claude/rules/parallel-projects.md](.claude/rules/parallel-projects.md)）。手順詳細・APK 完成ポーリングは [quest-build スキル](.claude/skills/quest-build/SKILL.md)。

# HMD なし・Editor での検証

| 経路 | 対象 | 手順 |
|---|---|---|
| EditMode テスト | 両アプリのロジック回帰 | Test Runner → Run All（または `Tools > FixedCamVr > Diagnostics > Run All Tests`） |
| Flat デバッグシーン | 廻リ視のストリーミング系 | **Ctrl+Shift+D** → Play → Tab/数字で切替（OVR 無しの通常シーン） |
| streaming-offline-test | スマホ実機なしで MJPEG E2E | fake server を立てて検証（[スキル](.claude/skills/streaming-offline-test/SKILL.md)） |
| TableDuo L0 | 実機ゼロで host/client/観戦 | Standalone ビルドを CLI 起動（`tdv_l0=on`・[memory](.claude/memory/table_duo_l0_desktop_test.md)） |
| Quest Link | 実機に近い Editor Play | Build Target = Standalone のまま、XR Plug-in（Windows）で Oculus を有効化 → Link 接続 → Play。**72Hz 上限**なので性能評価は実機 APK で |

⚠ **Link/HMD 無しの Editor で OVR シーンを Play するとハングする**（TableDuo 検証は L0 経由が正。[.claude/rules/mcp-unity.md](.claude/rules/mcp-unity.md)）。

# ドキュメント一覧

| ファイル | 内容 |
|---|---|
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | 配信不通 / HMD 真っ黒 / FPS 低下 / 実機検証で得た知見 |
| [docs/onsite-checklist.md](docs/onsite-checklist.md) | 現場での 60 秒チェック → 切り分けフロー |
| [docs/table-duo/](docs/table-duo/) | TableDuo の要件・調査設計・実施プロトコル・同意書・手バリアント設計 |
| [docs/ivrc-video/](docs/ivrc-video/) | IVRC2026 ビデオ審査の制作プラン一式 |
| [tools/web-compositor/README.md](tools/web-compositor/README.md) | Web オペレータ卓 + 映像合成検証ツール |
| [Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)（別リポ） | 配信側 Android アプリ |
| [CONTRIBUTING.md](CONTRIBUTING.md) | コミット規約・ブランチ運用 |
| [.claude/rules/](.claude/rules/) | 領域別の作業規約（streaming / meta-xr / unity-vr / parallel-projects / troubleshooting 等） |
| [.claude/plans/](.claude/plans/) | 実装計画書（完了済みは git log 参照） |

# ディレクトリ

```
fixed-cam-vr/
├── Assets/
│   ├── Scripts/              # 廻リ視本体（Streaming / Tracking / Fx / Diagnostics / OvrBridge）
│   ├── TableDuo/             # TableDuo（Scripts/Hands・Net・Editor / Scenes / Resources）— 相互参照禁止
│   ├── Scenes/               # Main.unity / Debug/ / Sandbox/ / FxSandbox.unity
│   ├── Art/                  # マテリアル + 映像加工シェーダ/Compute
│   ├── Prefabs/              # MjpegScreenStage / StreamingLogic 等
│   ├── Settings/             # URP / Quality / CameraSource SO（Cameras/Phone01–03）
│   ├── Oculus/ Resources/ XR/ # Meta XR / OVR / XR 設定（2 アプリ共有 — 片方の都合で触らない）
│   └── Tests/                # EditMode テスト（Streaming / Tracking / TableDuo）
├── docs/                     # onsite-checklist / table-duo/ / ivrc-video/
├── tools/                    # web-compositor / tableduo-role-swap.ps1 等
├── Builds/                   # APK 出力（gitignore）
├── Packages/ ProjectSettings/ # 依存・エディタ設定（変更は要注意 — CLAUDE.md 禁止事項）
├── .claude/                  # Claude Code ハーネス（rules / plans / memory / skills）
├── CLAUDE.md
└── README.md
```

# 開発フロー

- シュビー（Claude Code）と共同開発。動作モード・2 アプリ干渉防止・領域別ルールは [CLAUDE.md](CLAUDE.md) が入口
- 実装計画は `.claude/plans/YYYY-MM-DD_<slug>.md`、コミットは日本語 `<type>：<要約>` 形式
- 検証方針: Editor（EditMode テスト / L0 / Link）で潰せるものは実機に持ち込まない。実機ビルドは最後
