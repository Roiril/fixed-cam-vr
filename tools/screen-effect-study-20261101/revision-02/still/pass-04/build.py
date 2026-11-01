from pathlib import Path
import base64,hashlib,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from effect import process,point_stage,luma,tone,PARAMETERS
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study');OUT=ROOT/'revision-02/still/pass-04';BASE=ROOT/'still/round-11/base-B.png';ATLAS=ROOT/'still/round-23'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def write(name,data):(OUT/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
def gray(a,path):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(OUT/path)
def black(a):return Image.fromarray(np.floor(a[:,:,:3]*(a[:,:,3:4]/255)+.5).astype(np.uint8))
def longest(v):return max(map(len,''.join('1' if r else '0' for r in v).split('0')),default=0)
def authored(raw,config):
    h,w=raw.shape[:2];roi=np.zeros((h,w),np.float32);residual=np.zeros((h,w),np.float32);masks=[];base,_=point_stage(raw);l=luma(base)
    for region in config['regions']:
        points=np.array(region['points'],np.float32);ys=np.arange(int(points[:,1].min()),int(points[:,1].max())+1);cx=np.interp(ys,points[:,1],points[:,0]);mask=np.zeros((h,w),np.float32);atlas=np.load(ATLAS/f"atlas-{region['atlas']+1:02d}.npz")['residual'].astype(np.float32)
        for y,center in zip(ys,cx):
            xs=np.arange(max(0,int(np.floor(center-10))),min(w,int(np.ceil(center+10))+1));normal=xs-center;weight=np.clip((10-abs(normal))/2,0,1).astype(np.float32);mx=(normal*2.4+24).astype(np.float32)[None,:];my=np.full_like(mx,float(y%(atlas.shape[0]-1)));wave=cv2.remap(atlas,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)[0];contrast=abs(l[y,min(w-1,int(center+12))]-l[y,max(0,int(center-12))]);mask[y,xs]=weight;residual[y,xs]=wave*contrast;roi[y,xs]=np.maximum(roi[y,xs],weight)
        masks.append((region,mask))
    protect=np.zeros((h,w),np.uint8)
    for p in config['protect_polygons']:cv2.fillPoly(protect,[np.array(p['points'],np.int32)],1)
    for p in config['protect_polylines']:cv2.polylines(protect,[np.array(p['points'],np.int32)],False,1,p['width'],cv2.LINE_8)
    return roi,protect.astype(bool),residual,masks
def line_profile(a,angle,polarity):
    u=np.arange(-24,24.01,.125,dtype=np.float32);cx,cy=127.5,95.5;mx=(cx+u*np.cos(angle)).astype(np.float32)[None,:];my=(cy+u*np.sin(angle)).astype(np.float32)[None,:]
    v=cv2.remap(luma(a),mx,my,cv2.INTER_LINEAR)[0];background=np.median(v[abs(u)>18]);height=(v-background)*(1 if polarity=='bright' else -1);contrast=float(height.max());above=height>contrast*.5
    return dict(contrast=contrast,FWHM_px=float(above.sum()*.125))
def synthetic():
    folder=OUT/'synthetic';folder.mkdir(exist_ok=True);h,w=192,256;y,x=np.indices((h,w));tests={};failures=[]
    for width in [2,4,6,12]:
        for name,angle in [('vertical',0),('horizontal',np.pi/2),('diagonal',np.pi/4)]:
            distance=abs((x-127.5)*np.cos(angle)+(y-95.5)*np.sin(angle))
            for polarity in ['bright','dark']:
                fixture=f'{width}px-{name}-{polarity}';low,high=(.2,.7) if polarity=='bright' else (.7,.2);l=np.where(distance<width/2,high,low).astype(np.float32);l=cv2.GaussianBlur(l,(0,0),.75)
                a=np.zeros((h,w,4),np.uint8);a[:,:,:3]=np.floor(l[:,:,None]*255+.5);a[:,:,3]=255;zero=np.zeros((h,w),np.float32)
                b,info=process(a,zero,zero.astype(bool),zero);before=line_profile(a,angle,polarity);after=line_profile(b,angle,polarity);entry=dict(before=before,after=after,contrast_ratio=after['contrast']/before['contrast'],FWHM_change=after['FWHM_px']-before['FWHM_px'],FWHM_nonincrease_pass=after['FWHM_px']<=before['FWHM_px']+1e-7,OFF_exact=bool(np.array_equal(a,process(a,zero,zero.astype(bool),zero,False)[0])),repeat_exact=bool(np.array_equal(b,process(a,zero,zero.astype(bool),zero)[0])),spatial_delta_zero=not bool(info['spatial_delta'].any()))
                if not entry['FWHM_nonincrease_pass']:failures.append(fixture)
                tests[fixture]=entry;Image.fromarray(a).save(folder/f'{fixture}-input.png');Image.fromarray(b).save(folder/f'{fixture}-output.png')
    for name,l in [('flat',np.full((h,w),.4)),('vertical-step',np.where(x<128,.2,.7)),('horizontal-step',np.where(y<96,.2,.7)),('negative-step',np.where(x<128,.7,.2))]:
        a=np.zeros((h,w,4),np.uint8);a[:,:,:3]=np.floor(l[:,:,None]*255+.5);a[:,:,3]=255;zero=np.zeros((h,w),np.float32);b,info=process(a,zero,zero.astype(bool),zero)
        entry=dict(OFF_exact=bool(np.array_equal(a,process(a,zero,zero.astype(bool),zero,False)[0])),repeat_exact=bool(np.array_equal(b,process(a,zero,zero.astype(bool),zero)[0])),spatial_zero=not bool(info['spatial_delta'].any()))
        if name=='flat':entry['tone_only_exact']=bool(np.array_equal(b,info['tone_only']));assert entry['tone_only_exact']
        tests[name]=entry;Image.fromarray(a).save(folder/f'{name}-input.png');Image.fromarray(b).save(folder/f'{name}-output.png')
    a=np.random.default_rng(9).integers(0,256,(h,w,4),dtype=np.uint8);a[:,:,3]=0;a[16:-16,16:-16,3]=255;zero=np.zeros((h,w),np.float32);b,info=process(a,zero,zero.astype(bool),zero);inner=(a[:,:,3]>0)&~info['inside'];checks=dict(alpha_exact=bool(np.array_equal(a[:,:,3],b[:,:,3])),hidden_exact=bool(np.array_equal(a[a[:,:,3]==0],b[a[:,:,3]==0])),inner2_exact=bool(np.array_equal(a[inner],b[inner])));assert all(checks.values());tests['RGBA']=checks
    curve=tone(np.linspace(-.5,1.5,4097,dtype=np.float32));assert np.all(np.diff(curve)>=0);tests['tone_order']={'monotone':True,'sample_count':4097}
    write('synthetic-verification.json',dict(tests=tests,FWHM_increase_failures=failures,all_FWHM_nonincrease=not bool(failures)));return tests,failures
def main():
    OUT.mkdir(exist_ok=True,parents=True);config=json.loads((HERE/'regions.json').read_text(encoding='utf-8'));raw=np.array(Image.open(BASE).convert('RGBA'));roi,protect,residual,regions=authored(raw,config);out,info=process(raw,roi,protect,residual);spatial=info['spatial_delta'];base=info['base'];inside=info['inside'];zero=roi==0
    checks=dict(OFF_fullRGBA_exact=np.array_equal(raw,process(raw,roi,protect,residual,False)[0]),repeat_exact=np.array_equal(out,process(raw,roi,protect,residual)[0]),alpha_exact=np.array_equal(raw[:,:,3],out[:,:,3]),hiddenRGBA_exact=np.array_equal(raw[raw[:,:,3]==0],out[raw[:,:,3]==0]),inner2_exact=np.array_equal(raw[(raw[:,:,3]>0)&~inside],out[(raw[:,:,3]>0)&~inside]),outside_authored_spatial_zero=not spatial[zero].any(),protected_spatial_zero=not spatial[protect].any(),outside_authored_RGBA_equals_pointbase=np.array_equal(out[zero],base[zero]),protected_RGBA_equals_pointbase=np.array_equal(out[protect],base[protect]));assert all(checks.values())
    images={'base-B':raw,'B-pass04':out,'B-pointbase':base,'B-sharp-only':info['sharp_only'],'B-tone-only':info['tone_only']}
    for name,a in images.items():Image.fromarray(a).save(OUT/f'{name}.png');black(a).save(OUT/f'{name}-on-black.png')
    gray(.5+spatial/.32,'spatial-delta-neutral.png');gray(.5+info['point_delta']*.5,'point-delta-neutral.png');gray(info['gate'],'detail-gate.png');gray(roi,'region-mask.png');gray(protect,'spatial-protect-mask.png');np.savez_compressed(OUT/'layers.npz',spatial_delta=spatial,point_delta=info['point_delta'],region=roi,protect=protect)
    overlay=np.array(black(raw));overlay[roi>0]=(.6*overlay[roi>0]+np.array([230,159,0])*.4).astype(np.uint8);overlay[protect]=(.72*overlay[protect]+np.array([0,114,178])*.28).astype(np.uint8);Image.fromarray(overlay).save(OUT/'authored-overlay.png')
    overlayim=Image.fromarray(overlay);draw=ImageDraw.Draw(overlayim);font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14)
    for region in config['regions']:
        x,y=region['points'][0];label={'wall-board-vertical':'wall / board','clothing-left-vertical':'cloth-left','cloth-right-above-metal-crossbar':'cloth-right'}[region['name']];draw.text((x+12,y),label,fill='white',font=font)
    draw.text((760,470),'blue: cable / metal; spatial delta OFF',fill='white',font=font);draw.text((160,680),'blue: floor; spatial delta OFF',fill='white',font=font);overlayim.save(OUT/'authored-overlay-labelled.png')
    old03=np.array(Image.open(ROOT/'revision-02/still/pass-03/B-authored.png'));old23=Image.open(ATLAS/'B-standard-on-black.png').convert('RGB');comparison=[black(raw),black(old03),black(out),old23];names=['raw','pass03','pass04','old23'];crops={'full':None,'nativevertical':(595,85,735,650),'nativecable':(735,85,1010,560),'nativefloor':(680,740,1100,900),'nativeright':(935,210,1040,420)}
    for name,box in crops.items():
        pieces=[im.crop(box) if box else im for im in comparison];w,h=pieces[0].size;canvas=Image.new('RGB',(w*4,h+32),'white');d=ImageDraw.Draw(canvas)
        for i,(p,label) in enumerate(zip(pieces,names)):canvas.paste(p,(w*i,32));d.text((w*i+8,4),label,font=font,fill='black')
        canvas.save(OUT/f'comparison-{name}.png');(black(out).crop(box) if box else black(out)).save(OUT/f'B-pass04-{name}.png')
    coverage={};spchange=np.any(out!=base,axis=2)
    for region,mask in regions:
        selected=mask.any(axis=1);rows=(spchange&(mask>0)).any(axis=1);coverage[region['name']]=dict(selected_rows=int(selected.sum()),spatial_changed_rows=int(rows.sum()),longest_span=longest(rows))
    diagnostics={}
    for label,box in {'wall':(220,250,500,500),'floor':(680,740,1100,900),'cable':(735,85,1010,560)}.items():
        x0,y0,x1,y1=box;aa=luma(raw)[y0:y1,x0:x1];bb=luma(out)[y0:y1,x0:x1];ha=aa-cv2.GaussianBlur(aa,(0,0),2);hb=bb-cv2.GaussianBlur(bb,(0,0),2);diagnostics[label]=dict(raw_highpass_RMS=float(np.sqrt(np.mean(ha*ha))),new_highpass_RMS=float(np.sqrt(np.mean(hb*hb))),highpass_RMS_ratio=float(np.sqrt(np.mean(hb*hb))/max(1e-12,np.sqrt(np.mean(ha*ha)))),raw_white_gt99=int((aa>.99).sum()),new_white_gt99=int((bb>.99).sum()))
    tests,failures=synthetic();visible=raw[:,:,3]>0;manifest=dict(scope='B-only authored spatial regions; global specified unsharp and monotone tone',different_from_pass03=True,parameters=PARAMETERS,input_sha256=sha(BASE),output_sha256=sha(OUT/'B-pass04.png'),checks={k:bool(v) for k,v in checks.items()},coverage=coverage,point_gamut_clipped_pixels=int(info['point_gamut_clip'].sum()),spatial_gamut_clipped_pixels=int(info['spatial_gamut_clip'].sum()),gamut_clipping='final common-luma RGB additions clipped independently to [0,1]; hue may change at gamut limits',visible_changed_percent=float(np.any(out!=raw,axis=2).sum()/visible.sum()*100),RGB_RMS_visible=float(np.sqrt(np.mean((out[:,:,:3][visible].astype(float)-raw[:,:,:3][visible].astype(float))**2))),diagnostics=diagnostics,synthetic_FWHM_failures=failures,all_numeric_checks_passed=not bool(failures))
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in [*HERE.iterdir(),ATLAS/'atlas-01.npz',ATLAS/'atlas-02.npz',ATLAS/'atlas-manifest.json']:
        if p.is_file():shutil.copyfile(p,source/p.name)
    manifest['source_sha256']={p.name:sha(p) for p in source.iterdir()};write('manifest.json',manifest);write('verification.json',dict(checks=manifest['checks'],synthetic_FWHM_failures=failures,all_numeric_checks_passed=not bool(failures)))
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');embedded={k:'data:image/png;base64,'+base64.b64encode((OUT/f).read_bytes()).decode() for k,f in [('raw','base-B-on-black.png'),('pass04','B-pass04-on-black.png'),('sharp','B-sharp-only-on-black.png'),('tone','B-tone-only-on-black.png'),('mask','authored-overlay-labelled.png')]};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>B専用・明暗と細部の試作</title><style>CSS .view{overflow:auto}img{max-width:none;display:block}button{font:inherit;padding:8px}</style><main class="wrap"><h1>B専用・明暗と細部の試作</h1><p>全域のunsharpと単調LUTに加え、手で指定した広い縦境界だけへ横方向の加工を追加します。青いコード・金属・床の範囲は空間加工だけを止めます。明暗処理は適用します。最終RGBは色域でclipします。固定Bの比較であり、汎用の線認識ではありません。</p><button onclick="show('raw')">元</button><button onclick="show('pass04')">試作</button><button onclick="show('sharp')">unsharpのみ</button><button onclick="show('tone')">LUTのみ</button><button onclick="show('mask')">指定範囲</button></main><div class="view"><img id="image" alt="B画像の明暗と細部の試作"></div><script>const images=IMAGES;function show(k){document.getElementById('image').src=images[k]}show('pass04');</script></html>''';(OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(embedded)),encoding='utf-8');print(json.dumps(manifest,ensure_ascii=False),flush=True)
if __name__=='__main__':main()
