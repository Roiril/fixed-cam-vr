"""Freeze failed pass02 evidence without changing pass01 or the filter."""
from pathlib import Path
import ast,hashlib,json,shutil
import numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still')
OUT=ROOT/'pass-02'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def write(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def longest(v):return max(map(len,''.join('1' if value else '0' for value in v).split('0')),default=0)
frozen=json.loads((ROOT/'delivery-sha256.json').read_text(encoding='utf-8'))
assert all(sha(ROOT/name)==digest for name,digest in frozen.items())
old=(HERE.parent/'directional.py').read_text(encoding='utf-8');new=(HERE/'directional.py').read_text(encoding='utf-8')
functions=lambda source:{node.name:ast.get_source_segment(source,node) for node in ast.parse(source).body if isinstance(node,ast.FunctionDef)}
fa,fb=functions(old),functions(new);checks={name:fa[name]==fb[name] for name in ['luminance','tracks','process']};assert all(checks.values())
write(OUT/'control-verification.json',dict(unchanged_function_source=checks,pass01_frozen_artifact_count=len(frozen),pass01_all_hashes_match=True))
rois={'curtain-left-boundary':[735,160,810,620],'curtain-right-boundary':[930,200,1015,620],'wall-column-boundary':[615,130,655,620]}
a=np.array(Image.open(OUT/'base-B.png'));result={'ROIs':rois,'pass01':{},'pass02':{}}
for passname,folder in [('pass01',ROOT),('pass02',OUT)]:
    gate=np.array(Image.open(folder/'B-standard-gate-mask.png'))>0;b=np.array(Image.open(folder/'B-standard.png'));changed=np.any(a!=b,axis=2);protected=np.load(folder/'B-standard-layers.npz')['protected']
    for name,(x0,y0,x1,y1) in rois.items():
        g=gate[y0:y1,x0:x1];c=changed[y0:y1,x0:x1];rows=g.any(axis=1)
        result[passname][name]=dict(rows_total=len(rows),gate_rows=int(rows.sum()),gate_longest_span=longest(rows),gate_area=int(g.sum()),changed_rows=int(c.any(axis=1).sum()),changed_longest_span=longest(c.any(axis=1)),changed_pixels=int(c.sum()),protected_percent=float(protected[y0:y1,x0:x1].mean()*100))
write(OUT/'boundary-coverage.json',result)
page=OUT/'preview.html';html=page.read_text(encoding='utf-8');marker='<h1>広い垂直境界だけを加工する比較</h1>';assert marker in html
html=html.replace(marker,marker+'<p>この試作は未達です。12pxの縦線の完全保持と、広い縦境界の連続した加工を満たしていません。</p>');page.write_text(html,encoding='utf-8')
for name in ['inspection.md','finalize.py']:
    shutil.copyfile(HERE/name,OUT/'source-snapshot'/name)
shutil.copyfile(HERE/'inspection.md',OUT/'inspection.md')
manifest=json.loads((OUT/'manifest.json').read_text(encoding='utf-8'))
manifest['result']='failed: 12px vertical line protection and real curtain continuity unmet'
manifest['source_sha256']={p.name:sha(p) for p in (OUT/'source-snapshot').iterdir() if p.is_file()}
write(OUT/'manifest.json',manifest)
files={str(p.relative_to(OUT)).replace('\\','/'):sha(p) for p in sorted(OUT.rglob('*')) if p.is_file() and p.name!='delivery-sha256.json'}
write(OUT/'delivery-sha256.json',files)
print('pass01 preserved',len(frozen),'pass02 frozen',len(files),'manifest',sha(OUT/'manifest.json'),'directional',sha(HERE/'directional.py'))
