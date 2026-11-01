"""R32 visual microtexture transfer; reference content is not a separated pure effect."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys,types
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('surface19',HERE/'offline-round19.py');r19=importlib.util.module_from_spec(s);s.loader.exec_module(r19)
OUT=r19.SOURCE.parent/'round-32';REF=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png');REFSHA='ba078b369e842b60746ef7aa82133347e11e7ef71709ee3088a0fba18cfe92d8';BASESHA='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9';N=12;STRIDE=6;SIGMA=1.2;W=r19.WEIGHTS
ROIS={'windows':([1080,30,1160,290],96),'desk':([680,250,780,390],64),'floor':([1130,810,1270,1040],96)}
LIMIT='参照の微細模様を転写する見た目試作。純粋な加工原因の抽出ではなく、細い元物体由来の形が残り得る。内容混入ゼロや汎用性は証明しない。意味のある元物体の形が認識できる出力は点数に関係なく不採用。'
Y,X=np.mgrid[-1:1:complex(N),-1:1:complex(N)];PLANE=np.stack([X.ravel(),Y.ravel(),np.ones(N*N)],axis=1);PINV=np.linalg.pinv(PLANE);HANN=np.outer(np.hanning(N),np.hanning(N))

def features(a):
    contrast=float(np.ptp(a));small=cv2.resize(a,(4,4),interpolation=cv2.INTER_AREA);shape=((small-small.mean())/max(contrast,1e-9)).ravel();gy,gx=np.gradient(a);xx=float(np.sum(gx*gx));yy=float(np.sum(gy*gy));xy=float(np.sum(gx*gy));angle=.5*np.arctan2(2*xy,xx-yy);strength=float(np.sqrt((xx+yy)/(N*N)));polarity=1 if float(np.sum(gx*np.cos(angle)+gy*np.sin(angle)))>=0 else -1;return shape,float(angle),strength,polarity,contrast

def spatial_subsample(candidates,limit,box):
    if len(candidates)<=limit:return candidates
    pos=np.array([[v['x'],v['y']] for v in candidates],float);pos=(pos-np.array(box[:2]))/np.array([box[2]-box[0],box[3]-box[1]]);chosen=[int(np.argmin(np.sum((pos-.5)**2,axis=1)))];distance=np.sum((pos-pos[chosen[0]])**2,axis=1)
    while len(chosen)<limit:
        distance[chosen]=-1;index=int(np.argmax(distance));chosen.append(index);distance=np.minimum(distance,np.sum((pos-pos[index])**2,axis=1))
    return [candidates[i] for i in sorted(chosen,key=lambda i:(candidates[i]['y'],candidates[i]['x']))]

def sheets(out,residues,items):
    font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',12);paths=[]
    for start in range(0,len(items),64):
        chunk=items[start:start+64];rows=(len(chunk)+7)//8;canvas=Image.new('RGB',(8*112,rows*92+34),(246,246,246));draw=ImageDraw.Draw(canvas);draw.text((8,6),f'参照由来のscalar微細模様 {start:03d}..{start+len(chunk)-1:03d} / 12×12, nearest 4倍, 表示 .5+2R',font=font,fill=(25,25,25))
        for j,item in enumerate(chunk):
            x=(j%8)*112;y=34+(j//8)*92;draw.text((x+4,y),f'{item["id"]:03d} {item["roi"]}',font=font,fill=(25,25,25));draw.text((x+4,y+16),f'{item["x"]},{item["y"]} 12×12',font=font,fill=(25,25,25));im=Image.fromarray(np.clip(np.rint((.5+residues[item['id']]*2)*255),0,255).astype(np.uint8));canvas.paste(im.resize((48,48),Image.Resampling.NEAREST),(x+4,y+36))
        file=f'dictionary-sheet-{start//64+1:02d}.png';canvas.save(out/file);paths.append(file)
    return paths

def build_dictionary(out):
    assert sha(REF)==REFSHA;rgb=np.array(Image.open(REF).convert('RGB')).astype(float)/255;l=rgb@W;low=cv2.GaussianBlur(l,(0,0),SIGMA);hi=l-low;items=[];residues=[];shapes=[];angles=[];strengths=[];polarities=[];contrasts=[];roi_stats={}
    for name,(box,limit) in ROIS.items():
        Image.open(REF).crop(box).save(out/f'reference-ROI-{name}.png');candidates=[];examined=0
        for y in range(box[1],box[3]-N+1,STRIDE):
            for x in range(box[0],box[2]-N+1,STRIDE):
                examined+=1;f=features(low[y:y+N,x:x+N])
                if f[4]>=.04:candidates.append({'x':x,'y':y,'feature':f})
        chosen=spatial_subsample(candidates,limit,box);roi_stats[name]={'box':box,'examined':examined,'contrast_eligible':len(candidates),'chosen':len(chosen),'max':limit,'selection':'deterministic farthest-point spatial subsample among stride6 eligible positions'}
        for v in chosen:
            x,y=v['x'],v['y'];patch=hi[y:y+N,x:x+N];plane=(PLANE@(PINV@patch.ravel())).reshape(N,N);residue=patch-plane;assert abs(residue.mean())<1e-12;idx=len(items);items.append({'id':idx,'roi':name,'x':x,'y':y,'size':[N,N],'source_sha256':REFSHA,'source_photo_is_reference_derived':True,'localcontrast':v['feature'][4]});residues.append(residue);f=v['feature'];shapes.append(f[0]);angles.append(f[1]);strengths.append(f[2]);polarities.append(f[3]);contrasts.append(f[4])
    assert 0<len(items)<=256;path=out/'dictionary.npz';np.savez_compressed(path,residual=np.array(residues),shape=np.array(shapes),angle=np.array(angles),strength=np.array(strengths),polarity=np.array(polarities),contrast=np.array(contrasts));pages=sheets(out,np.array(residues),items);m={'source_path':str(REF),'source_sha256':REFSHA,'encoded_luma_weights':W.tolist(),'Gaussian_sigma':SIGMA,'residual':'Y-Gsigma1.2Y; each12x12 patch least-squares ax+by+c removed','stride':STRIDE,'max_dictionary':256,'items':items,'roi_stats':roi_stats,'sheets':{p:sha(out/p) for p in pages},'npz_sha256':sha(path),'limitation':LIMIT};(out/'dictionary-manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Dictionary ready',len(items),len(pages),flush=True);return m

def load_dictionary(out,review_required=True):
    m=json.loads((out/'dictionary-manifest.json').read_text(encoding='utf-8'));assert sha(out/'dictionary.npz')==m['npz_sha256'];z=np.load(out/'dictionary.npz');data={k:z[k] for k in z.files};ids=np.arange(len(m['items']))
    if review_required:
        review=json.loads((out/'dictionary-review.json').read_text(encoding='utf-8'));assert review['dictionary_sha256']==m['npz_sha256'];assert set(review['opened_sheets'])==set(m['sheets']);ids=np.array([i for i in ids if str(i) not in review['excluded_ids']],int);assert len(ids)
    return data,m,ids

def current_patches(raw):
    rgb=raw[:,:,:3].astype(float)/255;raw_l=rgb@W;surface=r19.surface(raw)@W;low=cv2.GaussianBlur(surface,(0,0),SIGMA);patches=[];h,w=raw.shape[:2]
    for y in range(0,h-N+1,STRIDE):
        for x in range(0,w-N+1,STRIDE):
            if np.all(raw[y:y+N,x:x+N,3]==255):
                f=features(low[y:y+N,x:x+N])
                if f[4]>=.04 and f[2]>1e-9:patches.append((x,y,f))
    return {'raw_l':raw_l,'surface_l':surface,'low':low,'patches':patches}

def transfer(raw,current,dictionary,meta,ids,mode):
    ids=np.array([i for i in ids if mode=='all' or meta['items'][i]['roi']=='windows'],int);h,w=raw.shape[:2];acc=np.zeros((h,w));weight=np.zeros((h,w));winner=np.full((h,w),-1,np.int16);winner_weight=np.zeros((h,w));grid=np.full(((h-N)//STRIDE+1,(w-N)//STRIDE+1),-1,np.int16);usage=np.zeros(len(meta['items']),int);distances=[];ratios=[];matched=0
    if len(ids):
        for x,y,f in current['patches']:
            shape,angle,strength,polarity,contrast=f;source_direction=dictionary['angle'][ids]+np.where(dictionary['polarity'][ids]<0,np.pi,0);current_direction=angle+(np.pi if polarity<0 else 0);angle_dist=np.abs((source_direction-current_direction+np.pi)%(2*np.pi)-np.pi);ratio=contrast/dictionary['contrast'][ids];allowed=(angle_dist<=np.deg2rad(15))&(ratio>=.5)&(ratio<=2);eligible=ids[allowed]
            if not len(eligible):continue
            shape_dist=np.mean((dictionary['shape'][eligible]-shape)**2,axis=1);order=np.argsort(shape_dist,kind='stable')[:8];top=eligible[order];scales=contrast/dictionary['contrast'][top];candidates=dictionary['residual'][top]*scales[:,None,None];oldweight=weight[y:y+N,x:x+N];overlap=oldweight>1e-12
            if overlap.any():
                existing=np.divide(acc[y:y+N,x:x+N],oldweight,out=np.zeros((N,N)),where=overlap);ow=HANN*overlap;error=np.sum((candidates-existing[None,:,:])**2*ow[None,:,:],axis=(1,2))/max(1e-12,ow.sum());chosen=int(np.argmin(error+shape_dist[order]*1e-12))
            else:chosen=0
            idx=int(top[chosen]);residue=candidates[chosen];acc[y:y+N,x:x+N]+=residue*HANN;weight[y:y+N,x:x+N]+=HANN;use=HANN>winner_weight[y:y+N,x:x+N];winner[y:y+N,x:x+N][use]=idx;winner_weight[y:y+N,x:x+N][use]=HANN[use];grid[y//STRIDE,x//STRIDE]=idx;usage[idx]+=1;matched+=1;distances.append(float(shape_dist[order[chosen]]));ratios.append(float(scales[chosen]))
    covered=weight>1e-12;residual=np.divide(acc,weight,out=np.zeros_like(acc),where=covered);target=current['surface_l'].copy();target[covered]=current['low'][covered]+residual[covered];valid=raw[:,:,3]>0;selected=grid[grid>=0];neighbors=(grid[:,1:]>=0)&(grid[:,:-1]>=0);same=(grid[:,1:]==grid[:,:-1])&neighbors;stats={'mode':mode,'eligible_dictionary_ids':ids.tolist(),'current_contrast_eligible_patches':len(current['patches']),'matched_patches':matched,'valid_transfer_coverage_fraction':float(covered[valid].mean()),'valid_transfer_pixels':int((covered&valid).sum()),'synthesized_residue_RMS':float(np.sqrt(np.mean(residual[covered]**2))) if covered.any() else 0,'highpass_replacement_delta_RMS':float(np.sqrt(np.mean((target[covered]-current['surface_l'][covered])**2))) if covered.any() else 0,'source_usage':{str(i):int(v) for i,v in enumerate(usage) if v},'unique_source_ids':int((usage>0).sum()),'maximum_source_repeat_share':float(usage.max()/max(1,matched)),'adjacent_left_same_source_fraction':float(same.sum()/max(1,neighbors.sum())),'shape_distance_quantiles':np.quantile(distances,[0,.5,.9,1]).tolist() if distances else [],'amplitude_ratio_quantiles':np.quantile(ratios,[0,.5,.9,1]).tolist() if ratios else []};return target,grid,winner,covered,stats

def mean_lock(raw,target):
    rgb=raw[:,:,:3].astype(float)/255;raw_l=rgb@W;visible=raw[:,:,3]>0;distance=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);t=np.clip((distance-2)/18,0,1);fade=t*t*(3-2*t);delta=(target-raw_l)*fade;lower=-rgb.min(axis=2);upper=1-rgb.max(axis=2);delta=np.clip(delta,lower,upper);h,w=delta.shape;hh=h//8*8;ww=w//8*8
    def blocks(a):return a[:hh,:ww].reshape(hh//8,8,ww//8,8).transpose(0,2,1,3).reshape(-1,64)
    valid=np.all(blocks((raw[:,:,3]==255)&(distance>20)),axis=1);d=blocks(delta).copy();lo=blocks(lower)[valid];hi=blocks(upper)[valid];original=d[valid].copy();left=np.min(original-hi,axis=1);right=np.max(original-lo,axis=1)
    for _ in range(52):
        middle=(left+right)*.5;mean=np.clip(original-middle[:,None],lo,hi).mean(axis=1);positive=mean>0;left=np.where(positive,middle,left);right=np.where(positive,right,middle)
    d[valid]=np.clip(original-((left+right)*.5)[:,None],lo,hi);delta[:hh,:ww]=d.reshape(hh//8,ww//8,8,8).transpose(0,2,1,3).reshape(hh,ww);candidate=rgb+delta[:,:,None];assert candidate.min()>-1e-12 and candidate.max()<1+1e-12;float_error=float(np.abs(blocks(delta)[valid].mean(axis=1)).max()) if valid.any() else 0;assert float_error<1e-12;a=raw.copy();a[:,:,:3]=np.clip(np.rint(candidate*255),0,255).astype(np.uint8);a[~visible]=raw[~visible];boundary=visible&(distance<=2);assert np.array_equal(a[boundary],raw[boundary]);assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~visible],raw[~visible]);errors=[]
    for c in range(3):errors.append(float(np.abs(blocks(a[:,:,c].astype(float)-raw[:,:,c])[valid].mean(axis=1)).max()) if valid.any() else 0)
    assert max(errors)<=.50000001;return a,{'complete_interior_alpha255_LL8_blocks':int(valid.sum()),'float_block_mean_scalar_error':float_error,'rounded_block_mean_RGB_max_error_LSB':errors,'inner2px_exact':int(boundary.sum()),'alpha_hidden_exact':True}

def id_image(ids):
    rgb=np.zeros((*ids.shape,3),np.uint8);valid=ids>=0;n=ids[valid].astype(int)+1;rgb[valid]=np.stack([(n*73)%211+32,(n*151)%211+32,(n*199)%211+32],axis=1);return Image.fromarray(rgb)

def synthetic(out,dictionary,meta,ids):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=128,192;y,x=np.indices((h,w));report={}
    for name,l in [('flat',np.full((h,w),.4)),('positive',np.where(x<96,.2,.7)),('negative',np.where(x<96,.7,.2))]:
        raw=np.dstack([np.clip(np.rint(np.repeat(l[:,:,None],3,axis=2)*255),0,255).astype(np.uint8),np.full((h,w),255,np.uint8)]);current=current_patches(raw);report[name]={}
        for mode in ['window','all']:
            target,grid,winner,covered,stats=transfer(raw,current,dictionary,meta,ids,mode);again=transfer(raw,current,dictionary,meta,ids,mode)[0];assert np.array_equal(target,again);a,lock=mean_lock(raw,target);assert np.isfinite(target).all();Image.fromarray(a).save(folder/f'{name}-{mode}.png');labelled_pair(Image.fromarray(raw),Image.fromarray(a),(0,0,w,h),folder/f'{name}-{mode}-comparison.png',f'{name}-{mode}.png',name+'-input');data={'matched_patches':stats['matched_patches'],'changed_pixels':int(np.any(a!=raw,axis=2).sum()),'repeat_exact':True,'LL8':lock}
            if name=='flat':assert stats['matched_patches']==0 and np.array_equal(a,raw)
            else:
                before=(raw[:,:,:3]@W/255).mean(axis=0);after=(a[:,:,:3]@W/255).mean(axis=0);mid=(before.min()+before.max())/2;before_cross=int(np.argmin(np.abs(cv2.GaussianBlur(before[None,:],(0,1),3)[0]-mid)));after_cross=int(np.argmin(np.abs(cv2.GaussianBlur(after[None,:],(0,1),3)[0]-mid)));shift=after_cross-before_cross;assert abs(shift)<=4;assert (after[:64].mean()-after[128:].mean())*(before[:64].mean()-before[128:].mean())>0;data.update({'edge_column_shift_after_sigma3_px':shift,'polarity_preserved':True})
            report[name][mode]=data
    (out/'synthetic-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return report

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={key:'data:image/png;base64,'+base64.b64encode((out/file).read_bytes()).decode() for key,file in [('base','base-B.png'),('window','B-window.png'),('all','B-all.png')]};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>参照の微細模様を転写する試作</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px}</style><div class="wrap"><h1>参照の微細模様を転写する試作</h1><p>参照の元物体に由来する細い形が残り得ます。実際の加工原因や内容混入ゼロを証明するものではありません。</p><select id="mode" aria-label="辞書の範囲"><option value="window">窓の辞書のみ</option><option value="all">窓・机・床の辞書</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="微細模様の転写比較"></div><script>const images=IMAGES,s=document.getElementById('mode');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);ap.add_argument('--dictionary-only',action='store_true');args=ap.parse_args();out=args.out;out.mkdir(exist_ok=True,parents=True)
    if args.dictionary_only:build_dictionary(out);return
    dictionary,meta,ids=load_dictionary(out);src=r19.SOURCE/'base-B.png';assert sha(src)==BASESHA and sha(REF)==REFSHA;raw=np.array(Image.open(src).convert('RGBA'));current=current_patches(raw)
    for name in ['base-B.png','B-OFF.png']:
        p=out/name
        if p.exists():assert sha(p)==BASESHA
        else:shutil.copyfile(src,p)
    r19.black(raw,out/'base-B-on-black.png');m={'round':32,'source_sha256':sha(Path(__file__)),'input':{'path':str(src.resolve()),'sha256':BASESHA,'OFF_exact':sha(out/'B-OFF.png')==BASESHA},'reference_sha256':REFSHA,'dictionary_manifest_sha256':sha(out/'dictionary-manifest.json'),'dictionary_review_sha256':sha(out/'dictionary-review.json'),'limitation':LIMIT,'previous_stage12_fault_atlas_kernel_used':False,'patch_size':12,'stride':6,'angle_gate_deg':15,'contrast_ratio_gate':[.5,2],'minimum_localcontrast':.04,'top_shape_matches':8,'same_parameters_both_modes':True,'current_matching':'Gsigma1.2(r19.surface L), 4x4 area normalized mean/contrast; axial angle and same polarity','unmatched':'denoised surface highpass preserved, no raw CRT luma-grain reinjection; LL8 restores raw mean only','chroma':'raw current R-G/B-G maintained by common scalar delta, not reference chroma','LL8':'bounded scalar delta projection by lambda bisection; full alpha255 interior20px blocks','outputs':{}}
    for mode in ['window','all']:
        target,grid,winner,covered,stats=transfer(raw,current,dictionary,meta,ids,mode);again=transfer(raw,current,dictionary,meta,ids,mode);assert np.array_equal(target,again[0]) and np.array_equal(grid,again[1]);a,lock=mean_lock(raw,target);assert np.array_equal(a,mean_lock(raw,again[0])[0]);file=f'B-{mode}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'B-{mode}-on-black.png');id_image(winner).save(out/f'B-{mode}-source-ID-map.png');np.savez_compressed(out/f'B-{mode}-transfer.npz',source_id_grid=grid,source_id_pixels=winner,valid_transfer=covered);(out/f'B-{mode}-usage.json').write_text(json.dumps(stats,indent=2),encoding='utf-8')
        for label,box in {'full':(0,0,1280,1030),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():labelled_pair(Image.fromarray(raw),Image.fromarray(a),box,out/f'B-{mode}-comparison-{label}.png',file,'base-B.png')
        m['outputs'][mode]={'file':file,'sha256':sha(out/file),'opaque_sha256':sha(out/f'B-{mode}-on-black.png'),'repeat_exact':True,'transfer':stats,'LL8':lock,'diagnostics':r19.diagnostics(a,raw)};print(mode,m['outputs'][mode]['sha256'],json.dumps({'matched':stats['matched_patches'],'coverage':stats['valid_transfer_coverage_fraction'],'LL8':lock}),flush=True)
    m['synthetic']=synthetic(out,dictionary,meta,ids);assert sha(src)==BASESHA and sha(REF)==REFSHA;preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');snap=out/'source-snapshots';snap.mkdir(exist_ok=True)
    for p in [Path(__file__),HERE/'offline-round19.py',HERE/'build.py']:shutil.copyfile(p,snap/p.name)
    (out/'source-frozen.json').write_text(json.dumps({'files':{str(p.resolve()):sha(p) for p in [Path(__file__),HERE/'offline-round19.py',HERE/'build.py']}},ensure_ascii=False,indent=2),encoding='utf-8');print('Round32 complete',flush=True)
if __name__=='__main__':main()
