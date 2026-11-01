from pathlib import Path
import hashlib,json,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion');OUT=BASE/'pass-04';OUT.mkdir(exist_ok=True)
for name in ['package.py','capture.mjs','compare.py','fields.mjs','measure.py']:
    p=H/name;s=p.read_text(encoding='utf-8');s=s.replace('revision-02/motion/pass-03','revision-02/motion/pass-04')
    if name=='package.py':s=s.replace("'pass':'03'","'pass':'04'").replace('spatial renderer combined;','spatial renderer superposed;')
    if name=='compare.py':s=s.replace('revision 02 | frame','revision 02 pass04 | frame')
    p.write_text(s,encoding='utf-8')
p=H/'preview.template.html';s=p.read_text(encoding='utf-8');assert '修正版3' in s;s=s.replace('修正版3','修正版4');s=s.replace('大きな傾きと前後の動きに、壁と床が曲がる動きと黒い靄を重ねています。','全体の運動は参照の2D測定値。局所的な曲がりは較正形状から計算。仮形状と境界12px拘束あり。');p.write_text(s,encoding='utf-8')
p=H/'motion-revision.js';s=p.read_text(encoding='utf-8');old='面の境界と外周から12pxは内部の曲がりを抑え、全体の傾きと前後動に合わせます。仮の奥行きで動きを補う近似で、実空間を復元したものではありません。確認表示では靄と文字を外します。';assert old in s;s=s.replace(old,'全体の動きと局所的な曲がりを重ねた近似です。仮の形状から計算した曲がりを、面の境界と外周から12pxでは抑えています。実空間を復元したものではありません。確認表示では靄と文字を外します。').replace("version:'revision-02-pass-03'","version:'revision-02-pass-04'");p.write_text(s,encoding='utf-8')
prediction='参照の全体傾きと前後のリズムに、床とカーテンの局所的な曲がりが重なる。\n仮の近い枠の深度移動を使わないため、pass03の巨大な伸びと壁段差が出ない。\n靄と前景はpass02と同じで、右下先行と終盤の広い領域の減衰を維持する。\n'
for p in [H/'prediction.md',OUT/'prediction.md']:p.write_text(prediction,encoding='utf-8')
print('Pass04 prepared; source/render wait for frozen superposed module and parent still confirmation.')
