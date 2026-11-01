"""Build isolated revision02 still evidence; source and prior outputs remain read-only."""
from pathlib import Path
import base64,hashlib,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from directional import process,masks,PARAMETERS,PROFILES
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study')
OUT=ROOT/'revision-02/still';INPUT=ROOT/'still/round-11';ATLAS=ROOT/'still/round-23'
REFERENCE=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png')
FONT=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14)
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def writejson(p,v):p.write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def black(a):
    rgb=np.floor(a[:,:,:3].astype(np.float32)*(a[:,:,3:4]/255)+.5).astype(np.uint8)
    return Image.fromarray(rgb)
def gray(a,path):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(path)
def panel(images,labels,path,roi=None):
    parts=[im.crop(roi) if roi else im for im in images];w=max(im.width for im in parts);h=max(im.height for im in parts)
    canvas=Image.new('RGB',(w*len(parts),h+48),'white');d=ImageDraw.Draw(canvas)
    for i,(im,label) in enumerate(zip(parts,labels)):
        canvas.paste(im,(i*w,48));d.text((i*w+8,8),label,font=FONT,fill='black')
    canvas.save(path)

def synthetic(atlases):
    folder=OUT/'synthetic';folder.mkdir(exist_ok=True);h,w=192,256;y,x=np.indices((h,w));report={}
    cases={'flat':np.full((h,w),.4),'vertical-wide-step':np.where(x<128,.2,.8),
           'horizontal-wide-step':np.where(y<96,.2,.8)}
    for width in [2,4,6,12]:
        for angle,distance in [('vertical',abs(x-127.5)),('horizontal',abs(y-95.5)),('diagonal',abs(x-y-32)/np.sqrt(2))]:
            for polarity in ['bright','dark']:
                line=distance<width/2
                cases[f'{width}px-{angle}-{polarity}']=np.where(line,.8 if polarity=='bright' else .2,.2 if polarity=='bright' else .8)
    cases['crossing-lines']=np.where((abs(x-128)<3)|(abs(y-96)<2)|(abs(x-y-32)/np.sqrt(2)<2),.8,.2)
    horizontal_core=abs(y-95.5)<2
    diagonal_core=abs(x-y-32)/np.sqrt(2)<2
    cases['wide-step-with-crossing-lines']=np.where(diagonal_core,.05,np.where(horizontal_core,.95,cases['vertical-wide-step']))
    for name,l in cases.items():
        a=np.zeros((h,w,4),np.uint8);a[:,:,:3]=np.floor(l[:,:,None]*255+.5);a[:,:,3]=255
        entry={}
        for profile in PROFILES:
            out,delta,m=process(a,atlases,profile);again,_,_=process(a,atlases,profile)
            changed=np.any(out!=a,axis=2);entry[profile]={'changed_pixels':int(changed.sum()),'max_delta':float(abs(delta).max()),'repeat_exact':bool(np.array_equal(out,again)),
                'protected_RGB_exact':bool(np.array_equal(out[:,:,:3][m['protected']],a[:,:,:3][m['protected']]))}
            assert np.array_equal(out,again)
            assert np.array_equal(out[:,:,:3][m['protected']],a[:,:,:3][m['protected']])
            if name not in ['vertical-wide-step','wide-step-with-crossing-lines']:
                assert not changed.any(),name
            else:assert changed.sum()>500
            if name=='wide-step-with-crossing-lines':
                expected=cv2.dilate((horizontal_core|diagonal_core).astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
                assert not changed[expected].any()
                entry[profile]['known_line_plus2px_exact']=True
            if 'px-' in name:
                before=a[h//2,:,0] if 'horizontal' not in name else a[:,w//2,0]
                after=out[h//2,:,0] if 'horizontal' not in name else out[:,w//2,0]
                def metrics(v):
                    line=(v>(v.min()+int(v.max()))/2) if name.endswith('bright') else (v<(int(v.min())+v.max())/2)
                    return {'contrast':float((int(v.max())-int(v.min()))/255),'FWHM_samples':int(line.sum())}
                entry[profile].update(before=metrics(before),after=metrics(after))
                assert metrics(before)==metrics(after)
            Image.fromarray(out).save(folder/f'{name}-{profile}.png')
        Image.fromarray(a).save(folder/f'{name}-input.png');gray(m['protected'],folder/f'{name}-protected.png')
        report[name]=entry
    # Hidden RGB and transparency are explicitly exercised, not inferred from fixtures.
    a=np.zeros((h,w,4),np.uint8);rng=np.random.default_rng(9);a[:,:,:3]=rng.integers(0,256,(h,w,3),dtype=np.uint8)
    a[16:-16,16:-16,3]=255;a[16:20,16:-16,3]=128
    b,d,m=process(a,atlases);off,_,_=process(a,atlases,enabled=False)
    border=(a[:,:,3]>0)&~cv2.erode((a[:,:,3]>0).astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
    assert np.array_equal(off,a) and np.array_equal(a[:,:,3],b[:,:,3]) and np.array_equal(a[a[:,:,3]==0],b[a[:,:,3]==0]) and np.array_equal(a[border],b[border])
    report['RGBA-fixture']={'OFF_fullRGBA_exact':True,'alpha_exact':True,'hiddenRGB_exact':True,'inner_2px_exact':True}
    writejson(OUT/'synthetic-verification.json',report);return report

def preview(manifest):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8')
    images={}
    for camera in ['B','A','C']:
        for mode in ['base','standard']+(['soft','old23'] if camera=='B' else []):
            f=f'base-{camera}-on-black.png' if mode=='base' else f'{camera}-{mode}-on-black.png'
            images[f'{camera}-{mode}']='data:image/png;base64,'+base64.b64encode((OUT/f).read_bytes()).decode()
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>広い垂直境界だけを加工する比較</title><style>CSS
.controls{display:flex;gap:16px;flex-wrap:wrap}select,button{font:inherit;background:var(--surface);color:var(--ink);padding:8px} .viewport{overflow:auto;max-width:100vw}img{display:block;max-width:none} .fit img{width:min(100%,1280px);height:auto}</style>
<main class="wrap"><h1>広い垂直境界だけを加工する比較</h1><p>元の画素を下地に使います。幅の狭い線とその近くでは追加量を0にします。参照由来の輝度残差には、元の縁の反射や形の影響が残る可能性があります。</p><div class="controls"><select id="camera" aria-label="カメラ"><option>B</option><option>A</option><option>C</option></select><select id="mode" aria-label="加工"><option value="standard">標準</option><option value="soft">軟</option><option value="old23">旧23版・失敗比較</option></select><button id="on">追加ON</button><button id="native">原寸</button></div><p id="name"></p></main><div id="viewport" class="viewport"><img id="image" alt="元画像と垂直境界の追加加工を切り替える比較"></div><script>
const images=IMAGES,c=document.getElementById('camera'),m=document.getElementById('mode'),v=document.getElementById('viewport');let enabled=true,native=true;function show(){for(const o of m.options)o.disabled=c.value!=='B'&&o.value!=='standard';if(c.value!=='B')m.value='standard';const key=c.value+'-'+(enabled?m.value:'base');document.getElementById('image').src=images[key];document.getElementById('name').textContent=key+' / 1280×1030px';document.getElementById('on').textContent=enabled?'追加ON':'追加OFF';v.classList.toggle('fit',!native)}c.onchange=m.onchange=show;document.getElementById('on').onclick=()=>{enabled=!enabled;show()};document.getElementById('native').onclick=()=>{native=!native;show()};show();</script></html>'''
    (OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    OUT.mkdir(exist_ok=True,parents=True);atlases=[];am=json.loads((ATLAS/'atlas-manifest.json').read_text(encoding='utf-8'))
    for item in am['items'][:2]:
        p=ATLAS/item['file'];assert sha(p)==item['sha256'];z=np.load(p);assert set(z.files)=={'residual','u'};atlases.append(z['residual'].astype(np.float32))
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in [HERE/'directional.py',HERE/'build.py',HERE/'prediction.md',ATLAS/'atlas-manifest.json',ATLAS/'atlas-01.npz',ATLAS/'atlas-02.npz']:
        shutil.copyfile(p,source/p.name)
    m={'revision':'02','parameters':PARAMETERS,'profiles':PROFILES,'source_sha256':{p.name:sha(p) for p in source.iterdir()},
       'atlas_RGB_used':False,'raw_RGB_plus_scalar_delta':True,'global_blur':False,'same_ABC_parameters':True,'inputs':{},'outputs':{},
       'limitation':'単一画像由来のscalar残差には元の縁の形や反射が残りうる。細線検出は幅15pxの形態学フィルタで、テクスチャも保守的に保護する。太さのあるコードや、ほかの境界と重なって見える線は完全には分離できない。',
       'reference':{'path':str(REFERENCE),'sha256':sha(REFERENCE)}}
    for camera in ['B','A','C']:
        src=INPUT/f'base-{camera}.png';expected=sha(src);a=np.array(Image.open(src).convert('RGBA'))
        shutil.copyfile(src,OUT/f'base-{camera}.png');shutil.copyfile(src,OUT/f'{camera}-OFF.png');black(a).save(OUT/f'base-{camera}-on-black.png')
        m['inputs'][camera]={'path':str(src),'sha256':expected,'OFF_file_exact':sha(OUT/f'{camera}-OFF.png')==expected}
        for profile in (PROFILES if camera=='B' else ['standard']):
            b,delta,mask=process(a,atlases,profile);again,_,_=process(a,atlases,profile);off,_,_=process(a,atlases,profile,False)
            inner=(a[:,:,3]>0)&~cv2.erode((a[:,:,3]>0).astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
            untouched=delta==0;protected=mask['protected']
            checks=dict(repeat_exact=np.array_equal(b,again),OFF_fullRGBA_exact=np.array_equal(a,off),alpha_exact=np.array_equal(a[:,:,3],b[:,:,3]),hiddenRGBA_exact=np.array_equal(a[a[:,:,3]==0],b[a[:,:,3]==0]),inner2_RGBA_exact=np.array_equal(a[inner],b[inner]),protected_RGBA_exact=np.array_equal(a[protected],b[protected]),zero_delta_RGBA_exact=np.array_equal(a[untouched],b[untouched]))
            assert all(checks.values());file=f'{camera}-{profile}.png';Image.fromarray(b).save(OUT/file);black(b).save(OUT/f'{camera}-{profile}-on-black.png')
            gray(protected,OUT/f'{camera}-{profile}-protected-mask.png');gray(mask['core'],OUT/f'{camera}-{profile}-thin-core.png');gray(mask['peaks'],OUT/f'{camera}-{profile}-vertical-seeds.png');gray(mask['support']&~protected&mask['safe'],OUT/f'{camera}-{profile}-gate-mask.png')
            gray(.5+delta/(2*PROFILES[profile]['delta_max']),OUT/f'{camera}-{profile}-delta-neutral.png');gray(np.minimum(1,abs(delta)/PROFILES[profile]['delta_max']),OUT/f'{camera}-{profile}-delta-magnitude.png')
            np.savez_compressed(OUT/f'{camera}-{profile}-layers.npz',delta=delta,protected=protected,gate=mask['support']&~protected&mask['safe'])
            changed=np.any(b[:,:,:3]!=a[:,:,:3],axis=2)
            m['outputs'][camera+'-'+profile]={'file':file,'sha256':sha(OUT/file),'checks':{k:bool(v) for k,v in checks.items()},'tracks':mask['track_count'],'changed_pixels':int(changed.sum()),'changed_visible_percent':float(changed.sum()/max(1,(a[:,:,3]>0).sum())*100),'protected_visible_percent':float((protected&(a[:,:,3]>0)).sum()/(a[:,:,3]>0).sum()*100),'delta_absmax':float(abs(delta).max()),'delta_absmean_visible':float(abs(delta)[a[:,:,3]>0].mean())}
            for label,roi in {'nativefloor':(680,740,1100,900),'nativeedge':(600,130,840,650),'nativewire':(690,100,1020,520)}.items():
                black(b).crop(roi).save(OUT/f'{camera}-{profile}-{label}.png')
                gray(protected[roi[1]:roi[3],roi[0]:roi[2]],OUT/f'{camera}-{profile}-{label}-protected.png')
            print(camera,profile,json.dumps(m['outputs'][camera+'-'+profile]),flush=True)
        assert sha(src)==expected
    old=ATLAS/'B-standard-on-black.png';shutil.copyfile(old,OUT/'B-old23-on-black.png');m['old23_failure_comparison']={'source':str(old),'sha256':sha(old),'used_as_filter_input':False}
    names=['base-B-on-black.png','B-soft-on-black.png','B-standard-on-black.png','B-old23-on-black.png'];ims=[Image.open(OUT/n).convert('RGB') for n in names]
    for label,roi in {'full':None,'nativefloor':(680,740,1100,900),'nativeedge':(600,130,840,650),'nativewire':(690,100,1020,520)}.items():panel(ims,names,OUT/f'B-comparison-{label}.png',roi)
    m['synthetic']=synthetic(atlases);preview(m);writejson(OUT/'manifest.json',m)
    verification={'inputs_unchanged':all(sha(Path(v['path']))==v['sha256'] for v in m['inputs'].values()),'outputs':{k:v['checks'] for k,v in m['outputs'].items()},'synthetic_case_count':len(m['synthetic']),'all_assertions_passed':True}
    writejson(OUT/'verification.json',verification)
    shutil.copyfile(HERE/'prediction.md',OUT/'prediction.md');print('PASS',json.dumps(verification),flush=True)
if __name__=='__main__':main()
