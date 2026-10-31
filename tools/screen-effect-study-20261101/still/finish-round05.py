from pathlib import Path
import sys,json
sys.dont_write_bytecode=True
import numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
out=Path(sys.argv[1]);m=json.loads((out/'round-05-processing.json').read_text(encoding='utf-8'));w,h=m['base']['size'];before=Image.open(out/'base.png').convert('RGBA');assert sha(out/'base.png')==m['base']['sha256'];assert np.array(before).tobytes()==(out/'base.rgba').read_bytes()
for name in ['default','weak-grain']:
    a=np.frombuffer((out/f'round-05-{name}.rgba').read_bytes(),dtype=np.uint8).reshape(h,w,4);im=Image.fromarray(a);im.save(out/f'round-05-{name}.png');bg=Image.new('RGBA',im.size,(18,18,18,255));bg.alpha_composite(im);bg.convert('RGB').save(out/f'round-05-{name}-on-black.png')
after=Image.open(out/'round-05-default.png')
for name,box in {'full':(0,0,w,h),'curtain':(440,180,840,520),'floor':(420,800,860,940),'rail':(180,420,620,680),'shadow':(780,620,1180,920)}.items():labelled_pair(before,after,box,out/f'round-05-comparison-{name}.png','round-05-default.png')
labelled_pair(after,Image.open(out/'round-05-weak-grain.png'),(440,180,840,520),out/'round-05-grain-comparison.png','round-05-weak-grain.png','round-05-default.png')
m['outputs']={p.name:sha(p) for p in out.glob('round-05-*.png')};(out/'round-05-processing.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'candidate':str(out/'round-05-default-on-black.png'),'sha256':sha(out/'round-05-default.png')},ensure_ascii=False,indent=2))
