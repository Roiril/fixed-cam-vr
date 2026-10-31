"""Confidence-gated carrier correspondence and thin-row reference-derived waveform sampling."""
from pathlib import Path
import argparse,base64,importlib.util,json,math,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('frozen26',HERE/'offline-round26.py');q26=importlib.util.module_from_spec(s);s.loader.exec_module(q26);r=q26.r;ATLAS=q26.ATLAS;DEFAULT=ATLAS.parent/'round-27';PROFILES={'modeA':{'mix':.65,'width':1.25,'thin_rows':False,'wave_contrast':1.0},'modeB':{'mix':.65,'width':.90,'thin_rows':True,'wave_contrast':1.0},'modeC':{'mix':.65,'width':.90,'thin_rows':True,'wave_contrast':1.25}}

def thin_strip(atlases,length,seed):
    rng=np.random.default_rng(seed);strip=np.zeros((0,49),np.float32);sequence=[]
    while len(strip)<length:
        index=int(rng.choice(len(atlases),p=[.45,.45,.10]));a=atlases[index];stretch=float(rng.uniform(.8,1.6));n=int(a.shape[0]*rng.uniform(.8,1.5));phase=float(rng.uniform(0,a.shape[0]-1));t=np.mod(phase+np.arange(n)/stretch,2*(a.shape[0]-1));t=np.where(t<=a.shape[0]-1,t,2*(a.shape[0]-1)-t);block=a[np.rint(t).astype(int)].copy();block*=.40 if index==2 else 1
        if len(strip):
            seam=min(16,len(strip),len(block));switch=(np.arange(seam)<seam/2)[:,None];strip=np.concatenate([strip[:-seam],np.where(switch,strip[-seam:],block[:seam]),block[seam:]])
        else:strip=block
        sequence.append({'atlas_id':index+1,'stretch':stretch,'phase':phase,'rows':n})
    return strip[:length],sequence

def correspond(stage,surface,paths,peaks):
    l=stage[:,:,:3].astype(float)@r.r9.WEIGHTS/255;records=[];h,w=l.shape
    for path in paths:
        ys=path['y'];center=path['x'];scale=1.25*np.sqrt(1+path['slope']**2);side=np.arange(14,23);darkx=np.rint(center[:,None]-path['polarity']*scale[:,None]*side).astype(int);brightx=np.rint(center[:,None]+path['polarity']*scale[:,None]*side).astype(int);valid=(darkx.min(axis=1)>=0)&(brightx.min(axis=1)>=0)&(darkx.max(axis=1)<w)&(brightx.max(axis=1)<w);darkx=np.clip(darkx,0,w-1);brightx=np.clip(brightx,0,w-1);valid&=np.all(stage[ys[:,None],darkx,3]>0,axis=1)&np.all(stage[ys[:,None],brightx,3]>0,axis=1);dl=l[ys[:,None],darkx];bl=l[ys[:,None],brightx];dark=np.median(dl,axis=1);bright=np.median(bl,axis=1);sd=1.4826*np.median(np.abs(dl-dark[:,None]),axis=1);sb=1.4826*np.median(np.abs(bl-bright[:,None]),axis=1);dark=cv2.GaussianBlur(dark[:,None],(1,0),sigmaX=0,sigmaY=3)[:,0];bright=cv2.GaussianBlur(bright[:,None],(1,0),sigmaX=0,sigmaY=3)[:,0];contrast=bright-dark;valid&=contrast>.08
        offsets=np.arange(-32,33);nx=np.clip(np.rint(center[:,None]+offsets).astype(int),0,w-1);other=peaks[ys[:,None],nx]&(np.abs(offsets)[None,:]>4);distance=np.min(np.where(other,np.abs(offsets)[None,:],99),axis=1);isolation=np.clip((distance-4)/12,0,1);variation=(sd+sb)/2/np.maximum(contrast,.08);stable=1/(1+(variation/.18)**2);strength=np.clip((contrast-.08)/.16,0,1);strength=strength*strength*(3-2*strength);confidence=.10+.90*stable*strength*isolation;confidence=cv2.GaussianBlur(confidence[:,None],(1,0),sigmaX=0,sigmaY=2)[:,0];confidence*=valid
        records.append({**path,'dark':dark,'bright':bright,'valid':valid,'confidence':confidence,'plateau_relative_variation':variation,'nearest_other_edge_px':distance})
    return records

def waveform(stage,paths,atlases,p):
    l=stage[:,:,:3].astype(float)@r.r9.WEIGHTS/255;h,w=l.shape;acc=np.zeros_like(l);weights=np.zeros_like(l);coverage=np.zeros_like(l);confmap=np.zeros_like(l);records=[];rowvalues=[]
    for path in paths:
        ys=path['y'];center=path['x'];slope=path['slope'];scale=p['width']*np.sqrt(1+slope*slope);arc=np.cumsum(np.sqrt(1+slope*slope));strip,seq=(thin_strip if p['thin_rows'] else r.residual_strip)(atlases,max(4096,int(arc[-1])+600),path['seed']);radius=24*scale.max();x0=max(0,int(np.floor(center.min()-radius)));x1=min(w,int(np.ceil(center.max()+radius))+1)
        if x1<=x0:continue
        u=path['polarity']*(np.arange(x0,x1)[None,:]-center[:,None])/scale[:,None];mx=(u+24).astype(np.float32);t=arc+path['seed']%431
        if p['thin_rows']:t=np.rint(t)
        my=np.repeat(t.astype(np.float32)[:,None],x1-x0,axis=1);residual=cv2.remap(strip,mx,my,cv2.INTER_CUBIC if p['thin_rows'] else cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT,borderValue=0);step=np.array([.5*(1+math.erf(float(v)/(math.sqrt(2)*3))) for v in np.arange(-24,25)],np.float32);ideal=cv2.remap(np.repeat(step[None,:],2,axis=0),mx,np.zeros_like(mx),cv2.INTER_CUBIC if p['thin_rows'] else cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE);wave=np.clip((ideal+residual-.5)*p['wave_contrast']+.5,-.35,1.5);contrast=path['bright']-path['dark'];target=np.clip(path['dark'][:,None]+contrast[:,None]*wave,.001,.85);t=np.clip(np.minimum(np.arange(len(ys)),np.arange(len(ys))[::-1])/6,0,1);rowfade=t*t*(3-2*t);geometric=np.clip((24-np.abs(u))/6,0,1)*path['valid'][:,None]*rowfade[:,None];envelope=geometric*path['confidence'][:,None];priority=envelope*np.maximum(contrast[:,None],0)**2;index=np.ix_(ys,np.arange(x0,x1));acc[index]+=target*priority;weights[index]+=priority;coverage[index]=np.maximum(coverage[index],envelope);confmap[index]=np.maximum(confmap[index],path['confidence'][:,None]*geometric);rowvalues.extend(path['confidence'][path['valid']].tolist());records.append({'seed':path['seed'],'rows':len(ys),'valid_rows':int(path['valid'].sum()),'confidence_mean':float(path['confidence'].mean()),'confidence_max':float(path['confidence'].max()),'source_blocks':seq})
    target=np.divide(acc,weights,out=l.copy(),where=weights>1e-12);delta=(target-l)*coverage;high=confmap>.60;low=(confmap>0)&(confmap<.25);events={'path_count':len(paths),'replacement_pixels':int((coverage>0).sum()),'coverage_above05_pixels':int((coverage>.5).sum()),'high_confidence_pixels':int(high.sum()),'high_confidence_delta_RMS':float(np.sqrt(np.mean(delta[high]**2))) if high.any() else 0,'high_confidence_delta_max':float(np.abs(delta[high]).max()) if high.any() else 0,'low_confidence_pixels':int(low.sum()),'confidence_row_quantiles':np.quantile(rowvalues,[0,.25,.5,.75,1]).tolist() if rowvalues else [],'maximum_abs_luma_delta':float(np.abs(delta).max()),'records':records};return delta,coverage,confmap,events

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for k,f in [('base',f'base-{c}.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+k]='data:image/png;base64,'+base64.b64encode((out/f).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>輪郭の対応と細い行を比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>輪郭の対応と細い行を比較</h1><p>置換量 .65。参照由来の波形は元形状や反射と完全には分離できません。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="加工方法"><option value="modeA">A：対応を重み付け ／ 幅 1.25</option><option value="modeB">B：細い行を保つ ／ 幅 .90</option><option value="modeC">C：細い行の明暗を締める ／ 対比 1.25</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="輪郭の対応と細い行の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);atlases,am=r.load_atlas(ATLAS);lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));stats=json.loads((r.r14.DEFAULT/'reference-residual-statistics.json').read_text(encoding='utf-8'));cameras=['custom'] if args.input else ['B','A','C'] if args.camera=='all' else [args.camera];m={'round':27,'profiles':PROFILES,'source_sha256':sha(Path(__file__)),'atlas_manifest_sha256':sha(ATLAS/'atlas-manifest.json'),'reference_derived_material_disclosed':True,'limitation':am['limitation'],'reference_RGB_used_as_output_pixels':False,'completed_prior_output_pixels_used':False,'same_parameters_for_ABC':True,'gross12_fault_mix':1.,'tangent_pitch':1.,'confidence_formula':'.10+.90 * stability * contrastSmoothstep(.08,.24) * clip((neighborDistance-4)/12); stability=1/(1+(relativeMAD/.18)^2), sigmaY2','plateau_measurement_shared_across_modes':'current stage median u14..22 using fixed width1.25, sigmaY3','thin_sampling':'nearest source row; block seam switches rows without blending; integer final Y; cubic X','inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else r.SOURCE/f'base-{c}.png';expected=sha(src);raw=np.array(Image.open(src).convert('RGBA'))
        for name in [f'base-{c}.png',f'{c}-OFF.png']:
            f=out/name
            if f.exists():assert sha(f)==expected
            else:shutil.copyfile(src,f)
        stage,constant=r.r22.stage12(raw,lookup);assert np.array_equal(stage,r.r22.stage12(raw,lookup)[0]);surface=r.r19.surface(raw);paths,peaks,_=r.tracks(surface,raw[:,:,3]);mapped=correspond(stage,surface,paths,peaks);noise=np.zeros(raw.shape[:2]) if constant else r.micro(stage,peaks,stats);Image.fromarray(stage).save(out/f'{c}-stage12.png');m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/f'{c}-OFF.png')==expected,'stage12_sha256':sha(out/f'{c}-stage12.png'),'edge_path_count':len(paths)}
        for name,p in PROFILES.items():
            delta,coverage,confmap,events=waveform(stage,mapped,atlases,p);again=waveform(stage,mapped,atlases,p)[0];assert np.array_equal(delta,again);a,count=q26.render(raw,stage,delta,noise,p['mix']);assert np.array_equal(a,q26.render(raw,stage,again,noise,p['mix'])[0]);file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r.r19.black(a,out/f'{c}-{name}-on-black.png');Image.fromarray(r.r9.gray8(confmap)).save(out/f'{c}-{name}-confidence.png');(out/f'{c}-{name}-sequences.json').write_text(json.dumps(events.pop('records'),indent=2),encoding='utf-8')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'shadow':(590,350,700,600),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'base-{c}.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':count,'repeat_exact':True,'events':events,'diagnostics':r.r19.diagnostics(a,raw)};print(c,name,json.dumps(m['outputs'][c+'-'+name]),flush=True)
        assert sha(src)==expected
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');shutil.copyfile(HERE/'prediction-round27.md',out/'prediction.md');print('Round27 complete',flush=True)
if __name__=='__main__':main()
