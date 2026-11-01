from pathlib import Path
import json,hashlib,base64,shutil
from PIL import Image
ROOT=Path(__file__).resolve().parents[4]
HERE=Path(__file__).resolve().parent
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial')
OUT.mkdir(parents=True,exist_ok=True)
SOURCES=['tools/web-compositor/show.json','tools/web-compositor/calib.js','tools/web-compositor/align-model.js','Assets/Scripts/Streaming/Editor/VirtualWalk/VirtualRoom.cs','tools/screen-effect-study-20261101/motion/motion-track.json','tools/web-compositor/captures/plate_B_20260924_172210_4abb5bae.jpg','tools/screen-effect-study-20261101/motion/motion.js']
manifest=[]
for f in SOURCES:
 p=ROOT/f; data=p.read_bytes(); dest=HERE/'snapshot'/Path(f).name;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
 manifest.append({'path':f,'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data)})
show=json.loads((ROOT/SOURCES[0]).read_text(encoding='utf-8-sig'))
cam=next(c for c in show['cameras'] if c['id']=='B')['calib']
room=show['layout']['room']
photo=(ROOT/SOURCES[5]).read_bytes()
assert hashlib.sha256(photo).hexdigest()=='342fdbe7daac0ea93982e4df7e53c06b5b154aed096dcbe547d4e09349d12f36'
adopted=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/motion/round-07/assets/base-b.png')
im=Image.open(adopted).convert('RGBA'); alpha=im.getchannel('A'); alpha.save(HERE/'aperture.png')
manifest.append({'path':str(adopted),'sha256':hashlib.sha256(adopted.read_bytes()).hexdigest(),'use':'alpha channel only; RGB never mapped to world'})
data={'camera':cam,'room':room,'track':json.loads((ROOT/SOURCES[4]).read_text()),'photo':'data:image/jpeg;base64,'+base64.b64encode(photo).decode(),'aperture':'data:image/png;base64,'+base64.b64encode((HERE/'aperture.png').read_bytes()).decode(),'sources':manifest}
(HERE/'assets.js').write_text('export const assets='+json.dumps(data,separators=(',',':'))+';\n',encoding='utf-8')
(HERE/'source-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print(json.dumps({'out':str(OUT),'camera':cam,'apertureSize':im.size}))
