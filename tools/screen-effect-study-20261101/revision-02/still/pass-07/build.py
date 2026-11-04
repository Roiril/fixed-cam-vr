from pathlib import Path
import ast,base64,hashlib,importlib.util,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from effect07 import process,sharpen_target,outside_weight
from authored18 import authored18
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;STILL=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still');OUT=STILL/'pass-07';S04=HERE.parent/'pass-04';FONT=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14)
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def write(name,v):(OUT/name).write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def load(name,path):
    s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);sys.modules[name]=m;s.loader.exec_module(m);return m
def black(a):return Image.fromarray(np.floor(a[:,:,:3]*(a[:,:,3:4]/255)+.5).astype(np.uint8))
def gray(a,name):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(OUT/name)
def longest(v):return max(map(len,''.join('1' if x else '0' for x in v).split('0')),default=0)
def core_profiles(raw,new,linecore):
    results=[];canvas=Image.new('RGB',(640,600),'white');d=ImageDraw.Draw(canvas)
    for i,(center,normal) in enumerate([((815,267),(.889,.458)),((850,202),(.889,.458)),((906,133),(0,1))]):
        u=np.arange(-10,10.01,.25,dtype=np.float32);mx=(center[0]+u*normal[0])[None,:];my=(center[1]+u*normal[1])[None,:];mask=cv2.remap(linecore.astype(np.float32),mx,my,cv2.INTER_LINEAR)[0]>.999;metrics=[];waves=[]
        for a in [raw,new]:
            v=cv2.remap(a[:,:,:3].astype(np.float32)/255,mx,my,cv2.INTER_LINEAR)[0]@np.array([.2126,.7152,.0722],np.float32);waves.append(v);metrics.append(dict(contrast=float(v.max()-v.min()),dark_FWHM_px=float((v<(v.min()+v.max())*.5).sum()*.25),min=float(v.min()),max=float(v.max())))
        results.append(dict(center=center,before=metrics[0],after=metrics[1],contrast_ratio=metrics[1]['contrast']/metrics[0]['contrast'],FWHM_change=metrics[1]['dark_FWHM_px']-metrics[0]['dark_FWHM_px'],core_sample_fraction=float(mask.mean()),scope='whole profile includes neighboring background; blue plot band identifies sharpened line core'))
        y0=i*200;d.text((16,y0+8),f'{center}: gray=raw / black=new / blue=sharpened core',font=FONT,fill='black');xs=40+(u+10)/20*560
        for j in range(len(u)-1):
            if mask[j]:d.rectangle((float(xs[j]),y0+156,float(xs[j+1]+1),y0+172),fill=(0,114,178))
        scale=max(.2,max(float(v.max()) for v in waves)*1.1)
        for v,col,width in [(waves[0],(140,140,140),1),(waves[1],(0,0,0),2)]:d.line([(float(x),float(y0+142-100*value/scale)) for x,value in zip(xs,v)],fill=col,width=width)
        d.text((40,y0+176),'-10px',font=FONT,fill='black');d.text((560,y0+176),'+10px',font=FONT,fill='black')
    canvas.save(OUT/'real-line-profiles.png');return results
def synthetic(old):
    h,w=192,256;y,x=np.indices((h,w));results={};folder=OUT/'synthetic';folder.mkdir(exist_ok=True)
    for width in [2,4,6,12]:
        for name,angle in [('vertical',0),('horizontal',np.pi/2),('diagonal',np.pi/4)]:
            for polarity in ['bright','dark']:
                distance=abs((x-127.5)*np.cos(angle)+(y-95.5)*np.sin(angle));l=np.where(distance<width/2,.7 if polarity=='bright' else .2,.2 if polarity=='bright' else .7).astype(np.float32);l=cv2.GaussianBlur(l,(0,0),.75);raw=np.zeros((h,w,4),np.uint8);raw[:,:,:3]=np.floor(l[:,:,None]*255+.5);raw[:,:,3]=255;inside=np.ones((h,w),bool);case=f'{width}px-{name}-{polarity}';entry={}
                u=np.arange(-24,24.01,.125,dtype=np.float32);mx=(127.5+u*np.cos(angle)).astype(np.float32)[None,:];my=(95.5+u*np.sin(angle)).astype(np.float32)[None,:]
                def metric(a):
                    v=cv2.remap(old.luma(a),mx,my,cv2.INTER_LINEAR)[0];background=np.median(v[abs(u)>18]);p=(v-background)*(1 if polarity=='bright' else -1);height=float(p.max());return dict(contrast=height,FWHM_px=float((p>height*.5).sum()*.125),min=float(v.min()),max=float(v.max()))
                for target,sigma,gain in [('wire',.9,1),('floor',1.1,.75)]:
                    out,_=sharpen_target(raw,sigma,gain,old,inside);before,after=metric(raw),metric(out);entry[target]=dict(before=before,after=after,contrast_ratio=after['contrast']/before['contrast'],FWHM_change=after['FWHM_px']-before['FWHM_px']);Image.fromarray(out).save(folder/f'{case}-{target}.png')
                results[case]=entry
    write('synthetic-line-measurements.json',results);return results
def main():
    OUT.mkdir(exist_ok=True,parents=True);frozen={}
    for folder in [STILL,*[STILL/f'pass-{i:02d}' for i in range(2,7)]]:
        j=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in j.items());frozen[folder.name]=len(j)
    old=load('effect',S04/'effect.py');a04=load('readonly_author04',S04/'build.py');c04=json.loads((S04/'regions.json').read_text(encoding='utf-8'));raw=np.array(Image.open(STILL/'pass-03/base-B.png'));roi,spatialprotect,residual,regions=authored18(raw,c04,old);config=json.loads((HERE/'authoring.json').read_text(encoding='utf-8'));linecore=np.zeros(raw.shape[:2],np.uint8)
    for p in config['line_polylines']:cv2.polylines(linecore,[np.array(p['points'],np.int32)],False,1,18,cv2.LINE_8)
    carpet=np.zeros_like(linecore);cv2.fillPoly(carpet,[np.array(config['carpet_polygon'],np.int32)],1);surface=np.zeros_like(linecore)
    for p in config['surface_polygons']:cv2.fillPoly(surface,[np.array(p['points'],np.int32)],1)
    distance=cv2.distanceTransform(surface,cv2.DIST_L2,cv2.DIST_MASK_PRECISE);t=np.clip(distance/20,0,1);sw=t*t*(3-2*t);sw[surface==0]=0;linecore=linecore.astype(bool);carpet=carpet.astype(bool)
    out,info=process(raw,roi,spatialprotect,residual,linecore,carpet,sw,old);point=process(raw,np.zeros_like(roi),spatialprotect,np.zeros_like(residual),linecore,carpet,sw,old)[0];old06=np.array(Image.open(STILL/'pass-06/B-pass06.png'));data06=np.load(STILL/'pass-06/layers.npz');assert np.array_equal(info['spatial_delta'],data06['spatial_delta']);assert np.array_equal(linecore,data06['line_core'].astype(bool)) and np.array_equal(carpet,data06['carpet'].astype(bool))
    allowed=(sw>0)|(info['wire_weight']>0)|(info['floor_weight']>0);inside=info['inside'];checks=dict(OFF_exact=np.array_equal(raw,process(raw,roi,spatialprotect,residual,linecore,carpet,sw,old,False)[0]),repeat_exact=np.array_equal(out,process(raw,roi,spatialprotect,residual,linecore,carpet,sw,old)[0]),wire_core_equals_LUT_free_target=np.array_equal(out[linecore],info['wire_target'][linecore]),carpet_core_equals_LUT_free_target=np.array_equal(out[carpet],info['floor_target'][carpet]),alpha_exact=np.array_equal(raw[:,:,3],out[:,:,3]),hidden_exact=np.array_equal(raw[raw[:,:,3]==0],out[raw[:,:,3]==0]),inner2_exact=np.array_equal(raw[(raw[:,:,3]>0)&~inside],out[(raw[:,:,3]>0)&~inside]),outside_authoring_equals06=np.array_equal(out[~allowed],old06[~allowed]),outside_spatial_equals_point=np.array_equal(out[roi==0],point[roi==0]));assert all(checks.values())
    Image.fromarray(raw).save(OUT/'base-B.png');Image.fromarray(out).save(OUT/'B-pass07.png');black(raw).save(OUT/'base-B-on-black.png');black(out).save(OUT/'B-pass07-on-black.png');gray(sw,'surface-weight.png');gray(linecore,'line-core.png');gray(carpet,'carpet-core.png');gray(.5+info['spatial_delta']/.32,'vertical-delta-neutral.png')
    for name,delta in [('surface',info['surface_delta']),('wire-sharpen',info['wire_delta']),('floor-sharpen',info['floor_delta']),('06-to07',out[:,:,:3].astype(np.float32)-old06[:,:,:3].astype(np.float32))]:Image.fromarray(np.clip(np.floor(128+delta*.5+.5),0,255).astype(np.uint8)).save(OUT/f'{name}-delta-neutral.png')
    np.savez_compressed(OUT/'layers.npz',surface_weight=sw,line_core=linecore,carpet=carpet,spatial_delta=info['spatial_delta'],wire_weight=info['wire_weight'],floor_weight=info['floor_weight'],allowed=allowed)
    overlay=np.array(black(raw));overlay[sw>0]=(.7*overlay[sw>0]+.3*np.array([0,158,115])).astype(np.uint8);overlay[linecore|carpet]=(.7*overlay[linecore|carpet]+.3*np.array([0,114,178])).astype(np.uint8);overlay[roi>0]=(.65*overlay[roi>0]+.35*np.array([230,159,0])).astype(np.uint8);Image.fromarray(overlay).save(OUT/'semantic-overlay.png')
    actual04=np.array(Image.open(STILL/'pass-04/B-pass04.png'));comparison=[black(raw),black(actual04),black(old06),black(out)];labels=['raw','pass04','pass06','pass07'];crops={'full':None,'nativeboard':(200,250,500,500),'nativeboard-lower':(180,735,420,860),'nativecable':(735,85,1010,560),'nativefloor':(680,740,1100,900),'nativevertical':(595,85,735,650),'nativecarpet-transition':(480,600,770,945),'nativeboard-transition':(100,540,650,740)}
    for name,box in crops.items():
        parts=[p.crop(box) if box else p for p in comparison];w,h=parts[0].size;canvas=Image.new('RGB',(w*4,h+32),'white');d=ImageDraw.Draw(canvas)
        for i,(p,label) in enumerate(zip(parts,labels)):canvas.paste(p,(i*w,32));d.text((i*w+8,4),label,font=FONT,fill='black')
        canvas.save(OUT/f'comparison-{name}.png');(black(out).crop(box) if box else black(out)).save(OUT/f'B-pass07-{name}.png')
    coverage={};changedsp=np.any(out!=point,axis=2)
    for r,mask in regions:
        rows=(changedsp&(mask>0)).any(axis=1);coverage[r['name']]=dict(selected_rows=int(mask.any(axis=1).sum()),changed_rows=int(rows.sum()),longest_span=longest(rows))
    profiles=core_profiles(raw,out,linecore);connectivity={}
    for threshold in [.04,.08,.12,.20]:
        a=(old.luma(raw)<threshold)&linecore;b=(old.luma(out)<threshold)&linecore;na=cv2.connectedComponents(a.astype(np.uint8),connectivity=8)[0]-1;nb=cv2.connectedComponents(b.astype(np.uint8),connectivity=8)[0]-1;connectivity[str(threshold)]=dict(raw_components=int(na),new_components=int(nb),raw_dark_pixels=int(a.sum()),new_dark_pixels=int(b.sum()),lost_dark_pixels=int((a&~b).sum()),scope='whole traced line core threshold mask; count changes can include contrast/noise and are not semantic break detection')
    diagnostics={}
    for name,box in {'upper-board':[220,250,500,500],'lower-board':[190,745,400,850],'carpet':[680,740,1100,900]}.items():
        x0,y0,x1,y1=box;values=[]
        for a in [raw,old06,out]:
            l=old.luma(a)[y0:y1,x0:x1];hp=l-cv2.GaussianBlur(l,(0,0),2);values.append(float(np.sqrt(np.mean(hp*hp))))
        diagnostics[name]=dict(raw_highpass_RMS=values[0],pass06_highpass_RMS=values[1],pass07_highpass_RMS=values[2],new_to_raw=values[2]/values[0],new_to06=values[2]/values[1])
    tests=synthetic(old);manifest=dict(scope=config['scope'],authoring=config,parameters04_unchanged=old.PARAMETERS,checks={k:bool(v) for k,v in checks.items()},coverage=coverage,real_profiles=profiles,connectivity=connectivity,diagnostics=diagnostics,synthetic_max_FWHM_increase=max(v[t]['FWHM_change'] for v in tests.values() for t in ['wire','floor']),wire_core_gamut_clipped_pixels=int((info['wire_clipping']&linecore).sum()),floor_core_gamut_clipped_pixels=int((info['floor_clipping']&carpet).sum()),opaque_actual_sha256=sha(OUT/'B-pass07-on-black.png'),RGBA_sha256=sha(OUT/'B-pass07.png'),outside_authoring_change_count=int(np.any(out!=old06,axis=2)[~allowed].sum()),old_frozen_counts_verified=frozen,limitations='Code RGB exact from06 is not carried over. Whole profiles include background. Dark object weak streaks receive no new treatment.')
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in HERE.iterdir():
        if p.is_file():shutil.copyfile(p,source/p.name)
    for name in ['effect.py','regions.json']:shutil.copyfile(S04/name,source/f'pass04-{name}')
    manifest['source_sha256']={p.name:sha(p) for p in source.iterdir()};write('manifest.json',manifest);write('verification.json',dict(checks=manifest['checks'],coverage=coverage,real_profiles=profiles,diagnostics=diagnostics))
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={k:'data:image/png;base64,'+base64.b64encode((OUT/f).read_bytes()).decode() for k,f in [('raw','base-B-on-black.png'),('pass07','B-pass07-on-black.png'),('mask','semantic-overlay.png'),('surface','surface-delta-neutral.png'),('wire','wire-sharpen-delta-neutral.png'),('floor','floor-sharpen-delta-neutral.png'),('vertical','vertical-delta-neutral.png')]};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>B専用・板面と細線を分ける試作</title><style>CSS .view{overflow:auto}img{display:block;max-width:none}button{font:inherit;padding:8px}</style><main class="wrap"><h1>B専用・板面と細線を分ける試作</h1><p>手描きの板面内部だけ粒を抑えます。コードとカーペットにはLUTを通さず、それぞれ指定のunsharpだけを使います。細線のRGBが元と完全一致するという旧06の主張は引き継ぎません。縦境界のsupport18pxとgainは06のままです。B専用semantic mask試作です。</p><button onclick="show('raw')">元</button><button onclick="show('pass07')">試作</button><button onclick="show('mask')">範囲</button><button onclick="show('surface')">板面</button><button onclick="show('wire')">コード</button><button onclick="show('floor')">床</button><button onclick="show('vertical')">縦境界</button></main><div class="view"><img id="image" alt="板面とコードと床を分けたB専用試作"></div><script>const images=IMAGES;function show(k){document.getElementById('image').src=images[k]}show('pass07');</script></html>''';(OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8');print(json.dumps(manifest,ensure_ascii=False),flush=True)
if __name__=='__main__':main()
