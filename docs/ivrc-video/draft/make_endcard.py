# -*- coding: utf-8 -*-
"""エンドカード透過PNG（「VR 学会での展示へ」削除・要素再配置）。出力: endcard.png (RGBA)"""
import os, numpy as np
from PIL import Image, ImageDraw, ImageFont
HERE=os.path.dirname(os.path.abspath(__file__)); FD=os.path.join(HERE,"fonts")
W,H=1920,1080
INK=(255,250,240); ACCENT=(255,222,173); DIM=(128,122,110); LINE=(64,60,50)
def f(n,s): return ImageFont.truetype(os.path.join(FD,n),s)
f_title=f("ShipporiMincho-ExtraBold.ttf",158); f_eng=f("ShipporiMincho-Bold.ttf",48)
f_kw=f("ZenKakuGothicNew-Medium.ttf",38); f_label=f("ZenKakuGothicNew-Regular.ttf",26); f_micro=f("ZenKakuGothicNew-Regular.ttf",24)
def A(c): return (c[0],c[1],c[2],255)
img=Image.new("RGBA",(W,H),(0,0,0,0)); d=ImageDraw.Draw(img)
def ctext(s,fnt,y,fill):
    w=d.textlength(s,font=fnt); d.text((W/2-w/2,y),s,font=fnt,fill=fill)
# corners
L=70;m=60
for (x,y,dx,dy) in [(m,m,1,1),(W-m,m,-1,1),(m,H-m,1,-1),(W-m,H-m,-1,-1)]:
    d.line([(x,y),(x+dx*L,y)],fill=A(ACCENT),width=2); d.line([(x,y),(x,y+dy*L)],fill=A(ACCENT),width=2)
# REC + timestamp
d.ellipse([84,90,108,114],fill=(210,60,60,255)); d.text((120,88),"REC",font=f_label,fill=(240,250,255,255))
ts="CAM 01   23:16:01"; w=d.textlength(ts,font=f_label); d.text((W-60-w,88),ts,font=f_label,fill=(185,205,215,255))
# center block (re-balanced after removing exhibition line)
ctext("廻 リ 視", f_title, 326, A(INK))
ctext("Fixed-Camera Horror in Real Space", f_eng, 586, A(ACCENT))
d.line([(660,684),(1260,684)],fill=A(LINE),width=1)
ctext("固定カメラの視点で、現実世界を歩く VR ホラー", f_kw, 726, (210,204,190,255))
# credits (bottom)
ctext("IVRC2026　／　企画・開発　白石大晴", f_micro, 948, A(DIM))
ctext("サポート　堀越・Kim・ほか 明治大学 橋本研究室メンバー", f_micro, 990, A(DIM))
img.save(os.path.join(HERE,"endcard.png"))
# dark preview for verification
prev=Image.new("RGBA",(W,H),(14,14,18,255)); prev.alpha_composite(img); prev.convert("RGB").save(os.path.join(HERE,"endcard_preview.png"))
print("wrote endcard.png (transparent) +", os.path.getsize(os.path.join(HERE,"endcard.png")),"bytes")
