# GitHub Actions ワークフロー

このディレクトリは fixed-cam-vr の CI 定義を置く場所。

## ワークフロー一覧

### `unity-test.yml` — Unity EditMode テスト

- **目的**: `Assets/Tests/` 配下の EditMode テスト（`WrapIndex` / `BuildUrl` 系）を push (master) と pull_request で自動実行する
- **ランナー**: `ubuntu-latest`
- **Unity バージョン**: 2022.3.62f2
- **使用アクション**: [`game-ci/unity-test-runner@v4`](https://github.com/game-ci/unity-test-runner)
- **キャッシュ**: `Library/` を `actions/cache@v4` でキャッシュし、2 回目以降のインポートを短縮
- **成果物**: `artifacts/` を `editmode-test-results` としてアップロード

## 必要シークレット

`Settings > Secrets and variables > Actions > New repository secret` から登録する。
**シークレットが 1 つでも未設定だとジョブはスキップされる**（`if: ${{ secrets.UNITY_LICENSE != '' }}`）。

| シークレット名 | 用途 |
|---|---|
| `UNITY_LICENSE` | Unity ライセンス XML（`.ulf` 全文） |
| `UNITY_EMAIL` | Unity アカウントのメールアドレス |
| `UNITY_PASSWORD` | Unity アカウントのパスワード |

### `UNITY_LICENSE` の取得手順（Personal license）

1. ローカル環境で GameCI の [activation guide](https://game.ci/docs/github/activation) に従い、`Unity_v2022.x.alf` を生成する
   - 例: `unity-request-activation-file` アクションを単発で走らせる、または公式の Unity Hub から手動でリクエストファイルを書き出す
2. <https://license.unity3d.com/manual> に `.alf` をアップロードし、`.ulf` をダウンロード
3. `.ulf` の中身全文を `UNITY_LICENSE` シークレットに貼り付け

Pro / Plus ライセンスの場合は GameCI の [Activation - Personal vs Professional](https://game.ci/docs/github/activation) を参照。

## ローカルでテストを走らせる代替手段

CI を待たずに手元で実行したい場合は Unity Editor の **Test Runner GUI** を使う。

1. Unity Editor を開く
2. `Window > General > Test Runner`
3. 上部タブで `EditMode` を選択 → `Run All`

CLI からの実行は以下（Library/ がキャッシュ済みの前提）:

```bash
"C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe" \
  -batchmode -nographics -quit \
  -projectPath . \
  -runTests -testPlatform editmode \
  -testResults TestResults-editmode.xml \
  -logFile -
```

実機 Quest 3 動作確認は CI の対象外。シュビーは「コンパイル OK / 実機未検証」を完了報告に明記する運用（`CLAUDE.md` 参照）。
