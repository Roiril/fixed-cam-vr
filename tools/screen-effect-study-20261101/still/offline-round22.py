"""Native grid14 tone with coherent local bundles of thin horizontal lines."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r20',HERE/'offline-round20.py');r20=importlib.util.module_from_spec(s);s.loader.exec_module(r20);r14=r20.r14;r12=r20.r12;r19=r20.r19;r9=r20.r9;r10=r20.r10;r11=r20.r11;SOURCE=r20.SOURCE;DEFAULT=SOURCE.parent/'round-22'
PROFILES={'compact':{'groups':4000,'lines':[3,5],'length':[3,10]},'longer':{'groups':4400,'lines':[3,6],'length':[5,12]}}

def stage12(raw,lookup):
    visible=raw[:,:,3]>0;l=raw[:,:,:3].astype(float)@r9.WEIGHTS/255
    if np.ptp(l[visible])<1e-7:return raw.copy(),True
    optical=r9.optical(l,'A');clean,pre,std,energy,_=r10.prepare(optical);p=r12.PROFILES['strong'];core,gate,dy=r12.line_core(clean,std,p);signal=pre+(core-pre)*gate*p['line_mix']+dy*gate*p['detail_gain'];target,_=r12.faults(signal,energy,lookup,visible,p);target,_=r12.dark_variation(target,l,p,visible);return r11.rgba(raw,target),False

def bundles(stage,f,noise,h_rms,p):
    (smooth,gx,span,peaks,low,high,distance,visible),energy,ew,tw=f;h,w=stage.shape;rng=np.random.default_rng(220223);maximum=max(v['groups'] for v in PROFILES.values());ea=rng.choice(h*w,maximum,p=ew.ravel());ta=rng.choice(h*w,maximum,p=tw.ravel());types=rng.random(maximum);detail=smooth-cv2.GaussianBlur(smooth,(0,0),1.2);result=stage.copy();paint=np.zeros(stage.shape,bool);emission=np.zeros(stage.shape,bool);lines=long_groups=broken=0;lengths=[]
    for group in range(p['groups']):
        interior=types[group]<.70;cy,cx=divmod(int(ta[group] if interior else ea[group]),w);bright=bool(detail[cy,cx]>=0) if interior else bool(rng.random()<.5);polarity=1 if gx[cy,cx]>0 else -1;direction=(1 if rng.random()<.5 else -1) if interior else (-polarity if bright else polarity);value=float(high[cy,cx] if bright else low[cy,cx]);contrast=float(np.clip((high[cy,cx]-low[cy,cx])/.25,0,1));opacity=.65*contrast*rng.uniform(.5,1);long=bool(rng.random()<.01);length=int(rng.integers(16,25)) if long else int(rng.integers(p['length'][0],p['length'][1]+1));number=int(rng.integers(p['lines'][0],p['lines'][1]+1));x0=cx+int(rng.integers(-2,3));y0=cy+int(rng.integers(-2,3));step=0;gap_position=int(length*rng.uniform(.35,.65));long_groups+=int(long)
        for line in range(number):
            if line:
                y0+=1 if rng.random()<.12 else 2;step+=int(rng.choice([-1,0,1],p=[.20,.60,.20]))
            current_length=max(3,length+int(rng.integers(-1,2)));notch=(line%3==2 and current_length>=6);gap_width=1 if rng.random()<.8 else 2
            for k in range(current_length):
                if notch and gap_position<=k<gap_position+gap_width:continue
                x=x0+step+direction*k
                if not(0<=x<w and 0<=y0<h and visible[y0,x]):continue
                alpha=opacity*(.55 if k in (0,current_length-1) else 1);result[y0,x]+=(value-result[y0,x])*alpha;paint[y0,x]=True;emission[y0,x]=True
            lines+=1;broken+=int(notch);lengths.append(current_length)
    mid=np.clip((stage-.05)/.12,0,1)*np.clip((.82-stage)/.14,0,1)*visible;near=np.exp(-distance/4.5)*(distance<14)*np.clip(span/.2,0,1);weight=mid*(h_rms*3+near*.002);centered=noise-float(np.sum(noise*weight)/max(1e-12,weight.sum()));result+=centered*weight;paint|=weight>0
    return np.clip(result,.001,.85),paint,emission,{'groups':p['groups'],'lines':lines,'rare_long_groups':long_groups,'interior_share':.70,'broken_lines':broken,'height_px':1,'length_quantiles':np.quantile(lengths,[0,.5,.9,1]).tolist(),'emission_area_fraction':float(emission[visible].mean()),'polarity_and_direction_coherent_per_group':True,'opacity_max_before_contrast':.65}

def composite(raw,stage,sl,target,paint):
    candidate=stage.copy();candidate[:,:,:3]=r9.gray8(stage[:,:,:3]/255+(target-sl)[:,:,None]);candidate[~paint]=stage[~paint];visible=raw[:,:,3]>0;distance=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);u=np.clip((distance-2)/18,0,1);fade=u*u*(3-2*u);a=raw.copy();a[:,:,:3]=r9.gray8(raw[:,:,:3]/255+(candidate[:,:,:3].astype(float)-raw[:,:,:3])/255*fade[:,:,None]);boundary=visible&(distance<=2);assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[~visible],raw[~visible]) and np.array_equal(a[boundary],raw[boundary]);return a,int(boundary.sum())

def signal_metrics(a,baseline,raw):
    l=a[:,:,:3].astype(float)@r9.WEIGHTS/255;b=baseline[:,:,:3].astype(float)@r9.WEIGHTS/255;visible=cv2.erode((raw[:,:,3]>0).astype(np.uint8),np.ones((41,41),np.uint8)).astype(bool);d=(l-b)*visible;active=(np.abs(d)>.012)&visible;count,labels,stats,_=cv2.connectedComponentsWithStats(active.astype(np.uint8),8);components=stats[1:];width=components[:,cv2.CC_STAT_WIDTH];height=components[:,cv2.CC_STAT_HEIGHT];area=components[:,cv2.CC_STAT_AREA];den=float(np.sum(d*d));rx=float(np.sum(d[:,2:]*d[:,:-2])/max(den,1e-12));ry=float(np.sum(d[2:]*d[:-2])/max(den,1e-12))
    return {'difference_threshold_L':.012,'active_fraction':float(active[visible].mean()),'horizontal_correlation_lag2':rx,'vertical_correlation_lag2':ry,'component_count':count-1,'median_component_width':float(np.median(width)) if len(width) else 0,'p90_component_width':float(np.quantile(width,.9)) if len(width) else 0,'horizontal_component_fraction':float(((width>=3)&(width>=2*height)).mean()) if len(width) else 0,'dot_component_fraction':float(((width<=2)&(height<=2)).mean()) if len(width) else 0,'thick_component_fraction':float(((height>=3)&(width<2*height)).mean()) if len(width) else 0}

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for key,file in [('base',f'base-{c}.png'),('stage',f'{c}-stage12.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+key]='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>水平線の束を比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}select:focus-visible{outline:2px solid var(--link)}</style><div class="wrap"><h1>水平線の束を比較</h1><p>同じ入力から、向きと明暗を揃えた細い横線の束を局所に加えています。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="線の束"><option value="compact">短い束</option><option value="longer">長めの束</option><option value="stage">束を加える前</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="水平線の束の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='all');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);sm=json.loads((SOURCE/'input-and-processing-manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r11.LOOKUP.read_text(encoding='utf-8'));stats_path=r14.DEFAULT/'reference-residual-statistics.json';stats=json.loads(stats_path.read_text(encoding='utf-8'));cameras=['custom'] if args.input else ['B','A','C'] if args.camera=='all' else [args.camera]
    m={'round':22,'profiles':PROFILES,'source_code':{p.name:sha(p) for p in [Path(__file__),HERE/'offline-round14.py',HERE/'offline-round12.py']},'working_scale':1.0,'completed_prior_output_pixels_used':False,'generated_RGB_used':False,'same_parameters_for_all_cameras':True,'inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else SOURCE/f'base-{c}.png';expected=sha(src)
        if not args.input:assert expected==sm['inputs'][c]['copy_sha256']
        for file in [f'base-{c}.png',f'{c}-OFF.png']:
            p=out/file
            if p.exists():assert sha(p)==expected
            else:shutil.copyfile(src,p)
        raw=np.array(Image.open(src).convert('RGBA'));stage,constant=stage12(raw,lookup);assert np.array_equal(stage,stage12(raw,lookup)[0]);Image.fromarray(stage).save(out/f'{c}-stage12.png');baseline,_=composite(raw,stage,stage[:,:,:3].astype(float)@r9.WEIGHTS/255,stage[:,:,:3].astype(float)@r9.WEIGHTS/255,np.zeros(raw.shape[:2],bool));sl=stage[:,:,:3].astype(float)@r9.WEIGHTS/255
        if not constant:f=r14.fields(r14.extend_alpha(raw),sl);noise,h_rms=r14.synthesize(sl.shape,stats)
        m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/f'{c}-OFF.png')==expected,'stage12_sha256':sha(out/f'{c}-stage12.png'),'stage12_repeat_exact':True,'constant_input':constant}
        if c=='B' and not args.input:
            # Comparison statistics only, never pixels used to create the output.
            frozen=np.array(Image.open(r14.DEFAULT/'B-strong.png').convert('RGBA'));m['comparison14_signal_metrics']=signal_metrics(frozen,baseline,raw)
        for name,p in PROFILES.items():
            if constant:a=raw.copy();bcount=0;events={'groups':0,'lines':0}
            else:
                target,paint,emission,events=bundles(sl,f,noise,h_rms,p);again,again_paint,_,_=bundles(sl,f,noise,h_rms,p);assert np.array_equal(target,again) and np.array_equal(paint,again_paint);a,bcount=composite(raw,stage,sl,target,paint)
            file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'{c}-{name}-on-black.png')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'base-{c}.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':bcount,'repeat_exact':True,'events':events,'diagnostics':r19.diagnostics(a,raw),'signal_metrics':signal_metrics(a,baseline,raw)};print(c,name,json.dumps(m['outputs'][c+'-'+name]),flush=True)
        assert sha(src)==expected
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
