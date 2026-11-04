"""R34 fixed-current-B visual composite: completed R23 gross plus R33 fine highpass."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from build import sha,labelled_pair
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent
ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still')
OUT=ROOT/'round-34';W=np.array([.2126,.7152,.0722]);GAINS={'fine035':.35,'fine065':.65,'fine100':1.}
SOURCES={
 'gross23':(ROOT/'round-23/B-standard.png','8b869eab53fd7d7b3cf5d80a97948ed2c87b4a85f4e7baaaf4fdbff69efad03b'),
 'dense33':(ROOT/'round-33/B-dense.png','9f3c9bf8347f46fae6e1851e891f215a4a5b7fdff232e957035b5a26ebf287b1'),
 'baseline33':(ROOT/'round-33/B-baseline-protected.png','8a13b77400607d08f7d235429529f8bf86cbb249603e06e43a24eaac212b9c0a'),
 'rawB':(ROOT/'round-11/base-B.png','db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9')}
ROIS={'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}
LIMIT='参照の微細模様を間接転写する固定入力の合成見た目試作。実際の加工原因、内容混入ゼロ、汎用性、実時間実装を示さない。意味のある異物の形が認識できる候補は採用しない。'
s=importlib.util.spec_from_file_location('r19protect',HERE/'offline-round19.py');r19=importlib.util.module_from_spec(s);s.loader.exec_module(r19)

def rms(x,mask):return float(np.sqrt(np.mean(x[mask]**2)))
def composite(gross,high,gain):return r19.protect(gross,gross[:,:,:3].astype(float)/255+gain*high[:,:,None])[0]
def triple(raw,gross,after,roi,path,name):
    x0,y0,x1,y1=roi;w=x1-x0;h=y1-y0;panel=max(320,w);sheet=Image.new('RGB',(3*panel,h+48),'white');draw=ImageDraw.Draw(sheet);font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',18)
    for i,(a,label) in enumerate([(raw,'元B'),(gross,'下地 / R23 standard'),(after,name)]):
        draw.text((i*panel+8,8),label,fill='black',font=font);p=Image.fromarray(a).crop(roi);bg=Image.new('RGBA',p.size,(18,18,18,255));bg.alpha_composite(p);sheet.paste(bg.convert('RGB'),(i*panel+(panel-w)//2,48))
    sheet.save(path)

def preview(out):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');files={'OFF':'B-OFF.png','raw':'input-rawB.png',**{k:f'B-{k}.png' for k in GAINS}};images={k:'data:image/png;base64,'+base64.b64encode((out/f).read_bytes()).decode() for k,f in files.items()}
    html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>輪郭を保ち微細成分を追加する固定入力試作</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>輪郭を保ち微細成分を追加する固定入力試作</h1><p class="lead">23版の完成画像へ33版の高周波差分だけを追加しています。既存の完成画像を素材として使う、現B専用の合成試作です。</p><p>参照の微細模様を間接転写しています。実加工原因や汎用性、実時間処理を証明しません。</p><select id="mode" aria-label="細線の追加量"><option value="OFF">追加なし / R23下地</option><option value="fine035">追加量 0.35</option><option value="fine065">追加量 0.65</option><option value="fine100">追加量 1.00</option><option value="raw">元B</option></select></div><div class="image"><img id="image" alt="追加量ごとの実画像"></div><script>const images=IMAGES,s=document.getElementById('mode');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);out=ap.parse_args().out;out.mkdir(parents=True,exist_ok=True)
    old=ROOT/'round-33';old_index=json.loads((old/'artifact-index.json').read_text(encoding='utf-8'));old_sources=json.loads((old/'source-frozen.json').read_text(encoding='utf-8'))['files']
    assert all(sha(old/k)==v for k,v in old_index['files'].items()) and all(sha(Path(k))==v for k,v in old_sources.items())
    material={};arrays={}
    for key,(path,expected) in SOURCES.items():
        assert sha(path)==expected;dest=out/f'input-{key}.png';shutil.copyfile(path,dest);assert sha(dest)==expected;arrays[key]=np.array(Image.open(path).convert('RGBA'));material[key]={'path':str(path.resolve()),'sha256':expected,'copy':dest.name}
    assert sha(ROOT/'round-23/B-standard-on-black.png')=='4839e9c7bfe2848279c79f63aafdbaf9fe9f1abc477c34c8ee1509cd06de6e72'
    raw=arrays['rawB'];gross=arrays['gross23'];assert all(a.shape==raw.shape for a in arrays.values());assert all(np.array_equal(a[:,:,3],raw[:,:,3]) for a in arrays.values())
    visible=raw[:,:,3]>0;distance=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);inner=visible&(distance<=2);assert int(inner.sum())==7394;assert np.array_equal(gross[~visible],raw[~visible]) and np.array_equal(gross[inner],raw[inner])
    d=(arrays['dense33'][:,:,:3].astype(float)-arrays['baseline33'][:,:,:3].astype(float))@W/255;low=cv2.GaussianBlur(d,(0,0),3);high=d-low
    np.savez_compressed(out/'fine-field.npz',difference=d,gaussian_sigma3=low,highpass=high)
    for name,field in [('before',d),('low',low),('high',high)]:
        Image.fromarray(r19.u8(.5+field*2)).save(out/f'fine-{name}-neutral.png');Image.fromarray(r19.u8(np.maximum(field,0)*4)).save(out/f'fine-{name}-positive.png');Image.fromarray(r19.u8(np.maximum(-field,0)*4)).save(out/f'fine-{name}-negative.png')
    shutil.copyfile(SOURCES['gross23'][0],out/'B-OFF.png');r19.black(gross,out/'B-OFF-on-black.png');r19.black(raw,out/'raw-B-on-black.png');assert sha(out/'B-OFF.png')==SOURCES['gross23'][1]
    # Fade the added difference against gross23; never fade the already protected gross a second time.
    assert np.array_equal(composite(gross,np.zeros_like(high),1),gross);assert np.array_equal(composite(gross,high,0),gross)
    dummy=np.full((64,96,4),255,np.uint8);dummy[:,:,:3]=[93,113,131];dummy_out=composite(dummy,np.zeros((64,96)),1);assert np.array_equal(dummy,dummy_out);Image.fromarray(dummy_out).save(out/'dummy-flat.png')
    field_stats={'difference_RMS':rms(d,visible),'difference_mean':float(d[visible].mean()),'low_sigma3_RMS_before':rms(low,visible),'high_RMS':rms(high,visible),'high_mean':float(high[visible].mean()),'low_sigma3_RMS_after':rms(cv2.GaussianBlur(high,(0,0),3),visible),'neutral_encoding':'0.5+2*field; display clip only','signed_encoding':'4*positive or 4*negative; display clip only'}
    m={'round':34,'kind':'fixed-input visual composite using completed outputs','limitation':LIMIT,'fixed_input_raw_sha256':SOURCES['rawB'][1],'uses_prior_completed_output_pixels':True,'sources':material,'source_filters':{str(HERE/'offline-round23.py'):sha(HERE/'offline-round23.py'),str(HERE/'offline-round33-quilt.py'):sha(HERE/'offline-round33-quilt.py')},'formula':'gross23RGB + gain * (d - Gaussian(d,sigma3)); d=(dense33RGB-baselineProtected33RGB) dot encoded_luma_weights','frame_protection':'r19.protect(raw=gross23,target=gross23+gain*dHigh); only additional delta faded2..20px; final alpha/hidden/inner2 checked vs rawB','OFF':'byte copy of completed gross23, no protect call','seed':None,'profiles':GAINS,'weights':W.tolist(),'field':field_stats,'dictionary_indirect_inheritance':{'approved_count':251,'excluded_ids':[167,178,181,198,245],'material':'R32 approved dictionary through R33 dense','new_extraction':False},'outputs':{}}
    for name,gain in GAINS.items():
        a=composite(gross,high,gain);assert np.array_equal(a,composite(gross,high,gain));assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[~visible],raw[~visible]) and np.array_equal(a[inner],raw[inner]);file=f'B-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'B-{name}-on-black.png')
        actual=(a[:,:,:3].astype(float)-gross[:,:,:3].astype(float))@W/255;pre=gross[:,:,:3]/255+gain*high[:,:,None];clipped=np.clip(pre,0,1)-pre;changed=np.any(a[:,:,:3]!=gross[:,:,:3],axis=2)&visible
        t=np.clip((distance-2)/18,0,1);fade=t*t*(3-2*t);final_unbounded=gross[:,:,:3]/255+gain*high[:,:,None]*fade[:,:,None];final_clip=np.clip(final_unbounded,0,1)-final_unbounded
        info={'gain':gain,'file':file,'sha256':sha(out/file),'opaque_sha256':sha(out/f'B-{name}-on-black.png'),'changed_visible_pixels':int(changed.sum()),'changed_RGB_channels':int((a[:,:,:3]!=gross[:,:,:3])[visible].sum()),'luma_delta_RMS':rms(actual,visible),'signed_luma_mean':float(actual[visible].mean()),'requested_gain_HF_RMS':rms(high*gain,visible),'preprotect_clipped_visible_pixels':int((np.any((pre<0)|(pre>1),axis=2)&visible).sum()),'preprotect_clip_RGB_RMS':float(np.sqrt(np.mean(clipped[visible]**2))),'final_clipped_visible_pixels':int((np.any((final_unbounded<0)|(final_unbounded>1),axis=2)&visible).sum()),'final_clip_RGB_RMS':float(np.sqrt(np.mean(final_clip[visible]**2))),'repeat_exact':True,'alpha_hidden_inner_exact':True,'inner_pixels':int(inner.sum())};m['outputs'][name]=info
        Image.fromarray(r19.u8(.5+actual*2)).save(out/f'B-{name}-actual-delta-neutral.png');Image.fromarray(r19.u8(np.maximum(actual,0)*4)).save(out/f'B-{name}-actual-delta-positive.png');Image.fromarray(r19.u8(np.maximum(-actual,0)*4)).save(out/f'B-{name}-actual-delta-negative.png')
        for roi,box in ROIS.items():
            if roi=='full':labelled_pair(Image.fromarray(gross),Image.fromarray(a),box,out/f'B-{name}-comparison-full.png',file,'R23 standard下地')
            else:triple(raw,gross,a,box,out/f'B-{name}-comparison-{roi}.png',f'{name} / 追加 {gain:.2f}')
        print(name,json.dumps(info),flush=True)
    for label,box in ROIS.items():labelled_pair(Image.fromarray(raw),Image.fromarray(gross),box,out/f'raw-to-gross-{label}.png','R23 standard下地','元B')
    assert len({x['sha256'] for x in m['outputs'].values()})==3;assert all(sha(path)==expected for path,expected in SOURCES.values());assert all(sha(old/k)==v for k,v in old_index['files'].items()) and all(sha(Path(k))==v for k,v in old_sources.items())
    verify={'dummy_flat_RGBA_exact':True,'real_gross_zero_fine_RGBA_exact':True,'real_gross_zero_gain_RGBA_exact':True,'OFF_gross23_byte_exact':True,'original4_sources_unchanged':True,'R33_artifacts_unchanged':len(old_index['files']),'R33_live_sources_unchanged':len(old_sources),'alpha_hidden_inner7394_repeat':True,'actual_three_distinct':True,'field':field_stats,'outputs':m['outputs']};(out/'verification.json').write_text(json.dumps(verify,ensure_ascii=False,indent=2),encoding='utf-8');(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');preview(out)
    folder=out/'source-snapshots';folder.mkdir(exist_ok=True);sourcefiles=[Path(__file__),HERE/'offline-round19.py',HERE/'build.py'];frozen={}
    for path in sourcefiles:shutil.copyfile(path,folder/path.name);frozen[str(path.resolve())]=sha(path)
    (out/'source-frozen.json').write_text(json.dumps({'files':frozen},ensure_ascii=False,indent=2),encoding='utf-8');print('Round34 complete',flush=True)
if __name__=='__main__':main()
