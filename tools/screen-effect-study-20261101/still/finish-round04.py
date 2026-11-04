from pathlib import Path
import sys,json
sys.dont_write_bytecode=True
import numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
out=Path(sys.argv[1]);m=json.loads((out/'round-04-processing.json').read_text(encoding='utf-8'));w,h=m['base']['size'];before=Image.open(out/'base.png').convert('RGBA');assert sha(out/'base.png')==m['base']['sha256'];assert np.array(before).tobytes()==(out/'base.rgba').read_bytes()
for name in ['default','soft','strong']:
    a=np.frombuffer((out/f'round-04-{name}.rgba').read_bytes(),dtype=np.uint8).reshape(h,w,4);im=Image.fromarray(a);im.save(out/f'round-04-{name}.png');bg=Image.new('RGBA',im.size,(18,18,18,255));bg.alpha_composite(im);bg.convert('RGB').save(out/f'round-04-{name}-on-black.png')
after=Image.open(out/'round-04-default.png')
for name,box in {'full':(0,0,w,h),'curtain':(440,180,840,520),'rail':(180,420,620,680),'shadow':(780,620,1180,920)}.items():labelled_pair(before,after,box,out/f'round-04-comparison-{name}.png','round-04-default.png')
m['outputs']={p.name:sha(p) for p in out.glob('round-04-*.png')};(out/'round-04-processing.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'candidate':str(out/'round-04-default-on-black.png'),'sha256':sha(out/'round-04-default.png')},ensure_ascii=False,indent=2))
