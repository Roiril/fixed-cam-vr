"""Pure array luma sharpening/tone and separately masked spatial delta."""
import cv2
import numpy as np
KNOT_X=np.array([0,.08,.18,.40,.55,.70,1],np.float32)
KNOT_Y=np.array([0,.015,.08,.35,.67,.86,1],np.float32)
PARAMETERS=dict(detection_mean_sigma=2,unsharp_gain=1.25,detail_gate_range=[.03,.10],detail_range_kernel=7,
                horizontal_sigma=1.4,blur_delta_gain=.4,residual_gain=.85,spatial_delta_cap=.16,
                tone_knots=list(zip(KNOT_X.tolist(),KNOT_Y.tolist())))
def luma(raw):return (raw[:,:,:3].astype(np.float32)/255)@np.array([.2126,.7152,.0722],np.float32)
def tone(l):return np.interp(l,KNOT_X,KNOT_Y).astype(np.float32)
def encode(raw,delta,inside):
    out=raw.copy();v=raw[:,:,:3].astype(np.float32)/255+delta[:,:,None]
    out[:,:,:3][inside]=np.clip(np.floor(v[inside]*255+.5),0,255).astype(np.uint8)
    return out,np.any((v<0)|(v>1),axis=2)&inside
def point_stage(raw):
    l=luma(raw);mean=cv2.GaussianBlur(l,(0,0),2)
    span=cv2.dilate(l,np.ones((7,7),np.uint8))-cv2.erode(l,np.ones((7,7),np.uint8))
    t=np.clip((span-.03)/.07,0,1);gate=t*t*(3-2*t)
    sharp=l+1.25*gate*(l-mean);toned=tone(sharp)
    inside=cv2.erode((raw[:,:,3]>0).astype(np.uint8),np.ones((5,5),np.uint8)).astype(bool)
    base,clip=encode(raw,toned-l,inside);sharp_only,_=encode(raw,sharp-l,inside);tone_only,_=encode(raw,tone(l)-l,inside)
    return base,dict(raw_luma=l,mean=mean,gate=gate,sharp_luma=sharp,toned_luma=toned,
                     inside=inside,point_delta=toned-l,point_gamut_clip=clip,sharp_only=sharp_only,tone_only=tone_only)
def process(raw,region,protect,residual,enabled=True):
    if not enabled:return raw.copy(),dict(spatial_delta=np.zeros(raw.shape[:2],np.float32))
    base,info=point_stage(raw);l=luma(base)
    horizontal=cv2.GaussianBlur(l,(0,1),sigmaX=1.4,sigmaY=0)
    spatial=np.clip(((horizontal-l)*.4+residual*.85)*region,-.16,.16)
    spatial[protect|~info['inside']]=0
    out,clip=encode(base,spatial,info['inside']);info.update(base=base,spatial_delta=spatial,spatial_gamut_clip=clip)
    return out,info
