"""Pure array same-pixel line restoration and locally authored surface-input change."""
import cv2
import numpy as np
def smoothstep(t):
    t=np.clip(t,0,1);return t*t*(3-2*t)
def outside_weight(core,width):
    d=cv2.distanceTransform((~core).astype(np.uint8),cv2.DIST_L2,cv2.DIST_MASK_PRECISE)
    weight=smoothstep(d/width);weight[core]=0;return weight.astype(np.float32)
def inside_weight(mask,width):
    d=cv2.distanceTransform(mask.astype(np.uint8),cv2.DIST_L2,cv2.DIST_MASK_PRECISE)
    weight=smoothstep(d/width);weight[~mask]=0;return weight.astype(np.float32)
def process(raw,spatial_region,spatial_protect,residual,semantic_core,surface_weight,effect04,enabled=True):
    if not enabled:return raw.copy(),{}
    original04,info=effect04.process(raw,spatial_region,spatial_protect,residual)
    l=info['raw_luma'];mean=info['mean'];surface_input=mean+.4*(l-mean)
    lut_input=info['sharp_luma']+surface_weight*(surface_input-info['sharp_luma'])
    toned=effect04.tone(lut_input);surfacebase,_=effect04.encode(raw,toned-l,info['inside'])
    surfaceprocessed,_=effect04.encode(surfacebase,info['spatial_delta'],info['inside'])
    weight=outside_weight(semantic_core,12);out=raw.copy()
    rgb=raw[:,:,:3].astype(np.float32);delta=surfaceprocessed[:,:,:3].astype(np.float32)-rgb
    out[:,:,:3]=np.clip(np.floor(rgb+weight[:,:,None]*delta+.5),0,255).astype(np.uint8)
    info.update(original04_scaled=original04,surfacebase=surfacebase,surfaceprocessed=surfaceprocessed,
                surface_input=surface_input,surface_weight=surface_weight,line_mix_weight=weight,
                surface_delta_rgb=surfaceprocessed[:,:,:3].astype(np.float32)-original04[:,:,:3].astype(np.float32),
                line_delta_rgb=out[:,:,:3].astype(np.float32)-surfaceprocessed[:,:,:3].astype(np.float32))
    return out,info
