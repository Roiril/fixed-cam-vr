"""Round24: independently calibrate contour-residual gain and width; frozen23 engine/material."""
from pathlib import Path
import base64,importlib.util,json,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('frozen23',HERE/'offline-round23.py');r23=importlib.util.module_from_spec(s);s.loader.exec_module(r23)
ATLAS=r23.DEFAULT;DEFAULT=ATLAS.parent/'round-24'
PROFILES={'soft':{'gain':.60,'normal_width':.80,'tangent_pitch':.82},'standard':{'gain':.60,'normal_width':1.0,'tangent_pitch':.82},'strong':{'gain':.72,'normal_width':.85,'tangent_pitch':.82}}

def apply_residual(surface,paths,atlases,p):
    h,w=surface.shape[:2];acc=np.zeros((h,w),float);weights=np.zeros((h,w),float);sequences=[]
    for path in paths:
        ys=path['y'];center=path['x'];slope=path['slope'];arc=np.cumsum(np.sqrt(1+slope*slope))/p['tangent_pitch'];strip,seq=r23.residual_strip(atlases,max(4096,int(arc[-1])+600),path['seed']);radius=24*p['normal_width']*np.sqrt(1+np.max(slope*slope));x0=max(0,int(np.floor(center.min()-radius)));x1=min(w,int(np.ceil(center.max()+radius))+1)
        if x1<=x0:continue
        xx=np.arange(x0,x1)[None,:];normal=path['polarity']*(xx-center[:,None])/np.sqrt(1+slope[:,None]**2);u=normal/p['normal_width'];mx=(u+24).astype(np.float32);phase=path['seed']%431;my=np.repeat((arc+phase).astype(np.float32)[:,None],x1-x0,axis=1);residual=cv2.remap(strip,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT,borderValue=0);weight=np.clip((24-np.abs(u))/6,0,1)*np.exp(-(u/14)**2)*(path['contrast'][:,None]>.08);delta=residual*path['contrast'][:,None]*p['gain'];acc[np.ix_(ys,np.arange(x0,x1))]+=delta*weight;weights[np.ix_(ys,np.arange(x0,x1))]+=weight;sequences.append({'seed':path['seed'],'source_blocks':seq,'rows':len(ys),'polarity':path['polarity'],'tangent_pitch':p['tangent_pitch']})
    delta=np.clip(acc/np.maximum(1,weights),-.30,.30);return delta,{'path_count':len(paths),'active_delta_pixels':int((np.abs(delta)>.001).sum()),'maximum_abs_delta':float(np.abs(delta).max()),'phase_sequences':sequences}

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for k,f in [('base',f'base-{c}.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+k]='data:image/png;base64,'+base64.b64encode((out/f).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>輪郭残差の幅と強さを分けて比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>輪郭残差の幅と強さを分けて比較</h1><p>参照由来の残差を同じ入力輪郭へ適用しています。元形状や反射との完全な分離はできません。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="gainと幅"><option value="soft">gain .60 ／ 幅 .80</option><option value="standard">gain .60 ／ 幅 1.00</option><option value="strong">gain .72 ／ 幅 .85</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="輪郭残差の幅とgainの比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    # All overrides are process-local. The frozen23 source and media are never written.
    r23.PROFILES=PROFILES;r23.apply_residual=apply_residual;r23.preview=preview;r23.DEFAULT=DEFAULT
    if '--out' not in sys.argv:sys.argv+=['--out',str(DEFAULT)]
    if '--atlas-dir' not in sys.argv:sys.argv+=['--atlas-dir',str(ATLAS)]
    if '--camera' not in sys.argv:sys.argv+=['--camera','B']
    r23.main();out=Path(sys.argv[sys.argv.index('--out')+1]);m=json.loads((out/'manifest.json').read_text(encoding='utf-8'));m['round']=24;m['source_sha256']=r23.sha(Path(__file__));m['shared_frozen23_source_sha256']=r23.sha(HERE/'offline-round23.py');m['frozen23_manifest_sha256']=r23.sha(ATLAS/'round23-freeze.json');m['unit_calibration']={'reference_active_size':[1440,1080],'current_aperture_alpha250_size':[1122,882],'ratio_xy':[1122/1440,882/1080],'shared_tangent_pitch':.82,'comparison':'gain .60 fixed at normal width .80 and1.00; compensation .72 at width .85'};(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Round24 metadata saved',flush=True)
if __name__=='__main__':main()
