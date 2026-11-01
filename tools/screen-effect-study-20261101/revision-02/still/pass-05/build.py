from pathlib import Path
import base64,hashlib,importlib.util,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from blend import process
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;SOURCE04=HERE.parent/'pass-04';ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still');OUT=ROOT/'pass-05';RAW=ROOT/'pass-03/base-B.png'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def write(name,data):(OUT/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);sys.modules[name]=m;spec.loader.exec_module(m);return m
def gray(a,file):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(OUT/file)
def black(a):return Image.fromarray(np.floor(a[:,:,:3]*(a[:,:,3:4]/255)+.5).astype(np.uint8))
def longest(v):return max(map(len,''.join('1' if x else '0' for x in v).split('0')),default=0)
def main():
    OUT.mkdir(exist_ok=True,parents=True);frozen={}
    for folder in [ROOT,ROOT/'pass-02',ROOT/'pass-03',ROOT/'pass-04']:
        j=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in j.items());frozen[folder.name]=len(j)
    old=load('effect',SOURCE04/'effect.py');author=load('readonly_author04',SOURCE04/'build.py');raw=np.array(Image.open(RAW));config04=json.loads((SOURCE04/'regions.json').read_text(encoding='utf-8'));roi,spatialprotect,residual,regions=author.authored(raw,config04);render04,info04=old.process(raw,roi,spatialprotect,residual);assert np.array_equal(render04,np.array(Image.open(ROOT/'pass-04/B-pass04.png')))
    config=json.loads((HERE/'core.json').read_text(encoding='utf-8'));core=np.zeros(raw.shape[:2],np.uint8)
    for p in config['core_polygons']:cv2.fillPoly(core,[np.array(p['points'],np.int32)],1)
    for p in config['core_polylines']:cv2.polylines(core,[np.array(p['points'],np.int32)],False,1,p['width'],cv2.LINE_8)
    for p in config['core_complete_ROIs']:
        x0,y0,x1,y1=p['box'];core[y0:y1,x0:x1]=1
    core=core.astype(bool);out,weight=process(raw,render04,core);point05,_=process(raw,info04['base'],core)
    delta=(render04[:,:,:3].astype(np.float32)-raw[:,:,:3].astype(np.float32))*weight[:,:,None];direct=raw.copy();direct[:,:,:3]=np.clip(np.floor(raw[:,:,:3].astype(np.float32)+delta+.5),0,255).astype(np.uint8)
    inner=(raw[:,:,3]>0)&~info04['inside'];checks=dict(OFF_fullRGBA_exact=np.array_equal(raw,process(raw,render04,core,False)[0]),repeat_exact=np.array_equal(out,process(raw,render04,core)[0]),core_fullRGBA_exact=np.array_equal(raw[core],out[core]),weight1_RGBA_equals04=np.array_equal(out[weight==1],render04[weight==1]),same_pixel_delta_formula_exact=np.array_equal(out,direct),alpha_exact=np.array_equal(raw[:,:,3],out[:,:,3]),hiddenRGBA_exact=np.array_equal(raw[raw[:,:,3]==0],out[raw[:,:,3]==0]),inner2_exact=np.array_equal(raw[inner],out[inner]),outside_authored_equals_blended_pointbase=np.array_equal(out[roi==0],point05[roi==0]));assert all(checks.values())
    preserve={}
    for p in config['core_complete_ROIs']:
        x0,y0,x1,y1=p['box'];preserve[p['name']]=bool(np.array_equal(raw[y0:y1,x0:x1],out[y0:y1,x0:x1]));assert preserve[p['name']]
    Image.fromarray(raw).save(OUT/'base-B.png');Image.fromarray(out).save(OUT/'B-pass05.png');black(raw).save(OUT/'base-B-on-black.png');black(out).save(OUT/'B-pass05-on-black.png');gray(core,'raw-preservation-core.png');gray(weight,'mix-weight.png');np.savez_compressed(OUT/'layers.npz',core=core,mix_weight=weight,final_delta_rgb=delta)
    d04=out[:,:,:3].astype(np.float32)-render04[:,:,:3].astype(np.float32);Image.fromarray(np.clip(np.floor(128+d04*.5+.5),0,255).astype(np.uint8)).save(OUT/'04-to05-difference-neutral.png')
    overlay=np.array(black(raw));overlay[core]=(.7*overlay[core]+.3*np.array([0,114,178])).astype(np.uint8);transition=(weight>0)&(weight<1);overlay[transition]=(.6*overlay[transition]+.4*np.array([230,159,0])).astype(np.uint8);Image.fromarray(overlay).save(OUT/'core-transition-overlay.png')
    comparison=[black(raw),Image.open(ROOT/'pass-03/B-authored-on-black.png').convert('RGB'),black(render04),black(out)];labels=['raw','pass03','pass04','pass05'];font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14)
    crops={'full':None,'nativevertical':(595,85,735,650),'nativecable':(735,85,1010,560),'nativefloor':(680,740,1100,900),'nativeright':(935,210,1040,420),'nativecore-seam':(960,460,1060,600),'nativefloor-seam':(160,590,700,670)}
    for name,box in crops.items():
        parts=[im.crop(box) if box else im for im in comparison];w,h=parts[0].size;canvas=Image.new('RGB',(w*4,h+32),'white');d=ImageDraw.Draw(canvas)
        for i,(p,label) in enumerate(zip(parts,labels)):canvas.paste(p,(i*w,32));d.text((i*w+8,4),label,font=font,fill='black')
        canvas.save(OUT/f'comparison-{name}.png');(black(out).crop(box) if box else black(out)).save(OUT/f'B-pass05-{name}.png')
    coverage={};sp05=np.any(out!=point05,axis=2);sp04=np.any(render04!=info04['base'],axis=2)
    for region,mask in regions:
        a=(sp04&(mask>0)).any(axis=1);b=(sp05&(mask>0)).any(axis=1);coverage[region['name']]=dict(selected_rows=int(mask.any(axis=1).sum()),pass04_rows=int(a.sum()),pass05_rows=int(b.sum()),pass04_longest=longest(a),pass05_longest=longest(b))
    profiles=[]
    for center,normal in [((815,267),(.889,.458)),((850,202),(.889,.458)),((906,133),(0,1))]:
        u=np.arange(-10,10.01,.25,dtype=np.float32);mx=(center[0]+u*normal[0])[None,:];my=(center[1]+u*normal[1])[None,:];metrics=[];rgb=[]
        for a in [raw,out]:
            p=cv2.remap(a[:,:,:3].astype(np.float32)/255,mx,my,cv2.INTER_LINEAR)[0];rgb.append(p);v=p@np.array([.2126,.7152,.0722],np.float32);metrics.append(dict(contrast=float(v.max()-v.min()),dark_FWHM_px=float((v<(v.min()+v.max())*.5).sum()*.25)))
        assert np.array_equal(rgb[0],rgb[1]);profiles.append(dict(center=center,before=metrics[0],after=metrics[1],RGB_exact=True))
    diagnostics={}
    for label,box in {'wall':(220,250,500,500),'floor':(680,740,1100,900),'cable':(735,85,1010,560)}.items():
        x0,y0,x1,y1=box;aa=old.luma(raw)[y0:y1,x0:x1];bb=old.luma(out)[y0:y1,x0:x1];ha=aa-cv2.GaussianBlur(aa,(0,0),2);hb=bb-cv2.GaussianBlur(bb,(0,0),2);diagnostics[label]=dict(highpass_RMS_ratio=float(np.sqrt(np.mean(hb*hb))/max(1e-12,np.sqrt(np.mean(ha*ha)))),RGB_RMS=float(np.sqrt(np.mean((out[y0:y1,x0:x1,:3].astype(float)-raw[y0:y1,x0:x1,:3].astype(float))**2))))
    manifest=dict(scope=config['scope'],parameters04_unchanged=old.PARAMETERS,mix='raw RGB + smoothstep(distance_to_core / 12) * (pass04 RGB - raw RGB), same pixel only',input_sha256=sha(RAW),opaque_actual_sha256=sha(OUT/'B-pass05-on-black.png'),RGBA_sha256=sha(OUT/'B-pass05.png'),checks={k:bool(v) for k,v in checks.items()},preserved_ROIs=preserve,coverage=coverage,real_cable_profiles=profiles,diagnostics=diagnostics,visible_changed_percent=float(np.any(out!=raw,axis=2).sum()/(raw[:,:,3]>0).sum()*100),RGB_RMS_visible=float(np.sqrt(np.mean((out[:,:,:3][raw[:,:,3]>0].astype(float)-raw[:,:,:3][raw[:,:,3]>0].astype(float))**2))),old_frozen_artifact_counts_verified=frozen,recomputed04_RGBA_exact=True)
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in HERE.iterdir():
        if p.is_file():shutil.copyfile(p,source/p.name)
    for name in ['effect.py','build.py','regions.json']:shutil.copyfile(SOURCE04/name,source/f'pass04-{name}')
    manifest['source_sha256']={p.name:sha(p) for p in source.iterdir()};write('manifest.json',manifest);write('verification.json',dict(checks=manifest['checks'],preserved_ROIs=preserve,all_pixel_contracts_passed=True,coverage=coverage,coverage_limit='whole nativecable ROI preservation overlaps cloth-right spatial region'))
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={k:'data:image/png;base64,'+base64.b64encode((OUT/f).read_bytes()).decode() for k,f in [('raw','base-B-on-black.png'),('pass05','B-pass05-on-black.png'),('mask','core-transition-overlay.png'),('weight','mix-weight.png')]};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>B専用・コードと床の元画像保持</title><style>CSS .view{overflow:auto}img{display:block;max-width:none}button{font:inherit;padding:8px}</style><main class="wrap"><h1>B専用・コードと床の元画像保持</h1><p>青い範囲は元の画素を完全に保ちます。周囲12pxで同じ画素の加工差分だけを減らします。原RGBをぼかしてはいません。B専用の手動指定です。nativecable全範囲の保持は布右境界の加工とも重なります。</p><button onclick="show('raw')">元</button><button onclick="show('pass05')">試作</button><button onclick="show('mask')">保持範囲</button><button onclick="show('weight')">加工の重み</button></main><div class="view"><img id="image" alt="Bのコードと床を元画素で保持した試作"></div><script>const images=IMAGES;function show(k){document.getElementById('image').src=images[k]}show('pass05');</script></html>''';(OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8');print(json.dumps(manifest,ensure_ascii=False),flush=True)
if __name__=='__main__':main()
