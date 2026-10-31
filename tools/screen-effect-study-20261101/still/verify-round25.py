"""Physical files, frozen old rounds, and end-to-end synthetic evidence for round25."""
from pathlib import Path
import importlib.util,json,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('round25',HERE/'offline-round25.py');q=importlib.util.module_from_spec(s);s.loader.exec_module(q);r=q.r;out=q.DEFAULT
def main():
    m=json.loads((out/'manifest.json').read_text(encoding='utf-8'));checks={};raw=np.array(Image.open(out/'base-B.png'));visible=raw[:,:,3]>0;d=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);border=visible&(d<=2);reference=np.array(Image.open(out.parent/'round-23/B-standard.png'));roi=(slice(740,900),slice(680,1100));legacy=np.array(Image.open(out/'B-legacy1-stage.png'));assert q.sha(out/'B-legacy1-stage.png')==q.sha(out.parent/'round-23/B-stage12.png');assert q.sha(out/'base-B.png')==q.sha(r.SOURCE/'base-B.png')=='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9'
    for k,v in m['outputs'].items():
        a=np.array(Image.open(out/v['file']));assert q.sha(out/v['file'])==v['sha256'];assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~visible],raw[~visible]);assert np.array_equal(a[border],raw[border]);assert v['repeat_exact'];l=a[:,:,:3]@q.r9.WEIGHTS/255;lr=reference[:,:,:3]@q.r9.WEIGHTS/255;diff=np.abs(l-lr);checks[k]={'sha256':v['sha256'],'alpha_hidden_exact':True,'inner2px_exact':int(border.sum()),'repeat_exact':True,'changed_pixels_vs_23standard':int((diff>1e-7).sum()),'floor_mean_abs_delta_vs_23standard':float(diff[roi].mean()),'floor_white_above065':float((l[roi]>.65).mean()),'floor_black_below003':float((l[roi]<.03).mean()),'floor_mid003to065':float(((l[roi]>=.03)&(l[roi]<=.65)).mean())}
    frozen={}
    for n in [23,24]:
        folder=out.parent/f'round-{n}';f=json.loads((folder/f'round{n}-freeze.json').read_text(encoding='utf-8'));changed=[k for k,v in f['files'].items() if q.sha(folder/k)!=v];source_changed=[k for k,v in f['repo_sources'].items() if q.sha(Path(k))!=v];assert not changed and not source_changed;frozen[str(n)]={'files':len(f['files']),'sources':len(f['repo_sources']),'changed':changed,'source_changed':source_changed}
    atlases,_=r.load_atlas(q.ATLAS);lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));stats=json.loads((r.r14.DEFAULT/'reference-residual-statistics.json').read_text(encoding='utf-8'));folder=out/'synthetic-full-pipeline';folder.mkdir(exist_ok=True);tests={}
    for name in ['flat','positive','negative','diagonal','multiple']:
        a=np.array(Image.open(out/'synthetic'/f'{name}-input.png'));parts=q.signals(a,lookup);base=q.stage(a,parts,1);surface=r.r19.surface(a);paths,peaks,_=r.tracks(surface,a[:,:,3]);delta,events=r.apply_residual(surface,paths,atlases,q.PARAM);noise=np.zeros(a.shape[:2]) if parts is None else r.micro(base,peaks,stats);results={}
        for key,mix in q.PROFILES.items():
            final,_=r.r19.protect(a,q.stage(a,parts,mix)[:,:,:3]/255+(delta+noise)[:,:,None]);Image.fromarray(final).save(folder/f'{name}-{key}.png');r.labelled_pair(Image.fromarray(a),Image.fromarray(final),(0,0,a.shape[1],a.shape[0]),folder/f'{name}-{key}-comparison.png',f'{name}-{key}.png',f'{name}-input.png');changed=int(np.any(final!=a,axis=2).sum());results[key]={'changed_pixels':changed,'alpha_exact':bool(np.array_equal(final[:,:,3],a[:,:,3])),'continuous_mid_fraction':float((((final[:,:,:3]@q.r9.WEIGHTS/255)>.1)&((final[:,:,:3]@q.r9.WEIGHTS/255)<.75)).mean())}
            if name=='flat':assert changed==0
        tests[name]=results
    for key in q.PROFILES:
        after=Image.open(out/f'B-{key}.png');before=Image.open(out.parent/'round-23/B-standard.png')
        for label,box in {'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500)}.items():r.labelled_pair(before,after,box,out/f'B-{key}-vs23standard-{label}.png',f'25 B-{key}.png','23 B-standard.png')
    dependencies=[HERE/n for n in ['offline-round25.py','offline-round23.py','offline-round22.py','offline-round20.py','offline-round19.py','offline-round14.py','offline-round12.py','offline-round11.py','offline-round10.py','offline-round09.py','build.py','verify-round25.py']];dependencies=[p for p in dependencies if p.exists()]
    result={'outputs':checks,'OFF_byte_exact':q.sha(out/'B-OFF.png')==q.sha(out/'base-B.png'),'legacy1_stage_exact_frozen23':True,'frozen_rounds':frozen,'atlas_synthetic':m['synthetic_verification'],'full_pipeline_synthetic':tests,'source_sha256':q.sha(HERE/'offline-round25.py'),'dependency_hashes':{str(p):q.sha(p) for p in dependencies}};(out/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps({'outputs':checks,'frozen_rounds':frozen,'full_pipeline_flat':tests['flat']},indent=2),flush=True)
if __name__=='__main__':main()
