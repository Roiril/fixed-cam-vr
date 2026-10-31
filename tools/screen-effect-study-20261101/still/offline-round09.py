"""Reproducible OpenCV offline study. No generated-image pixels are read."""
from pathlib import Path
import argparse,base64,hashlib,json,shutil,sys,time
sys.dont_write_bytecode=True
import cv2
import numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
cv2.setNumThreads(1);cv2.setRNGSeed(104729)
ROOT=Path(__file__).resolve().parent
DEFAULT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still')
EXPECTED='221da6ca07392595c4c04ef15f12afba28f09c8f95a3c02459df42f695f41e7a'
WEIGHTS=np.array([.2126,.7152,.0722])
PARAMS={
 'A':{'name':'光学ぼけ補正と水平信号','nlm_h':5.5,'wiener_sigma':1.2,'wiener_rho':.45,'wiener_K':.025,'horizontal_gain':1.15,'echo':.65},
 'B':{'name':'境界保持と細部強調','nlm_h':4.0,'edge_sigma_s':32,'edge_sigma_r':.18,'detail_sigma_s':10,'detail_sigma_r':.10,'detail_blend':.55,'horizontal_gain':.65,'echo':.8},
 'C':{'name':'面の整理と線の強調','nlm_h':4.0,'stylize_sigma_s':36,'stylize_sigma_r':.18,'horizontal_gain':.85,'echo':.9},
 'D':{'name':'横へ伸ばした上下差分の線片','nlm_h':5.5,'wiener_sigma':1.2,'wiener_rho':.45,'wiener_K':.025,'horizontal_correlation_sigma':1.1,'vertical_radius':2,'line_blend':.9,'echo':.5}}

def gray8(l):return np.floor(np.clip(l,0,1)*255+.5).astype(np.uint8)
def triple(l):return cv2.cvtColor(gray8(l),cv2.COLOR_GRAY2BGR)
def luminance_bgr(im):return im[:,:,::-1].astype(float)@WEIGHTS/255
def wiener(l,sigma,rho,k):
    # Reflection padding avoids using the opposite aperture edge as a neighbor.
    pad=32;a=np.pad(l,pad,mode='reflect');h,w=a.shape;fy=np.fft.fftfreq(h)[:,None];fx=np.fft.rfftfreq(w)[None,:]
    transfer=np.exp(-2*np.pi*np.pi*sigma*sigma*(fx*fx+fy*fy+2*rho*fx*fy))
    f=np.fft.rfft2(a);restored=np.fft.irfft2(f*transfer/(transfer*transfer+k)*(1+k),s=a.shape)
    return np.clip(restored[pad:-pad,pad:-pad],0,1)

def optical(l,key):
    p=PARAMS[key];denoised=cv2.fastNlMeansDenoising(gray8(l),None,p['nlm_h'],7,21)/255
    if key in ['A','D']:return wiener(denoised,p['wiener_sigma'],p['wiener_rho'],p['wiener_K'])
    if key=='B':
        smooth=cv2.edgePreservingFilter(triple(denoised),flags=cv2.RECURS_FILTER,sigma_s=p['edge_sigma_s'],sigma_r=p['edge_sigma_r'])
        enhanced=cv2.detailEnhance(smooth,sigma_s=p['detail_sigma_s'],sigma_r=p['detail_sigma_r'])
        return luminance_bgr(smooth)*(1-p['detail_blend'])+luminance_bgr(enhanced)*p['detail_blend']
    return luminance_bgr(cv2.stylization(triple(denoised),sigma_s=p['stylize_sigma_s'],sigma_r=p['stylize_sigma_r']))

def signal(l,key,lookup):
    p=PARAMS[key];h,w=l.shape;x=np.arange(w);result=np.empty_like(l)
    if key=='D':
        horizontal=cv2.GaussianBlur(l.astype(np.float32),(7,1),p['horizontal_correlation_sigma'],borderType=cv2.BORDER_REFLECT_101)
        low=cv2.erode(horizontal,np.ones((5,1),np.uint8));high=cv2.dilate(horizontal,np.ones((5,1),np.uint8));span=high-low
        position=(horizontal-low)/np.maximum(.005,span);u=np.clip((position-.12)/.76,0,1);core=.01+.84*u*u*(3-2*u)
        vertical_lp=cv2.GaussianBlur(horizontal,(1,5),.9,borderType=cv2.BORDER_REFLECT_101);dy=horizontal-vertical_lp
        gate=np.clip((span-.014)/.06,0,1)*np.clip(np.abs(dy)/np.maximum(.006,span)*4,0,1)
        continuous=np.interp(horizontal,[0,.07,.15,.35,.55,.75,1],[.008,.015,.09,.34,.63,.845,.85])
        pre=continuous+(core-continuous)*gate*p['line_blend']
        # The y difference makes thin horizontal cores; x low-pass sets their length.
    else:
        horizontal=l-cv2.GaussianBlur(l.astype(np.float32),(9,1),1.8,borderType=cv2.BORDER_REFLECT_101)
        pre=l+horizontal*p['horizontal_gain']
    j=np.array(lookup['jitter']);slow=sum(np.roll(j,k) for k in range(-7,8))/15;j=j-slow
    for y in range(h):
        r=y%len(j);v=j[r];runs=lookup['runs_threshold_008'][r];run=runs[r%len(runs)] if runs else {'length':3,'sign':1 if v>=0 else -1}
        length=max(2,min(8,run['length']));direction=run['sign']
        shift=round(v*.65) if abs(v)>6 else 0
        if abs(v)>16:shift=int(np.sign(v)*min(25,15+abs(v)-16))
        sx=np.clip(x+shift,0,w-1);c=pre[y,sx];lag=pre[y,np.clip(sx-direction*length,0,w-1)];far=pre[y,np.clip(sx-direction*(length+2),0,w-1)]
        result[y]=c+(c-lag)*p['echo']*.30+(lag-far)*p['echo']*.35
    if key=='D':return np.clip(result,.008,.85)
    return np.interp(np.clip(result,0,1),[0,.07,.15,.35,.55,.75,1],[.008,.015,.09,.34,.63,.845,.85])

def apply(src,key,lookup,strength=1):
    if strength==0:return src.copy()
    l=src[:,:,:3].astype(float)@WEIGHTS/255;stage=optical(l,key);target=signal(stage,key,lookup);target=l+(target-l)*strength
    gain=np.divide(target,l,out=np.zeros_like(l),where=l>.00001);result=src.copy();result[:,:,:3]=gray8(src[:,:,:3]/255*gain[:,:,None]);result[src[:,:,3]==0]=src[src[:,:,3]==0]
    return result,stage

def preview(out,keys):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8')
    images={'base':'data:image/png;base64,'+base64.b64encode((out/'base.png').read_bytes()).decode()}
    for k in keys:images[k]='data:image/png;base64,'+base64.b64encode((out/f'round-09-{k}.png').read_bytes()).decode()
    options=''.join(f'<option value="{k}">{k}：{PARAMS[k]["name"]}</option>' for k in keys)
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>OpenCV追加加工の比較</title><style>CSS
.wide{max-width:72rem;padding:16px}img{display:block;width:100%;height:auto;background:var(--surface)}button,select{font:inherit;padding:8px 16px;margin:4px;color:var(--ink);background:var(--surface);border:0}button:focus-visible,select:focus-visible{outline:2px solid var(--link)}.controls{display:flex;flex-wrap:wrap;gap:8px}</style><main><div class="wrap"><h1>OpenCVで整理した加工を比較する</h1><p>固定したCRT実描画をオフラインで計算したPNGです。加工前へ切り替えて比較できます。</p><div class="controls"><select id="candidate" aria-label="加工の候補">OPTIONS</select><button id="toggle" aria-pressed="true">追加加工を切る</button></div><p id="label" role="status"></p></div><div class="wide"><img id="image" alt="CRTの追加加工を比較"></div><div class="wrap"><p>生成目標画のRGBと固定補正場は使っていません。計算はPython CLIで再実行できます。本編Unityは変更していません。</p></div></main><script>const images=IMAGES;let enabled=true;const select=document.getElementById('candidate'),button=document.getElementById('toggle');function show(){const key=enabled?select.value:'base';document.getElementById('image').src=images[key];document.getElementById('label').textContent=enabled?'追加後：'+select.value:'追加前';button.textContent=enabled?'追加加工を切る':'追加加工を入れる';button.setAttribute('aria-pressed',String(enabled))}select.onchange=show;button.onclick=()=>{enabled=!enabled;show()};show();</script></html>'''
    (out/'round-09-preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--candidate',choices=['A','B','C','D','all'],default='all');ap.add_argument('--off',action='store_true');args=ap.parse_args();out=args.out;assert sha(out/'base.png')==EXPECTED
    src=np.array(Image.open(out/'base.png').convert('RGBA'));assert src.tobytes()==(out/'base.rgba').read_bytes()
    if args.off:shutil.copyfile(out/'base.png',out/'round-09-off.png');assert (out/'round-09-off.png').read_bytes()==(out/'base.png').read_bytes();print('OFF: byte-exact PNG copy');return
    lookup=json.loads((out/'round-04-measured-lookup.json').read_text(encoding='utf-8'));keys=list(PARAMS) if args.candidate=='all' else [args.candidate];manifest={'round':9,'base_sha256':EXPECTED,'input':str(out/'base.png'),'opencv':cv2.__version__,'numpy':np.__version__,'source_sha256':sha(Path(__file__)),'lookup_sha256':sha(out/'round-04-measured-lookup.json'),'generated_target_used_for_pixels':False,'parameters':PARAMS,'candidates':{},'docs':['https://docs.opencv.org/4.x/d1/d79/group__photo__denoise.html','https://docs.opencv.org/4.x/df/dac/group__photo__render.html']}
    previous_path=out/'round-09-processing.json'
    if previous_path.exists():
        old=json.loads(previous_path.read_text(encoding='utf-8'));assert old['base_sha256']==EXPECTED
        for k,v in old['candidates'].items():
            if k not in keys and old['parameters'][k]==PARAMS[k]:
                assert sha(out/f'round-09-{k}.png')==v['sha256'];manifest['candidates'][k]={**v,'source_sha256':v.get('source_sha256',old['source_sha256'])}
    for key in keys:
        start=time.perf_counter();actual,stage=apply(src,key,lookup);again,_=apply(src,key,lookup);assert np.array_equal(actual,again);assert np.array_equal(apply(src,key,lookup,0),src);assert np.array_equal(actual[:,:,3],src[:,:,3]);assert np.array_equal(actual[src[:,:,3]==0],src[src[:,:,3]==0]);im=Image.fromarray(actual);im.save(out/f'round-09-{key}.png');black=Image.new('RGBA',im.size,(18,18,18,255));black.alpha_composite(im);black.convert('RGB').save(out/f'round-09-{key}-on-black.png');Image.fromarray(gray8(stage)).save(out/f'round-09-{key}-optical-stage.png')
        before=Image.fromarray(src)
        for name,roi in {'full':(0,0,1280,1030),'curtain':(440,180,840,520),'floor':(420,800,860,940),'wall':(110,200,225,430)}.items():labelled_pair(before,im,roi,out/f'round-09-{key}-comparison-{name}.png',f'round-09-{key}.png')
        manifest['candidates'][key]={'sha256':sha(out/f'round-09-{key}.png'),'source_sha256':sha(Path(__file__)),'off_exact':True,'alpha_exact':True,'transparent_exact':True,'repeat_exact':True,'elapsed_sec':time.perf_counter()-start}
        print(json.dumps({key:manifest['candidates'][key]},indent=2))
    preview(out,list(manifest['candidates']));(out/'round-09-processing.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
if __name__=='__main__':main()
