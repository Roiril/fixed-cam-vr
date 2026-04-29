# fixed-cam-vr

Meta Quest 3 で固定視点カメラ（バイオハザード風）の無線映像を VR 内に表示する実験プロジェクト。

## 必要環境

- Unity **2022.3.62f2** LTS
- Meta Quest 3（開発者モード有効）
- Android Build Support（Unity Hub からインストール）
- 配信用スマホ（DroidCam または IP Webcam）×N
- 全デバイスが同一 LAN（推奨：5GHz Wi-Fi）

## 初回セットアップ（手動作業）

### 1. SDK 導入

Unity Editor でプロジェクトを開いた状態で：

1. **Window → Package Manager**
2. 左上 **+ → Add package by name** → 以下を 1 つずつ追加
   - `com.unity.xr.openxr`
   - `com.unity.xr.interaction.toolkit`
3. Asset Store で **「Meta XR All-in-One SDK」** を My Assets に追加
   - https://assetstore.unity.com/packages/tools/integration/meta-xr-all-in-one-sdk-269657
4. Package Manager → **My Assets** タブ → Meta XR All-in-One SDK を Import
5. ダイアログが出たら指示通り **Apply / Fix All**

### 2. プラットフォーム切替

1. **File → Build Settings → Android → Switch Platform**
2. **Texture Compression**: ASTC
3. **Edit → Project Settings → XR Plug-in Management → Android タブ**
   - **Oculus** にチェック
4. **Player Settings → Other Settings**
   - **Color Space**: Linear
   - **Auto Graphics API**: OFF → Vulkan を最上位に
   - **Scripting Backend**: IL2CPP
   - **Target Architectures**: ARM64 のみ
   - **Minimum API Level**: Android 10 (API 29) 以上

### 3. Meta XR Project Setup Tool

エディタ右下に表示される **Project Setup Tool** で **Fix All / Apply All** を実行。

### 4. 動作確認用シーン

- 新規シーンに `OVRCameraRig` プレハブを配置
- Build & Run で Quest 3 にデプロイ → ヘッドトラッキングが効けば OK

## ビルド & デプロイ

```
File → Build Settings → Build And Run
```

Quest 3 を USB-C 接続、開発者モードで USB デバッグ許可済みであること。

## 開発フロー

1. シナリオ・実装計画は `.agent/plans/` に置く
2. シュビー（Claude Code）と共同開発。動作モード等は [CLAUDE.md](CLAUDE.md) 参照
3. コミット規約：日本語 `<type>：<要約>` 形式（グローバル `~/.claude/CLAUDE.md` 参照）

## ディレクトリ

```
fixed-cam-vr/
├── Assets/              # Unity アセット
├── Packages/            # Package Manager
├── ProjectSettings/     # エディタ設定
├── .agent/rules/        # 領域別ルール（Claude Code 参照）
├── .claude/memory/      # 自動メモリ
├── CLAUDE.md            # シュビー（Claude Code）への指示
└── README.md
```
