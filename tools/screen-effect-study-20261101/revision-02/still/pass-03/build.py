from pathlib import Path
import base64,hashlib,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from manual import process,PARAMETERS
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study')
OUT=ROOT/'revision-02/still/pass-03';BASE=ROOT/'still/round-11/base-B.png';ATLAS=ROOT/'still/round-23'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def write(name,data):(OUT/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
def gray(a,p):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(OUT/p)
def longest(rows):return max(map(len,''.join('1' if r else '0' for r in rows).split('0')),default=0)
def fixture_tests():
    y,x=np.indices((192,256));report={}
    for name,line in [('horizontal',abs(y-95.5)<2),('vertical',abs(x-127.5)<2),('diagonal',abs(x-y-32)/np.sqrt(2)<2),('cross',((abs(x-127.5)<2)|(abs(y-95.5)<2)))]:
        l=np.where(x<128,.2,.8);l[line]=.95
        a=np.zeros((192,256,4),np.uint8);a[:,:,:3]=np.floor(l[:,:,None]*255+.5);a[:,:,3]=255
        roi=np.zeros((192,256),np.float32);roi[:,118:139]=1
        protection=cv2.dilate(line.astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
        out,d=process(a,roi,protection,np.ones((192,256),np.float32)*.04)
        assert np.array_equal(a[protection],out[protection]);assert np.array_equal(a[roi==0],out[roi==0]);assert np.any(out[roi>0]!=a[roi>0])
        assert np.array_equal(a,process(a,roi,protection,np.zeros_like(roi),False)[0])
        report[name]=dict(manual_mask_contract_only=True,line_plus2_RGB_exact=True,outside_ROI_exact=True,effect_present=True,OFF_exact=True)
    return report
def main():
    OUT.mkdir(exist_ok=True,parents=True);config=json.loads((HERE/'regions.json').read_text(encoding='utf-8'));raw=np.array(Image.open(BASE).convert('RGBA'));h,w=raw.shape[:2];roi=np.zeros((h,w),np.float32);residual=np.zeros((h,w),np.float32);regions=[]
    l=(raw[:,:,:3].astype(np.float32)/255)@np.array([.2126,.7152,.0722],np.float32)
    for region in config['regions']:
        points=np.array(region['points'],np.float32);ys=np.arange(int(points[:,1].min()),int(points[:,1].max())+1);cx=np.interp(ys,points[:,1],points[:,0]);mask=np.zeros((h,w),np.float32)
        atlas=np.load(ATLAS/f"atlas-{region['atlas']+1:02d}.npz")['residual'].astype(np.float32)
        for y,center in zip(ys,cx):
            xs=np.arange(max(0,int(np.floor(center-10))),min(w,int(np.ceil(center+10))+1));normal=xs-center;weight=np.clip((10-abs(normal))/2,0,1).astype(np.float32)
            mx=(normal*2.4+24).astype(np.float32)[None,:];my=np.full_like(mx,float(y%(atlas.shape[0]-1)))
            wave=cv2.remap(atlas,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)[0]
            contrast=abs(l[y,min(w-1,int(center+12))]-l[y,max(0,int(center-12))])
            mask[y,xs]=weight;residual[y,xs]=wave*contrast;roi[y,xs]=np.maximum(roi[y,xs],weight)
        regions.append((region,mask))
    protect=np.zeros((h,w),np.uint8)
    for p in config['protect_polygons']:cv2.fillPoly(protect,[np.array(p['points'],np.int32)],1)
    for p in config['protect_polylines']:cv2.polylines(protect,[np.array(p['points'],np.int32)],False,1,p['width'],cv2.LINE_8)
    protect=protect.astype(bool);out,delta=process(raw,roi,protect,residual);again,_=process(raw,roi,protect,residual);off,_=process(raw,roi,protect,residual,False)
    changed=np.any(out!=raw,axis=2);inside=cv2.erode((raw[:,:,3]>0).astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool);border=(raw[:,:,3]>0)&~inside
    checks=dict(OFF_fullRGBA_exact=np.array_equal(raw,off),outside_ROI_fullRGBA_exact=np.array_equal(raw[roi==0],out[roi==0]),protect_fullRGBA_exact=np.array_equal(raw[protect],out[protect]),alpha_exact=np.array_equal(raw[:,:,3],out[:,:,3]),hiddenRGBA_exact=np.array_equal(raw[raw[:,:,3]==0],out[raw[:,:,3]==0]),inner2_exact=np.array_equal(raw[border],out[border]),repeat_exact=np.array_equal(out,again))
    assert all(checks.values());assert changed.any()
    shutil.copyfile(BASE,OUT/'base-B.png');Image.fromarray(out).save(OUT/'B-authored.png')
    def black(a):return Image.fromarray(np.floor(a[:,:,:3]*(a[:,:,3:4]/255)+.5).astype(np.uint8))
    black(raw).save(OUT/'base-B-on-black.png');black(out).save(OUT/'B-authored-on-black.png')
    gray(roi,'region-mask.png');gray(protect,'protect-mask.png');gray(.5+delta/.32,'delta-neutral.png');np.savez_compressed(OUT/'layers.npz',region=roi,protect=protect,residual=residual,delta=delta)
    overlay=np.array(black(raw));overlay[roi>0]=(.6*overlay[roi>0]+np.array([230,159,0])*.4).astype(np.uint8);overlay[protect]=(.72*overlay[protect]+np.array([0,114,178])*.28).astype(np.uint8)
    Image.fromarray(overlay).save(OUT/'authored-mask-overlay.png');coverage={}
    for region,mask in regions:
        rows=np.any(changed&(mask>0),axis=1);ys=np.where(mask.any(axis=1))[0];coverage[region['name']]=dict(selected_rows=len(ys),changed_rows=int(rows.sum()),longest_changed_span=longest(rows),changed_pixels=int((changed&(mask>0)).sum()))
    crops={'nativevertical':(595,85,735,650),'nativecable':(735,85,1010,560),'nativefloor':(680,740,1100,900)}
    font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14)
    for label,box in crops.items():
        a=black(raw).crop(box);b=black(out).crop(box);a.save(OUT/f'base-B-{label}.png');b.save(OUT/f'B-authored-{label}.png');Image.fromarray(overlay).crop(box).save(OUT/f'overlay-{label}.png')
        pair=Image.new('RGB',(a.width*2,a.height+32),'white');pair.paste(a,(0,32));pair.paste(b,(a.width,32));d=ImageDraw.Draw(pair);d.text((8,4),'base-B',font=font,fill='black');d.text((a.width+8,4),'B-authored',font=font,fill='black');pair.save(OUT/f'comparison-{label}.png')
    preserved={}
    for label,box in crops.items():
        if label=='nativevertical':continue
        x0,y0,x1,y1=box;aa=raw[y0:y1,x0:x1];bb=out[y0:y1,x0:x1];preserved[label]=dict(fullRGBA_exact=bool(np.array_equal(aa,bb)),RGB_RMS=float(np.sqrt(np.mean((aa[:,:,:3].astype(float)-bb[:,:,:3])**2))));assert preserved[label]['fullRGBA_exact']
    manifest=dict(scope=config['scope'],different_method_from_pass02=True,parameters=PARAMETERS,input_sha256=sha(BASE),output_sha256=sha(OUT/'B-authored.png'),checks={k:bool(v) for k,v in checks.items()},coverage=coverage,preserved_ROIs=preserved,visible_changed_percent=float(changed.sum()/(raw[:,:,3]>0).sum()*100),changed_pixels=int(changed.sum()),RGB_RMS_visible=float(np.sqrt(np.mean((out[:,:,:3].astype(float)[raw[:,:,3]>0]-raw[:,:,:3].astype(float)[raw[:,:,3]>0])**2))),synthetic=fixture_tests())
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in [*HERE.iterdir(),ATLAS/'atlas-01.npz',ATLAS/'atlas-02.npz',ATLAS/'atlas-manifest.json']:
        if p.is_file():shutil.copyfile(p,source/p.name)
    manifest['source_sha256']={p.name:sha(p) for p in source.iterdir()};write('manifest.json',manifest);write('verification.json',dict(all_checks_passed=True,scope='manual protection input contract, not automatic recognition',checks=manifest['checks'],coverage=coverage,preserved=preserved))
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={k:'data:image/png;base64,'+base64.b64encode((OUT/f).read_bytes()).decode() for k,f in [('base','base-B-on-black.png'),('on','B-authored-on-black.png'),('mask','authored-mask-overlay.png')]}
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>B専用・手動指定の縦境界</title><style>CSS img{display:block;max-width:none}.view{overflow:auto}button{font:inherit;padding:8px}</style><div class="wrap"><h1>B専用・手動指定の縦境界</h1><p>このB画像だけの見た目試作です。対象と保護範囲を手で指定しています。pass02と別方式です。任意の画像へ自動適用する機能ではありません。橙は加工範囲。青は保護範囲。</p><button onclick="show('base')">追加OFF</button><button onclick="show('on')">追加ON</button><button onclick="show('mask')">指定範囲</button></div><div class="view"><img id="image" alt="B専用の手動指定による縦境界の加工"></div><script>const images=IMAGES;function show(k){document.getElementById('image').src=images[k]}show('on');</script></html>'''
    (OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8');print(json.dumps(manifest,ensure_ascii=False),flush=True)
if __name__=='__main__':main()
