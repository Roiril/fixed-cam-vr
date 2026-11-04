# レンズの研究（LEDGER 0307）

参照画像のレンズを直線から解き、カメラ C の無人画像へ当てた版を並べる。本編は変更しない。
結果の読み方は [memory/cam_c_lens_study.md](../../.claude/memory/cam_c_lens_study.md)。

```powershell
$out = "G:\マイドライブ\shubie\reports\2026-11-05_cam-c-lens-study"
py -3.11 tools/lens-study/extract_edges.py --image <参照.jpg> --out $out        # 線を取り出す
py -3.11 tools/lens-study/fit_ref_lens.py --selftest                            # 計器の確認（魚眼と普通のレンズの両方）
py -3.11 tools/lens-study/fit_ref_lens.py --image <参照.jpg> --out $out          # レンズを解く
& "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --factory-startup --python tools/lens-study/twin_c_panorama.py -- --out Logs/twin-cycles/lens-study
py -3.11 tools/lens-study/lens_study.py --plate <plate_C.jpg> --reference <参照.jpg> --fit "$out\lens-fit.json" --twin Logs/twin-cycles/lens-study --out $out
```

| ファイル | 役割 |
|---|---|
| `extract_edges.py` | 参照から長い線の連なりを取り出す。番号入りの確認画像も書く |
| `fit_ref_lens.py` | 線が直線へ戻る式と、部屋の直交条件で焦点距離を解く |
| `twin_c_panorama.py` | ツインの C を普通のレンズと全周で撮る（Blender 内で動く） |
| `lens_study.py` | 無人画像へ各版の変形を掛け、比較シートと `summary.json` を書く |

版の違いは本編のシェーダを通していない。Python の変形だけの画である。
