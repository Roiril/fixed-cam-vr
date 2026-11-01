"""R33 ablation: computed grade without rectangular faults, hard seam quilting, amplitude2."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys,types
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent
def module(name,file):
    s=importlib.util.spec_from_file_location(name,HERE/file);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
q32=module('reviewed32','offline-round32-texture-transfer.py');q25=module('baseline25','offline-round25.py');r19=q32.r19;W=q32.W;N=12;STEP=6;HANN=q32.HANN;MATERIAL=q32.OUT;OUT=MATERIAL.parent/'round-33';GEOMETRY_MEAN='computed baseline without legacy rectangular faults'

def load_material(out):
    approved=json.loads((MATERIAL/'dictionary-approved-manifest.json').read_text(encoding='utf-8'));review=json.loads((MATERIAL/'dictionary-review.json').read_text(encoding='utf-8'));draft=json.loads((MATERIAL/'dictionary-manifest.json').read_text(encoding='utf-8'));assert approved['count']==251;assert sha(MATERIAL/'dictionary-approved.npz')==approved['sha256'];assert sha(MATERIAL/'dictionary.npz')==review['dictionary_sha256']==draft['npz_sha256'];z=np.load(MATERIAL/'dictionary-approved.npz');d={k:z[k] for k in z.files};assert len(d['original_id'])==251;assert set(d['original_id'])==set(approved['approved_ids']);excluded={int(k) for k in review['excluded_ids']};assert not excluded.intersection(d['original_id'].tolist())
    for name in ['dictionary-approved.npz','dictionary-approved-manifest.json','dictionary-review.json','dictionary-manifest.json']:shutil.copyfile(MATERIAL/name,out/name)
    return d,{'approved_npz_sha256':approved['sha256'],'review_sha256':sha(MATERIAL/'dictionary-review.json'),'draft_npz_sha256':draft['npz_sha256'],'approved_manifest_sha256':sha(MATERIAL/'dictionary-approved-manifest.json'),'source_photo_sha256':draft['source_sha256'],'excluded_ids':sorted(excluded),'limitation':q32.LIMIT}

def current_patches(baseline):
    l=baseline[:,:,:3].astype(float)@W/255;low=cv2.GaussianBlur(l,(0,0),1.2);patches=[];h,w=l.shape
    for y in range(0,h-N+1,STEP):
        for x in range(0,w-N+1,STEP):
            if np.all(baseline[y:y+N,x:x+N,3]==255):
                f=q32.features(low[y:y+N,x:x+N])
                if f[4]>=.04 and f[2]>1e-9:patches.append((x,y,f))
    return {'l':l,'low':low,'patches':patches}

def top_candidates(feature,d):
    shape,angle,strength,polarity,contrast=feature;source_angle=d['angle']+np.where(d['polarity']<0,np.pi,0);current_angle=angle+(np.pi if polarity<0 else 0);diff=np.abs((source_angle-current_angle+np.pi)%(2*np.pi)-np.pi);ratios=contrast/d['contrast'];valid=(diff<=np.deg2rad(15))&(ratios>=.5)&(ratios<=2);eligible=np.flatnonzero(valid)
    if not len(eligible):return None
    distances=np.mean((d['shape'][eligible]-shape)**2,axis=1);order=np.argsort(distances,kind='stable')[:8];top=eligible[order];ratio=contrast/d['contrast'][top];return top,d['residual'][top]*ratio[:,None,None],distances[order],ratio

def min_path(error):
    # Vertical path: adjacent rows differ by at most one column. Transpose for a horizontal path.
    rows,cols=error.shape;cost=error[0].copy();back=np.zeros((rows,cols),np.int16);xx=np.arange(cols)
    for y in range(1,rows):
        options=np.stack([np.r_[np.inf,cost[:-1]],cost,np.r_[cost[1:],np.inf]]);choice=np.argmin(options,axis=0);back[y]=np.clip(xx+choice-1,0,cols-1);cost=error[y]+options[choice,xx]
    path=np.empty(rows,np.int16);path[-1]=int(np.argmin(cost))
    for y in range(rows-1,0,-1):path[y-1]=back[y,path[y]]
    return path,float(cost[path[-1]])

def sew_mask(old,covered,new):
    error=(old-new)**2;take=np.ones((N,N),bool);seamcost=0.;cuts=0
    if covered[:,:STEP].any():
        path,cost=min_path(np.where(covered[:,:STEP],error[:,:STEP],0));take[:,:STEP]&=np.arange(STEP)[None,:]>path[:,None];seamcost+=cost;cuts+=1
    if covered[:STEP,:].any():
        path,cost=min_path(np.where(covered[:STEP,:],error[:STEP,:],0).T);take[:STEP,:]&=np.arange(STEP)[:,None]>path[None,:];seamcost+=cost;cuts+=1
    # Intersection of the new sides of left/top cuts; retain old if either cut requests old.
    return (~covered)|take,seamcost,cuts

def compose(baseline,current,d,method):
    h,w=current['l'].shape;canvas=np.zeros((h,w));covered=np.zeros((h,w),bool);weight=np.zeros((h,w));second=np.zeros((h,w));winner=np.full((h,w),-1,np.int16);placement_map=np.full((h,w),-1,np.int32);largest=np.zeros((h,w));grid=np.full(((h-N)//STEP+1,(w-N)//STEP+1),-1,np.int16);usage=np.zeros(len(d['original_id']),int);placements=[];seamcost=0.;cuts=0;costs=[]
    for x,y,f in current['patches']:
        candidates=top_candidates(f,d)
        if candidates is None:continue
        top,patches,distances,ratios=candidates;region=np.s_[y:y+N,x:x+N];overlap=covered[region]
        if method=='graded':
            old=np.divide(canvas[region],weight[region],out=np.zeros((N,N)),where=weight[region]>1e-12);ow=HANN*overlap
            if ow.sum()>0:score=np.sum((patches-old[None,:,:])**2*ow[None,:,:],axis=(1,2))/ow.sum()+distances*1e-12
            else:score=distances
        else:
            old=canvas[region]
            if overlap.any():
                mse=np.mean((patches[:,overlap]-old[overlap][None,:])**2,axis=1);old_energy=float(np.mean(old[overlap]**2));candidate_energy=np.mean(patches[:,overlap]**2,axis=1);score=mse/np.maximum(1e-12,old_energy+candidate_energy)+distances
            else:score=distances
        choice=int(np.argmin(score));row=int(top[choice]);patch=patches[choice];original_id=int(d['original_id'][row]);pid=len(placements);placements.append((x,y,row,float(ratios[choice])));usage[row]+=1;grid[y//STEP,x//STEP]=original_id;costs.append(float(score[choice]))
        if method=='graded':
            canvas[region]+=patch*HANN;weight[region]+=HANN;second[region]+=patch*patch*HANN;new=HANN>largest[region];winner[region][new]=original_id;placement_map[region][new]=pid;largest[region][new]=HANN[new];covered[region]|=HANN>1e-12
        else:
            take,cost,cut=sew_mask(old,overlap,patch);canvas[region][take]=patch[take];winner[region][take]=original_id;placement_map[region][take]=pid;covered[region]=True;seamcost+=cost;cuts+=cut
    if method=='graded':
        canvas=np.divide(canvas,weight,out=np.zeros_like(canvas),where=weight>1e-12);expected=np.divide(second,weight,out=np.zeros_like(second),where=weight>1e-12);reference_energy=float(expected[covered].mean()) if covered.any() else 0;retention=float(np.mean(canvas[covered]**2)/max(1e-15,reference_energy)) if covered.any() else 0;witness=None
    else:
        yy,xx=np.where(covered);pids=placement_map[yy,xx];array=np.asarray(placements).reshape(-1,4);indices=array[pids,2].astype(int);ratios=array[pids,3];values=d['residual'][indices,yy-array[pids,1].astype(int),xx-array[pids,0].astype(int)]*ratios;error=float(np.abs(canvas[yy,xx]-values).max()) if len(yy) else 0;assert error==0.;reference_energy=float(np.mean(values**2)) if len(values) else 0;retention=float(np.mean(canvas[covered]**2)/max(1e-15,reference_energy)) if covered.any() else 0;witness={'max_abs_pixel_error_vs_one_unaveraged_source_sample':error,'pixels':len(yy)}
    seamx=covered[:,1:]&covered[:,:-1]&(placement_map[:,1:]!=placement_map[:,:-1]);seamy=covered[1:]&covered[:-1]&(placement_map[1:]!=placement_map[:-1]);jumps=np.r_[np.abs(np.diff(canvas,axis=1))[seamx],np.abs(np.diff(canvas,axis=0))[seamy]];valid=baseline[:,:,3]>0;neighbors=(grid[:,:-1]>=0)&(grid[:,1:]>=0);stats={'method':method,'matched_patches':len(placements),'coverage_fraction':float(covered[valid].mean()),'covered_pixels':int((covered&valid).sum()),'unique_source_ids':int((usage>0).sum()),'usage':{str(int(d['original_id'][i])):int(n) for i,n in enumerate(usage) if n},'maximum_source_repeat_share':float(usage.max()/max(1,len(placements))),'adjacent_left_same_ID_fraction':float(((grid[:,:-1]==grid[:,1:])&neighbors).sum()/max(1,neighbors.sum())),'residual_RMS':float(np.sqrt(np.mean(canvas[covered]**2))) if covered.any() else 0,'pre_average_source_energy':reference_energy,'energy_retention_relative_to_own_source_samples':retention,'hard_source_witness':witness,'seam_cut_count':cuts,'seam_DP_cost_sum':seamcost,'seam_boundary_pairs':len(jumps),'seam_abs_jump_mean':float(jumps.mean()) if len(jumps) else 0,'seam_abs_jump_max':float(jumps.max()) if len(jumps) else 0,'selection_cost_quantiles':np.quantile(costs,[0,.5,.9,1]).tolist() if costs else []};return {'residual':canvas,'covered':covered,'source_id_grid':grid,'winner':winner,'placement_map':placement_map,'placements':np.asarray(placements).reshape(-1,4),'stats':stats}

def render(raw,baseline,current,result,amplitude):
    rgb=baseline[:,:,:3].astype(float)/255;bl=rgb@W;target=bl.copy();mask=result['covered'];target[mask]=current['low'][mask]+result['residual'][mask]*amplitude;delta=target-bl;lower=-rgb.min(axis=2);upper=1-rgb.max(axis=2);delta=np.clip(delta,lower,upper);visible=raw[:,:,3]>0;distance=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);h,w=delta.shape;hh=h//8*8;ww=w//8*8
    def blocks(a):return a[:hh,:ww].reshape(hh//8,8,ww//8,8).transpose(0,2,1,3).reshape(-1,64)
    valid=blocks((raw[:,:,3]==255)&(distance>20)).all(axis=1);d=blocks(delta).copy();original=d[valid].copy();lo=blocks(lower)[valid];hi=blocks(upper)[valid];left=np.min(original-hi,axis=1);right=np.max(original-lo,axis=1)
    for _ in range(52):
        mid=(left+right)/2;mean=np.clip(original-mid[:,None],lo,hi).mean(axis=1);left=np.where(mean>0,mid,left);right=np.where(mean>0,right,mid)
    d[valid]=np.clip(original-((left+right)/2)[:,None],lo,hi);delta[:hh,:ww]=d.reshape(hh//8,ww//8,8,8).transpose(0,2,1,3).reshape(hh,ww);candidate=rgb+delta[:,:,None];t=np.clip((distance-2)/18,0,1);fade=t*t*(3-2*t);final_float=raw[:,:,:3]/255+(candidate-raw[:,:,:3]/255)*fade[:,:,None];a=raw.copy();a[:,:,:3]=np.clip(np.rint(final_float*255),0,255).astype(np.uint8);a[~visible]=raw[~visible];boundary=visible&(distance<=2);assert np.array_equal(a[boundary],raw[boundary]) and np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[~visible],raw[~visible]);float_errors=[float(np.abs(blocks(final_float[:,:,c]-rgb[:,:,c])[valid].mean(axis=1)).max()) if valid.any() else 0 for c in range(3)];errors=[float(np.abs(blocks(a[:,:,c].astype(float)-baseline[:,:,c])[valid].mean(axis=1)).max()) if valid.any() else 0 for c in range(3)];assert max(float_errors)<1e-12 and max(errors)<=.50000001;return a,{'geometry_mean_reference':GEOMETRY_MEAN,'LL8_complete_blocks':int(valid.sum()),'LL8_float_RGB_mean_max_error':float_errors,'LL8_8bit_RGB_mean_max_error_LSB':errors,'inner2px_exact':int(boundary.sum()),'alpha_hidden_exact':True,'amplitude':amplitude,'post_projection_delta_RMS':float(np.sqrt(np.mean(delta[visible]**2)))}

def synth(out,d,lookup):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=96,144;y,x=np.indices((h,w));report={}
    for name,l in [('flat',np.full((h,w),.4)),('positive',np.where(x<72,.2,.7)),('negative',np.where(x<72,.7,.2))]:
        raw=np.dstack([np.clip(np.rint(np.repeat(l[:,:,None],3,axis=2)*255),0,255).astype(np.uint8),np.full((h,w),255,np.uint8)]);parts=q25.signals(raw,lookup);baseline=q25.stage(raw,parts,0.);current=current_patches(baseline);g=compose(baseline,current,d,'graded');q=compose(baseline,current,d,'quilt');report[name]={};Image.fromarray(baseline).save(folder/f'{name}-baseline.png')
        for mode,result,amp in [('graded',g,1.),('quilt',q,1.),('dense',q,2.)]:
            a,lock=render(raw,baseline,current,result,amp);assert np.isfinite(result['residual']).all();data={'matched':result['stats']['matched_patches'],'residual_RMS':result['stats']['residual_RMS'],'LL8':lock};Image.fromarray(a).save(folder/f'{name}-{mode}.png');labelled_pair(Image.fromarray(baseline),Image.fromarray(a),(0,0,w,h),folder/f'{name}-{mode}-comparison.png',f'{name}-{mode}.png',f'{name}-baseline.png')
            if name=='flat':assert not result['covered'].any() and not result['residual'].any() and np.array_equal(a,baseline);data['baseline_expected_RGBA_exact']=True
            else:
                b=(baseline[:,:,:3]@W/255).mean(axis=0);v=(a[:,:,:3]@W/255).mean(axis=0);threshold=(b[:40].mean()+b[100:].mean())/2;bb=cv2.GaussianBlur(b[None,:],(0,1),3)[0];vv=cv2.GaussianBlur(v[None,:],(0,1),3)[0];shift=int(np.argmin(abs(vv-threshold))-np.argmin(abs(bb-threshold)));assert abs(shift)<=4 and (b[:40].mean()-b[100:].mean())*(v[:40].mean()-v[100:].mean())>0;data.update({'baseline_edge_mean_shift_px':shift,'polarity_preserved':True})
            report[name][mode]=data
    (out/'synthetic-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return report

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');files={'base':'base-B.png','baseline':'B-baseline-protected.png',**{k:v['file'] for k,v in m['outputs'].items()}};images={k:'data:image/png;base64,'+base64.b64encode((out/v).read_bytes()).decode() for k,v in files.items()};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>微細模様の平均と縫合を比較</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px}</style><div class="wrap"><h1>微細模様の平均と縫合を比較</h1><p>参照の微細模様を転写する試作です。平均の基準は矩形欠けなしの加工下地で、元写真の平均とは異なります。</p><p>細線の内容混入や反復は残り得ます。意味のある元物体の形が認識できる候補は採用しません。</p><select id="mode" aria-label="加工の段階"><option value="graded">明暗整理＋平均合成</option><option value="quilt">明暗整理＋硬い縫合</option><option value="dense">同じ縫合＋振幅2倍</option><option value="baseline">転写前の下地</option><option value="base">元B</option></select></div><div class="image"><img id="image" alt="平均と縫合の比較"></div><script>const images=IMAGES,s=document.getElementById('mode');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def snapshots(out):
    pending=[q25,q32];seen=set();files={Path(__file__).name:Path(__file__)}
    while pending:
        mod=pending.pop()
        if id(mod) in seen:continue
        seen.add(id(mod));f=getattr(mod,'__file__',None)
        if f and Path(f).resolve().parent==HERE:files[Path(f).name]=Path(f);pending.extend(v for v in vars(mod).values() if isinstance(v,types.ModuleType))
    folder=out/'source-snapshots';folder.mkdir(exist_ok=True)
    for name,p in files.items():shutil.copyfile(p,folder/name)
    (out/'source-frozen.json').write_text(json.dumps({'files':{str(p.resolve()):sha(p) for p in files.values()}},ensure_ascii=False,indent=2),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);d,provenance=load_material(out);oldindex=json.loads((MATERIAL/'artifact-index.json').read_text(encoding='utf-8'));assert all(sha(MATERIAL/k)==v for k,v in oldindex['files'].items());src=r19.SOURCE/'base-B.png';assert sha(src)==q32.BASESHA;raw=np.array(Image.open(src).convert('RGBA'));lookup=json.loads(q25.r.r22.r11.LOOKUP.read_text(encoding='utf-8'));parts=q25.signals(raw,lookup);baseline=q25.stage(raw,parts,0.);assert np.array_equal(baseline,q25.stage(raw,q25.signals(raw,lookup),0.));Image.fromarray(baseline).save(out/'B-baseline-no-faults.png');frozen=r19.SOURCE.parent/'round-25/B-zero-stage.png';assert sha(out/'B-baseline-no-faults.png')==sha(frozen);protected,_=r19.protect(raw,baseline[:,:,:3]/255);Image.fromarray(protected).save(out/'B-baseline-protected.png');r19.black(protected,out/'B-baseline-no-faults-on-black.png')
    for name in ['base-B.png','B-OFF.png']:
        p=out/name
        if p.exists():assert sha(p)==q32.BASESHA
        else:shutil.copyfile(src,p)
    r19.black(raw,out/'base-B-on-black.png');current=current_patches(baseline);graded=compose(baseline,current,d,'graded');quilt=compose(baseline,current,d,'quilt');again_g=compose(baseline,current,d,'graded');again_q=compose(baseline,current,d,'quilt');assert np.array_equal(graded['residual'],again_g['residual']) and np.array_equal(graded['source_id_grid'],again_g['source_id_grid']);assert np.array_equal(quilt['residual'],again_q['residual']) and np.array_equal(quilt['winner'],again_q['winner']);m={'round':33,'source_sha256':sha(Path(__file__)),'input':{'path':str(src.resolve()),'sha256':q32.BASESHA,'OFF_exact':sha(out/'B-OFF.png')==q32.BASESHA},'baseline':{'computed_from_raw':True,'mix':0.,'legacy_faults_calculated_but_pixel_contribution':0.,'frozen_assert_only_path':str(frozen),'sha256':sha(out/'B-baseline-no-faults.png'),'exact_frozen25_zero_stage':True},'geometry_mean_reference':GEOMETRY_MEAN,'raw8block_means_preserved':False,'reference_material':provenance,'matching':'baseline Gsigma1.2; reviewed fixed251; size12 stride6 directed-angle15 ratio.5..2 shapeTop8','quilt_selection':'overlapMSE/(existing_overlap_RMS^2+candidate_overlap_RMS^2) + shapeDistance; no overlap means shapeDistance','seam_combination':'left6 vertical DP plus top6 horizontal DP; new-side masks intersect at corner, uncovered pixels always new; no Hann or alpha averaging','dense':'same quilt canvas, placements, source-ID and hard mask; amplitude2 only','old23_atlas_or28kernel_used':False,'outputs':{}}
    for mode,result,amp in [('graded',graded,1.),('quilt',quilt,1.),('dense',quilt,2.)]:
        a,lock=render(raw,baseline,current,result,amp);again=again_g if mode=='graded' else again_q;assert np.array_equal(a,render(raw,baseline,current,again,amp)[0]);file=f'B-{mode}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'B-{mode}-on-black.png');q32.id_image(result['winner']).save(out/f'B-{mode}-source-ID-map.png');np.savez_compressed(out/f'B-{mode}-transfer.npz',residual=result['residual']*amp,covered=result['covered'],source_id_grid=result['source_id_grid'],pixel_winner=result['winner'],placement_map=result['placement_map'],placements=result['placements']);(out/f'B-{mode}-usage.json').write_text(json.dumps(result['stats'],indent=2),encoding='utf-8')
        for label,box in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():labelled_pair(Image.fromarray(protected),Image.fromarray(a),box,out/f'B-{mode}-comparison-{label}.png',file,'再計算した矩形欠けなし下地')
        for label,box in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650)}.items():labelled_pair(Image.fromarray(raw),Image.fromarray(a),box,out/f'B-{mode}-raw-comparison-{label}.png',file,'元B')
        assert not set(int(k) for k in result['stats']['usage']).intersection(provenance['excluded_ids']);m['outputs'][mode]={'file':file,'sha256':sha(out/file),'opaque_sha256':sha(out/f'B-{mode}-on-black.png'),'amplitude':amp,'repeat_exact':True,'statistics':result['stats'],'LL8':lock,'diagnostics':r19.diagnostics(a,raw)};print(mode,m['outputs'][mode]['sha256'],json.dumps({'rms':result['stats']['residual_RMS']*amp,'coverage':result['stats']['coverage_fraction'],'energy_retention':result['stats']['energy_retention_relative_to_own_source_samples'],'LL8':lock}),flush=True)
    zq=np.load(out/'B-quilt-transfer.npz');zd=np.load(out/'B-dense-transfer.npz');assert np.array_equal(zq['source_id_grid'],zd['source_id_grid']) and np.array_equal(zq['pixel_winner'],zd['pixel_winner']) and np.array_equal(zq['placement_map'],zd['placement_map']) and np.array_equal(zd['residual'],zq['residual']*2);m['quilt_dense_source_ID_and_placement_exact']=True;m['synthetic']=synth(out,d,lookup);assert sha(src)==q32.BASESHA and all(sha(MATERIAL/k)==v for k,v in oldindex['files'].items());m['R32_artifact_files_unchanged']=len(oldindex['files']);preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');snapshots(out);print('Round33 complete',flush=True)
if __name__=='__main__':main()
