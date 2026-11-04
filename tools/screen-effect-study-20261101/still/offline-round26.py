"""Reference-derived edge waveform replacement, matched to current-stage plateaus."""
from pathlib import Path
import argparse,base64,importlib.util,json,math,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('frozen23',HERE/'offline-round23.py');r=importlib.util.module_from_spec(s);s.loader.exec_module(r);ATLAS=r.DEFAULT;DEFAULT=ATLAS.parent/'round-26';PROFILES={'soft':.65,'standard':.85,'full':1.0};WIDTH=1.25

def waveform(stage,paths,atlases):
    l=stage[:,:,:3].astype(float)@r.r9.WEIGHTS/255;h,w=l.shape;acc=np.zeros_like(l);weights=np.zeros_like(l);coverage=np.zeros_like(l);records=[]
    for path in paths:
        ys=path['y'];center=path['x'];slope=path['slope'];normal_scale=WIDTH*np.sqrt(1+slope*slope);polarity=path['polarity'];arc=np.cumsum(np.sqrt(1+slope*slope));strip,seq=r.residual_strip(atlases,max(4096,int(arc[-1])+600),path['seed']);radius=24*normal_scale.max();x0=max(0,int(np.floor(center.min()-radius)));x1=min(w,int(np.ceil(center.max()+radius))+1)
        if x1<=x0:continue
        # Current-stage median plateaus in canonical dark->bright normal coordinates.
        side=np.arange(14,23);darkx=np.rint(center[:,None]-polarity*normal_scale[:,None]*side).astype(int);brightx=np.rint(center[:,None]+polarity*normal_scale[:,None]*side).astype(int);valid=(darkx.min(axis=1)>=0)&(brightx.min(axis=1)>=0)&(darkx.max(axis=1)<w)&(brightx.max(axis=1)<w);darkx=np.clip(darkx,0,w-1);brightx=np.clip(brightx,0,w-1);valid&=np.all(stage[ys[:,None],darkx,3]>0,axis=1)&np.all(stage[ys[:,None],brightx,3]>0,axis=1);dark=np.median(l[ys[:,None],darkx],axis=1);bright=np.median(l[ys[:,None],brightx],axis=1);dark=cv2.GaussianBlur(dark[:,None],(1,0),sigmaX=0,sigmaY=3)[:,0];bright=cv2.GaussianBlur(bright[:,None],(1,0),sigmaX=0,sigmaY=3)[:,0];contrast=bright-dark;valid&=contrast>.08
        # Replacement changes low-frequency content too: soften finite track endpoints instead of cutting a rectangular band.
        t=np.clip(np.minimum(np.arange(len(ys)),np.arange(len(ys))[::-1])/6,0,1);rowfade=t*t*(3-2*t)
        xx=np.arange(x0,x1)[None,:];u=polarity*(xx-center[:,None])/normal_scale[:,None];mx=(u+24).astype(np.float32);my=np.repeat((arc+path['seed']%431).astype(np.float32)[:,None],x1-x0,axis=1);residual=cv2.remap(strip,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT,borderValue=0);step=np.array([.5*(1+math.erf(float(v)/(math.sqrt(2)*3))) for v in np.arange(-24,25)],np.float32);ideal=cv2.remap(np.repeat(step[None,:],2,axis=0),mx,np.zeros_like(mx),cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE);wave=np.clip(ideal+residual,-.35,1.5);target=np.clip(dark[:,None]+contrast[:,None]*wave,.001,.85);envelope=np.clip((24-np.abs(u))/6,0,1)*valid[:,None]*rowfade[:,None];priority=envelope*np.maximum(contrast[:,None],0)**2;index=np.ix_(ys,np.arange(x0,x1));acc[index]+=target*priority;weights[index]+=priority;coverage[index]=np.maximum(coverage[index],envelope)
        records.append({'seed':path['seed'],'rows':len(ys),'valid_rows':int(valid.sum()),'plateau_contrast_median':float(np.median(contrast)),'source_blocks':seq})
    target=np.divide(acc,weights,out=l.copy(),where=weights>1e-12);delta=(target-l)*coverage;return delta,coverage,{'path_count':len(paths),'replacement_pixels':int((coverage>0).sum()),'full_replacement_band_pixels':int((coverage>=1-1e-9).sum()),'maximum_abs_luma_delta':float(np.abs(delta).max()),'records':records}

def render(raw,stage,delta,noise,mix):
    # Add only scalar luma difference: current per-pixel RGB chroma is preserved before gamut clipping.
    rgb=stage[:,:,:3].astype(float)/255+(delta*mix+noise)[:,:,None];return r.r19.protect(raw,rgb)

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for k,f in [('base',f'base-{c}.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+k]='data:image/png;base64,'+base64.b64encode((out/f).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>輪郭の波形を置き換える</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>輪郭の波形を置き換える</h1><p>現在画像の暗明に合わせ、参照由来の輪郭波形へ置き換えます。元形状や反射との完全な分離はできません。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="置換の強さ"><option value="soft">置換 .65</option><option value="standard">置換 .85</option><option value="full">置換 1.00</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="輪郭波形の置換の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);atlases,am=r.load_atlas(ATLAS);lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));stats=json.loads((r.r14.DEFAULT/'reference-residual-statistics.json').read_text(encoding='utf-8'));cameras=['custom'] if args.input else ['B','A','C'] if args.camera=='all' else [args.camera];m={'round':26,'profiles':{k:{'replacement_mix':v,'normal_width':WIDTH,'tangent_pitch':1.,'ideal_sigma':3.,'legacy_fault_mix':1.} for k,v in PROFILES.items()},'source_sha256':sha(Path(__file__)),'atlas_manifest_sha256':sha(ATLAS/'atlas-manifest.json'),'reference_derived_material_disclosed':True,'limitation':am['limitation'],'reference_RGB_used_as_output_pixels':False,'completed_prior_output_pixels_used':False,'same_parameters_for_ABC':True,'overlap':'contrast squared weighted target average; max envelope coverage, no delta addition','plateaus':'current stage median u14..22 each side, row sigma3, positive canonical contrast>.08','chroma':'scalar luma difference added to current RGB; gamut clipping can alter saturated chroma','inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else r.SOURCE/f'base-{c}.png';expected=sha(src)
        for name in [f'base-{c}.png',f'{c}-OFF.png']:
            p=out/name
            if p.exists():assert sha(p)==expected
            else:shutil.copyfile(src,p)
        raw=np.array(Image.open(src).convert('RGBA'));stage,constant=r.r22.stage12(raw,lookup);assert np.array_equal(stage,r.r22.stage12(raw,lookup)[0]);surface=r.r19.surface(raw);paths,peaks,_=r.tracks(surface,raw[:,:,3]);delta,coverage,events=waveform(stage,paths,atlases);delta2,coverage2,_=waveform(stage,paths,atlases);assert np.array_equal(delta,delta2) and np.array_equal(coverage,coverage2);noise=np.zeros(raw.shape[:2]) if constant else r.micro(stage,peaks,stats);Image.fromarray(stage).save(out/f'{c}-stage12.png');Image.fromarray(r.r9.gray8(coverage)).save(out/f'{c}-replacement-coverage.png');m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/f'{c}-OFF.png')==expected,'stage12_sha256':sha(out/f'{c}-stage12.png'),'edge_path_count':len(paths)};(out/f'{c}-waveform-sequences.json').write_text(json.dumps(events.pop('records'),indent=2),encoding='utf-8')
        for name,mix in PROFILES.items():
            a,count=render(raw,stage,delta,noise,mix);assert np.array_equal(a,render(raw,stage,delta2,noise,mix)[0]);file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r.r19.black(a,out/f'{c}-{name}-on-black.png')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'base-{c}.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':count,'repeat_exact':True,'events':events,'diagnostics':r.r19.diagnostics(a,raw)};print(c,name,m['outputs'][c+'-'+name]['sha256'],flush=True)
        assert sha(src)==expected
    preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');shutil.copyfile(HERE/'prediction-round26.md',out/'prediction.md');print('Round26 complete',flush=True)
if __name__=='__main__':main()
