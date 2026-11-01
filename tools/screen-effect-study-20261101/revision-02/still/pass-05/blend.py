"""Pure same-pixel delta blend; RGB samples are never blurred or resampled."""
import cv2
import numpy as np
def mix_weight(core,width=12):
    distance=cv2.distanceTransform((~core).astype(np.uint8),cv2.DIST_L2,cv2.DIST_MASK_PRECISE)
    t=np.clip(distance/width,0,1);weight=t*t*(3-2*t);weight[core]=0
    return weight.astype(np.float32)
def process(raw,processed04,core,enabled=True):
    if raw.dtype!=np.uint8 or processed04.dtype!=np.uint8 or raw.shape!=processed04.shape:raise ValueError('matching uint8 RGBA required')
    if core.shape!=raw.shape[:2]:raise ValueError('core shape mismatch')
    if not enabled:return raw.copy(),np.zeros(core.shape,np.float32)
    weight=mix_weight(core,12)
    out=raw.copy();delta=processed04[:,:,:3].astype(np.float32)-raw[:,:,:3].astype(np.float32)
    out[:,:,:3]=np.clip(np.floor(raw[:,:,:3].astype(np.float32)+weight[:,:,None]*delta+.5),0,255).astype(np.uint8)
    return out,weight
