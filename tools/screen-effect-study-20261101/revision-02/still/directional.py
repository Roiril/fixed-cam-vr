"""Pure array directional residual filter. No input/output or mutable global state."""
import cv2
import numpy as np

PARAMETERS = dict(tensor_sigma=2.5, detection_sigma=.85, coherence_min=.60,
                  grad_x_fraction_min=.82, gradient_min=.012,
                  thin_kernel=15, thin_contrast=.028, thin_margin=2,
                  minimum_track_rows=14, contrast_min=.075, radius=10)
PROFILES = {'soft': dict(gain=.65, delta_max=.12),
            'standard': dict(gain=1.10, delta_max=.20)}

def luminance(rgb):
    return (rgb.astype(np.float32) / 255) @ np.array([.2126,.7152,.0722],np.float32)

def masks(rgba):
    """The smoothed copies are detection-only. Their RGB never reaches the output."""
    l=luminance(rgba[:,:,:3]); p=PARAMETERS
    visible=rgba[:,:,3]>0
    safe=cv2.erode(visible.astype(np.uint8),np.ones((25,25),np.uint8)).astype(bool)
    # Both white and black top-hats: orientation-independent ridge/trough support.
    # A disk of diameter15 rejects ridges up to12px plus the transition shoulders.
    kernel=cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(p['thin_kernel'],)*2)
    detector=cv2.GaussianBlur(l,(0,0),.45)
    white=detector-cv2.morphologyEx(detector,cv2.MORPH_OPEN,kernel)
    black=cv2.morphologyEx(detector,cv2.MORPH_CLOSE,kernel)-detector
    core=(np.maximum(white,black)>p['thin_contrast'])&visible
    protected=cv2.dilate(core.astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
    smooth=cv2.GaussianBlur(l,(0,0),p['detection_sigma'])
    gx=cv2.Sobel(smooth,cv2.CV_32F,1,0,ksize=3)/8
    gy=cv2.Sobel(smooth,cv2.CV_32F,0,1,ksize=3)/8
    jx=cv2.GaussianBlur(gx*gx,(0,0),p['tensor_sigma'])
    jy=cv2.GaussianBlur(gy*gy,(0,0),p['tensor_sigma'])
    jxy=cv2.GaussianBlur(gx*gy,(0,0),p['tensor_sigma'])
    total=jx+jy
    coherence=np.sqrt((jx-jy)**2+4*jxy*jxy)/(total+1e-12)
    fraction=jx/(total+1e-12)
    vertical=(coherence>p['coherence_min'])&(fraction>p['grad_x_fraction_min'])
    mag=np.abs(gx)
    peaks=(mag>=cv2.dilate(mag,np.ones((1,5),np.uint8))-1e-7)&(mag>p['gradient_min'])
    peaks&=vertical&safe&~protected
    return dict(luminance=l,gx=gx,coherence=coherence,grad_x_fraction=fraction,
                core=core,protected=protected,safe=safe,peaks=peaks,vertical=vertical)

def tracks(m):
    l=m['luminance'];gx=m['gx'];mag=np.abs(gx);result=[];p=PARAMETERS
    for polarity in [-1,1]:
        peaks=m['peaks']&(gx*polarity>0)
        joined=cv2.dilate(peaks.astype(np.uint8),np.ones((3,3),np.uint8))
        n,labels,stats,_=cv2.connectedComponentsWithStats(joined,8)
        for label in range(1,n):
            x0,y0,w,h,area=stats[label]
            if h<p['minimum_track_rows'] or w>h*.65:continue
            yy,xx=np.where(peaks&(labels==label));ys=np.unique(yy)
            if len(ys)<p['minimum_track_rows']:continue
            center=np.array([xx[yy==y][np.argmax(mag[y,xx[yy==y]])] for y in ys],np.float32)
            # Preserve gaps. A protected wire crossing must never become a bridge.
            center=cv2.GaussianBlur(center[:,None],(1,0),sigmaY=1.1,sigmaX=0)[:,0]
            cx=np.rint(center).astype(int)
            left=l[ys,np.clip(cx-12,0,l.shape[1]-1)]
            right=l[ys,np.clip(cx+12,0,l.shape[1]-1)]
            contrast=np.abs(right-left)
            keep=(contrast>p['contrast_min'])&((right-left)*polarity>0)
            if keep.sum()<p['minimum_track_rows']:continue
            result.append(dict(y=ys[keep],x=center[keep],contrast=contrast[keep],
                               polarity=polarity,seed=230023+len(result)*7919))
    return result

def process(rgba,atlases,profile='standard',enabled=True):
    """Return exact copied RGBA plus scalar delta and diagnostics. No RGB patch reuse."""
    if rgba.dtype!=np.uint8 or rgba.ndim!=3 or rgba.shape[2]!=4:
        raise ValueError('Expected HxWx4 uint8 RGBA')
    if not enabled:return rgba.copy(),np.zeros(rgba.shape[:2],np.float32),{}
    m=masks(rgba);paths=tracks(m);h,w=rgba.shape[:2];acc=np.zeros((h,w),np.float32)
    weight=np.zeros((h,w),np.float32);p=PROFILES[profile];radius=PARAMETERS['radius']
    for path in paths:
        ys=path['y'];center=path['x'];seed=path['seed'];rng=np.random.default_rng(seed)
        atlas=atlases[int(rng.integers(0,len(atlases)))]
        phase=float(rng.uniform(0,atlas.shape[0]-1));stretch=float(rng.uniform(.85,1.4))
        yy=np.mod(phase+ys/stretch,2*(atlas.shape[0]-1))
        yy=np.where(yy<=atlas.shape[0]-1,yy,2*(atlas.shape[0]-1)-yy)
        x0=max(0,int(np.floor(center.min()-radius)));x1=min(w,int(np.ceil(center.max()+radius))+1)
        xx=np.arange(x0,x1)[None,:];normal=path['polarity']*(xx-center[:,None])
        # Compress the source residual support[-24,24] into[-10,10] current pixels.
        mx=(normal*2.4+24).astype(np.float32)
        my=np.repeat(yy.astype(np.float32)[:,None],x1-x0,axis=1)
        residual=cv2.remap(atlas,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)
        taper=np.clip((radius-np.abs(normal))/2,0,1)
        delta=residual*path['contrast'][:,None]*p['gain']
        ix=np.ix_(ys,np.arange(x0,x1));acc[ix]+=delta*taper;weight[ix]+=taper
    delta=np.clip(acc/np.maximum(1,weight),-p['delta_max'],p['delta_max'])
    delta[~m['safe']|m['protected']]=0
    result=rgba.copy();active=delta!=0
    result[:,:,:3][active]=np.clip(np.floor(rgba[:,:,:3][active].astype(np.float32)+delta[active,None]*255+.5),0,255).astype(np.uint8)
    m.update(track_count=len(paths),support=weight>0,delta=delta)
    return result,delta,m
