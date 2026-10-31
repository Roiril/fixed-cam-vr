"""Input-edge normal coordinates with disclosed reference-derived scalar contour residuals."""
from pathlib import Path
import argparse,base64,importlib.util,json,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
from build import labelled_pair,sha
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1);cv2.setRNGSeed(104729)
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('r22',HERE/'offline-round22.py');r22=importlib.util.module_from_spec(s);s.loader.exec_module(r22);r14=r22.r14;r19=r22.r19;r9=r22.r9;SOURCE=r22.SOURCE;DEFAULT=SOURCE.parent/'round-23';PROFILES={'soft':{'gain':.35,'normal_width':1.0},'standard':{'gain':.60,'normal_width':1.25},'strong':{'gain':.85,'normal_width':1.50}}

def load_atlas(out):
    manifest=json.loads((out/'atlas-manifest.json').read_text(encoding='utf-8'));arrays=[]
    for item in manifest['items']:
        p=out/item['file'];assert sha(p)==item['sha256'];z=np.load(p);assert set(z.files)=={'residual','u'};a=z['residual'];assert a.ndim==2 and a.shape[1]==49;arrays.append(a.astype(np.float32))
    return arrays,manifest

def tracks(surface,alpha):
    l=(surface@r9.WEIGHTS).astype(np.float32);gx=cv2.Sobel(l,cv2.CV_32F,1,0,ksize=3)/8;gy=cv2.Sobel(l,cv2.CV_32F,0,1,ksize=3)/8;mag=np.abs(gx);inside=cv2.erode((alpha>0).astype(np.uint8),np.ones((41,41),np.uint8)).astype(bool);peaks=(mag>=cv2.dilate(mag,np.ones((1,5),np.uint8))-1e-7)&(mag>.018)&(mag>.7*np.abs(gy))&inside;result=[]
    for polarity in [-1,1]:
        p=peaks&(gx*polarity>0);joined=cv2.dilate(p.astype(np.uint8),np.ones((3,3),np.uint8));n,labels,stats,_=cv2.connectedComponentsWithStats(joined,8)
        for label in range(1,n):
            x0,y0,w,h,area=stats[label]
            if h<8 or w>1.5*h:continue
            yy,xx=np.where(p&(labels==label));unique=np.unique(yy)
            if len(unique)<8:continue
            centers=np.array([xx[yy==row][np.argmax(mag[row,xx[yy==row]])] for row in unique],float);ys=np.arange(unique.min(),unique.max()+1);centers=np.interp(ys,unique,centers);centers=cv2.GaussianBlur(centers[:,None],(1,0),sigmaY=1.5,sigmaX=0)[:,0];slope=np.clip(np.gradient(centers),-1.2,1.2);left=l[ys,np.clip(np.rint(centers-10).astype(int),0,l.shape[1]-1)];right=l[ys,np.clip(np.rint(centers+10).astype(int),0,l.shape[1]-1)];contrast=np.abs(right-left);contrast=cv2.GaussianBlur(contrast[:,None],(1,0),sigmaY=2,sigmaX=0)[:,0]
            if np.median(contrast)<.08:continue
            result.append({'y':ys,'x':centers,'slope':slope,'contrast':contrast,'polarity':polarity,'seed':230023+len(result)*7919})
    return result,peaks,l

def residual_strip(atlases,length,seed):
    rng=np.random.default_rng(seed);strip=np.zeros((0,49),np.float32);sequence=[]
    while len(strip)<length:
        index=int(rng.choice(len(atlases),p=[.45,.45,.10]));a=atlases[index];stretch=float(rng.uniform(.8,1.6));n=int(a.shape[0]*rng.uniform(.8,1.5));phase=float(rng.uniform(0,a.shape[0]-1));t=np.mod(phase+np.arange(n)/stretch,2*(a.shape[0]-1));t=np.where(t<=a.shape[0]-1,t,2*(a.shape[0]-1)-t);mx=np.repeat(np.arange(49,dtype=np.float32)[None,:],n,axis=0);my=np.repeat(t.astype(np.float32)[:,None],49,axis=1);block=cv2.remap(a,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REFLECT_101);block*=.40 if index==2 else 1
        if len(strip):
            seam=min(16,len(strip),len(block));u=np.linspace(0,1,seam,dtype=np.float32)[:,None];strip=np.concatenate([strip[:-seam],strip[-seam:]*(1-u)+block[:seam]*u,block[seam:]])
        else:strip=block
        sequence.append({'atlas_id':index+1,'stretch':stretch,'phase':phase,'rows':n})
    return strip[:length],sequence

def apply_residual(surface,paths,atlases,p):
    h,w=surface.shape[:2];acc=np.zeros((h,w),float);weights=np.zeros((h,w),float);sequences=[]
    for path in paths:
        ys=path['y'];center=path['x'];slope=path['slope'];arc=np.cumsum(np.sqrt(1+slope*slope));strip,seq=residual_strip(atlases,max(4096,int(arc[-1])+600),path['seed']);radius=24*p['normal_width']*np.sqrt(1+np.max(slope*slope));x0=max(0,int(np.floor(center.min()-radius)));x1=min(w,int(np.ceil(center.max()+radius))+1)
        if x1<=x0:continue
        xx=np.arange(x0,x1)[None,:];normal=path['polarity']*(xx-center[:,None])/np.sqrt(1+slope[:,None]**2);u=normal/p['normal_width'];mx=(u+24).astype(np.float32);phase=(path['seed']%431);my=np.repeat((arc+phase).astype(np.float32)[:,None],x1-x0,axis=1);residual=cv2.remap(strip,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT,borderValue=0);weight=np.clip((24-np.abs(u))/6,0,1)*np.exp(-(u/14)**2)*(path['contrast'][:,None]>.08);delta=residual*path['contrast'][:,None]*p['gain'];acc[np.ix_(ys,np.arange(x0,x1))]+=delta*weight;weights[np.ix_(ys,np.arange(x0,x1))]+=weight;sequences.append({'seed':path['seed'],'source_blocks':seq,'rows':len(ys),'polarity':path['polarity']})
    delta=np.clip(acc/np.maximum(1,weights),-.30,.30);return delta,{'path_count':len(paths),'active_delta_pixels':int((np.abs(delta)>.001).sum()),'maximum_abs_delta':float(np.abs(delta).max()),'phase_sequences':sequences}

def micro(stage,peaks,stats):
    l=stage[:,:,:3].astype(float)@r9.WEIGHTS/255;noise,h_rms=r14.synthesize(l.shape,stats);distance=cv2.distanceTransform((~peaks).astype(np.uint8),cv2.DIST_L2,5);span=cv2.dilate(l,np.ones((3,15),np.uint8))-cv2.erode(l,np.ones((3,15),np.uint8));mid=np.clip((l-.05)/.12,0,1)*np.clip((.82-l)/.14,0,1)*(stage[:,:,3]>0);near=np.exp(-distance/4.5)*(distance<14)*np.clip(span/.2,0,1);weight=mid*(h_rms*3+near*.002);noise-=float(np.sum(noise*weight)/max(1e-12,weight.sum()));return noise*weight

def synth_tests(out,atlases):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=260,480;y,x=np.indices((h,w));cases={'flat':np.full((h,w),.4),'positive':np.where(x<160,.2,.7),'negative':np.where(x<160,.7,.2),'diagonal':np.where(x<120+y*.3,.2,.7),'multiple':np.where(((x>=80)&(x<240))|(x>=400),.7,.2)};report={}
    for name,l in cases.items():
        # Quantize the fixture once: float .7 can otherwise differ by1LSB between half-up and even rounding.
        rgb=r9.gray8(np.repeat(l[:,:,None],3,axis=2)).astype(np.float32)/255;alpha=np.full((h,w),255,np.uint8);paths,_,_=tracks(rgb,alpha);delta,info=apply_residual(rgb,paths,atlases,PROFILES['standard']);again,_=apply_residual(rgb,paths,atlases,PROFILES['standard']);assert np.array_equal(delta,again);a=np.dstack([r9.gray8(rgb),alpha]);final,count=r19.protect(a,np.clip(rgb+delta[:,:,None],0,1));untouched=np.abs(delta)<1e-12;assert np.array_equal(final[untouched],a[untouched]);Image.fromarray(a).save(folder/(name+'-input.png'));Image.fromarray(final).save(folder/(name+'-output.png'));labelled_pair(Image.fromarray(a),Image.fromarray(final),(0,0,w,h),folder/(name+'-comparison.png'),name+'-output.png',name+'-input.png');r={'paths':len(paths),'repeat_exact':True,'outside_residual_RGBA_exact':True,'new_pixels':int(np.any(final[:,:,:3]!=a[:,:,:3],axis=2).sum())}
        if name=='flat':assert r['new_pixels']==0
        if name=='multiple':
            patches=[delta[30:230,pos-20:pos+21].ravel() for pos in [80,240,400]];corr=[float(np.corrcoef(patches[i],patches[j])[0,1]) for i in range(3) for j in range(i+1,3)];r['pairwise_pattern_correlations']=corr;assert max(abs(v) for v in corr)<.95
        report[name]=r
    (out/'synthetic-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8');return report

def preview(out,m):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');images={}
    for c in m['inputs']:
        for k,f in [('base',f'base-{c}.png'),*[(p,f'{c}-{p}.png') for p in PROFILES]]:images[c+'-'+k]='data:image/png;base64,'+base64.b64encode((out/f).read_bytes()).decode()
    options=''.join(f'<option>{c}</option>' for c in m['inputs']);html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>参照由来の輪郭残差を比較</title><style>CSS
img{width:100%;height:auto;display:block;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>参照由来の輪郭残差を比較</h1><p>参照の輪郭から得た輝度残差を、入力の実物境界へ対応させています。元の形や反射との完全な分離はできません。</p><select id="camera" aria-label="カメラ">OPTIONS</select><select id="mode" aria-label="強さ"><option value="soft">弱め</option><option value="standard">標準</option><option value="strong">強め</option><option value="base">加工前</option></select></div><div class="image"><img id="image" alt="参照由来の輪郭残差の比較"></div><script>const images=IMAGES,c=document.getElementById('camera'),s=document.getElementById('mode');function show(){document.getElementById('image').src=images[c.value+'-'+s.value]}c.onchange=s.onchange=show;show();</script></html>'''
    (out/'preview.html').write_text(html.replace('CSS',css).replace('OPTIONS',options).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);ap.add_argument('--atlas-dir',type=Path,default=DEFAULT);ap.add_argument('--camera',choices=['A','B','C','all'],default='all');ap.add_argument('--input',type=Path);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);atlases,am=load_atlas(args.atlas_dir);sm=json.loads((SOURCE/'input-and-processing-manifest.json').read_text(encoding='utf-8'));lookup=json.loads(r22.r11.LOOKUP.read_text(encoding='utf-8'));stats=json.loads((r14.DEFAULT/'reference-residual-statistics.json').read_text(encoding='utf-8'));cameras=['custom'] if args.input else ['B','A','C'] if args.camera=='all' else [args.camera]
    m={'round':23,'profiles':PROFILES,'source_sha256':sha(Path(__file__)),'atlas_manifest_sha256':sha(args.atlas_dir/'atlas-manifest.json'),'atlas_items':am['items'],'reference_derived_material_disclosed':True,'limitation':am['limitation'],'reference_RGB_used_as_output_pixels':False,'completed_prior_output_pixels_used':False,'random_fragment_paint_used':False,'same_parameters_for_ABC':True,'scope':'vertical and moderately diagonal current-input edges first','inputs':{},'outputs':{}}
    for c in cameras:
        src=args.input if args.input else SOURCE/f'base-{c}.png';expected=sha(src)
        if not args.input:assert expected==sm['inputs'][c]['copy_sha256']
        for name in [f'base-{c}.png',f'{c}-OFF.png']:
            p=out/name
            if p.exists():assert sha(p)==expected
            else:shutil.copyfile(src,p)
        raw=np.array(Image.open(src).convert('RGBA'));stage,constant=r22.stage12(raw,lookup);assert np.array_equal(stage,r22.stage12(raw,lookup)[0]);surface=r19.surface(raw);paths,peaks,l=tracks(surface,raw[:,:,3]);noise=np.zeros(raw.shape[:2]) if constant else micro(stage,peaks,stats);Image.fromarray(stage).save(out/f'{c}-stage12.png');m['inputs'][c]={'path':str(src.resolve()),'sha256':expected,'OFF_exact':sha(out/f'{c}-OFF.png')==expected,'stage12_sha256':sha(out/f'{c}-stage12.png'),'stage12_repeat_exact':True,'edge_path_count':len(paths)}
        for name,p in PROFILES.items():
            delta,events=apply_residual(surface,paths,atlases,p);again,_=apply_residual(surface,paths,atlases,p);assert np.array_equal(delta,again);rgb=stage[:,:,:3].astype(float)/255+(delta+noise)[:,:,None];a,count=r19.protect(raw,rgb);file=f'{c}-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'{c}-{name}-on-black.png');(out/f'{c}-{name}-phase-sequences.json').write_text(json.dumps(events.pop('phase_sequences'),indent=2),encoding='utf-8')
            for label,roi in {'full':(0,0,raw.shape[1],raw.shape[0]),'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)}.items():
                if roi[2]<=raw.shape[1] and roi[3]<=raw.shape[0]:labelled_pair(Image.fromarray(raw),Image.fromarray(a),roi,out/f'{c}-{name}-comparison-{label}.png',file,f'base-{c}.png')
            m['outputs'][c+'-'+name]={'file':file,'sha256':sha(out/file),'alpha_exact':True,'hidden_exact':True,'boundary_pixels_exact':count,'repeat_exact':True,'events':events,'diagnostics':r19.diagnostics(a,raw)};print(c,name,json.dumps(m['outputs'][c+'-'+name]),flush=True)
        assert sha(src)==expected
    m['synthetic_verification']=synth_tests(out,atlases);preview(out,m);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print('Complete',flush=True)
if __name__=='__main__':main()
