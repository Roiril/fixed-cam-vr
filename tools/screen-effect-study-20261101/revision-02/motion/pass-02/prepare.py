from pathlib import Path
import hashlib,json,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
P=H.parent
OLD=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion')
OUT=OLD/'pass-02';OUT.mkdir(exist_ok=True)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
freeze=[]
for base in [P,OLD]:
    for p in sorted(base.rglob('*')):
        if p.is_file() and 'pass-02' not in p.relative_to(base).parts:freeze.append({'path':str(p),'bytes':p.stat().st_size,'sha256':sha(p)})
(OUT/'pass-01-freeze.json').write_text(json.dumps(freeze,ensure_ascii=False,indent=2),encoding='utf-8')
prediction='右下の小片から下辺へ開き、4.3秒より前の左35%はほぼ閉じたままになる。\n輪郭の半透明帯を3〜9pxへ狭め、持続移流する細かな欠けが見える。\n終盤12.4秒は広い中央下の部屋が薄れ、12.6秒は細かな孔へ分かれて消える。\n'
(H/'prediction.md').write_text(prediction,encoding='utf-8');(OUT/'prediction.md').write_text(prediction,encoding='utf-8')
for name in ['package.py','capture.mjs','compare.py','delivery.py']:
    p=H/name;s=p.read_text(encoding='utf-8')
    s=s.replace("H.parent/'spatial'","H.parent.parent/'spatial'").replace("'../spatial'","'../../spatial'")
    s=s.replace("revision-02/motion'","revision-02/motion/pass-02'")
    if name=='compare.py':s=s.replace("OUT/'evidence/manifest.json'","OUT.parent/'evidence/manifest.json'")
    if name=='capture.mjs':s=s.replace('3.2,3.6,4,4.1','3.2,3.6,3.8,4,4.1').replace('10.5,11.8,12.4','10.5,11.8,12,12.4')
    if name=='delivery.py':s=s.replace('len(stills)==32','len(stills)==34').replace("'stills':32","'stills':34").replace("'requestedStillsNative':18","'requestedStillsNative':20")
    p.write_text(s,encoding='utf-8')
print(json.dumps({'frozenFiles':len(freeze),'output':str(OUT)}))
