"""R35 controlled additive R23 experiment: half-up row sampling and screen-raster coordinates."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys,types
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from build import sha,labelled_pair
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent
s=importlib.util.spec_from_file_location('frozen23',HERE/'offline-round23.py');q=importlib.util.module_from_spec(s);s.loader.exec_module(q)
ROOT=q.DEFAULT.parent;OUT=ROOT/'round-35';MODES={'nearest':1.25,'raster125':1.25,'raster100':1.};BASESHA='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9'
ROIS={'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}

def sample(a,mx,my,nearest_y):
    # Integer half-up Y prevents cv2's bilinear Y blend. X retains cv2 INTER_LINEAR exactly.
    if nearest_y:my=np.floor(my.astype(np.float64)+.5).astype(np.float32)
    return cv2.remap(a,mx.astype(np.float32),my.astype(np.float32),cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT,borderValue=0)

def strip(atlases,length,seed,raster):
    rng=np.random.default_rng(seed);result=np.zeros((0,49),np.float32);sequence=[]
    while len(result)<length:
        index=int(rng.choice(len(atlases),p=[.45,.45,.10]));a=atlases[index];drawn_stretch=float(rng.uniform(.8,1.6));n=int(a.shape[0]*rng.uniform(.8,1.5));drawn_phase=float(rng.uniform(0,a.shape[0]-1));stretch=1. if raster else drawn_stretch;phase=float(np.floor(drawn_phase+.5)) if raster else drawn_phase;t=np.mod(phase+np.arange(n)/stretch,2*(a.shape[0]-1));t=np.where(t<=a.shape[0]-1,t,2*(a.shape[0]-1)-t);mx=np.repeat(np.arange(49,dtype=np.float32)[None,:],n,axis=0);my=np.repeat(t.astype(np.float32)[:,None],49,axis=1);block=sample(a,mx,my,True);block*=.40 if index==2 else 1;seam=0
        if len(result):
            seam=min(16,len(result),len(block));u=np.linspace(0,1,seam,dtype=np.float32)[:,None];result=np.concatenate([result[:-seam],result[-seam:]*(1-u)+block[:seam]*u,block[seam:]])
        else:result=block
        sequence.append({'atlas_id':index+1,'stretch':stretch,'phase':phase,'rows':n,'seam_blend_rows':seam,'rng_draw_stretch':drawn_stretch,'rng_draw_phase':drawn_phase,'source_row_half_up':np.floor(t+.5).astype(int).tolist()})
    return result[:length],sequence

def coordinates(path,width,mode,w):
    center=path['x'];slope=path['slope'];raster=mode.startswith('raster');arc=np.cumsum(np.sqrt(1+slope*slope));scale=1. if raster else np.sqrt(1+np.max(slope*slope));radius=24*width*scale;x0=max(0,int(np.floor(center.min()-radius)));x1=min(w,int(np.ceil(center.max()+radius))+1);xx=np.arange(x0,x1)[None,:];normal=path['polarity']*(xx-center[:,None]);normal=normal if raster else normal/np.sqrt(1+slope[:,None]**2);u=normal/width;row=np.arange(len(center),dtype=float) if raster else arc;row+=path['seed']%431;mx=(u+24).astype(np.float32);my=np.repeat(row.astype(np.float32)[:,None],x1-x0,axis=1);return x0,x1,u,mx,my,arc,row

def apply(surface,paths,atlases,mode):
    h,w=surface.shape[:2];acc=np.zeros((h,w));weights=np.zeros((h,w));sequences=[];width=MODES[mode]
    for path in paths:
        ys=path['y'];x0,x1,u,mx,my,arc,row=coordinates(path,width,mode,w);raster=mode.startswith('raster');length=max(4096,int(len(ys) if raster else arc[-1])+600);field,seq=strip(atlases,length,path['seed'],raster)
        if x1<=x0:continue
        residual=sample(field,mx,my,True);weight=np.clip((24-np.abs(u))/6,0,1)*np.exp(-(u/14)**2)*(path['contrast'][:,None]>.08);delta=residual*path['contrast'][:,None]*.60;region=np.ix_(ys,np.arange(x0,x1));acc[region]+=delta*weight;weights[region]+=weight;sequences.append({'seed':path['seed'],'source_blocks':seq,'rows':len(ys),'polarity':path['polarity'],'mapped_source_row_half_up':np.floor(row+.5).astype(int).tolist(),'coordinate':'screen raster' if raster else 'R23 arc/normal'})
    delta=np.clip(acc/np.maximum(1,weights),-.30,.30);return delta,{'path_count':len(paths),'active_delta_pixels':int((abs(delta)>.001).sum()),'maximum_abs_delta':float(abs(delta).max()),'phase_sequences':sequences}

def checker_tests(out):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);n=64;rows=np.arange(n,dtype=np.float32);amplitude=.2;reports={};plot_data={}
    for polarity in [1,-1]:
        a=(np.where(np.arange(128)%2,1,-1)*amplitude*polarity).astype(np.float32)[:,None]*np.ones((1,49),np.float32);mx=np.repeat(np.arange(49,dtype=np.float32)[None,:],n,0);my=np.repeat((rows+.5)[:,None],49,1);old_source=sample(a,mx,my,False);new_source=sample(a,mx,my,True);current_mx=np.full((n-2,1),24.25,np.float32);current_my=(np.arange(n-2,dtype=np.float32)+.5)[:,None];old=sample(old_source,current_mx,current_my,False)[:,0];new=sample(new_source,current_mx,current_my,True)[:,0];expected=np.where(np.arange(n-2)%2,1,-1)*amplitude*polarity;assert np.allclose(new,expected,atol=1e-7) and abs(old).max()<1e-7
        alternate=lambda x:float(np.mean(x[:-1]*x[1:]<0));r={'linear_peak':float(abs(old).max()),'nearest_peak':float(abs(new).max()),'linear_variance':float(old.var()),'nearest_variance':float(new.var()),'nearest_sign_alternation_rate':alternate(new),'linear_sign_alternation_rate':alternate(old),'expected_signed_value_max_error':float(abs(new-expected).max()),'source_and_current_half_phase':.5,'X_sampling':'linear at24.25','Y_sampling':'floor(y+.5) at both stages'};assert r['nearest_sign_alternation_rate']==1 and r['nearest_variance']>.0399;reports[str(polarity)]=r;plot_data[str(polarity)]={'linear':old.tolist(),'nearest':new.tolist()};np.savetxt(folder/f'checker-{polarity}-row-profile.csv',np.c_[np.arange(n-2),old,new,expected],delimiter=',',header='output_row,linear,half_up_nearest,expected',comments='')
        panel=np.repeat(np.vstack([old,new]),20,axis=0);Image.fromarray(q.r19.u8(.5+panel)).resize((620,80),Image.Resampling.NEAREST).save(folder/f'checker-{polarity}-mapped-rows.png')
    image=Image.new('RGB',(1050,500),'white');draw=ImageDraw.Draw(image);font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',18)
    for k,polarity in enumerate([1,-1]):
        y0=70+k*210;draw.text((20,y0-40),f'1px交代線 / 極性 {polarity:+} / Y半画素を2回写す',font=font,fill='black');draw.line((60,y0+60,1010,y0+60),fill=(140,140,140));draw.text((15,y0-8),'+0.2',fill='black');draw.text((15,y0+110),'-0.2',fill='black')
        for name,color in [('linear',(120,120,120)),('nearest',(26,26,26))]:
            values=plot_data[str(polarity)][name];points=[(60+i*950/(len(values)-1),y0+60-v*300) for i,v in enumerate(values)];draw.line(points,fill=color,width=1)
        draw.text((60,y0+130),'横軸:出力画素行0..61 / 黒:半上げnearest / 灰:linear',font=font,fill='black')
    image.save(folder/'source-row-profile.png')
    densities={};ys=np.arange(80);surface=np.zeros((100,220,3),np.float32)
    for slope in [0.,.3,.9]:
        path={'x':60+ys*slope,'slope':np.full(len(ys),slope),'seed':230023,'polarity':1,'contrast':np.full(len(ys),.5),'y':ys};v={}
        for mode in MODES:
            row=coordinates(path,MODES[mode],mode,220)[-1];v[mode]={'mean_source_row_advance':float(np.diff(row).mean()),'integer_source_rows':np.floor(row+.5).astype(int).tolist()}
            if mode.startswith('raster'):assert np.all(np.diff(row)==1)
        densities[str(slope)]=v
    assert densities['0.0']['raster125']['integer_source_rows']==densities['0.9']['raster125']['integer_source_rows'];return {'checker':reports,'slope_row_density':densities,'checker_fixture_limit':'16row source-block seams intentionally retain R23 linear blending; checker isolates row sampling away from seams'}

def synthetic(out,atlases):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=160,280;y,x=np.indices((h,w));report={}
    for name,l in [('flat',np.full((h,w),.4)),('positive',np.where(x<130,.2,.7)),('negative',np.where(x<130,.7,.2)),('diagonal',np.where(x<90+y*.3,.2,.7))]:
        rgb=q.r9.gray8(np.repeat(l[:,:,None],3,2)).astype(np.float32)/255;alpha=np.full((h,w),255,np.uint8);raw=np.dstack([q.r9.gray8(rgb),alpha]);paths,_,_=q.tracks(rgb,alpha);Image.fromarray(raw).save(folder/f'{name}-input.png');report[name]={}
        for mode in MODES:
            delta,info=apply(rgb,paths,atlases,mode);assert np.isfinite(delta).all();a,_=q.r19.protect(raw,rgb+delta[:,:,None]);Image.fromarray(a).save(folder/f'{name}-{mode}.png');labelled_pair(Image.fromarray(raw),Image.fromarray(a),(0,0,w,h),folder/f'{name}-{mode}-comparison.png',f'{name}-{mode}.png',f'{name}-input.png');r={'paths':len(paths),'new_pixels':int(np.any(a[:,:,:3]!=raw[:,:,:3],2).sum()),'finite':True}
            if name=='flat':assert not delta.any() and np.array_equal(a,raw);r['flat_exact']=True
            else:
                before=raw[:,:,:3].mean(2).mean(0);after=a[:,:,:3].mean(2).mean(0);threshold=(before[:30].mean()+before[-30:].mean())/2
                def crossings(values):
                    indices=np.flatnonzero(np.diff(np.signbit(values-threshold)));return indices+(threshold-values[indices])/(values[indices+1]-values[indices])
                original_cross=crossings(before);new_cross=crossings(after);assert len(original_cross) and len(new_cross);center=float(original_cross[0]);position=float(new_cross[np.argmin(abs(new_cross-center))]);shift=position-center;assert abs(shift)<=6 and (before[:30].mean()-before[-30:].mean())*(after[:30].mean()-after[-30:].mean())>0;r.update({'mean_edge_shift_px':shift,'polarity_preserved':True,'measurement':'interpolated half-plateau crossing nearest the original crossing; avoids flat-plateau argmin ties'})
            report[name][mode]=r
    return report

def preview(out):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');files={'base':'base-B.png','baseline':'B-baseline23.png',**{k:f'B-{k}.png' for k in MODES}};images={k:'data:image/png;base64,'+base64.b64encode((out/v).read_bytes()).decode() for k,v in files.items()};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>輪郭残差を走査行へ写す</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>輪郭残差を走査行へ写す</h1><p>23版の加算方式を保ち、行方向の補間と写し方だけを変えています。参照の輪郭残差には元形や反射が残り得ます。</p><select id="mode" aria-label="行の写し方"><option value="baseline">元の23standard</option><option value="nearest">行だけnearest / 幅1.25</option><option value="raster125">走査行 / 幅1.25</option><option value="raster100">走査行 / 幅1.00</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="行の写し方の比較"></div><script>const images=IMAGES,s=document.getElementById('mode');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);out=ap.parse_args().out;out.mkdir(parents=True,exist_ok=True);old=ROOT/'round-34';idx=json.loads((old/'artifact-index.json').read_text(encoding='utf-8'));assert all(sha(old/k)==v for k,v in idx['files'].items());atlases,am=q.load_atlas(q.DEFAULT);source_hashes={str(HERE/'offline-round23.py'):sha(HERE/'offline-round23.py'),**{str(q.DEFAULT/i['file']):i['sha256'] for i in am['items']}};src=q.SOURCE/'base-B.png';assert sha(src)==BASESHA;raw=np.array(Image.open(src).convert('RGBA'));lookup=json.loads(q.r22.r11.LOOKUP.read_text(encoding='utf-8'));stats=json.loads((q.r14.DEFAULT/'reference-residual-statistics.json').read_text(encoding='utf-8'));stage,constant=q.r22.stage12(raw,lookup);surface=q.r19.surface(raw);paths,peaks,_=q.tracks(surface,raw[:,:,3]);noise=np.zeros(raw.shape[:2]) if constant else q.micro(stage,peaks,stats);old_delta,old_events=q.apply_residual(surface,paths,atlases,q.PROFILES['standard']);baseline,_=q.r19.protect(raw,stage[:,:,:3]/255+(old_delta+noise)[:,:,None]);Image.fromarray(baseline).save(out/'B-baseline23.png');q.r19.black(baseline,out/'B-baseline23-on-black.png');assert sha(out/'B-baseline23-on-black.png')=='4839e9c7bfe2848279c79f63aafdbaf9fe9f1abc477c34c8ee1509cd06de6e72';assert sha(out/'B-baseline23.png')==sha(q.DEFAULT/'B-standard.png');shutil.copyfile(src,out/'base-B.png');shutil.copyfile(src,out/'B-OFF.png');Image.fromarray(stage).save(out/'B-stage12.png');np.savez_compressed(out/'fixed-components.npz',micro=noise,baseline_residual=old_delta)
    m={'round':35,'input_path':str(src),'input_sha256':BASESHA,'baseline23_reconstructed_exact':True,'baseline_opaque_sha256':sha(out/'B-baseline23-on-black.png'),'fixed_gain':.60,'profiles':MODES,'mapping_only_changed':True,'stage12_legacy_faults':1.,'micro_unchanged':True,'track_paths_unchanged':True,'source_row_rounding':'floor(y+.5) half-up; never bankers rounding','source_block_seam':'existing16row linear crossfade retained','X_interpolation':'cv2 INTER_LINEAR at integer half-up Y','uses_R32_R33_or_completed_output_pixels':False,'reference_derived_scalar_material':am,'limitation':am['limitation'],'frozen_sources':source_hashes,'outputs':{}}
    for mode in MODES:
        delta,events=apply(surface,paths,atlases,mode);again,again_events=apply(surface,paths,atlases,mode);assert np.array_equal(delta,again) and events==again_events;a,count=q.r19.protect(raw,stage[:,:,:3]/255+(delta+noise)[:,:,None]);assert np.array_equal(a,q.r19.protect(raw,stage[:,:,:3]/255+(again+noise)[:,:,None])[0]);file=f'B-{mode}.png';Image.fromarray(a).save(out/file);q.r19.black(a,out/f'B-{mode}-on-black.png');np.savez_compressed(out/f'B-{mode}-mapped-residual.npz',residual=delta);Image.fromarray(q.r19.u8(.5+delta*2)).save(out/f'B-{mode}-mapped-residual-neutral.png');(out/f'B-{mode}-phase-sequences.json').write_text(json.dumps(events.pop('phase_sequences'),indent=2),encoding='utf-8')
        for label,box in ROIS.items():labelled_pair(Image.fromarray(baseline),Image.fromarray(a),box,out/f'B-{mode}-comparison-{label}.png',file,'再構築23standard')
        m['outputs'][mode]={'file':file,'sha256':sha(out/file),'opaque_sha256':sha(out/f'B-{mode}-on-black.png'),'events':events,'diagnostics_vs_raw':q.r19.diagnostics(a,raw),'diagnostics_vs_baseline23':q.r19.diagnostics(a,baseline),'alpha_hidden_inner_exact':True,'inner_pixels':count,'repeat_exact':True};assert count==7394;print(mode,json.dumps(m['outputs'][mode]),flush=True)
    b=json.loads((out/'B-raster125-phase-sequences.json').read_text());c=json.loads((out/'B-raster100-phase-sequences.json').read_text());assert b==c;m['raster125_100_source_blocks_phases_rows_exact']=True;m['sampling_calibration']=checker_tests(out);m['synthetic']=synthetic(out,atlases);assert sha(src)==BASESHA and sha(out/'B-OFF.png')==BASESHA and all(sha(Path(k))==v for k,v in source_hashes.items());assert all(sha(old/k)==v for k,v in idx['files'].items());m['R34_artifacts_unchanged']=len(idx['files']);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');(out/'verification.json').write_text(json.dumps({'baseline23_reconstructed_exact':True,'OFF_raw_byte_exact':True,'raw_source_unchanged':True,'source23_atlas_unchanged':True,'old34_artifacts_unchanged':len(idx['files']),'raster125_100_source_blocks_exact':True,'sampling_calibration':m['sampling_calibration'],'synthetic':m['synthetic'],'outputs':m['outputs']},ensure_ascii=False,indent=2),encoding='utf-8');preview(out)
    pending=[q];seen=set();files={Path(__file__).name:Path(__file__)}
    while pending:
        mod=pending.pop()
        if id(mod) in seen:continue
        seen.add(id(mod));f=getattr(mod,'__file__',None)
        if f and Path(f).resolve().parent==HERE:files[Path(f).name]=Path(f);pending.extend(v for v in vars(mod).values() if isinstance(v,types.ModuleType))
    folder=out/'source-snapshots';folder.mkdir(exist_ok=True)
    for name,p in files.items():shutil.copyfile(p,folder/name)
    (out/'source-frozen.json').write_text(json.dumps({'files':{str(p.resolve()):sha(p) for p in files.values()}},ensure_ascii=False,indent=2),encoding='utf-8');print('Round35 complete',flush=True)
if __name__=='__main__':main()
