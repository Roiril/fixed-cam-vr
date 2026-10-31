"""Measure scalar residual statistics of the specified reference mid-plane ROI."""
from pathlib import Path
import sys,json
sys.dont_write_bytecode=True
import numpy as np,cv2
from PIL import Image
from build import sha
sys.stdout.reconfigure(encoding='utf-8')
OUT=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/still/round-14');OUT.mkdir(parents=True,exist_ok=True)
REF=Path('C:/Users/kouga/Downloads/Screenshot_20261101-004612.png');ROI=(1130,50,1310,200)
def measure(crop):
    l=np.array(crop.convert('L'),dtype=float)/255;h,w=l.shape;y,x=np.mgrid[-1:1:complex(h),-1:1:complex(w)];design=np.stack([np.ones_like(x),x,y,x*x,x*y,y*y],axis=2);coef=np.linalg.lstsq(design.reshape(-1,6),l.ravel(),rcond=None)[0];residual=l-design@coef
    micro=residual-cv2.GaussianBlur(residual,(0,0),6,borderType=cv2.BORDER_REFLECT_101);window=np.hanning(h)[:,None]*np.hanning(w)[None,:];power=np.abs(np.fft.fft2(micro*window))**2;fx=np.fft.fftfreq(w);fy=np.fft.fftfreq(h);horizontal=(np.abs(fy[:,None])>2*np.abs(fx[None,:]))&(np.abs(fy[:,None])>.02);vertical=(np.abs(fx[None,:])>2*np.abs(fy[:,None]))&(np.abs(fx[None,:])>.02)
    px=power.mean(axis=0);py=power.mean(axis=1);px/=max(px.mean(),1e-20);py/=max(py.mean(),1e-20)
    return {'quadratic_coefficients':coef.tolist(),'quadratic_removed_rms':float(np.sqrt(np.mean(residual**2))),'micro_rms':float(np.sqrt(np.mean(micro**2))),'micro_mean':float(micro.mean()),'dx_rms':float(np.sqrt(np.mean(np.diff(micro,axis=1)**2))),'dy_rms':float(np.sqrt(np.mean(np.diff(micro,axis=0)**2))),'horizontal_line_orientation_power':float(power[horizontal].sum()/power.sum()),'vertical_line_orientation_power':float(power[vertical].sum()/power.sum()),'frequency_x':fx.tolist(),'frequency_y':fy.tolist(),'marginal_psd_x':px.tolist(),'marginal_psd_y':py.tolist()}
crop=Image.open(REF).crop(ROI);crop.save(OUT/'reference-flat-roi.png');whole=measure(crop);interior=crop.crop((35,0,180,150));interior.save(OUT/'reference-flat-interior.png')
stats={'reference':str(REF),'source_sha256':sha(REF),'requested_roi':list(ROI),'requested_roi_diagnostic':{k:v for k,v in whole.items() if not isinstance(v,list)},'roi':[1165,50,1310,200],'excluded_left_columns':35,'reason':'Actual requested crop includes bright window-edge slivers at its left. Use its interior to measure flat-plane micro-signal, retain whole-ROI diagnosis separately.','luminance':'Pillow L/255 scalar','low_frequency_removal':'least-squares quadratic2D plus Gaussian sigma6 high-pass for micro-band',**measure(interior),'transfer':'only RMS and marginal PSD; new full-frame Gaussian random phases; no source residual pixel field or scene coordinates transferred'}
(OUT/'reference-residual-statistics.json').write_text(json.dumps(stats,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps({k:v for k,v in stats.items() if not isinstance(v,list)},ensure_ascii=False,indent=2))
