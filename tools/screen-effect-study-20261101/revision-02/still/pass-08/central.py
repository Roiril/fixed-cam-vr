"""Pure array revision of one central authored boundary only."""
import cv2
import numpy as np
def revised_delta(raw,atlas,points,effect04,inside,protect):
    base,_=effect04.point_stage(raw);l=effect04.luma(base)
    horizontal=cv2.GaussianBlur(l,(0,1),sigmaX=1.4,sigmaY=0)
    blur=(horizontal-l)*.4;h,w=l.shape;delta=np.zeros((h,w),np.float32);support=np.zeros((h,w),np.float32)
    p=np.asarray(points,np.float32);ys=np.arange(int(p[:,1].min()),int(p[:,1].max())+1);centers=np.interp(ys,p[:,1],p[:,0])
    # One fixed data-derived rule: omit blur on at/below-median reference rows;
    # increase continuously to1 at the strongest reference row. Scalar residual
    # itself is never gated. No coefficient search or new random field.
    amplitude=np.max(np.abs(atlas),axis=1);median=float(np.median(amplitude));maximum=float(amplitude.max())
    refgate=np.clip((amplitude-median)/max(1e-12,maximum-median),0,1)
    gates=[]
    for y,c in zip(ys,centers):
        source_y=int(y%(atlas.shape[0]-1));gate=float(refgate[source_y]);gates.append(gate)
        xs=np.arange(max(0,int(np.floor(c-12))),min(w,int(np.ceil(c+12))+1));normal=xs-c;taper=np.clip((12-abs(normal))/2,0,1).astype(np.float32)
        mx=(normal*2+24).astype(np.float32)[None,:];my=np.full_like(mx,float(source_y));wave=cv2.remap(atlas,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)[0]
        contrast=abs(l[y,min(w-1,int(c+12))]-l[y,max(0,int(c-12))]);scalar=wave*contrast*.85
        delta[y,xs]=np.clip((blur[y,xs]*gate+scalar)*taper,-.16,.16);support[y,xs]=taper
    delta[protect|~inside]=0
    stats=dict(radius_before=18,radius_after=12,blur_sigma=1.4,blur_gain=.4,residual_gain=.85,delta_cap=.16,
               reference_row_amplitude='max(abs(scalar atlas residual)) across its49samples',reference_amplitude_median=median,
               reference_amplitude_maximum=maximum,row_gate_rule='clip((amplitude-median)/(maximum-median),0,1); scalar residual not gated',
               authored_rows=len(ys),blur_zero_rows=int((np.array(gates)==0).sum()),blur_nonzero_rows=int((np.array(gates)>0).sum()),row_gates=gates)
    return delta,support,stats
class ReplaceCentralSpatial:
    """Array-operation adapter; the reused07 pipeline remains unchanged."""
    def __init__(self,effect04,mask,delta):self.old=effect04;self.mask=mask;self.delta=delta
    def __getattr__(self,name):return getattr(self.old,name)
    def process(self,raw,region,protect,residual,enabled=True):
        out,info=self.old.process(raw,region,protect,residual,enabled)
        if enabled:
            d=info['spatial_delta'].copy();d[self.mask]=self.delta[self.mask];info['spatial_delta']=d
            out,clip=self.old.encode(info['base'],d,info['inside']);info['spatial_gamut_clip']=clip
        return out,info
