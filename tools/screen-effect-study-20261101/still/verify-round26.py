"""Waveform, polarity, arbitrary chroma/alpha, repeat, and frozen-source physical checks."""
from pathlib import Path
import importlib.util,json,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('round26',HERE/'offline-round26.py');q=importlib.util.module_from_spec(s);s.loader.exec_module(q);r=q.r;out=q.DEFAULT
def main():
    m=json.loads((out/'manifest.json').read_text(encoding='utf-8'));raw=np.array(Image.open(out/'base-B.png'));vis=raw[:,:,3]>0;border=vis&(cv2.distanceTransform(vis.astype(np.uint8),cv2.DIST_L2,5)<=2);assert q.sha(out/'base-B.png')==q.sha(r.SOURCE/'base-B.png')=='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9';assert q.sha(out/'B-stage12.png')==q.sha(out.parent/'round-23/B-stage12.png');checks={}
    previous=Image.open(out.parent/'round-23/B-standard.png')
    for k,v in m['outputs'].items():
        p=out/v['file'];a=np.array(Image.open(p));assert q.sha(p)==v['sha256'];assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~vis],raw[~vis]);assert np.array_equal(a[border],raw[border]);assert v['repeat_exact'];checks[k]={'sha256':v['sha256'],'alpha_hidden_exact':True,'inner2px_exact':int(border.sum()),'repeat_exact':True}
        for label,box in {'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500)}.items():r.labelled_pair(previous,Image.fromarray(a),box,out/f'{k}-vs23standard-{label}.png',v['file'],'23 B-standard.png')
    frozen={}
    for n in [23,24,25]:
        folder=out.parent/f'round-{n}';f=json.loads((folder/f'round{n}-freeze.json').read_text(encoding='utf-8'));changed=[k for k,v in f['files'].items() if q.sha(folder/k)!=v];source_changed=[k for k,v in f['repo_sources'].items() if q.sha(Path(k))!=v];assert not changed and not source_changed;frozen[str(n)]={'files':len(f['files']),'sources':len(f['repo_sources']),'changed':changed,'source_changed':source_changed}
    atlases,_=r.load_atlas(q.ATLAS);h,w=260,480;y,x=np.indices((h,w));cases={'flat':np.full((h,w),.4),'positive':np.where(x<160,.2,.7),'negative':np.where(x<160,.7,.2),'diagonal':np.where(x<120+y*.3,.2,.7),'multiple':np.where(((x>=80)&(x<240))|(x>=400),.7,.2),'RGB-chroma-alpha':np.where(x<200,.24,.66)};folder=out/'synthetic';folder.mkdir(exist_ok=True);tests={}
    for name,l in cases.items():
        rgb=np.repeat(l[:,:,None],3,axis=2)
        if name=='RGB-chroma-alpha':rgb+=np.array([-.035,0,.035])[None,None,:]
        a=np.dstack([r.r9.gray8(rgb),np.full((h,w),255,np.uint8)])
        if name=='RGB-chroma-alpha':
            a[(x<8)|(x>w-9)|(y<8)|(y>h-9),3]=0;a[a[:,:,3]==0,:3]=[67,29,123];a[(x==8)|(x==w-9),3]=127
        surface=r.r19.surface(a);paths,peaks,_=r.tracks(surface,a[:,:,3]);delta,coverage,events=q.waveform(a,paths,atlases);again,againcov,_=q.waveform(a,paths,atlases);assert np.array_equal(delta,again) and np.array_equal(coverage,againcov);record={'paths':len(paths),'repeat_exact':True,'replacement_pixels':int((coverage>0).sum()),'mixes':{}};Image.fromarray(a).save(folder/f'{name}-input.png');visible=a[:,:,3]>0;boundary=visible&(cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5)<=2)
        for profile,mix in q.PROFILES.items():
            final,count=q.render(a,a,delta,np.zeros_like(l),mix);assert np.array_equal(final,q.render(a,a,again,np.zeros_like(l),mix)[0]);assert np.array_equal(final[:,:,3],a[:,:,3]);assert np.array_equal(final[~visible],a[~visible]);assert np.array_equal(final[boundary],a[boundary]);outside=coverage==0;assert np.array_equal(final[outside],a[outside]);changed=int(np.any(final!=a,axis=2).sum());record['mixes'][profile]={'changed_pixels':changed,'outside_RGBA_exact':True,'alpha_hidden_boundary_exact':True};Image.fromarray(final).save(folder/f'{name}-{profile}.png');r.labelled_pair(Image.fromarray(a),Image.fromarray(final),(0,0,w,h),folder/f'{name}-{profile}-comparison.png',f'{name}-{profile}.png',f'{name}-input.png')
            if name=='flat':assert changed==0
            if name=='RGB-chroma-alpha':
                before=a[:,:,:3].astype(int);after=final[:,:,:3].astype(int);valid=(coverage>0)&(np.min(after,axis=2)>0)&(np.max(after,axis=2)<255);error=np.abs((after[:,:,0]-after[:,:,1])-(before[:,:,0]-before[:,:,1]));error2=np.abs((after[:,:,2]-after[:,:,1])-(before[:,:,2]-before[:,:,1]));maximum=max(int(error[valid].max()),int(error2[valid].max()));record['mixes'][profile]['max_chroma_pair_error_8bit']=maximum;assert maximum<=1
        if name=='multiple':
            patches=[delta[30:230,pos-20:pos+21].ravel() for pos in [80,240,400]];corr=[float(np.corrcoef(patches[i],patches[j])[0,1]) for i in range(3) for j in range(i+1,3)];record['pairwise_pattern_correlations']=corr;assert max(abs(v) for v in corr)<.95
        if name in ['positive','negative']:
            final=np.array(Image.open(folder/f'{name}-full.png'));beforemean=a[50:210,80:100,:3].mean()-a[50:210,220:240,:3].mean();aftermean=final[50:210,80:100,:3].mean()-final[50:210,220:240,:3].mean();assert beforemean*aftermean>0;record['dark_bright_polarity_preserved']=True
        tests[name]=record
    lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));flat=np.array(Image.open(folder/'flat-input.png'));st,constant=r.r22.stage12(flat,lookup);assert constant and np.array_equal(st,flat);result={'outputs':checks,'OFF_byte_exact':q.sha(out/'B-OFF.png')==q.sha(out/'base-B.png'),'legacy1_stage_exact_frozen23':True,'frozen_rounds':frozen,'synthetic':tests,'whole_pipeline_constant_stage_exact':True,'source_sha256':q.sha(HERE/'offline-round26.py'),'shared_frozen23_source_sha256':q.sha(HERE/'offline-round23.py')};(out/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps({'outputs':checks,'frozen_rounds':frozen,'synthetic':tests},indent=2),flush=True)
if __name__=='__main__':main()
