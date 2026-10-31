"""Reference-derived contour residuals, not a claim of fully separated pure effects."""
from pathlib import Path
import argparse,hashlib,json,math,sys
import cv2,numpy as np
from PIL import Image,ImageDraw,ImageFont
sys.stdout.reconfigure(encoding='utf-8')
REFERENCE=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png')
DEFAULT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-23')
ROIS=[('window-left',[880,20,970,300]),('window-right',[1080,20,1150,300]),('monitor-right-flank',[1230,270,1285,445])]
LIMITATION='単一画像から加工と元の輪郭・反射・陰影を完全には分離できない。特にmonitorの残差には元の縁の反射や幅の影響が残りうる。色や人物や部屋のRGBではなく、行正規化した輪郭の2D scalar residualを再利用する。'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def u8(a):return np.clip(np.floor(a*255+.5),0,255).astype(np.uint8)

def extract(image,box):
    x0,y0,x1,y1=box;l=np.array(image.crop(tuple(box)).convert('L'),dtype=float)/255;h,w=l.shape;left=np.median(l[:,:8],axis=1);right=np.median(l[:,-8:],axis=1);polarity=1 if np.median(right-left)>0 else -1
    if polarity<0:l=l[:,::-1];left,right=right,left
    dark=cv2.GaussianBlur(left[:,None],(1,0),sigmaY=3,sigmaX=0)[:,0];bright=cv2.GaussianBlur(right[:,None],(1,0),sigmaY=3,sigmaX=0)[:,0];contrast=bright-dark;valid=contrast>.06;assert valid.mean()>.8;norm=(l-dark[:,None])/np.maximum(.06,contrast[:,None]);norm=np.clip(norm,-.35,1.5);smooth=cv2.GaussianBlur(norm,(0,1),.8);grad=np.diff(smooth,axis=1);locations=np.argmax(grad,axis=1).astype(float)+.5;rows=np.arange(h);coeff=np.polyfit(rows[valid],locations[valid],2);base=np.polyval(coeff,rows);u=np.arange(-24,25,dtype=np.float32);mx=(base[:,None]+u[None,:]).astype(np.float32);my=np.repeat(rows[:,None],len(u),axis=1).astype(np.float32);wave=cv2.remap(norm.astype(np.float32),mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE)
    # Follow the main half-level transition into the bright plateau rather than an isolated highlight.
    fallback=0
    for row in range(h):
        crossings=np.where((smooth[row,:-1]<.5)&(smooth[row,1:]>=.5))[0]
        if len(crossings):
            j=int(crossings[-1]);locations[row]=j+(.5-smooth[row,j])/max(1e-6,smooth[row,j+1]-smooth[row,j])
        else:fallback+=1
    keep=valid.copy()
    for iteration in range(3):
        coeff=np.polyfit(rows[keep],locations[keep],2);base=np.polyval(coeff,rows);r=locations-base;mad=np.median(np.abs(r[keep]-np.median(r[keep])));keep=valid&(np.abs(r)<max(6,4*mad))
    mx=(base[:,None]+u[None,:]).astype(np.float32);wave=cv2.remap(norm.astype(np.float32),mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE)
    choices=[]
    for sigma in [.6,.9,1.2,1.6,2.2,3.0]:
        step=np.array([.5*(1+math.erf(float(v)/(math.sqrt(2)*sigma))) for v in u]);error=float(np.mean((wave[valid][:,np.abs(u)<10]-step[np.abs(u)<10])**2));choices.append((error,sigma,step))
    error,sigma,step=min(choices,key=lambda x:x[0]);outer=14 if box[0]>1200 else 24;taper=np.clip((outer-np.abs(u))/6,0,1);residual=np.clip(wave-step[None,:],-.75,.75)*taper[None,:];residual[~valid]=0
    return residual.astype(np.float32),wave,step,{'canonical_dark_left_bright_right':True,'source_polarity':polarity,'row_local_dark_bright':'median8px side bands, Gaussian sigmaY3','row_contrast_median':float(np.median(contrast)),'valid_row_fraction':float(valid.mean()),'edge_location':'last half-level crossing into bright plateau, gradient fallback','gradient_fallback_rows':fallback,'base_shape':'robust quadratic fit of main-edge row positions','quad_coefficients':coeff.tolist(),'u_range':[-24,24],'ideal_blur_sigma':sigma,'ideal_fit_MSE':error,'jitter_RMS_after_quad_px':float(np.std(locations[valid]-base[valid])),'residual_RMS':float(np.sqrt(np.mean(residual**2))),'residual_clip':[-.75,.75],'outer_taper_u':[outer-6,outer]}

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--out',type=Path,default=DEFAULT);args=ap.parse_args();out=args.out;out.mkdir(parents=True,exist_ok=True);image=Image.open(REFERENCE);m={'name':'参照由来の輪郭残差','reference_path':str(REFERENCE),'reference_sha256':sha(REFERENCE),'extractor_sha256':sha(Path(__file__)),'limitation':LIMITATION,'no_RGB_in_atlas':True,'items':[]};panels=[]
    font=ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',16)
    for i,(name,box) in enumerate(ROIS,1):
        residual,wave,step,info=extract(image,box);file=f'atlas-{i:02d}.npz';np.savez_compressed(out/file,residual=residual,u=np.arange(-24,25,dtype=np.float32));neutral=Image.fromarray(u8(.5+residual*.5));neutral.save(out/f'atlas-{i:02d}-residual-neutral.png');Image.fromarray(u8(np.clip(wave,0,1))).save(out/f'atlas-{i:02d}-normalized-waveform.png');image.crop(tuple(box)).save(out/f'atlas-{i:02d}-source-ROI-review.png');info.update({'id':i,'name':name,'source_ROI':box,'residual_dimensions':[residual.shape[1],residual.shape[0]],'file':file,'sha256':sha(out/file)});m['items'].append(info)
        panel=Image.new('RGB',(310,700),(245,245,245));draw=ImageDraw.Draw(panel);draw.text((10,10),f'{i:02d} {name}',font=font,fill=(25,25,25));draw.text((10,40),str(box),font=font,fill=(25,25,25));draw.text((10,70),f'{residual.shape[1]}×{residual.shape[0]} scalar',font=font,fill=(25,25,25));panel.paste(neutral.convert('RGB').resize((196,residual.shape[0]*2),Image.Resampling.NEAREST),(10,110));panels.append(panel)
    overview=Image.new('RGB',(len(panels)*310,700),(245,245,245))
    for i,panel in enumerate(panels):overview.paste(panel,(i*310,0))
    overview.save(out/'atlas-numbered-overview.png');(out/'atlas-manifest.json').write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(m,ensure_ascii=True,indent=2))
if __name__=='__main__':main()
