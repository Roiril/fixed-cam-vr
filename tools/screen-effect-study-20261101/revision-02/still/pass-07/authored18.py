from pathlib import Path
import cv2,numpy as np
ATLAS=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-23')
def authored18(raw,config,old):
    h,w=raw.shape[:2];roi=np.zeros((h,w),np.float32);residual=np.zeros((h,w),np.float32);regions=[];point,_=old.point_stage(raw);l=old.luma(point)
    for r in config['regions']:
        p=np.array(r['points'],np.float32);ys=np.arange(int(p[:,1].min()),int(p[:,1].max())+1);centers=np.interp(ys,p[:,1],p[:,0]);mask=np.zeros((h,w),np.float32);atlas=np.load(ATLAS/f"atlas-{r['atlas']+1:02d}.npz")['residual'].astype(np.float32)
        for y,c in zip(ys,centers):
            xs=np.arange(max(0,int(np.floor(c-18))),min(w,int(np.ceil(c+18))+1));normal=xs-c;weight=np.clip((18-abs(normal))/2,0,1).astype(np.float32);mx=(normal*(24/18)+24).astype(np.float32)[None,:];my=np.full_like(mx,float(y%(atlas.shape[0]-1)));wave=cv2.remap(atlas,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)[0];contrast=abs(l[y,min(w-1,int(c+12))]-l[y,max(0,int(c-12))]);mask[y,xs]=weight;residual[y,xs]=wave*contrast;roi[y,xs]=np.maximum(roi[y,xs],weight)
        regions.append((r,mask))
    protect=np.zeros((h,w),np.uint8)
    for p in config['protect_polygons']:cv2.fillPoly(protect,[np.array(p['points'],np.int32)],1)
    for p in config['protect_polylines']:cv2.polylines(protect,[np.array(p['points'],np.int32)],False,1,p['width'],cv2.LINE_8)
    return roi,protect.astype(bool),residual,regions
