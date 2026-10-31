"""Round19: raw current PNG -> conservative surface -> sparse row timebase -> bounded ringing."""
from pathlib import Path
import argparse,base64,json,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent
SOURCE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-11')
DEFAULT=SOURCE.parent/'round-19'
WEIGHTS=np.array([.2126,.7152,.0722])
PROFILES={'soft':{'maximum_dx':1,'ring_distance':2,'ring_gain':.10},'standard':{'maximum_dx':2,'ring_distance':3,'ring_gain':.18},'strong':{'maximum_dx':3,'ring_distance':4,'ring_gain':.25}}

def u8(a):return np.clip(np.rint(a*255),0,255).astype(np.uint8)

def extend_alpha(raw):
    valid=raw[:,:,3]>=250
    if not valid.any():valid=raw[:,:,3]>0
    assert valid.any(),'Input needs at least one visible pixel'
    _,labels=cv2.distanceTransformWithLabels((~valid).astype(np.uint8),cv2.DIST_L2,cv2.DIST_MASK_5,labelType=cv2.DIST_LABEL_PIXEL);lut=np.zeros((int(labels.max())+1,3),np.uint8);lut[labels[valid]]=raw[:,:,:3][valid];rgb=lut[labels];rgb[valid]=raw[:,:,:3][valid];return rgb

def surface(raw):
    rgb=extend_alpha(raw);nlm=cv2.fastNlMeansDenoising(rgb,None,5.5,7,21).astype(np.float32)/255
    s=cv2.bilateralFilter(nlm,9,.05,3);s+=np.clip(rgb.astype(np.float32)/255-s,-.03,.03)*.20
    return np.clip(s,0,1)

def row_timebase(height,p):
    rng=np.random.default_rng(19019);dx=np.zeros(height,np.int32);runs=[];y=0
    while y<height:
        quiet=int(rng.integers(4,13));end=min(height,y+quiet);runs.append({'kind':'quiet','start':y,'length':end-y,'requested_length':quiet,'dx':0});y=end
        if y>=height:break
        active=int(rng.integers(1,4));magnitude=min(p['maximum_dx'],int(rng.integers(1,4)));shift=magnitude*(1 if rng.random()<.5 else -1);end=min(height,y+active);dx[y:end]=shift;runs.append({'kind':'active','start':y,'length':end-y,'requested_length':active,'dx':shift});y=end
    return dx,runs

def scan(s,p):
    h,w,_=s.shape;dx,runs=row_timebase(h,p);indices=np.clip(np.arange(w)[None,:]+dx[:,None],0,w-1);shifted=s[np.arange(h)[:,None],indices];distance=p['ring_distance'];lag=shifted[:,np.clip(np.arange(w)-distance,0,w-1)];l=shifted@WEIGHTS
    # min/max is used only to measure contrast for the gate/cap, never to normalize brightness.
    kernel=np.ones((1,2*distance+1),np.uint8);local_range=cv2.dilate(l,kernel)-cv2.erode(l,kernel);gate=local_range>.08
    cap=.25*local_range;ring=np.clip((shifted-lag)*p['ring_gain'],-cap[:,:,None],cap[:,:,None]);ring*=gate[:,:,None];result=np.clip(shifted+ring,0,1)
    return result,shifted,dx,runs,{'active_rows':int((dx!=0).sum()),'active_fraction':float((dx!=0).mean()),'dx_min':int(dx.min()),'dx_max':int(dx.max()),'ring_gate_fraction':float(gate.mean()),'ring_max_abs_luma':float(np.abs(ring@WEIGHTS).max()),'ring_cap_valid':bool(np.all(np.abs(ring)<=cap[:,:,None]+1e-9))}

def protect(raw,rgb):
    visible=raw[:,:,3]>0;d=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);t=np.clip((d-2)/18,0,1);fade=t*t*(3-2*t);out=raw.copy();out[:,:,:3]=u8(raw[:,:,:3]/255+(rgb-raw[:,:,:3]/255)*fade[:,:,None]);out[~visible]=raw[~visible];boundary=visible&(d<=2)
    assert np.array_equal(out[:,:,3],raw[:,:,3]) and np.array_equal(out[~visible],raw[~visible]) and np.array_equal(out[boundary],raw[boundary]);return out,int(boundary.sum())

def black(a,path):
    bg=Image.new('RGBA',(a.shape[1],a.shape[0]),(18,18,18,255));bg.alpha_composite(Image.fromarray(a));bg.convert('RGB').save(path)

def diagnostics(a,raw):
    l=a[:,:,:3].astype(float)@WEIGHTS/255;before=raw[:,:,:3].astype(float)@WEIGHTS/255;visible=raw[:,:,3]>0;result={'changed_visible_fraction':float(np.any(a[:,:,:3]!=raw[:,:,:3],axis=2)[visible].mean()),'luma_delta_RMS':float(np.sqrt(np.mean((l[visible]-before[visible])**2))),'continuous_mid_fraction_L01to075':float(((l>.1)&(l<.75))[visible].mean())}
    for name,(x0,y0,x1,y1) in {'floor':(680,740,1100,900),'wall':(180,250,500,500),'black':(820,450,880,530)}.items():
        if x1<=a.shape[1] and y1<=a.shape[0]:
            q=l[y0:y1,x0:x1];result[name]={'mean':float(q.mean()),'std':float(q.std()),'gray_L003to04_fraction':float(((q>.03)&(q<.4)).mean())}
    return result

def verify_synthetic(out):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=96,192;y,x=np.indices((h,w));flat=np.zeros((h,w,4),np.uint8);flat[:,:,:3]=[93,113,131];flat[:,:,3]=255
    edge=flat.copy();v=np.where(x<65+y*.25,38,166).astype(np.uint8);edge[:,:,:3]=v[:,:,None]
    fine=flat.copy();v=np.full((h,w),102,np.uint8);v[25,30:160]=153;v[51,55:145]=40;v[72:74,20:170]=140;fine[:,:,:3]=v[:,:,None]
    report={}
    for name,raw in [('flat',flat),('slanted-edge',edge),('fine-lines',fine)]:
        s=surface(raw);assert np.array_equal(s,surface(raw));srgb,_=protect(raw,s);final,shifted,dx,runs,info=scan(s,PROFILES['strong']);a,bcount=protect(raw,final);assert np.array_equal(a,protect(raw,scan(s,PROFILES['strong'])[0])[0]);Image.fromarray(raw).save(folder/(name+'-input.png'));Image.fromarray(srgb).save(folder/(name+'-surface.png'));Image.fromarray(a).save(folder/(name+'-final.png'));labelled_pair(Image.fromarray(raw),Image.fromarray(a),(0,0,w,h),folder/(name+'-comparison.png'),name+'-final.png',name+'-input.png')
        r={'alpha_hidden_boundary_exact':True,'repeat_exact':True,'midgray_L03to05_fraction':float((((a[:,:,:3]@WEIGHTS)/255>.3)&((a[:,:,:3]@WEIGHTS)/255<.5)).mean()),'boundary_pixels':bcount}
        if name=='flat':r['new_pattern_pixels']=int(np.any(a[:,:,:3]!=raw[:,:,:3],axis=2).sum());assert r['new_pattern_pixels']==0
        if name=='slanted-edge':
            before=np.argmax((s@WEIGHTS)>.4,axis=1);after=np.argmax((shifted@WEIGHTS)>.4,axis=1);error=after-before+dx;r['edge_translation_max_error_px']=int(np.abs(error).max());assert r['edge_translation_max_error_px']==0
        if name=='fine-lines':assert r['midgray_L03to05_fraction']>.80
        report[name]=r
    (out/'synthetic-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return report

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={};cameras=list(m['inputs'])
    for camera in cameras:
        for key,file in [('base',f'base-{camera}.png'),('surface',f'{camera}-surface.png'),*[(p,f'{camera}-{p}.png') for p in PROFILES]]:images[camera+'-'+key]='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in cameras);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>面と走査信号を比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>面と走査信号を比較</h1><p>現行描画から面を整理し、短い行ずれと片側の明暗の返しを加えた出力です。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="加工段階"><option value="base">加工前</option><option value="surface">面だけ</option><option value="soft">弱め</option><option value="standard">標準</option><option value="strong">強め</option></select></div><div class="image"><img id="image" alt="加工段階の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='all');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);source_m=json.loads((SOURCE/'input-and-processing-manifest.json').read_text(encoding='utf-8'));cameras=['custom'] if args.input else list('ABC') if args.camera=='all' else [args.camera]
    m={'round':19,'pipeline':['alpha nearest-valid RGB extension','NLM h5.5','bilateral sigma_color .05 sigma_space3 diameter9','restore20% residual clipped+/-.03','nonperiodic quiet4..12 active1..3 row-common dx','one-sided ringing gated localrange>.08 capped25%','raw difference alpha-border fade2..20'],'profiles':PROFILES,'source_code_sha256':sha(Path(__file__)),'opencv':cv2.__version__,'generated_RGB_used':False,'legacy_filter_outputs_used':False,'camera_parameters_identical':True,'inputs':{},'outputs':{}}
    for camera in cameras:
        src=args.input if args.input else SOURCE/f'base-{camera}.png';expected=sha(src)
        if not args.input:assert expected==source_m['inputs'][camera]['copy_sha256']
        for name in [f'base-{camera}.png',f'{camera}-OFF.png']:
            dest=out/name
            if dest.exists():assert sha(dest)==expected
            else:shutil.copyfile(src,dest)
        raw=np.array(Image.open(src).convert('RGBA'));s=surface(raw);assert np.array_equal(s,surface(raw));a,bcount=protect(raw,s);Image.fromarray(a).save(out/f'{camera}-surface.png');black(a,out/f'{camera}-surface-on-black.png');m['inputs'][camera]={'path':str(src.resolve()),'sha256':expected,'OFF_byte_exact':sha(out/f'{camera}-OFF.png')==expected,'surface_sha256':sha(out/f'{camera}-surface.png'),'surface_repeat_exact':True,'surface_diagnostics':diagnostics(a,raw)}
        for profile,p in PROFILES.items():
            target,shifted,dx,runs,info=scan(s,p);again=scan(s,p)[0];assert np.array_equal(target,again);a,bcount=protect(raw,target);file=f'{camera}-{profile}.png';Image.fromarray(a).save(out/file);black(a,out/f'{camera}-{profile}-on-black.png');series_file=out/f'{camera}-{profile}-row-timebase.json';series_file.write_text(json.dumps({'dx':dx.tolist(),'runs':runs},indent=2),encoding='utf-8')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{camera}-{profile}-comparison-{label}.png',file,f'base-{camera}.png')
            m['outputs'][camera+'-'+profile]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':bcount,'repeat_exact':True,'row_series_sha256':sha(series_file),'scan':info,'diagnostics':diagnostics(a,raw)};print(camera,profile,json.dumps(m['outputs'][camera+'-'+profile]),flush=True)
        assert sha(src)==expected
    m['synthetic_verification']=verify_synthetic(out);preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
