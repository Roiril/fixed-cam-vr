from pathlib import Path
import base64,hashlib,importlib.util,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from layers import process,inside_weight
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;S04=HERE.parent/'pass-04';ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');STILL=ROOT/'revision-02/still';OUT=STILL/'pass-06';ATLAS=ROOT/'still/round-23'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def load(name,path):
    s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);sys.modules[name]=m;s.loader.exec_module(m);return m
def write(name,v):(OUT/name).write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def black(a):return Image.fromarray(np.floor(a[:,:,:3]*(a[:,:,3:4]/255)+.5).astype(np.uint8))
def gray(a,file):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(OUT/file)
def longest(a):return max(map(len,''.join('1' if b else '0' for b in a).split('0')),default=0)
def authored18(raw,config,old):
    h,w=raw.shape[:2];roi=np.zeros((h,w),np.float32);residual=np.zeros((h,w),np.float32);regions=[];point,_=old.point_stage(raw);l=old.luma(point)
    for r in config['regions']:
        p=np.array(r['points'],np.float32);ys=np.arange(int(p[:,1].min()),int(p[:,1].max())+1);centers=np.interp(ys,p[:,1],p[:,0]);mask=np.zeros((h,w),np.float32);atlas=np.load(ATLAS/f"atlas-{r['atlas']+1:02d}.npz")['residual'].astype(np.float32)
        for y,c in zip(ys,centers):
            xs=np.arange(max(0,int(np.floor(c-18))),min(w,int(np.ceil(c+18))+1));normal=xs-c;weight=np.clip((18-abs(normal))/2,0,1).astype(np.float32);mx=(normal*(24/18)+24).astype(np.float32)[None,:];my=np.full_like(mx,float(y%(atlas.shape[0]-1)));wave=cv2.remap(atlas,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)[0];contrast=abs(l[y,min(w-1,int(c+12))]-l[y,max(0,int(c-12))]);mask[y,xs]=weight;residual[y,xs]=wave*contrast;roi[y,xs]=np.maximum(roi[y,xs],weight)
        regions.append((r,mask))
    protect=np.zeros((h,w),np.uint8)
    for p in config['protect_polygons']:cv2.fillPoly(protect,[np.array(p['points'],np.int32)],1)
    for p in config['protect_polylines']:cv2.polylines(protect,[np.array(p['points'],np.int32)],False,1,p['width'],cv2.LINE_8)
    return roi,protect.astype(bool),residual,regions
def profile_evidence(raw,new,linecore,font):
    output=[];canvas=Image.new('RGB',(640,3*200),'white');d=ImageDraw.Draw(canvas)
    for i,(center,normal) in enumerate([((815,267),(.889,.458)),((850,202),(.889,.458)),((906,133),(0,1))]):
        u=np.arange(-10,10.01,.25,dtype=np.float32);mx=(center[0]+u*normal[0])[None,:];my=(center[1]+u*normal[1])[None,:];mask=cv2.remap(linecore.astype(np.uint8),mx,my,cv2.INTER_NEAREST)[0]>0;profiles=[];rgb=[];waves=[]
        for a in [raw,new]:
            p=cv2.remap(a[:,:,:3].astype(np.float32)/255,mx,my,cv2.INTER_LINEAR)[0];rgb.append(p);v=p@np.array([.2126,.7152,.0722],np.float32);waves.append(v);profiles.append(dict(contrast=float(v.max()-v.min()),dark_FWHM_px=float((v<(v.min()+v.max())*.5).sum()*.25)))
        core_rgb_equal=bool(np.array_equal(rgb[0][mask],rgb[1][mask]));entry=dict(center=center,normal=normal,before=profiles[0],after=profiles[1],sampled_core_fraction=float(mask.mean()),core_sample_RGB_exact=core_rgb_equal,background_profile_scope='entire normal profile includes surrounding background; not isolated cable width')
        output.append(entry);y0=i*200;d.text((16,y0+8),f'{center}: gray=raw / black=new; blue=raw-preserved core',font=font,fill='black');xs=40+(u+10)/20*560
        for j in range(len(u)-1):
            if mask[j]:d.rectangle((float(xs[j]),y0+156,float(xs[j+1]+1),y0+172),fill=(0,114,178))
        scale=max(.2,float(max(w.max() for w in waves))*1.1)
        for v,col,width in [(waves[0],(140,140,140),1),(waves[1],(0,0,0),2)]:d.line([(float(x),float(y0+142-100*value/scale)) for x,value in zip(xs,v)],fill=col,width=width)
        d.text((40,y0+176),'-10px',font=font,fill='black');d.text((560,y0+176),'+10px',font=font,fill='black')
    canvas.save(OUT/'cable-profiles-core-vs-background.png');return output
def main():
    OUT.mkdir(exist_ok=True,parents=True);frozen={}
    for folder in [STILL,STILL/'pass-02',STILL/'pass-03',STILL/'pass-04',STILL/'pass-05']:
        j=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in j.items());frozen[folder.name]=len(j)
    old=load('effect',S04/'effect.py');author04=load('readonly_author04',S04/'build.py');raw=np.array(Image.open(STILL/'pass-03/base-B.png'));c04=json.loads((S04/'regions.json').read_text(encoding='utf-8'));r04,p04,a04,_=author04.authored(raw,c04);render04,info04=old.process(raw,r04,p04,a04);assert np.array_equal(render04,np.array(Image.open(STILL/'pass-04/B-pass04.png')))
    config=json.loads((HERE/'authoring.json').read_text(encoding='utf-8'));roi,spatialprotect,residual,regions=authored18(raw,c04,old);linecore=np.zeros(raw.shape[:2],np.uint8)
    for p in config['line_polylines']:cv2.polylines(linecore,[np.array(p['points'],np.int32)],False,1,18,cv2.LINE_8)
    carpet=np.zeros_like(linecore);cv2.fillPoly(carpet,[np.array(config['carpet_polygon'],np.int32)],1);core=(linecore|carpet).astype(bool);surface=np.zeros_like(linecore)
    for p in config['surface_polygons']:cv2.fillPoly(surface,[np.array(p['points'],np.int32)],1)
    sw=inside_weight(surface.astype(bool),20);out,info=process(raw,roi,spatialprotect,residual,core,sw,old);again,_=process(raw,roi,spatialprotect,residual,core,sw,old);point06=process(raw,np.zeros_like(roi),spatialprotect,np.zeros_like(residual),core,sw,old)[0]
    allowed=(sw>0)|(info['line_mix_weight']<1)|(roi>0);inside=info['inside'];checks=dict(OFF_fullRGBA_exact=np.array_equal(raw,process(raw,roi,spatialprotect,residual,core,sw,old,False)[0]),repeat_exact=np.array_equal(out,again),line_core_RGBA_exact=np.array_equal(raw[linecore>0],out[linecore>0]),carpet_RGBA_exact=np.array_equal(raw[carpet>0],out[carpet>0]),alpha_exact=np.array_equal(raw[:,:,3],out[:,:,3]),hidden_exact=np.array_equal(raw[raw[:,:,3]==0],out[raw[:,:,3]==0]),inner2_exact=np.array_equal(raw[(raw[:,:,3]>0)&~inside],out[(raw[:,:,3]>0)&~inside]),outside_authoring_equals04=np.array_equal(out[~allowed],render04[~allowed]),outside_spatial_equals_point06=np.array_equal(out[roi==0],point06[roi==0]));assert all(checks.values())
    Image.fromarray(raw).save(OUT/'base-B.png');Image.fromarray(out).save(OUT/'B-pass06.png');black(raw).save(OUT/'base-B-on-black.png');black(out).save(OUT/'B-pass06-on-black.png');gray(core,'semantic-core.png');gray(linecore,'line-core.png');gray(carpet,'carpet-core.png');gray(info['line_mix_weight'],'line-mix-weight.png');gray(sw,'surface-weight.png');gray(.5+info['spatial_delta']/.32,'vertical-residual-neutral.png')
    for name,delta in [('surface-grain',info['surface_delta_rgb']),('line-protection',info['line_delta_rgb']),('04-to06',out[:,:,:3].astype(np.float32)-render04[:,:,:3].astype(np.float32))]:Image.fromarray(np.clip(np.floor(128+delta*.5+.5),0,255).astype(np.uint8)).save(OUT/f'{name}-delta-neutral.png')
    np.savez_compressed(OUT/'layers.npz',line_core=linecore,carpet=carpet,surface_weight=sw,line_mix_weight=info['line_mix_weight'],spatial_delta=info['spatial_delta'],allowed_authoring=allowed)
    overlay=np.array(black(raw));overlay[sw>0]=(.7*overlay[sw>0]+.3*np.array([0,158,115])).astype(np.uint8);overlay[core]=(.7*overlay[core]+.3*np.array([0,114,178])).astype(np.uint8);overlay[roi>0]=(.65*overlay[roi>0]+.35*np.array([230,159,0])).astype(np.uint8);Image.fromarray(overlay).save(OUT/'authoring-overlay.png');font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14)
    compare=[black(raw),black(render04),Image.open(STILL/'pass-05/B-pass05-on-black.png').convert('RGB'),black(out)];labels=['raw','pass04','pass05','pass06'];crops={'full':None,'nativevertical':(595,85,735,650),'nativewire':(735,85,1010,560),'nativefloor':(680,740,1100,900),'nativeboard':(200,250,500,500),'nativeboard-lower':(180,735,420,860),'nativeright':(935,210,1040,420),'nativeold-seam':(960,460,1060,600),'nativecarpet-edge':(480,600,770,945)}
    for name,box in crops.items():
        parts=[p.crop(box) if box else p for p in compare];w,h=parts[0].size;canvas=Image.new('RGB',(w*4,h+32),'white');d=ImageDraw.Draw(canvas)
        for i,(p,label) in enumerate(zip(parts,labels)):canvas.paste(p,(i*w,32));d.text((i*w+8,4),label,font=font,fill='black')
        canvas.save(OUT/f'comparison-{name}.png');(black(out).crop(box) if box else black(out)).save(OUT/f'B-pass06-{name}.png')
    coverage={};spchange=np.any(out!=point06,axis=2)
    for r,mask in regions:
        rows=(spchange&(mask>0)).any(axis=1);coverage[r['name']]=dict(selected_rows=int(mask.any(axis=1).sum()),spatial_changed_rows=int(rows.sum()),longest_span=longest(rows))
    profiles=profile_evidence(raw,out,linecore,font);connections={}
    for threshold in [.04,.08,.12,.20]:
        a=(old.luma(raw)<threshold)&(linecore>0);b=(old.luma(out)<threshold)&(linecore>0);assert np.array_equal(a,b);na=cv2.connectedComponents(a.astype(np.uint8),connectivity=8)[0]-1;nb=cv2.connectedComponents(b.astype(np.uint8),connectivity=8)[0]-1;connections[str(threshold)]=dict(raw_components=int(na),new_components=int(nb),dark_core_mask_exact=True,scope='within full hand-traced line core, not isolated full-scene segmentation')
    diagnostics={}
    for name,box in {'upper-board':[220,250,500,500],'lower-board':[190,745,400,850],'carpet':[680,740,1100,900],'wire-environment':[735,85,1010,560]}.items():
        x0,y0,x1,y1=box;values=[]
        for a in [raw,render04,out]:
            l=old.luma(a)[y0:y1,x0:x1];hp=l-cv2.GaussianBlur(l,(0,0),2);values.append(float(np.sqrt(np.mean(hp*hp))))
        diagnostics[name]=dict(raw_highpass_RMS=values[0],pass04_highpass_RMS=values[1],pass06_highpass_RMS=values[2],new_to_raw_ratio=values[2]/values[0],new_to04_ratio=values[2]/values[1])
    manifest=dict(scope=config['scope'],parameters04=old.PARAMETERS,authoring=config,checks={k:bool(v) for k,v in checks.items()},old_frozen_counts_verified=frozen,coverage=coverage,profiles=profiles,core_connectivity=connections,diagnostics=diagnostics,opaque_actual_sha256=sha(OUT/'B-pass06-on-black.png'),RGBA_sha256=sha(OUT/'B-pass06.png'),changed_visible_percent=float(np.any(out!=raw,axis=2).sum()/(raw[:,:,3]>0).sum()*100),core_pixels=int(core.sum()),surface_pixels=int((sw>0).sum()),outside_authoring_change_count=int(np.any(out!=render04,axis=2)[~allowed].sum()),limitations='B-only semantic authoring. Whole normal-profile contrast/FWHM include background and must be distinguished from exact core line pixels.')
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in HERE.iterdir():
        if p.is_file():shutil.copyfile(p,source/p.name)
    for name in ['effect.py','build.py','regions.json']:shutil.copyfile(S04/name,source/f'pass04-{name}')
    manifest['source_sha256']={p.name:sha(p) for p in source.iterdir()};write('manifest.json',manifest);write('verification.json',dict(checks=manifest['checks'],coverage=coverage,diagnostics=diagnostics,profiles=profiles))
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={k:'data:image/png;base64,'+base64.b64encode((OUT/f).read_bytes()).decode() for k,f in [('raw','base-B-on-black.png'),('pass06','B-pass06-on-black.png'),('mask','authoring-overlay.png'),('surface','surface-grain-delta-neutral.png'),('line','line-protection-delta-neutral.png'),('vertical','vertical-residual-neutral.png')]};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>B専用・実線と床に沿う保護</title><style>CSS .view{overflow:auto}img{display:block;max-width:none}button{font:inherit;padding:8px}</style><main class="wrap"><h1>B専用・実線と床に沿う保護</h1><p>コードと金属は幅18pxの手描き線。カーペットは実際の斜めの形に沿う多角形。左板の無地の内部だけ粒を抑えます。青は元画素を保つ範囲。緑は無地の内部。橙は広い縦境界の加工範囲です。参照の表示幅に合わせ、縦境界の横support半径を18pxにしています。B専用の手動試作です。</p><button onclick="show('raw')">元</button><button onclick="show('pass06')">試作</button><button onclick="show('mask')">範囲</button><button onclick="show('surface')">面の粒の差分</button><button onclick="show('line')">線の保護の差分</button><button onclick="show('vertical')">縦境界の差分</button></main><div class="view"><img id="image" alt="B専用の実線と床に沿う保護"></div><script>const images=IMAGES;function show(k){document.getElementById('image').src=images[k]}show('pass06');</script></html>''';(OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8');print(json.dumps(manifest,ensure_ascii=False),flush=True)
if __name__=='__main__':main()
