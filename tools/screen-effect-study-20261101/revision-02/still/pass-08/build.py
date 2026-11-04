from pathlib import Path
import base64,hashlib,importlib.util,json,shutil,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from central import revised_delta,ReplaceCentralSpatial
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;STILL=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/still');OUT=STILL/'pass-08';S07=HERE.parent/'pass-07';S04=HERE.parent/'pass-04';ATLAS=STILL.parent.parent/'still/round-23'
# Fixed material path is deliberately explicit; no prior picture is used as a base.
ATLAS=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-23')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def load(name,path):
    s=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(s);sys.modules[name]=m;s.loader.exec_module(m);return m
def write(name,v):(OUT/name).write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def black(a):return Image.fromarray(np.floor(a[:,:,:3]*(a[:,:,3:4]/255)+.5).astype(np.uint8))
def gray(a,name):Image.fromarray(np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)).save(OUT/name)
def longest(a):return max(map(len,''.join('1' if x else '0' for x in a).split('0')),default=0)
def main():
    OUT.mkdir(exist_ok=True,parents=True);frozen={}
    for folder in [STILL,*[STILL/f'pass-{i:02d}' for i in range(2,8)]]:
        j=json.loads((folder/'delivery-sha256.json').read_text(encoding='utf-8'));assert all(sha(folder/name)==digest for name,digest in j.items());frozen[folder.name]=len(j)
    old=load('effect',S04/'effect.py');a18=load('readonly_authored18',S07/'authored18.py');pipeline=load('readonly_effect07',S07/'effect07.py');raw=np.array(Image.open(STILL/'pass-03/base-B.png'));c04=json.loads((S04/'regions.json').read_text(encoding='utf-8'));roi,spatialprotect,residual,regions=a18.authored18(raw,c04,old);config=json.loads((S07/'authoring.json').read_text(encoding='utf-8'))
    linecore=np.zeros(raw.shape[:2],np.uint8)
    for p in config['line_polylines']:cv2.polylines(linecore,[np.array(p['points'],np.int32)],False,1,18,cv2.LINE_8)
    carpet=np.zeros_like(linecore);cv2.fillPoly(carpet,[np.array(config['carpet_polygon'],np.int32)],1);surface=np.zeros_like(linecore)
    for p in config['surface_polygons']:cv2.fillPoly(surface,[np.array(p['points'],np.int32)],1)
    distance=cv2.distanceTransform(surface,cv2.DIST_L2,cv2.DIST_MASK_PRECISE);t=np.clip(distance/20,0,1);sw=t*t*(3-2*t);sw[surface==0]=0;linecore=linecore.astype(bool);carpet=carpet.astype(bool)
    old07,info07=pipeline.process(raw,roi,spatialprotect,residual,linecore,carpet,sw,old);assert np.array_equal(old07,np.array(Image.open(STILL/'pass-07/B-pass07.png')))
    central_region,central_mask=regions[0];assert central_region['name']=='wall-board-vertical';inside=info07['inside'];atlas=np.load(ATLAS/'atlas-01.npz')['residual'].astype(np.float32);delta,support,stats=revised_delta(raw,atlas,central_region['points'],old,inside,spatialprotect)
    allowed=central_mask>0;adapter=ReplaceCentralSpatial(old,allowed,delta);out,info=pipeline.process(raw,roi,spatialprotect,residual,linecore,carpet,sw,adapter);again,_=pipeline.process(raw,roi,spatialprotect,residual,linecore,carpet,sw,adapter)
    checks=dict(recomputed07_RGBA_exact=True,outside_central_RGBA_equals07=np.array_equal(out[~allowed],old07[~allowed]),other2_spatial_delta_exact=np.array_equal(info['spatial_delta'][~allowed],info07['spatial_delta'][~allowed]),wire_target_exact=np.array_equal(info['wire_target'],info07['wire_target']),floor_target_exact=np.array_equal(info['floor_target'],info07['floor_target']),surface_base_exact=np.array_equal(info['surface_base'],info07['surface_base']),line_core_RGBA_equals07=np.array_equal(out[linecore],old07[linecore]),carpet_RGBA_equals07=np.array_equal(out[carpet],old07[carpet]),OFF_exact=np.array_equal(raw,pipeline.process(raw,roi,spatialprotect,residual,linecore,carpet,sw,adapter,False)[0]),alpha_exact=np.array_equal(raw[:,:,3],out[:,:,3]),hidden_exact=np.array_equal(raw[raw[:,:,3]==0],out[raw[:,:,3]==0]),inner2_exact=np.array_equal(raw[(raw[:,:,3]>0)&~inside],out[(raw[:,:,3]>0)&~inside]),repeat_exact=np.array_equal(out,again));assert all(checks.values())
    exactROIs={}
    for name,box in {'board':[200,250,500,500],'board-lower':[180,735,420,860],'cable':[735,85,1010,560],'floor':[680,740,1100,900],'other-vertical-left':[672,345,719,625],'other-vertical-right':[955,225,1020,390]}.items():
        x0,y0,x1,y1=box;exactROIs[name]=dict(box=box,RGBA_exact=bool(np.array_equal(out[y0:y1,x0:x1],old07[y0:y1,x0:x1])));assert exactROIs[name]['RGBA_exact']
    Image.fromarray(out).save(OUT/'B-pass08.png');black(out).save(OUT/'B-pass08-on-black.png');Image.fromarray(raw).save(OUT/'base-B.png');black(raw).save(OUT/'base-B-on-black.png');gray(allowed,'central-allowed-region.png');gray(support,'central-new-support.png');gray(.5+delta/.32,'central-delta-neutral.png');diff=out[:,:,:3].astype(np.float32)-old07[:,:,:3].astype(np.float32);Image.fromarray(np.clip(np.floor(128+diff*.5+.5),0,255).astype(np.uint8)).save(OUT/'07-to08-delta-neutral.png');np.savez_compressed(OUT/'layers.npz',central_allowed=allowed,central_delta=delta,central_support=support,row_gates=np.array(stats['row_gates']))
    font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',14);comparison=[black(raw),black(old07),black(out)];labels=['raw','pass07','pass08'];crops={'full':None,'nativevertical':(595,85,735,650),'nativecentral':(605,90,663,635),'nativecable':(735,85,1010,560),'nativefloor':(680,740,1100,900),'nativeboard':(200,250,500,500)}
    for name,box in crops.items():
        parts=[p.crop(box) if box else p for p in comparison];w,h=parts[0].size;canvas=Image.new('RGB',(w*3,h+32),'white');d=ImageDraw.Draw(canvas)
        for i,(p,label) in enumerate(zip(parts,labels)):canvas.paste(p,(i*w,32));d.text((i*w+4,4),label,font=font,fill='black')
        canvas.save(OUT/f'comparison-{name}.png');(black(out).crop(box) if box else black(out)).save(OUT/f'B-pass08-{name}.png')
    changed=np.any(out!=old07,axis=2);rows=(np.any(out!=info['surface_base'],axis=2)&allowed).any(axis=1);stats['changed_pixels_vs07']=int(changed.sum());stats['changed_rows_vs07']=int(changed.any(axis=1).sum());stats['central_effect_rows']=int(rows.sum());stats['central_effect_longest_span']=longest(rows);stats['outside_allowed_change_count']=int(changed[~allowed].sum())
    manifest=dict(scope='B-only single central wall-board boundary revision; all other07 processing frozen',input_sha256=sha(STILL/'pass-03/base-B.png'),source07_RGBA_sha256=sha(STILL/'pass-07/B-pass07.png'),source07_recomputed_exact=True,central=stats,reference_basis='Native white window edge shows intermittent short horizontal fragments; narrow only the central authored support18 to12 and gate continuous blur by existing scalar row activity. Scalar residual itself retained, no reference RGB patches.',checks={k:bool(v) for k,v in checks.items()},other_ROIs=exactROIs,old_frozen_counts_verified=frozen,opaque_actual_sha256=sha(OUT/'B-pass08-on-black.png'),RGBA_sha256=sha(OUT/'B-pass08.png'),self_score=None)
    source=OUT/'source-snapshot';source.mkdir(exist_ok=True)
    for p in HERE.iterdir():
        if p.is_file():shutil.copyfile(p,source/p.name)
    for folder,name in [(S07,'effect07.py'),(S07,'authored18.py'),(S07,'authoring.json'),(S04,'effect.py'),(S04,'regions.json')]:shutil.copyfile(folder/name,source/f'{folder.name}-{name}')
    shutil.copyfile(ATLAS/'atlas-01.npz',source/'atlas-01.npz');manifest['source_sha256']={p.name:sha(p) for p in source.iterdir()};write('manifest.json',manifest);write('verification.json',dict(checks=manifest['checks'],other_ROIs=exactROIs,central=stats))
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={k:'data:image/png;base64,'+base64.b64encode(p.read_bytes()).decode() for k,p in [('raw',OUT/'base-B-on-black.png'),('pass07',STILL/'pass-07/B-pass07-on-black.png'),('pass08',OUT/'B-pass08-on-black.png'),('delta',OUT/'07-to08-delta-neutral.png')]};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>中央の縦境界だけを狭める比較</title><style>CSS .view{overflow:auto}img{display:block;max-width:none}button{font:inherit;padding:8px}</style><main class="wrap"><h1>中央の縦境界だけを狭める比較</h1><p>B専用の手動試作。中央の板の境界だけ半幅18pxを12pxへ変更し、参照scalar残差の行強度から連続blur差分を間引きます。scalar残差の横筋は残します。板面、コード、床、他2本は07と完全一致。</p><button onclick="show('raw')">元</button><button onclick="show('pass07')">07</button><button onclick="show('pass08')">08</button><button onclick="show('delta')">差分</button></main><div class="view"><img id="image" alt="中央の縦境界だけを変更したBの比較"></div><script>const images=IMAGES;function show(k){document.getElementById('image').src=images[k]}show('pass08');</script></html>''';(OUT/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8');print(json.dumps({**manifest,'central':{k:v for k,v in stats.items() if k!='row_gates'}},ensure_ascii=False),flush=True)
if __name__=='__main__':main()
