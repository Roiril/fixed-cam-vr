from pathlib import Path
import sys,json
sys.dont_write_bytecode=True
import numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
out=Path(sys.argv[1]);m=json.loads((out/'round-08-processing.json').read_text(encoding='utf-8'));w,h=m['base']['size'];before=Image.open(out/'base.png').convert('RGBA');assert sha(out/'base.png')==m['base']['sha256'];assert np.array(before).tobytes()==(out/'base.rgba').read_bytes()
a=np.frombuffer((out/'round-08-default.rgba').read_bytes(),dtype=np.uint8).reshape(h,w,4);after=Image.fromarray(a);after.save(out/'round-08-default.png');bg=Image.new('RGBA',after.size,(18,18,18,255));bg.alpha_composite(after);bg.convert('RGB').save(out/'round-08-default-on-black.png')
for name,box in {'full':(0,0,w,h),'curtain':(440,180,840,520),'floor':(420,800,860,940),'rail':(180,420,620,680),'shadow':(780,620,1180,920)}.items():labelled_pair(before,after,box,out/f'round-08-comparison-{name}.png','round-08-default.png')
m['outputs']={p.name:sha(p) for p in out.glob('round-08-*.png')};(out/'round-08-processing.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'candidate':str(out/'round-08-default-on-black.png'),'sha256':sha(out/'round-08-default.png')},ensure_ascii=False,indent=2))
