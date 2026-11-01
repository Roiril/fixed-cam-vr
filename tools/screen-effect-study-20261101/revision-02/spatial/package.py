from pathlib import Path
import shutil,hashlib,json
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial')
parts=[]
for f in ['assets.js','geometry.js','renderer.js','preview.js']:
 s=(H/f).read_text(encoding='utf-8')
 s='\n'.join(l for l in s.splitlines() if not l.startswith('import '))
 s=s.replace('export async function','async function').replace('export function','function').replace('export const','const')
 parts.append(s)
html=(H/'preview.template.html').read_text(encoding='utf-8').replace('/*BUNDLE*/','\n'.join(parts))
O.mkdir(exist_ok=True,parents=True)
(O/'spatial-preview.html').write_bytes(html.encode('utf-8'))
(H/'spatial-preview.html').write_bytes(html.encode('utf-8'))
for f in ['assets.js','geometry.js','renderer.js','source-manifest.json','prediction.md','README.md']:
 shutil.copyfile(H/f,O/f)
print(json.dumps({'htmlBytes':len(html.encode()),'sha256':hashlib.sha256(html.encode()).hexdigest()}))
