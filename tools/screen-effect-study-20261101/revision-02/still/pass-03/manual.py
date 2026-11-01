"""Pure array composition with explicitly supplied authored masks and scalar residual."""
import cv2
import numpy as np
PARAMETERS=dict(horizontal_sigma=1.4,blur_delta_gain=.4,residual_gain=.85,delta_cap=.16)
def process(raw,regionMask,protectMask,residual,enabled=True):
    if raw.dtype!=np.uint8 or raw.ndim!=3 or raw.shape[2]!=4:raise ValueError('HxWx4 uint8 required')
    shape=raw.shape[:2]
    if any(value.shape!=shape for value in [regionMask,protectMask,residual]):raise ValueError('Mask shape mismatch')
    if not enabled:return raw.copy(),np.zeros(shape,np.float32)
    rgb=raw[:,:,:3].astype(np.float32)/255
    # The only blur used for output is horizontal. It contributes exclusively in ROI.
    horizontal=cv2.GaussianBlur(rgb,(0,1),sigmaX=1.4,sigmaY=0)
    blur_delta=((horizontal-rgb)@np.array([.2126,.7152,.0722],np.float32))*.4
    delta=np.clip((blur_delta+residual*.85)*regionMask,-.16,.16)
    visible=raw[:,:,3]>0
    inside=cv2.erode(visible.astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
    delta[protectMask|~inside]=0
    out=raw.copy();active=delta!=0
    out[:,:,:3][active]=np.clip(np.floor(raw[:,:,:3][active].astype(np.float32)+delta[active,None]*255+.5),0,255).astype(np.uint8)
    return out,delta
