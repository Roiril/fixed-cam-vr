# -*- coding: utf-8 -*-
"""
全ページを背景透過 PNG で個別出力（動画ではなく画像）。順番が分かる連番ファイル名で
docs/ivrc-video/pages/ にまとめる。各ページ＝字幕＋CCTV UI（四隅/REC/カメラ番号/タイムスタンプ）
＋実機/イメージ バッジ＋グラフィック（タイトル/原理図/演出プラン/エンドカード）。背景は完全透過。
build_final.py の台本(cuts)・フォント・組版ヘルパを再利用（build_final は __main__ ガード済み）。
"""
import os, numpy as np
from PIL import Image, ImageDraw
import build_final as B

W,H = B.W,B.H
INK,ACCENT,DIM,REAL,IMAGE = B.INK,B.ACCENT,B.DIM,B.REAL,B.IMAGE
LINE=(64,60,50)
tl,cen,fit_lines,fnt = B.tl,B.cen,B.fit_lines,B.fnt
f_title,f_eng,f_titlesub,f_kw = B.f_title,B.f_eng,B.f_titlesub,B.f_kw
f_badge,f_micro,f_label,f_dia_t,f_dia_s,f_nov = B.f_badge,B.f_micro,B.f_label,B.f_dia_t,B.f_dia_s,B.f_nov
SUB_SIZES,ZB,ZM = B.SUB_SIZES,B.ZB,B.ZM
f_eng_end=fnt("ShipporiMincho-Bold.ttf",48); f_plan_h=fnt(ZB,58); f_plan=fnt(ZM,42); f_plan_s=fnt(ZM,30)
cuts=B.cuts
OUTDIR=os.path.join(B.HERE,os.pardir,"pages"); OUTDIR=os.path.abspath(OUTDIR); os.makedirs(OUTDIR,exist_ok=True)

SLUG_SUFFIX={"COLD":"cold","A1":"title","A2":"inside-frame","A3":"turn-around","A4":"auto-switch",
      "A5":"something-appears","B0":"prototype","B1":"walk-switch","B2":"diagram",
      "B3":"fx-filter","B4":"effects-plan","B5":"endcard"}
def Al(c): return (c[0],c[1],c[2],255)

def ctext(d,s,f,y,fill):
    w=tl(d,s,f); d.text((W/2-w/2,y),s,font=f,fill=fill)

def draw_graphics(d,cut):
    sp=cut.get("special"); cid=cut["id"]
    if cid=="B4":  # 演出プラン一覧（構想）
        ctext(d,"これから加える演出（構想）",f_plan_h,250,Al(INK))
        items=[("日本人形に、追いかけられる","実空間を移動する脅威が、固定カメラの画角を横切る"),
               ("見ている映像の一部が、別撮り映像にすり替わる","主要演出。居ないはずのものが現れる"),
               ("死角からの気配","カメラに映らない場所で動き、切替の瞬間に現れる")]
        x=470; y=430
        for t,s in items:
            d.text((x,y),"▸",font=f_plan,fill=Al(ACCENT))
            d.text((x+56,y),t,font=f_plan,fill=Al(INK))
            d.text((x+56,y+52),s,font=f_plan_s,fill=Al(DIM)); y+=128
        return
    if sp=="diagram":
        f_h=fnt(ZB,40); f_bt=fnt(ZB,38); f_bs=fnt(ZM,26); f_lab=fnt(ZM,24)
        def ahead(x,y,dr,s=11):
            if dr=='r': pts=[(x,y),(x-s,y-s),(x-s,y+s)]
            elif dr=='l': pts=[(x,y),(x+s,y-s),(x+s,y+s)]
            elif dr=='u': pts=[(x,y),(x-s,y+s),(x+s,y+s)]
            else: pts=[(x,y),(x-s,y-s),(x+s,y-s)]
            d.polygon(pts,fill=Al(ACCENT))
        def lab(t,cx,cy):
            w=tl(d,t,f_lab); d.text((cx-w/2,cy),t,font=f_lab,fill=Al(DIM))
        def box(x,y,w,h,t,s,oc):
            d.rectangle([x,y,x+w,y+h],outline=Al(oc),width=2)
            tw=tl(d,t,f_bt); d.text((x+w/2-tw/2,y+38),t,font=f_bt,fill=Al(INK))
            sw=tl(d,s,f_bs); d.text((x+w/2-sw/2,y+102),s,font=f_bs,fill=Al(DIM))
        up=cut.get("upper")                 # 上寄せ（下半分を UI 動画用に空ける）
        hy=138 if up else 196
        ctext(d,"システム構成 — 体験者の移動が「見るカメラ」を選ぶ",f_h,hy,Al(INK))
        y0=210 if up else 320; ay=y0+75
        box(150, y0,400,150,"固定カメラ ×3","実空間のスマートフォン",(86,82,70))
        box(1370,y0,400,150,"体験者","HMD装着・実空間を歩く",(86,82,70))
        # Quest3（背の高い箱・内部に「自動切替」の強調枠）
        d.rectangle([760,y0,1160,y0+230],outline=Al((86,82,70)),width=2)
        tw=tl(d,"Quest 3",f_bt); d.text((960-tw/2,y0+26),"Quest 3",font=f_bt,fill=Al(INK))
        sw=tl(d,"受信・デコード・表示",f_bs); d.text((960-sw/2,y0+88),"受信・デコード・表示",font=f_bs,fill=Al(DIM))
        f_in=fnt(ZB,26); d.rectangle([788,y0+150,1132,y0+206],outline=Al(ACCENT),width=2)
        iw=tl(d,"頭の位置でカメラ自動切替",f_in); d.text((960-iw/2,y0+163),"頭の位置でカメラ自動切替",font=f_in,fill=Al(ACCENT))
        # 前向き: 固定カメラ → Quest3 → 体験者（伝送は矢印ラベルに降格）
        d.line([(550,ay),(754,ay)],fill=Al(ACCENT),width=3); ahead(755,ay,'r'); lab("Wi-Fi / MJPEG",652,ay-44)
        d.line([(1160,ay),(1364,ay)],fill=Al(ACCENT),width=3); ahead(1365,ay,'r'); lab("表示",1262,ay-44)
        # 切替の主体は Quest3 自身（カード内に明記）。体験者→Quest3 の逆向き矢印は描かない。
        if not up:
            ctext(d,"操作ボタンは無い — 身体の移動だけで切り替わる",fnt(ZM,28),726,Al(ACCENT))
    elif sp=="endcard":  # 「VR 学会での展示へ」削除・再配置版
        ctext(d,"廻 リ 視",f_title,326,Al(INK))
        ctext(d,"Fixed-Camera Horror in Real Space",f_eng_end,586,Al(ACCENT))
        d.line([(660,684),(1260,684)],fill=Al(LINE),width=1)
        ctext(d,"固定カメラの視点で、現実世界を歩く VR ホラー",f_kw,726,(210,204,190,255))
        ctext(d,"IVRC2026　／　企画・開発　白石大晴",f_micro,948,Al(DIM))
        ctext(d,"サポート　堀越・Kim・ほか 明治大学 橋本研究室メンバー",f_micro,990,Al(DIM))
    elif sp=="title":
        ctext(d,"廻 リ 視",f_title,340,Al(INK))
        ctext(d,"固定視点実世界探索ホラー",f_titlesub,600,Al(ACCENT))
    elif sp=="autoswitch":  # 中央に webUI（切替の様子）を貼る前提。上に見出しのみ
        ctext(d,"歩くと、見るカメラが自動で切り替わる",fnt(ZB,46),150,Al(INK))
        ctext(d,"位置に応じて固定カメラを自動選択 — 操作ボタンなし",fnt(ZM,28),232,Al(DIM))
    elif sp=="flow":  # 学術トーン。通常=上寄せ（下に画像）／center=画像無しで中央寄せ
        ctr=cut.get("center")
        hy,sy,cy=(430,512,612) if ctr else (116,200,332)
        ctext(d,cut["head"],fnt(ZB,46),hy,Al(INK))
        st=cut.get("step")           # None=ステッパー無 / 0..3=該当周を強調 / "all"=全表示・無強調
        if st is not None:
            steps=["導入","1周目","2周目","3周目"]; f_s=fnt(ZM,26); sep="   →   "
            ws=[tl(d,s,f_s) for s in steps]; sw=tl(d,sep,f_s)
            x=W/2-(sum(ws)+sw*3)/2; y=sy
            for i,s in enumerate(steps):
                if st==i: d.rectangle([x-14,y-7,x+ws[i]+14,y+41],outline=Al(REAL),width=2)
                d.text((x,y),s,font=f_s,fill=Al(REAL if st==i else (104,100,88))); x+=ws[i]
                if i<3: d.text((x,y),sep,font=f_s,fill=Al((80,76,66))); x+=sw
        f,lines=fit_lines(d,cut["cap"],1500,[30,28,26],ZM,max_lines=3); cen(d,lines,f,cy,(210,205,193,255))
        t="構想"; w=tl(d,t,f_badge); d.rectangle([W-60-w-44,138,W-60,194],outline=Al(IMAGE),width=2); d.text((W-60-w-22,146),t,font=f_badge,fill=Al(IMAGE))
    elif sp=="flowtitle":  # 章扉（学術調）：これから体験の流れを解説する合図
        ctext(d,"これから作る体験 — 構想",fnt(ZM,28),400,Al(DIM))
        ctext(d,"体験の流れ",fnt(ZB,76),452,Al(INK))
        d.line([(W/2-300,574),(W/2+300,574)],fill=Al(LINE),width=1)
        ctext(d,"固定カメラ映像のみで周回経路を 3 周し、周ごとに映像演出が深まる。",fnt(ZM,32),616,(210,205,193,255))
        t="構想"; w=tl(d,t,f_badge); d.rectangle([W-60-w-44,138,W-60,194],outline=Al(IMAGE),width=2); d.text((W-60-w-22,146),t,font=f_badge,fill=Al(IMAGE))
    elif sp=="loop3":  # 3周目：操作する自己と映像の中の自己の「入れ替わり」を図解（審査員レビュー反映）
        f_h=fnt(ZB,46); f_s=fnt(ZM,26); f_bt=fnt(ZB,36); f_bs=fnt(ZM,24); f_sum=fnt(ZM,30)
        ctext(d,"3 周目 — 人形となって、過去の自分を追う",f_h,292,Al(INK))
        steps=["導入","1周目","2周目","3周目"]; sep="   →   "
        ws=[tl(d,s,f_s) for s in steps]; sw=tl(d,sep,f_s); x=W/2-(sum(ws)+sw*3)/2; y=378
        for i,s in enumerate(steps):
            if i==3: d.rectangle([x-14,y-7,x+ws[i]+14,y+41],outline=Al(REAL),width=2)
            d.text((x,y),s,font=f_s,fill=Al(REAL if i==3 else (104,100,88))); x+=ws[i]
            if i<3: d.text((x,y),sep,font=f_s,fill=Al((80,76,66))); x+=sw
        # 入れ替わりの前後対比（薄い1行）— 図が「追跡」だけにならないように
        ctext(d,"これまで：画面の外から操作　→　3周目：あなたが画面の中にいる",f_s,452,Al(DIM))
        # 2箱（主体＝操作する自己／対象＝映像の中の自己 に非対称化）
        bw,bh,by=560,150,524
        def cbox(bx,oc,t,s):
            d.rectangle([bx,by,bx+bw,by+bh],outline=Al(oc),width=2)
            w=tl(d,t,f_bt); d.text((bx+bw/2-w/2,by+34),t,font=f_bt,fill=Al(INK))
            w=tl(d,s,f_bs); d.text((bx+bw/2-w/2,by+98),s,font=f_bs,fill=Al(DIM))
        cbox(200,ACCENT,"操作する自己","人形になった、いまのあなた")
        cbox(1160,(86,82,70),"映像の中の自己","1周目の、過去のあなた")
        ay=by+bh//2
        d.line([(760,ay),(1154,ay)],fill=Al(ACCENT),width=3); d.polygon([(1160,ay),(1146,ay-9),(1146,ay+9)],fill=Al(ACCENT))
        w=tl(d,"自分の身体を追う",f_s); d.text((957-w/2,ay-44),"自分の身体を追う",font=f_s,fill=Al(DIM))
        ctext(d,"操作する自分と、映像の中の自分。どちらが「本当の自分」か、分からなくなる。",f_sum,742,Al(ACCENT))
        t="構想"; w=tl(d,t,f_badge); d.rectangle([W-60-w-44,138,W-60,194],outline=Al(IMAGE),width=2); d.text((W-60-w-22,146),t,font=f_badge,fill=Al(IMAGE))

def page(cut, idx, total):
    img=Image.new("RGBA",(W,H),(0,0,0,0)); d=ImageDraw.Draw(img)
    L=70;m=60
    for (x,y,dx,dy) in [(m,m,1,1),(W-m,m,-1,1),(m,H-m,1,-1),(W-m,H-m,-1,-1)]:
        d.line([(x,y),(x+dx*L,y)],fill=Al(ACCENT),width=2); d.line([(x,y),(x,y+dy*L)],fill=Al(ACCENT),width=2)
    # static REC + CAM/timestamp（順番に並べると連続して見えるよう累積秒で時刻を進める）
    d.ellipse([84,90,108,114],fill=(210,60,60,255)); d.text((120,88),"REC",font=f_label,fill=(240,250,255,255))
    cam=cut.get("cam","01");  cam=cam[0] if isinstance(cam,list) else cam
    sec=23*3600+14*60+7+idx*7
    ts=f"CAM {cam}   {sec//3600%24:02d}:{sec//60%60:02d}:{sec%60:02d}"
    w=tl(d,ts,f_label); d.text((W-60-w,88),ts,font=f_label,fill=(185,205,215,255))
    draw_graphics(d,cut)
    bd=cut.get("badge")
    if cut["id"]=="B4": bd=("構想",IMAGE)
    if bd:
        txt,col=bd; bw=tl(d,txt,f_badge); bx=W/2-bw/2
        by=726 if cut.get("special") not in("title","endcard","diagram") and cut["id"]!="B4" else 980
        d.rectangle([bx-22,by-8,bx+bw+22,by+50],outline=Al(col),width=2); d.text((bx,by),txt,font=f_badge,fill=Al(col))
    if cut.get("sub") and cut["id"]!="B4":
        d.rectangle([0,852,W,1016],fill=(4,4,4,205))
        f,lines=fit_lines(d,cut["sub"],W-260,SUB_SIZES,ZB,max_lines=2); cen(d,lines,f,936,Al(INK))
    return img

# タイトル《廻リ視》と「九〇年代…」字幕は別ページに分割する
seq=[]
for c in cuts:
    if c["id"]=="A1":
        t=dict(c); t["sub"]=None; t["slug"]="title"; seq.append(t)                 # タイトルのみ
        seq.append({"id":"A1b","cam":"01","sub":c.get("sub"),"slug":"intro-90s"})   # 九〇年代字幕のみ
    else:
        d=dict(c); d["slug"]=SLUG_SUFFIX.get(c["id"],c["id"]); seq.append(d)
for i,c in enumerate(seq):
    if c["id"]=="B4": continue   # 旧「演出プラン一覧」ページは廃止 → 下の flow スライド群に置換（endcard の番号は維持）
    img=page(c,i,len(seq)); name=f"{i:02d}_{c['slug']}.png"
    img.save(os.path.join(OUTDIR,name)); print("wrote",name)
# 自動切り替えの様子スライド（中央に webUI を貼る前提・上に見出しのみ）
asw={"id":"AUTOSW","special":"autoswitch","cam":"01"}
img=page(asw,9,len(seq)); img.save(os.path.join(OUTDIR,"09b_autoswitch.png")); print("wrote 09b_autoswitch.png")
# 章扉（体験の流れの解説に入る合図）
ft={"id":"FLOWTITLE","special":"flowtitle","cam":"01"}
img=page(ft,10,len(seq)); img.save(os.path.join(OUTDIR,"10b_flow-section.png")); print("wrote 10b_flow-section.png")
# これから作る体験の流れ（学術トーン・上寄せ・下に Blender 画像/動画）
flows=[
 ("11_flow-00-stage","舞台 — L字壁を巡る周回経路","L字の壁の周りを歩く。日本人形が、追ってくる。",None),
 ("11_flow-01-loop","体験の構造 — 同じ経路を 3 周する","同じ道を3周。周ごとに、起きることが変わる。","all"),
 ("11_flow-02-intro","導入 — 固定視点に慣れる","壁をたどって歩き、固定視点に慣れる。",0),
 ("11_flow-03-loop1","1 周目 — 死角に現れる、血の手形","触れた壁に、血の手形が現れる。",1),
 ("11_flow-04-loop2","2 周目 — 人形が、怪物へ変わる","人形が画面の中で、怪物に変わって襲ってくる。",2),
 ("11_flow-06-theme","自己認識の、段階的な揺らぎ","「自分」が、少しずつ別の何かへずれていく。",None),
]
CENTER={"11_flow-02-intro"}  # 画像準備中 → 中央寄せの説明スライド（loop3 は専用図解）
for slug,head,cap,step in flows:
    cut={"id":"FLOW","special":"flow","head":head,"cap":cap,"step":step,"cam":"01","center":slug in CENTER}
    img=page(cut,11,len(seq)); img.save(os.path.join(OUTDIR,slug+".png")); print("wrote",slug+".png")
# 3周目は専用図解（入れ替わりを2箱＋矢印で明示）
l3={"id":"LOOP3","special":"loop3","cam":"01"}
img=page(l3,11,len(seq)); img.save(os.path.join(OUTDIR,"11_flow-05-loop3.png")); print("wrote 11_flow-05-loop3.png")
print("DIR:",OUTDIR)
