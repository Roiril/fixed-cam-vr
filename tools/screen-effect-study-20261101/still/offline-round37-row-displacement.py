"""R37 reference-measured horizontal row displacement only; no residual/patch pixel transfer."""
from pathlib import Path
import argparse,base64,hashlib,importlib.util,json,shutil,sys
sys.dont_write_bytecode=True
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
from build import sha,labelled_pair
sys.stdout.reconfigure(encoding='utf-8');cv2.setNumThreads(1)
HERE=Path(__file__).resolve().parent;ROOT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still');OUT=ROOT/'round-37';REF=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png');REFSHA='ba078b369e842b60746ef7aa82133347e11e7ef71709ee3088a0fba18cfe92d8';BASE=ROOT/'round-33/B-baseline-protected.png';BASESHA='8a13b77400607d08f7d235429529f8bf86cbb249603e06e43a24eaac212b9c0a';RAW=ROOT/'round-11/base-B.png';RAWSHA='db21e8bfa1fa41d80237a46ab9567bfaefea49c7206c46d37f3998474003a2b9';W=np.array([.2126,.7152,.0722]);ROIS={'floor':(680,740,1100,900),'edge':(600,100,880,650),'flat':(180,250,500,500),'black':(820,450,880,530),'border':(75,250,160,650)};SOURCE_ROIS={'left':([880,20,970,300],1),'right':([1080,20,1150,300],-1)};PROFILES={'gain075-plain':(.75,False),'gain125-plain':(1.25,False),'gain075-softtail':(.75,True),'gain125-softtail':(1.25,True)}
s=importlib.util.spec_from_file_location('frame19',HERE/'offline-round19.py');r19=importlib.util.module_from_spec(s);s.loader.exec_module(r19)

def tree(folder):return {p.relative_to(folder).as_posix():sha(p) for p in sorted(folder.rglob('*')) if p.is_file()}
def crossings(row,level):
    ix=np.flatnonzero(np.diff(np.signbit(row-level)));return ix+(level-row[ix])/(row[ix+1]-row[ix])

def extract(im,box,polarity):
    x0,y0,x1,y1=box;l=np.array(im.crop(box).convert('RGB'),float)@W/255;h,w=l.shape;yy=np.arange(y0,y1);left=np.median(l[:,:8],axis=1);right=np.median(l[:,-8:],axis=1);level=(left+right)/2;cross=np.full(h,np.nan);count=np.zeros(h,int);valid=np.zeros(h,bool)
    for i,row in enumerate(l):
        candidates=crossings(row,level[i]);count[i]=len(candidates)
        if len(candidates)==1 and (right[i]-left[i])*polarity>.08 and 8<=candidates[0]<w-8:
            cross[i]=x0+candidates[0];valid[i]=True
    assert valid.sum()>=100;t=np.linspace(-1,1,h);weights=np.ones(valid.sum());coeff=np.polyfit(t[valid],cross[valid],2)
    for _ in range(10):
        coeff=np.polyfit(t[valid],cross[valid],2,w=np.sqrt(weights));res=cross[valid]-np.polyval(coeff,t[valid]);center=np.median(res);mad=np.median(abs(res-center));weights=1/np.maximum(1,abs(res-center)/max(1e-6,2.5*1.4826*mad))
    fitted=np.polyval(coeff,t);residual=cross-fitted;median=float(np.median(residual[valid]));mad=float(np.median(abs(residual[valid]-median)));keep=valid&(abs(residual-median)<=max(.5,4*1.4826*mad));clean=residual-median;filled=np.interp(np.arange(h),np.flatnonzero(keep),clean[keep]);return {'y':yy,'crossing':cross,'fit':fitted,'delta':clean,'valid':valid,'keep':keep,'filled':filled,'crossing_count':count,'left_plateau':left,'right_plateau':right,'halflevel':level,'info':{'source_ROI':box,'polarity':polarity,'valid_unique_step_rows':int(valid.sum()),'total_rows':h,'robust_kept_rows':int(keep.sum()),'quadratic_coefficients_normalized_y_minus1_to1':coeff.tolist(),'median_residual_subtracted':median,'MAD_px':mad,'outlier_cutoff_px':max(.5,4*1.4826*mad),'jitter_RMS_valid_kept_px':float(np.sqrt(np.mean(clean[keep]**2))),'peak_abs_kept_px':float(abs(clean[keep]).max()),'missing_rows_absolute_y':yy[~valid].tolist(),'outlier_rows_absolute_y':yy[valid&~keep].tolist(),'missing_fill':'linear interpolation only for missing/outlier integer Y; measured kept rows unchanged','halflevel':'row-local medians of left/right8px; no Y smoothing','validity':'exactly one raw halflevel crossing, expected polarity contrast>.08, crossing at least8px from sides'}}

def reflected_field(series,height):
    n=len(series);period=2*(n-1);phase=np.arange(height)%period;indices=np.where(phase<n,phase,period-phase);field=series[indices];turns=np.flatnonzero((np.arange(height)>0)&((np.arange(height)%(n-1))==0));seams=[]
    for j in turns:
        if j+1>=height:continue
        before=field[max(0,j-8):j];after=field[j+1:min(height,j+9)];left=field[j]-field[j-1];right=field[j+1]-field[j];seams.append({'row':int(j),'source_row_index':int(indices[j]),'value_continuity_error_at_shared_endpoint':0.,'left8_mean':float(before.mean()),'right8_mean':float(after.mean()),'local_mean_difference':float(after.mean()-before.mean()),'incoming_gradient':float(left),'outgoing_gradient':float(right),'gradient_difference':float(right-left)})
    return field,indices,{'period_rows':period,'join':'integer-row reflect101 sequence without seam averaging','seams':seams,'full_mean':float(field.mean()),'full_RMS':float(np.sqrt(np.mean(field**2)))}

def extend_rgb(base):
    valid=base[:,:,3]>0
    if not valid.any():return base[:,:,:3].astype(float)/255
    _,labels=cv2.distanceTransformWithLabels((~valid).astype(np.uint8),cv2.DIST_L2,5,labelType=cv2.DIST_LABEL_PIXEL);lut=np.zeros((int(labels.max())+1,3));lut[labels[valid]]=base[:,:,:3][valid]/255;rgb=lut[labels];rgb[valid]=base[:,:,:3][valid]/255;return rgb

def row_filter(base,dx,gain,tail):
    """Pure array filter; dx is caller-supplied, same horizontal displacement throughout each row."""
    assert base.dtype==np.uint8 and base.ndim==3 and base.shape[2]==4 and dx.shape==(base.shape[0],) and np.isfinite(dx).all() and gain>=0
    rgb=base[:,:,:3].astype(float)/255;effective=np.clip(dx*gain,-12,12)
    if gain==0:return base.copy(),{'sampled':rgb.copy(),'tail_luma':np.zeros(dx.shape+(base.shape[1],)),'target':rgb.copy(),'effective_dx':effective,'zero_gain_fast_OFF':True}
    h,w=base.shape[:2];extended=extend_rgb(base);mx=np.repeat(np.arange(w,dtype=np.float32)[None,:],h,axis=0)-effective.astype(np.float32)[:,None];my=np.repeat(np.arange(h,dtype=np.float32)[:,None],w,axis=1);sampled=cv2.remap(extended,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE);target=sampled.copy();tail_luma=np.zeros((h,w))
    if tail:
        target=.92*sampled+.08*cv2.GaussianBlur(sampled,(0,1),sigmaX=2,sigmaY=0,borderType=cv2.BORDER_REFLECT_101);l=sampled@W;tail_luma=.12*(l[:,np.clip(np.arange(w)-2,0,w-1)]-l[:,np.clip(np.arange(w)-5,0,w-1)]);target+=tail_luma[:,:,None]
    a,count=r19.protect(base,target);return a,{'sampled':sampled,'tail_luma':tail_luma,'target':target,'effective_dx':effective,'inner_pixels':count,'zero_gain_fast_OFF':False}

def diagnostics(base,a,layers):
    visible=base[:,:,3]>0;b=base[:,:,:3].astype(float)@W/255;l=a[:,:,:3].astype(float)@W/255;delta=l-b;target=layers['target'];dist=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);t=np.clip((dist-2)/18,0,1);fade=t*t*(3-2*t);before_clip=base[:,:,:3]/255+(target-base[:,:,:3]/255)*fade[:,:,None];clip=np.clip(before_clip,0,1)-before_clip;d=layers['effective_dx'];result={'changed_visible_pixels':int((np.any(a[:,:,:3]!=base[:,:,:3],2)&visible).sum()),'luma_delta_RMS':float(np.sqrt(np.mean(delta[visible]**2))),'signed_luma_delta_mean':float(delta[visible].mean()),'actual_mean':float(l[visible].mean()),'base_mean':float(b[visible].mean()),'white_ge098_fraction':float((l[visible]>=.98).mean()),'white_ge075_fraction':float((l[visible]>=.75).mean()),'black_le003_fraction':float((l[visible]<=.03).mean()),'effective_dx_min_max':[float(d.min()),float(d.max())],'effective_dx_RMS':float(np.sqrt(np.mean(d*d))),'sample_quantization':'cv2 INTER_LINEAR fractional X uses1/32px coefficient table','final_clipped_pixels':int((np.any((before_clip<0)|(before_clip>1),2)&visible).sum()),'final_clip_RGB_RMS':float(np.sqrt(np.mean(clip[visible]**2))),'ROIs':{}}
    for name,(x0,y0,x1,y1) in ROIS.items():
        if x1>a.shape[1] or y1>a.shape[0]:continue
        box=np.s_[y0:y1,x0:x1];result['ROIs'][name]={'mean':float(l[box].mean()),'variance':float(l[box].var()),'delta_RMS':float(np.sqrt(np.mean(delta[box]**2))),'white_ge098_fraction':float((l[box]>=.98).mean()),'black_le003_fraction':float((l[box]<=.03).mean())}
    return result

def plot_traces(out,extractions,field):
    im=Image.new('RGB',(1120,630),'white');d=ImageDraw.Draw(im);font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',17)
    for panel,(title,arrays,limits) in enumerate([('窓縁の半値交点と二次曲線 / ROI内X座標',[(v['crossing']-SOURCE_ROIS[k][0][0],(26,26,26)) for k,v in extractions.items()]+[(v['fit']-SOURCE_ROIS[k][0][0],(130,130,130)) for k,v in extractions.items()],(0,70)),('傾きを引いた左右の水平変位 / px',[(v['delta'],(26,26,26) if k=='left' else (130,130,130)) for k,v in extractions.items()],(-16,16)),('出力行の水平変位 / px（gain前）',[(field,(26,26,26))],(-16,16))]):
        top=35+panel*205;d.text((20,top-28),title,font=font,fill=(26,26,26));lo,hi=limits;d.line((55,top,55,top+140),fill=(150,150,150));d.line((55,top+140,1095,top+140),fill=(150,150,150));d.text((8,top),str(hi),font=font,fill=(26,26,26));d.text((8,top+120),str(lo),font=font,fill=(26,26,26))
        for values,color in arrays:
            previous=None
            for i,value in enumerate(values):
                if not np.isfinite(value):previous=None;continue
                point=(55+i*1040/max(1,len(values)-1),top+140-(value-lo)/(hi-lo)*140)
                if previous:d.line([previous,point],fill=color,width=1)
                previous=point
        d.text((55,top+145),'横軸:整数Y / 黒:左・実測 / 灰:右または二次曲線',font=font,fill=(26,26,26))
    im.save(out/'source-crossings-and-row-displacements.png')

def calibration(out,field):
    folder=out/'synthetic';folder.mkdir(exist_ok=True);h,w=160,300;dx=field[:h];y,x=np.indices((h,w));report={}
    for color in [[51]*3,[102]*3,[178]*3,[51,102,178]]:
        base=np.full((h,w,4),255,np.uint8);base[:,:,:3]=color
        for name,(gain,tail) in PROFILES.items():assert np.array_equal(row_filter(base,dx,gain,tail)[0],base)
    report['flat_gray020_040_070_color_all4_exact']=True
    fixtures={'vertical':x<140,'diagonal-forward':x<100+y*.4,'horizontal':y<80,'diagonal-backward':x<210-y*.4,'negative':x>=140,'two-edges':(x<70)|(x>=220)}
    for name,dark in fixtures.items():
        base=np.dstack([np.repeat(np.where(dark,51,178)[:,:,None],3,2).astype(np.uint8),np.full((h,w),255,np.uint8)]);Image.fromarray(base).save(folder/f'{name}-input.png');report[name]={}
        for profile,(gain,tail) in PROFILES.items():
            a,layers=row_filter(base,dx,gain,tail);assert np.array_equal(a,row_filter(base,dx,gain,tail)[0]) and np.isfinite(layers['target']).all();assert np.array_equal(row_filter(base,dx,0,tail)[0],base);l=a[:,:,:3].astype(float)@W/255;b=base[:,:,:3].astype(float)@W/255;level=(51+178)/2/255;r={'finite':True,'repeat_exact':True,'zero_gain_RGBA_exact':True,'changed_pixels':int(np.any(a!=base,2).sum())}
            if name=='horizontal':assert np.array_equal(a,base);r['horizontal_step_RGBA_exact']=True
            else:
                errors=[];centroid_errors=[];row_differences=[]
                for j in range(h):
                    before=crossings(b[j],level);after=crossings(l[j],level);measured=[]
                    for edge in before:
                        expected=edge+layers['effective_dx'][j];position=after[np.argmin(abs(after-expected))];errors.append(position-expected)
                        # A half-level crossing of a resampled sharp step is nonlinear in subpixel phase.
                        # The signed gradient centroid measures the actual translation; tail kernel moment is -.36px.
                        def centroid(values,center):
                            start=max(0,int(np.floor(center))-26);stop=min(w-1,int(np.ceil(center))+26);g=np.diff(values)[start:stop];return float(np.sum(g*(np.arange(start,stop)+.5))/g.sum())
                        before_position=centroid(b[j],edge);after_position=centroid(l[j],expected);measured.append(after_position-before_position);centroid_errors.append(after_position-before_position-layers['effective_dx'][j]-(-.36 if tail else 0.))
                    if len(measured)==2:row_differences.append(measured[1]-measured[0])
                r['halflevel_position_error_vs_sampling_dx_max_px']=float(np.max(abs(np.asarray(errors))));r['gradient_centroid_error_vs_sampling_dx_and_tail_moment_max_px']=float(np.max(abs(np.asarray(centroid_errors))));r['expected_tail_kernel_first_moment_px']=-.36 if tail else 0.;assert r['gradient_centroid_error_vs_sampling_dx_and_tail_moment_max_px']<.06
                if row_differences:r['two_edges_X_coherence_max_displacement_difference_px']=float(np.max(abs(np.asarray(row_differences))));assert r['two_edges_X_coherence_max_displacement_difference_px']<.1
            Image.fromarray(a).save(folder/f'{name}-{profile}.png');labelled_pair(Image.fromarray(base),Image.fromarray(a),(0,0,w,h),folder/f'{name}-{profile}-comparison.png',profile,name+'-input.png');report[name][profile]=r
    return report

def preview(out):
    css=Path('C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css').read_text(encoding='utf-8');files={'OFF':'B-OFF.png','raw':'raw-B.png',**{k:f'B-{k}.png' for k in PROFILES}};images={k:'data:image/png;base64,'+base64.b64encode((out/v).read_bytes()).decode() for k,v in files.items()};html='''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>窓縁から測った水平変位で行をずらす</title><style>CSS
img{display:block;width:100%;height:auto;background:var(--surface)}.image{max-width:1280px}select{font:inherit;padding:8px;margin:4px;background:var(--surface);color:var(--ink)}</style><div class="wrap"><h1>窓縁から測った水平変位で行をずらす</h1><p>完成済みの矩形欠けなし下地に、参照から測った水平変位配列を使う静止画試作です。参照のRGBや輪郭残差は貼りません。</p><p>左右の窓縁が同じ行ずれを持つと証明したものではありません。元の形や反射の影響は残り得ます。</p><select id="mode" aria-label="変位の倍率と仕上げ"><option value="gain075-plain">倍率0.75 / 行ずれのみ</option><option value="gain125-plain">倍率1.25 / 行ずれのみ</option><option value="gain075-softtail">倍率0.75 / 弱い横尾</option><option value="gain125-softtail">倍率1.25 / 弱い横尾</option><option value="OFF">追加なし</option><option value="raw">元B</option></select></div><div class="image"><img id="image" alt="水平行ずれの比較"></div><script>const images=IMAGES,s=document.getElementById('mode');function show(){document.getElementById('image').src=images[s.value]}s.onchange=show;show();</script></html>''';(out/'preview.html').write_text(html.replace('CSS',css).replace('IMAGES',json.dumps(images)),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=OUT);out=ap.parse_args().out;out.mkdir(parents=True,exist_ok=True);old=ROOT/'round-36';oldtree=tree(old);oldsource=json.loads((old/'source-frozen.json').read_text(encoding='utf-8'))['files'];assert all(sha(Path(k))==v for k,v in oldsource.items());(out/'old36-start-treehash.json').write_text(json.dumps(oldtree,ensure_ascii=False,indent=2),encoding='utf-8');assert sha(REF)==REFSHA and sha(BASE)==BASESHA and sha(RAW)==RAWSHA;im=Image.open(REF);extractions={}
    for name,(box,polarity) in SOURCE_ROIS.items():
        im.crop(box).save(out/f'reference-window-{name}-ROI.png');v=extract(im,box,polarity);extractions[name]=v;np.savez_compressed(out/f'reference-window-{name}-measurements.npz',**{k:a for k,a in v.items() if k!='info'});np.savetxt(out/f'reference-window-{name}-measurements.csv',np.c_[v['y'],v['crossing'],v['fit'],v['delta'],v['valid'],v['keep']],delimiter=',',header='source_y,crossing_absolute_x,quadratic_fit,centered_delta,unique_step_valid,robust_keep',comments='');Image.fromarray(np.repeat((v['valid']*255).astype(np.uint8)[:,None],24,1)).save(out/f'{name}-validmask.png');Image.fromarray(np.repeat((v['keep']*255).astype(np.uint8)[:,None],24,1)).save(out/f'{name}-robustmask.png')
    assert extractions['left']['info']['valid_unique_step_rows']==266 and extractions['right']['info']['valid_unique_step_rows']==222;chosen=max(extractions,key=lambda k:extractions[k]['info']['valid_unique_step_rows']);assert chosen=='left';left,right=extractions['left'],extractions['right'];common=left['keep']&right['keep'];correlation=float(np.corrcoef(left['delta'][common],right['delta'][common])[0,1]);raw=np.array(Image.open(RAW).convert('RGBA'));base=np.array(Image.open(BASE).convert('RGBA'));assert raw.shape==base.shape==(1030,1280,4);field,indices,joins=reflected_field(extractions[chosen]['filled'],base.shape[0]);np.savez_compressed(out/'row-displacement-field.npz',dx_before_gain=field,source_row_index=indices);np.savetxt(out/'effective-row-displacements.csv',np.c_[np.arange(len(field)),indices,field,np.clip(field*.75,-12,12),np.clip(field*1.25,-12,12)],delimiter=',',header='output_y,source_row_index,dx_before_gain,effective_gain075,effective_gain125',comments='');plot_traces(out,extractions,field);shutil.copyfile(BASE,out/'B-OFF.png');shutil.copyfile(BASE,out/'base-B.png');shutil.copyfile(RAW,out/'raw-B.png');r19.black(base,out/'base-B-on-black.png');visible=raw[:,:,3]>0;distance=cv2.distanceTransform(visible.astype(np.uint8),cv2.DIST_L2,5);inner=visible&(distance<=2);assert inner.sum()==7394 and np.array_equal(base[inner],raw[inner]) and np.array_equal(base[~visible],raw[~visible]) and np.array_equal(base[:,:,3],raw[:,:,3]);m={'round':37,'source_code_sha256':sha(Path(__file__)),'completed_base_input':{'path':str(BASE),'sha256':BASESHA,'provenance':'raw B -> q25 signals/stage mix0 -> raw frame protection in offline-round33-quilt.py; saved completed baseline-protected loaded'},'raw_frame_reference':{'path':str(RAW),'sha256':RAWSHA},'reference':{'path':str(REF),'sha256':REFSHA,'source_ROIs':SOURCE_ROIS,'measurements':{k:v['info'] for k,v in extractions.items()},'selected_side_before_profile_generation':chosen,'selection':'more unique valid rows before robust outlier rejection','same_Y_common_robust_rows':int(common.sum()),'same_Y_left_right_delta_Pearson_r':correlation,'global_jitter_proven':False},'field':joins,'operation':'global screen raster X displacement, same dx throughout each row; not R23 per-edge additive residual','sampling':'nearest alpha>0 valid RGB extension, cv2 remap INTER_LINEAR X, integer Y, positive dx moves image features right','cap':'effective dx=clip(gain*field,-12,+12) after gain','soft_tail':'0.92*sampled+0.08*GaussianXsigma2(sampled)+0.12*(shiftRight2(Lsampled)-shiftRight5(Lsampled)) common RGB scalar','zero_gain':'return full base RGBA before blur/tail, both finishes exact OFF','new_tonemap_threshold_binary_chips_blocks_dictionary_atlas_noise':False,'reference_RGB_or_contour_residual_used_as_output_pixels':False,'limitation':'参照から測った水平変位配列だけを使用。一画像から元形や反射を完全に分離できず、同一global row jitterを証明しない。時間安定性・live性能・意味形保持の保証は未検証。','outputs':{}}
    for name,(gain,tail) in PROFILES.items():
        a,layers=row_filter(base,field,gain,tail);assert np.array_equal(a,row_filter(base,field,gain,tail)[0]) and np.array_equal(row_filter(base,field,0,tail)[0],base);assert np.array_equal(a[:,:,3],raw[:,:,3]) and np.array_equal(a[~visible],raw[~visible]) and np.array_equal(a[inner],raw[inner]);file=f'B-{name}.png';Image.fromarray(a).save(out/file);r19.black(a,out/f'B-{name}-on-black.png');np.savez_compressed(out/f'B-{name}-layers.npz',sampled=layers['sampled'],tail_luma=layers['tail_luma'],target=layers['target'],effective_dx=layers['effective_dx']);sampled,_=r19.protect(base,layers['sampled']);Image.fromarray(sampled).save(out/f'B-{name}-sampled-protected.png');actual=(a[:,:,:3].astype(float)-base[:,:,:3].astype(float))@W/255;Image.fromarray(r19.u8(.5+layers['tail_luma']*4)).save(out/f'B-{name}-tail-neutral.png');Image.fromarray(r19.u8(.5+actual)).save(out/f'B-{name}-delta-neutral.png');Image.fromarray(r19.u8(np.maximum(actual,0)*2)).save(out/f'B-{name}-delta-positive.png');Image.fromarray(r19.u8(np.maximum(-actual,0)*2)).save(out/f'B-{name}-delta-negative.png');labelled_pair(Image.fromarray(base),Image.fromarray(a),(0,0,1280,1030),out/f'B-{name}-comparison-full.png',file,'矩形欠けなし入力下地')
        for label,box in ROIS.items():labelled_pair(Image.fromarray(base),Image.fromarray(a),box,out/f'B-{name}-comparison-{label}.png',file,'矩形欠けなし入力下地')
        info=diagnostics(base,a,layers);info.update({'file':file,'sha256':sha(out/file),'opaque_sha256':sha(out/f'B-{name}-on-black.png'),'gain':gain,'soft_tail':tail,'zero_gain_RGBA_exact':True,'repeat_RGBA_exact':True,'alpha_hidden_inner7394_exact':True,'requested_rows_clipped':int((abs(field*gain)>12).sum())});m['outputs'][name]=info;print(name,json.dumps({k:info[k] for k in ['opaque_sha256','changed_visible_pixels','luma_delta_RMS','effective_dx_min_max','effective_dx_RMS']}),flush=True)
    m['calibration']=calibration(out,field);assert sha(REF)==REFSHA and sha(BASE)==BASESHA and sha(RAW)==RAWSHA and sha(out/'B-OFF.png')==BASESHA;assert tree(old)==oldtree and all(sha(Path(k))==v for k,v in oldsource.items());m['old36_artifacts_unchanged']=len(oldtree);m['old36_live_sources_unchanged']=len(oldsource);(out/'manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');(out/'verification.json').write_text(json.dumps({'source_SHA_unchanged':True,'OFF_base_byte_exact':True,'old36_tree_unchanged':len(oldtree),'old36_live_sources_unchanged':len(oldsource),'calibration':m['calibration'],'outputs':m['outputs']},ensure_ascii=False,indent=2),encoding='utf-8');preview(out);folder=out/'source-snapshots';folder.mkdir(exist_ok=True);files=[Path(__file__),HERE/'offline-round19.py',HERE/'build.py']
    for p in files:shutil.copyfile(p,folder/p.name)
    (out/'source-frozen.json').write_text(json.dumps({'files':{str(p.resolve()):sha(p) for p in files}},ensure_ascii=False,indent=2),encoding='utf-8');print('Round37 complete',flush=True)
if __name__=='__main__':main()
