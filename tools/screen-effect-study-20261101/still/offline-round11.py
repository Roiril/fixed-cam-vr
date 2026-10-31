"""Apply unchanged 09A and10B profiles to all three immutable adopted cameras."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys,time
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
SOURCE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-adoption/gpu-complete')
FINAL=SOURCE.parent/'finalized-captures.json'
DEFAULT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-11')
def module(name,file):
    spec=importlib.util.spec_from_file_location(name,HERE/file);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
r9=module('study09','offline-round09.py');r10=module('study10','offline-round10.py')
LOOKUP=DEFAULT.parent/'round-04-measured-lookup.json'

def rgba(src,target):
    l=src[:,:,:3].astype(float)@r9.WEIGHTS/255;gain=np.divide(target,l,out=np.zeros_like(l),where=l>.00001);out=src.copy();out[:,:,:3]=r9.gray8(src[:,:,:3]/255*gain[:,:,None]);out[src[:,:,3]==0]=src[src[:,:,3]==0];return out

def black(im,path):
    bg=Image.new('RGBA',im.size,(18,18,18,255));bg.alpha_composite(im);bg.convert('RGB').save(path)

def preview(out):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for camera in 'ABC':
        for profile in ['base','09A','10B']:
            p=out/(f'base-{camera}.png' if profile=='base' else f'{camera}-{profile}.png');assert p.exists();images[f'{camera}-{profile}']='data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode()
    template='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>3カメラに同じ加工を適用する</title><style>CSS
.wide{max-width:72rem;padding:16px}img{display:block;width:100%;height:auto;background:var(--surface)}button,select{font:inherit;padding:8px 16px;margin:4px;border:0;background:var(--surface);color:var(--ink)}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>3カメラに同じ追加加工を適用する</h1><p>現行採用描画の固定コピーを入力に使います。カメラごとの値は変えていません。</p><select id="camera" aria-label="カメラ"><option>A</option><option>B</option><option>C</option></select><select id="profile" aria-label="加工"><option value="09A">9-A：光学整理と信号</option><option value="10B">10-B：平坦面の整理と局所的な欠け</option></select><button id="toggle" aria-pressed="true">追加加工を切る</button><p id="status"></p></div><div class="wide"><img id="image" alt="カメラと加工の比較"></div><div class="wrap"><p>構図の一致と加工の一致は分けて比較します。生成目標画は入力にしていません。本編への反映はありません。</p></div><script>const images=IMAGES;let enabled=true;const camera=document.getElementById('camera'),profile=document.getElementById('profile'),button=document.getElementById('toggle');function show(){const p=enabled?profile.value:'base';document.getElementById('image').src=images[camera.value+'-'+p];document.getElementById('status').textContent='カメラ'+camera.value+' ／ '+(enabled?p:'追加前');button.textContent=enabled?'追加加工を切る':'追加加工を入れる';button.setAttribute('aria-pressed',String(enabled))}camera.onchange=profile.onchange=show;button.onclick=()=>{enabled=!enabled;show()};show();</script></html>'''
    (out/'preview.html').write_text(template.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);args=ap.parse_args();out=args.out;assert SOURCE.resolve() not in [out.resolve(),*out.resolve().parents],'immutable folder cannot be an output';out.mkdir(parents=True,exist_ok=True)
    gpu=json.loads((SOURCE/'manifest.json').read_text(encoding='utf-8'));final=json.loads(FINAL.read_text(encoding='utf-8'));assert final['changedSinceGpu']==0 and 'immutable' in final['note']
    source_manifest_hash=sha(SOURCE/'manifest.json');final_hash=sha(FINAL);photos={v['plate']:v for v in gpu['photos']};lookup=json.loads(LOOKUP.read_text(encoding='utf-8'))
    for source,name in [(SOURCE/'manifest.json','input-gpu-manifest.json'),(FINAL,'input-finalized-captures.json')]:
        dest=out/name
        if dest.exists():assert sha(dest)==sha(source)
        else:shutil.copyfile(source,dest)
    manifest={'round':11,'purpose':'Camera coverage check; evaluate processing independently of scene identity. No camera-specific parameter changes.','source_gpu_manifest':{'path':str(SOURCE/'manifest.json'),'sha256':source_manifest_hash},'source_finalized_manifest':{'path':str(FINAL),'sha256':final_hash},'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round09.py',HERE/'offline-round10.py']},'profiles':{'09A':r9.PARAMS['A'],'10B':r10.PARAMS['B']},'profile_parameters_identical_for_all_cameras':True,'lookup_sha256':sha(LOOKUP),'opencv':cv2.__version__,'numpy':np.__version__,'inputs':{},'outputs':{}}
    for camera in 'ABC':
        start=time.perf_counter();source=SOURCE/photos[camera]['file'];assert sha(source)==photos[camera]['sha256'];base=out/f'base-{camera}.png'
        if base.exists():assert sha(base)==sha(source)
        else:shutil.copyfile(source,base)
        src=np.array(Image.open(base).convert('RGBA'));visible=src[:,:,3]>0;assert np.array_equal(src,np.array(Image.open(source).convert('RGBA')));off=out/f'{camera}-off.png';shutil.copyfile(base,off);assert sha(off)==sha(base);black(Image.fromarray(src),out/f'base-{camera}-on-black.png')
        manifest['inputs'][camera]={'source':str(source),'frozen_copy':base.name,'source_sha256':sha(source),'copy_sha256':sha(base),'off_png_byte_exact':True,'size':list(Image.open(base).size),'mode':Image.open(base).mode,'alpha_range':[int(src[:,:,3].min()),int(src[:,:,3].max())]}
        a9,optical=r9.apply(src,'A',lookup);again9,_=r9.apply(src,'A',lookup);assert np.array_equal(a9,again9)
        clean,pre,std,energy,flatWeight=r10.prepare(optical);again_prepare=r10.prepare(optical);assert all(np.array_equal(a,b) for a,b in zip((clean,pre,std,energy,flatWeight),again_prepare));target,events=r10.faults(pre,energy,lookup,r10.PARAMS['B'],visible);again_target,_=r10.faults(pre,energy,lookup,r10.PARAMS['B'],visible);assert np.array_equal(target,again_target);a10=rgba(src,target)
        for profile,arr in [('09A',a9),('10B',a10)]:
            assert np.array_equal(arr[:,:,3],src[:,:,3]);assert np.array_equal(arr[~visible],src[~visible]);im=Image.fromarray(arr);path=out/f'{camera}-{profile}.png';im.save(path);black(im,out/f'{camera}-{profile}-on-black.png')
            for label,roi in {'full':(0,0,1280,1030),'center':(400,260,840,610),'floor':(420,780,860,940)}.items():labelled_pair(Image.fromarray(src),im,roi,out/f'{camera}-{profile}-comparison-{label}.png',path.name,base.name)
            manifest['outputs'][f'{camera}-{profile}']={'file':path.name,'sha256':sha(path),'input_sha256':sha(base),'alpha_exact':True,'transparent_rgba_exact':True,'repeat_exact':True,'same_camera_off_exact':True,**({'fault_events':events} if profile=='10B' else {})}
        print(json.dumps({'camera':camera,'seconds':time.perf_counter()-start,'input_sha256':sha(base),'09A':manifest['outputs'][f'{camera}-09A']['sha256'],'10B':manifest['outputs'][f'{camera}-10B']['sha256']},indent=2),flush=True)
    assert sha(SOURCE/'manifest.json')==source_manifest_hash and sha(FINAL)==final_hash
    for camera in 'ABC':assert sha(SOURCE/photos[camera]['file'])==manifest['inputs'][camera]['source_sha256']
    manifest['immutable_sources_unchanged_after_processing']=True;preview(out);(out/'input-and-processing-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print('Complete: six outputs; immutable inputs unchanged; OFF and alpha exact',flush=True)
if __name__=='__main__':main()
