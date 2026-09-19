"""Render old/new masks against the same plate; retain every video frame in evidence."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import sys

import cv2
import imageio_ffmpeg
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(ROOT / 'tools/gen-plate'))
import screen


def frames(path):
    if Path(path).suffix.lower() in ('.png', '.jpg', '.jpeg'):
        yield np.array(Image.open(path).convert('RGB'))
        return
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise ValueError(f'Cannot open {path}')
    try:
        while True:
            ok, bgr = cap.read()
            if not ok:
                break
            yield cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    finally:
        cap.release()


def alpha(path, size):
    w, h = size
    im = Image.open(path).convert('L')
    fa = im.width / im.height
    if w / h > fa:
        cw, ch = im.width, round(im.width * h / w)
    else:
        cw, ch = round(im.height * w / h), im.height
    x, y = (im.width-cw)//2, (im.height-ch)//2
    return np.array(im.crop((x,y,x+cw,y+ch)).resize(size, Image.Resampling.BILINEAR),float)/255


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ('reference', 'old-source', 'new-source', 'old-mask', 'new-mask', 'out-dir'):
        ap.add_argument('--'+name, required=True)
    ap.add_argument('--lap', type=float, default=2)
    a = ap.parse_args()
    out = Path(a.out_dir).resolve()
    paths = [Path(getattr(a,k.replace('-','_'))).resolve() for k in ('reference','old-source','new-source','old-mask','new-mask')]
    if not all(p.is_file() for p in paths):
        raise ValueError('All input files must exist')
    if any(p.is_relative_to(out) for p in paths):
        raise ValueError('Evidence directory must not contain inputs')
    ref = Image.open(a.reference).convert('RGB'); w,h=ref.size
    masks = [alpha(a.old_mask,ref.size),alpha(a.new_mask,ref.size)]
    base = screen.srgb_to_linear(np.array(ref,float))
    font = ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',18)
    small = ImageFont.truetype('C:/Windows/Fonts/meiryo.ttc',12)
    out.mkdir(parents=True,exist_ok=True)
    pairs=[]
    oldit,newit=iter(frames(a.old_source)),iter(frames(a.new_source))
    i=0
    while True:
        left,right=next(oldit,None),next(newit,None)
        if left is None or right is None:
            if left is not None or right is not None: raise ValueError('Frame counts differ')
            break
        if left.shape != (h,w,3) or right.shape != (h,w,3):raise ValueError('Dimension mismatch')
        panels=[]
        for j,img in enumerate((left,right)):
            lin=screen.srgb_to_linear(img.astype(float))
            m=masks[j][...,None]
            rgb=screen.linear_to_srgb(base*(1-m)+lin*m).round().clip(0,255).astype('uint8')
            panel=Image.new('RGB',(w,h+32),'white')
            panel.paste(Image.fromarray(rgb),(0,32))
            ImageDraw.Draw(panel).text((8,3),('変更前' if j==0 else '変更後')+f' / コマ {i}',font=font,fill='black')
            panels.append(panel)
        pair=Image.new('RGB',(w*2,h+32),'white')
        pair.paste(panels[0],(0,0));pair.paste(panels[1],(w,0))
        pairs.append(pair)
        Image.fromarray(right).save(out/f'source-{i:03}.png') if i in (0,48,95) else None
        i+=1
    if not pairs:raise ValueError('No frames')
    indices=sorted(set([0,(len(pairs)-1)//2,len(pairs)-1]))
    contact=Image.new('RGB',(w*2,(h+32)*len(indices)),'white')
    for row,ix in enumerate(indices):contact.paste(pairs[ix],(0,row*(h+32)));pairs[ix].save(out/f'comparison-{ix:03}.png')
    contact.save(out/'comparison.png')
    cols=8;tw,th=320,128
    allframes=Image.new('RGB',(cols*tw,((len(pairs)+cols-1)//cols)*th),'white')
    for ix,pic in enumerate(pairs):
        cell=pic.resize((tw,th),Image.Resampling.LANCZOS)
        allframes.paste(cell,((ix%cols)*tw,(ix//cols)*th))
    allframes.save(out/'all-frames.png')
    fps=24
    if Path(a.new_source).suffix.lower() not in ('.png', '.jpg', '.jpeg'):
        cap=cv2.VideoCapture(a.new_source)
        fps=cap.get(cv2.CAP_PROP_FPS) if cap.isOpened() else 24
        cap.release()
    fps=fps if 0<fps<1000 else 24
    cmd=[imageio_ffmpeg.get_ffmpeg_exe(),'-nostdin','-y','-f','rawvideo','-pix_fmt','rgb24','-s',f'{w*2}x{h+32}','-r',str(fps),'-i','pipe:0','-an','-c:v','libx264','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart',str(out/'comparison.mp4')]
    p=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    _,err=p.communicate(b''.join(np.asarray(pic).tobytes() for pic in pairs))
    if p.returncode:raise RuntimeError(err.decode('utf-8',errors='replace')[-2000:])
    # Render identical indices through the shared screen preview, with repeatable noise.
    settings=dict(exposure=-1.05,contrast=1.12,saturation=.52,temperature=.48,tint=0.,lift=.02,vignette=.38)
    show=screen.load_show()
    for k,v in (show.get('post') or {}).items():
        if k in settings and isinstance(v,(int,float)):settings[k]=float(v)
    total=max(2.,float((show.get('run') or {}).get('totalLaps',3)))
    progress=float(np.clip((a.lap-1)/(total-1),0,1))
    blocks=screen.FINE_BLOCKS+(screen.END_BLOCKS-screen.FINE_BLOCKS)*progress
    postpics=[]
    for j,(src,m) in enumerate(((a.old_source,a.old_mask),(a.new_source,a.new_mask))):
        for ix,img in enumerate(frames(src)):
            if ix not in indices:continue
            temp=out/f'post-input-{j}-{ix:03}.png';Image.fromarray(img).save(temp)
            screen.RNG=np.random.default_rng(20260817)
            rgb=screen.linear_to_srgb(screen.render(a.reference,str(temp),m,settings,blocks,progress)).astype('uint8')
            imgout=Image.fromarray(rgb);imgout.save(out/f'post-{j}-{ix:03}.png')
            postpics.append((j,ix,imgout))
    report={'frames_per_version':len(pairs),'total_composite_frames':len(pairs)*2,'preview_fps':fps,'preview_only':True,
            'post_indices':indices,'lap':a.lap,'post_settings':settings,'inputs':{str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}}
    (out/'preview-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8',newline='\n')
    print(json.dumps(report,ensure_ascii=False))


if __name__=='__main__':main()
