from pathlib import Path
import hashlib,json,shutil,sys
sys.stdout.reconfigure(encoding='utf-8')
root=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion')
out=root/'round-01'
if out.exists():raise RuntimeError('round-01 already exists; refusing to overwrite')
out.mkdir()
files=list(root.glob('prototype-*.png'))+[root/n for n in ['motion-prototype.mp4','motion-prototype.html','verification.json','pixel-metrics.json','time-aligned-comparison.jpg','analysis.md']]
manifest=[]
for p in files:
    q=out/p.name
    if not p.is_file():raise FileNotFoundError(p)
    shutil.copy2(p,q)
    a=hashlib.sha256(p.read_bytes()).hexdigest();b=hashlib.sha256(q.read_bytes()).hexdigest()
    if a!=b:raise RuntimeError('hash mismatch '+p.name)
    manifest.append({'file':p.name,'bytes':p.stat().st_size,'sha256':a,'copyVerified':True})
(out/'archive-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(f'round-01 archived and SHA256 verified: {len(files)} files')
