"""Physical outputs, confidence activity, thin-row provenance, and synthetic carrier tests."""
from pathlib import Path
import importlib.util,json,sys,hashlib
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image
HERE=Path(__file__).resolve().parent;s=importlib.util.spec_from_file_location('round27',HERE/'offline-round27.py');q=importlib.util.module_from_spec(s);s.loader.exec_module(q);r=q.r;out=q.DEFAULT
def main():
    m=json.loads((out/'manifest.json').read_text(encoding='utf-8'));raw=np.array(Image.open(out/'base-B.png'));vis=raw[:,:,3]>0;border=vis&(cv2.distanceTransform(vis.astype(np.uint8),cv2.DIST_L2,5)<=2);assert q.sha(out/'base-B.png')==q.sha(r.SOURCE/'base-B.png')=='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9';assert q.sha(out/'B-stage12.png')==q.sha(out.parent/'round-23/B-stage12.png');checks={};old=Image.open(out.parent/'round-26/B-soft.png')
    for k,v in m['outputs'].items():
        a=np.array(Image.open(out/v['file']));assert q.sha(out/v['file'])==v['sha256'];assert np.array_equal(a[:,:,3],raw[:,:,3]);assert np.array_equal(a[~vis],raw[~vis]);assert np.array_equal(a[border],raw[border]);assert v['repeat_exact'];assert v['events']['high_confidence_pixels']>0 and v['events']['high_confidence_delta_RMS']>.001;checks[k]={'sha256':v['sha256'],'alpha_hidden_exact':True,'inner2px_exact':int(border.sum()),'repeat_exact':True,'high_confidence_activity':v['events']}
        name=k.split('-',1)[1]
        for label,box in {'edge':(600,100,880,650),'shadow':(590,350,700,600),'floor':(680,740,1100,900)}.items():r.labelled_pair(old,Image.fromarray(a),box,out/f'{k}-vs26soft-{label}.png',v['file'],'26 B-soft.png')
        conf=np.array(Image.open(out/f'B-{name}-confidence.png'))/255;n,labels,stats,_=cv2.connectedComponentsWithStats((conf>.60).astype(np.uint8),8);assert n>1;label=1+int(np.argmax(stats[1:,cv2.CC_STAT_AREA]));x,y,w,h,area=stats[label];box=tuple(int(v) for v in (max(0,x-16),max(0,y-16),min(a.shape[1],x+w+16),min(a.shape[0],y+h+16)));r.labelled_pair(Image.fromarray(raw),Image.fromarray(a),box,out/f'{k}-high-confidence-native.png',v['file'],'base-B.png');checks[k]['largest_high_confidence_roi']={'box':box,'pixels':int(area)}
    frozen={}
    for n in [23,24,25,26]:
        folder=out.parent/f'round-{n}';f=json.loads((folder/f'round{n}-freeze.json').read_text(encoding='utf-8'));changed=[k for k,v in f['files'].items() if q.sha(folder/k)!=v];source_changed=[k for k,v in f['repo_sources'].items() if q.sha(Path(k))!=v];assert not changed and not source_changed;frozen[str(n)]={'files':len(f['files']),'sources':len(f['repo_sources']),'changed':changed,'source_changed':source_changed}
    atlases,_=r.load_atlas(q.ATLAS);strip,seq=q.thin_strip(atlases,4096,230023);smooth,original_seq=r.residual_strip(atlases,4096,230023);assert seq==original_seq;allowed={row.tobytes() for i,a in enumerate(atlases) for row in (a*.4 if i==2 else a)};non_source=sum(row.tobytes() not in allowed for row in strip);assert non_source==0;thin_proof={'rows':len(strip),'rows_not_exactly_a_source_scalar_row':non_source,'phase_sequence_identical_to_previous':True,'linear_strip_non_source_rows':sum(row.tobytes() not in allowed for row in smooth)}
    h,w=260,480;y,x=np.indices((h,w));cases={'flat':np.full((h,w),.4),'positive':np.where(x<160,.2,.7),'negative':np.where(x<160,.7,.2),'diagonal':np.where(x<120+y*.3,.2,.7),'multiple':np.where(((x>=80)&(x<240))|(x>=400),.7,.2),'RGB-chroma-alpha':np.where(x<200,.24,.66),'thin-shadow':np.where((x>=158)&(x<163),.2,.7),'near-ribs':np.where((x%14)<7,.2,.7),'nearby-edge-step':np.where(x<160,.2,np.where((x>=170)&(x<173),.42,.7)),'variable-plateau-step':np.where(x<160,.2,.7+.12*np.sin(x*.8))};folder=out/'synthetic';folder.mkdir(exist_ok=True);tests={}
    for name,l in cases.items():
        rgb=np.repeat(l[:,:,None],3,axis=2)
        if name=='RGB-chroma-alpha':rgb+=np.array([-.035,0,.035])[None,None,:]
        a=np.dstack([r.r9.gray8(rgb),np.full((h,w),255,np.uint8)])
        if name=='RGB-chroma-alpha':a[(x<8)|(x>w-9)|(y<8)|(y>h-9),3]=0;a[a[:,:,3]==0,:3]=[67,29,123];a[(x==8)|(x==w-9),3]=127
        surface=r.r19.surface(a);paths,peaks,_=r.tracks(surface,a[:,:,3]);mapped=q.correspond(a,surface,paths,peaks);record={'paths':len(paths),'modes':{}};Image.fromarray(a).save(folder/f'{name}-input.png');visible=a[:,:,3]>0;boundary=visible&(cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5)<=2)
        for profile,p in q.PROFILES.items():
            delta,coverage,confmap,events=q.waveform(a,mapped,atlases,p);again=q.waveform(a,mapped,atlases,p)[0];assert np.array_equal(delta,again);final,count=q.q26.render(a,a,delta,np.zeros_like(l),p['mix']);assert np.array_equal(final[:,:,3],a[:,:,3]);assert np.array_equal(final[~visible],a[~visible]);assert np.array_equal(final[boundary],a[boundary]);assert np.array_equal(final[coverage==0],a[coverage==0]);changed=int(np.any(final!=a,axis=2).sum());info={'changed_pixels':changed,'repeat_exact':True,'outside_RGBA_exact':True,'alpha_hidden_boundary_exact':True,**{k:v for k,v in events.items() if k!='records'}};Image.fromarray(final).save(folder/f'{name}-{profile}.png');r.labelled_pair(Image.fromarray(a),Image.fromarray(final),(0,0,w,h),folder/f'{name}-{profile}-comparison.png',f'{name}-{profile}.png',f'{name}-input.png')
            if name=='flat':assert changed==0
            if name=='RGB-chroma-alpha':
                before=a[:,:,:3].astype(int);after=final[:,:,:3].astype(int);valid=(coverage>0)&(np.min(after,axis=2)>0)&(np.max(after,axis=2)<255);errors=[np.abs((after[:,:,i]-after[:,:,1])-(before[:,:,i]-before[:,:,1]))[valid] for i in [0,2]];maximum=max(int(z.max()) for z in errors);info['max_chroma_pair_error_8bit']=maximum;assert maximum<=1
            if name in ['positive','negative']:assert changed>1000;beforemean=a[50:210,80:100,:3].mean()-a[50:210,220:240,:3].mean();aftermean=final[50:210,80:100,:3].mean()-final[50:210,220:240,:3].mean();assert beforemean*aftermean>0;info['dark_bright_polarity_preserved']=True
            if name=='multiple':
                patches=[delta[30:230,pos-18:pos+19].ravel() for pos in [80,240,400]];corr=[float(np.corrcoef(patches[i],patches[j])[0,1]) for i in range(3) for j in range(i+1,3)];info['pairwise_pattern_correlations']=corr;assert max(abs(v) for v in corr)<.95
            record['modes'][profile]=info
        tests[name]=record
    assert tests['positive']['modes']['modeA']['high_confidence_pixels']>1000;assert tests['positive']['modes']['modeA']['replacement_pixels']>tests['thin-shadow']['modes']['modeA']['replacement_pixels']
    for name in ['nearby-edge-step','variable-plateau-step']:
        quantiles=tests[name]['modes']['modeA']['confidence_row_quantiles'];assert quantiles and quantiles[2]<.99
    lookup=json.loads(r.r22.r11.LOOKUP.read_text(encoding='utf-8'));flat=np.array(Image.open(folder/'flat-input.png'));st,constant=r.r22.stage12(flat,lookup);assert constant and np.array_equal(st,flat);result={'outputs':checks,'OFF_byte_exact':q.sha(out/'B-OFF.png')==q.sha(out/'base-B.png'),'gross12_exact_frozen23':True,'frozen_rounds':frozen,'thin_row_proof':thin_proof,'synthetic':tests,'whole_pipeline_constant_stage_exact':True,'source_sha256':q.sha(HERE/'offline-round27.py')};(out/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps({'outputs':checks,'frozen_rounds':frozen,'thin_row_proof':thin_proof},indent=2),flush=True)
if __name__=='__main__':main()
