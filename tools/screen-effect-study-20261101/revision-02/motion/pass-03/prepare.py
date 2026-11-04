from pathlib import Path
import hashlib,json,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent;P=H.parent/'pass-02'
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion');OUT=BASE/'pass-03';OUT.mkdir(exist_ok=True)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
frozen=[{'path':str(p),'sha256':sha(p),'bytes':p.stat().st_size} for root in [P,BASE/'pass-02'] for p in sorted(root.rglob('*')) if p.is_file()]
(OUT/'pass-02-freeze.json').write_text(json.dumps(frozen,ensure_ascii=False,indent=2),encoding='utf-8')
for n in ['package.py','capture.mjs','compare.py','fields.mjs','measure.py']:
    p=H/n;s=p.read_text(encoding='utf-8');s=s.replace('revision-02/motion/pass-02','revision-02/motion/pass-03')
    if n=='package.py':s=s.replace("sha(H.parent/name)","sha(H.parent/'pass-02'/name)")
    if n=='compare.py':s=s.replace("OUT.parent/'evidence/manifest.json'","OUT.parent/'evidence/manifest.json'")
    if n=='capture.mjs':s=s.replace("['global','local','none']","['combined','global','local','none']")
    p.write_text(s,encoding='utf-8')
predictions='大きな傾きと前後動を保ちながら床とカーテンの内部が曲がる。\ncombined/local/global/none は同じ1.35倍の表示範囲と固定した表示処理で比較できる。\n靄と文字と薄片と粒はpass-02と同一の場を使い、右下から退く順序を変えない。\n'
for p in [H/'prediction.md',OUT/'prediction.md']:p.write_text(predictions,encoding='utf-8')
print(json.dumps({'frozenPass02Files':len(frozen),'output':str(OUT)}))
