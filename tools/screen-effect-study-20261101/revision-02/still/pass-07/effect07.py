"""Pure array authored surface input and LUT-free line/carpet sharpening targets."""
import cv2
import numpy as np
def outside_weight(core,width):
    d=cv2.distanceTransform((~core).astype(np.uint8),cv2.DIST_L2,cv2.DIST_MASK_PRECISE);t=np.clip(d/width,0,1);w=t*t*(3-2*t);w[core]=0;return w.astype(np.float32)
def sharpen_target(raw,sigma,gain,effect04,inside):
    l=effect04.luma(raw);g=cv2.GaussianBlur(l,(0,0),sigma)
    return effect04.encode(raw,gain*(l-g),inside)
def process(raw,region,spatialprotect,residual,linecore,carpet,surface_weight,effect04,enabled=True):
    if not enabled:return raw.copy(),{}
    original,info=effect04.process(raw,region,spatialprotect,residual)
    l=info['raw_luma'];mean=cv2.GaussianBlur(l,(0,0),4);surface_input=mean+.10*(l-mean)
    lut_input=info['sharp_luma']+surface_weight*(surface_input-info['sharp_luma'])
    base,_=effect04.encode(raw,effect04.tone(lut_input)-l,info['inside'])
    rendered,_=effect04.encode(base,info['spatial_delta'],info['inside'])
    wire,wireclip=sharpen_target(raw,.9,1.0,effect04,info['inside']);floor,floorclip=sharpen_target(raw,1.1,.75,effect04,info['inside'])
    ww=1-outside_weight(linecore,12);fw=1-outside_weight(carpet,12)
    if np.any((ww>0)&(fw>0)):raise ValueError('Authored wire/carpet transition supports overlap')
    rgb=rendered[:,:,:3].astype(np.float32);wire_delta=ww[:,:,None]*(wire[:,:,:3].astype(np.float32)-rgb);floor_delta=fw[:,:,None]*(floor[:,:,:3].astype(np.float32)-rgb)
    out=raw.copy();out[:,:,:3]=np.clip(np.floor(rgb+wire_delta+floor_delta+.5),0,255).astype(np.uint8)
    info.update(surface_input=surface_input,surface_weight=surface_weight,surface_base=base,surface_rendered=rendered,
                surface_delta=rendered[:,:,:3].astype(np.float32)-original[:,:,:3].astype(np.float32),wire_delta=wire_delta,floor_delta=floor_delta,
                wire_target=wire,floor_target=floor,wire_weight=ww,floor_weight=fw,wire_clipping=wireclip,floor_clipping=floorclip)
    return out,info
