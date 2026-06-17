# -*- coding: utf-8 -*-
"""
廻リ視 IVRC ビデオ — 背景透過オーバーレイ書き出し（アルファ付き .mov）
残す要素: 字幕 ＋ CCTV UI（四隅枠・REC・カメラ番号・タイムスタンプ）＋ 実機/イメージ バッジ
        ＋ グラフィックカット（B2 原理図・B5 エンドカード・タイトル文字）。
省く要素: 各カットの〔仮〕シーン説明ボックス、PART 表示、進捗バー、走査線。
背景は完全透過 → 編集ソフトで背面トラックに実映像を敷けば完成。
出力: ivrc-overlay.mov（qtrle / argb / アルファ付き・ロスレス）。build_final.py の定義を再利用。
"""
import os, subprocess, numpy as np, imageio_ffmpeg
from PIL import Image, ImageDraw
import build_final as B

W,H,FPS,total,cuts = B.W,B.H,B.FPS,B.total,B.cuts
INK,ACCENT,DIM,REAL,IMAGE,LINE = B.INK,B.ACCENT,B.DIM,B.REAL,B.IMAGE,B.LINE
tl,cen,fit_lines = B.tl,B.cen,B.fit_lines
f_title,f_eng,f_exhibit,f_titlesub,f_kw = B.f_title,B.f_eng,B.f_exhibit,B.f_titlesub,B.f_kw
f_badge,f_micro,f_label,f_dia_t,f_dia_s,f_nov = B.f_badge,B.f_micro,B.f_label,B.f_dia_t,B.f_dia_s,B.f_nov
SUB_SIZES,ZB = B.SUB_SIZES,B.ZB
HERE=B.HERE; OUT=os.path.join(HERE,"ivrc-overlay.mov"); FF=imageio_ffmpeg.get_ffmpeg_exe()

def A(c): return (c[0],c[1],c[2],255)

def draw_graphics(d,cut):
    sp=cut.get("special")
    if sp=="diagram":
        boxes=[("① 固定カメラ","スマホ ×3 (三脚)"),("② 映像伝送","Wi-Fi / MJPEG"),("③ Quest 3","頭の位置でカメラ選択")]
        bw,bh,gap=470,220,72; tot=bw*3+gap*2; x0=(W-tot)//2; y0=298
        for i,(t,s) in enumerate(boxes):
            x=x0+i*(bw+gap); d.rectangle([x,y0,x+bw,y0+bh],outline=(60,58,50,255),width=2)
            w=tl(d,t,f_dia_t); d.text((x+bw/2-w/2,y0+52),t,font=f_dia_t,fill=A(INK))
            w=tl(d,s,f_dia_s); d.text((x+bw/2-w/2,y0+132),s,font=f_dia_s,fill=A(DIM))
            if i<2:
                ax=x+bw+12; ay=y0+bh//2; d.line([(ax,ay),(ax+gap-24,ay)],fill=A(ACCENT),width=3)
                d.polygon([(ax+gap-24,ay-9),(ax+gap-8,ay),(ax+gap-24,ay+9)],fill=A(ACCENT))
        nov="操作ボタンなし — 切替トリガは「身体の移動」だけ"; by=y0+bh+78
        w=tl(d,nov,f_nov); d.rectangle([W/2-w/2-32,by-14,W/2+w/2+32,by+72],outline=A(ACCENT),width=2)
        d.text((W/2-w/2,by+4),nov,font=f_nov,fill=A(ACCENT))
    elif sp=="endcard":
        cen(d,["廻 リ 視"],f_title,372,A(INK))
        e="Fixed-Camera Horror in Real Space"; w=tl(d,e,f_eng); d.text((W/2-w/2,468),e,font=f_eng,fill=A(ACCENT))
        d.line([(700,556),(1220,556)],fill=A(LINE),width=1)
        kw="固定カメラの視点で、現実世界を歩く VR ホラー"; w=tl(d,kw,f_kw); d.text((W/2-w/2,596),kw,font=f_kw,fill=(210,204,190,255))
        ex="VR 学会での展示へ"; w=tl(d,ex,f_exhibit); d.text((W/2-w/2,690),ex,font=f_exhibit,fill=A(INK))
        cr="Music: ____(DOVA-SYNDROME) / SE: 効果音ラボ / Fonts: Shippori Mincho・Zen Kaku Gothic New"
        w=tl(d,cr,f_micro); d.text((W/2-w/2,848),cr,font=f_micro,fill=A(DIM))
    elif sp=="title":
        cen(d,["廻 リ 視"],f_title,436,A(INK))
        s="固定視点実世界探索ホラー"; w=tl(d,s,f_titlesub); d.text((W/2-w/2,548),s,font=f_titlesub,fill=A(ACCENT))

def static_overlay(cut):
    img=Image.new("RGBA",(W,H),(0,0,0,0)); d=ImageDraw.Draw(img)
    L=70;m=60
    for (x,y,dx,dy) in [(m,m,1,1),(W-m,m,-1,1),(m,H-m,1,-1),(W-m,H-m,-1,-1)]:
        d.line([(x,y),(x+dx*L,y)],fill=A(ACCENT),width=2); d.line([(x,y),(x,y+dy*L)],fill=A(ACCENT),width=2)
    draw_graphics(d,cut)
    bd=cut.get("badge")
    if bd:
        txt,col=bd; bw=tl(d,txt,f_badge); bx=W/2-bw/2
        sp=cut.get("special"); by=726 if sp not in("title","endcard","diagram") else 980
        d.rectangle([bx-22,by-8,bx+bw+22,by+50],outline=A(col),width=2); d.text((bx,by),txt,font=f_badge,fill=A(col))
    sub=cut.get("sub")
    if sub:
        d.rectangle([0,852,W,1016],fill=(4,4,4,205))
        f,lines=fit_lines(d,sub,W-260,SUB_SIZES,ZB,max_lines=2); cen(d,lines,f,936,A(INK))
    return img

cmd=[FF,"-y","-f","rawvideo","-pix_fmt","rgba","-s",f"{W}x{H}","-r",str(FPS),"-i","-",
     "-c:v","qtrle","-pix_fmt","argb","-movflags","+faststart",OUT]
proc=subprocess.Popen(cmd,stdin=subprocess.PIPE)
clock0=23*3600+14*60+7; g=0
for c in cuts:
    base=static_overlay(c); nfr=int(round(c["dur"]*FPS))
    for fi in range(nfr):
        im=base.copy(); d=ImageDraw.Draw(im); t=g/FPS
        if int(t*2)%2==0: d.ellipse([84,90,108,114],fill=(210,60,60,255))
        d.text((120,88),"REC",font=f_label,fill=(240,250,255,255))
        cam=c.get("cam","01")
        if isinstance(cam,list): cam=cam[int(t/3)%len(cam)]
        sec=clock0+int(t); ts=f"CAM {cam}   {sec//3600%24:02d}:{sec//60%60:02d}:{sec%60:02d}"
        w=tl(d,ts,f_label); d.text((W-60-w,88),ts,font=f_label,fill=(185,205,215,255))
        proc.stdin.write(np.array(im).tobytes()); g+=1
proc.stdin.close(); rc=proc.wait()
print("ffmpeg rc",rc); print("wrote",OUT,os.path.getsize(OUT),"bytes" if os.path.exists(OUT) else "(missing)")
