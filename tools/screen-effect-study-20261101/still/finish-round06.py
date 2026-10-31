from pathlib import Path
import sys,json
sys.dont_write_bytecode=True
import numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
out=Path(sys.argv[1]);m=json.loads((out/'round-06-processing.json').read_text(encoding='utf-8'));w,h=m['base']['size'];before=Image.open(out/'base.png').convert('RGBA');assert sha(out/'base.png')==m['base']['sha256'];assert np.array(before).tobytes()==(out/'base.rgba').read_bytes()
a=np.frombuffer((out/'round-06-default.rgba').read_bytes(),dtype=np.uint8).reshape(h,w,4);after=Image.fromarray(a);after.save(out/'round-06-default.png');bg=Image.new('RGBA',after.size,(18,18,18,255));bg.alpha_composite(after);bg.convert('RGB').save(out/'round-06-default-on-black.png')
for name,box in {'full':(0,0,w,h),'curtain':(440,180,840,520),'floor':(420,800,860,940),'rail':(180,420,620,680),'shadow':(780,620,1180,920)}.items():labelled_pair(before,after,box,out/f'round-06-comparison-{name}.png','round-06-default.png')
previous=np.array(Image.open(out/'round-05-default.png'));delta=np.abs(a[:,:,:3].astype(int)-previous[:,:,:3].astype(int));visible=a[:,:,3]>0
m['difference_from_round05']={'mean_abs_rgb_8bit':float(delta[visible].mean()),'changed_visible_pixels':int(((delta>0).any(axis=2)&visible).sum())}
def floor_stats(im):
    l=np.array(im.crop((420,800,860,940)))[:,:,:3]@np.array([.2126,.7152,.0722])/255
    return {'dark_below_006':float((l<.06).mean()),'light_above_075':float((l>.75).mean()),'horizontal_difference_mean':float(np.abs(np.diff(l,axis=1)).mean())}
m['same_floor_roi']={'base':floor_stats(before),'candidate':floor_stats(after),'purpose':'同場面同座標の変化。別室の面積比を目標にしない。'}
m['outputs']={p.name:sha(p) for p in out.glob('round-06-*.png')};(out/'round-06-processing.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({'candidate':str(out/'round-06-default-on-black.png'),'sha256':sha(out/'round-06-default.png')},ensure_ascii=False,indent=2))
