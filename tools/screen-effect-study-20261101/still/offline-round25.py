"""Isolate legacy12 local rectangular faults, holding frozen23 standard atlas constant."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8')
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('frozen23',HERE/'offline-round23.py');r=importlib.util.module_from_spec(s);s.loader.exec_module(r)
ATLAS=r.DEFAULT;DEFAULT=ATLAS.parent/'round-25';PROFILES={'zero':0.,'partial35':.35,'partial65':.65};PARAM={'gain':.60,'normal_width':1.25};r12=r.r22.r12;r9=r.r9;r19=r.r19

def signals(raw,lookup):
    visible=raw[:,:,3]>0;l=raw[:,:,:3].astype(float)@r9.WEIGHTS/255
    if np.ptp(l[visible])<1e-7:return None
    optical=r9.optical(l,'A');clean,pre,std,energy,_=r.r22.r10.prepare(optical);p=r12.PROFILES['strong'];core,gate,dy=r12.line_core(clean,std,p);signal=pre+(core-pre)*gate*p['line_mix']+dy*gate*p['detail_gain'];faulted,events=r12.faults(signal,energy,lookup,visible,p)
    return signal,faulted,l,visible,events

def stage(raw,parts,mix):
    if parts is None:return raw.copy()
    signal,faulted,l,visible,_=parts;target=signal+mix*(faulted-signal);target,_=r12.dark_variation(target,l,r12.PROFILES['strong'],visible);return r.r22.r11.rgba(raw,target)

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for key,file in [('base',f'base-{c}.png'),*[(k,f'{c}-{k}.png') for k in PROFILES]]:images[c+'-'+key]='data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>矩形の欠けの寄与を比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>矩形の欠けの寄与を比較</h1><p>輪郭の擦れは強さ .60・幅 1.25で固定。参照由来の輪郭残差には元形状や反射の影響が残ります。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="矩形の欠けの寄与"><option value="zero">寄与 0</option><option value="partial35">寄与 .35</option><option value="partial65">寄与 .65</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="矩形の欠けの寄与の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='B');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);atlases,am=r.load_atlas(ATLAS);lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));stats=json.loads((r.r14.DEFAULT/'reference-residual-statistics.json').read_text(encoding='utf-8'));cameras=['custom'] if args.input else ['B','A','C'] if args.camera=='all' else [args.camera];m={'round':25,'profiles':{k:{'fault_mix':v,**PARAM,'tangent_pitch':1.0} for k,v in PROFILES.items()},'source_sha256':sha(Path(__file__)),'shared_frozen23_source_sha256':sha(HERE/'offline-round23.py'),'atlas_manifest_sha256':sha(ATLAS/'atlas-manifest.json'),'reference_derived_material_disclosed':True,'limitation':am['limitation'],'reference_RGB_used_as_output_pixels':False,'completed_prior_output_pixels_used':False,'new_random_fragment_paint_used':False,'same_parameters_for_ABC':True,'inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else r.SOURCE/f'base-{c}.png';expected=sha(src);raw=np.array(Image.open(src).convert('RGBA'))
        for file in [f'base-{c}.png',f'{c}-OFF.png']:
            dest=out/file
            if dest.exists():assert sha(dest)==expected
            else:shutil.copyfile(src,dest)
        parts=signals(raw,lookup);original=stage(raw,parts,1.);assert np.array_equal(original,r.r22.stage12(raw,lookup)[0]);surface=r19.surface(raw);paths,peaks,_=r.tracks(surface,raw[:,:,3]);delta,events=r.apply_residual(surface,paths,atlases,PARAM);assert np.array_equal(delta,r.apply_residual(surface,paths,atlases,PARAM)[0]);noise=np.zeros(raw.shape[:2]) if parts is None else r.micro(original,peaks,stats);Image.fromarray(original).save(out/f'{c}-legacy1-stage.png');m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/f'{c}-OFF.png')==expected,'edge_path_count':len(paths),'legacy1_reconstruction_exact':True,'legacy1_stage_sha256':sha(out/f'{c}-legacy1-stage.png')};sequence=events.pop('phase_sequences');(out/f'{c}-phase-sequences.json').write_text(json.dumps(sequence,indent=2),encoding='utf-8')
        for name,mix in PROFILES.items():
            st=stage(raw,parts,mix);assert np.array_equal(st,stage(raw,signals(raw,lookup),mix));rgb=st[:,:,:3].astype(float)/255+(delta+noise)[:,:,None];a,count=r19.protect(raw,rgb);again,_=r19.protect(raw,stage(raw,signals(raw,lookup),mix)[:,:,:3].astype(float)/255+(delta+noise)[:,:,None]);assert np.array_equal(a,again);file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'{c}-{name}-on-black.png');Image.fromarray(st).save(out/f'{c}-{name}-stage.png')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'base-{c}.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'fault_mix':mix,'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':count,'repeat_exact':True,'atlas_events':events,'diagnostics':r19.diagnostics(a,raw),'stage_difference_from_legacy1_mean_abs':float(np.abs(st[:,:,:3].astype(float)-original[:,:,:3]).mean()/255)};print(c,name,m['outputs'][c+'-'+name]['sha256'],flush=True)
        assert sha(src)==expected
    m['synthetic_verification']=r.synth_tests(out,atlases);preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');shutil.copyfile(HERE/'prediction-round25.md',out/'prediction.md');print('Round25 complete',flush=True)
if __name__=='__main__':main()
