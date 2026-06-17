# -*- coding: utf-8 -*-
"""
廻リ視 IVRC ビデオ — ファイナルビルダー（素材取り込み + 日本語組版対応）
- footage/<CUT>.mp4(.mov) があれば実映像、無ければ黒板＋キャプションの仮にフォールバック。
- フォント: 見出し=Shippori Mincho ExtraBold / 本文UI=Zen Kaku Gothic New（fonts/、OFL）。
- 字幕は禁則処理＋行バランス＋自動縮小で、変な改行・孤立文字を防ぐ。
- CCTV風UI・字幕・実機/イメージ表記・走査線・尺合わせを合成。出力: ivrc-final-v1.mp4 (1920x1080/12fps/無音)
"""
import os, numpy as np, cv2
from PIL import Image, ImageDraw, ImageFont

W, H, FPS = 1920, 1080, 12
HERE = os.path.dirname(os.path.abspath(__file__))
FOOT = os.path.join(HERE, "footage")
FONTD = os.path.join(HERE, "fonts")
OUT  = os.path.join(HERE, "ivrc-final-v1.mp4")

BG=(8,8,8); INK=(255,250,240); ACCENT=(255,222,173); DIM=(128,122,110)
REAL=(150,214,150); IMAGE=(156,152,144); LINE=(46,44,36)

def fpath(n): return os.path.join(FONTD,n)
def fnt(n,s): return ImageFont.truetype(fpath(n),s)
MIN="ShipporiMincho-ExtraBold.ttf"; MINB="ShipporiMincho-Bold.ttf"
ZB="ZenKakuGothicNew-Bold.ttf"; ZM="ZenKakuGothicNew-Medium.ttf"; ZR="ZenKakuGothicNew-Regular.ttf"
f_title=fnt(MIN,150); f_eng=fnt(MINB,46); f_exhibit=fnt(MIN,56)
f_titlesub=fnt(ZM,36); f_kw=fnt(ZM,36)
f_scene=fnt(ZM,42); f_badge=fnt(ZB,30); f_label=fnt(ZR,26); f_micro=fnt(ZR,24)
f_dia_t=fnt(ZB,40); f_dia_s=fnt(ZM,26); f_nov=fnt(ZB,46)
SUB_SIZES=[58,54,50,46,42]   # 字幕の自動縮小候補

# ---- 日本語組版（禁則 + 単語(カタカナ/英数)非分割 + バランス + 自動フィット）----
HEAD=set("、。，．）」』】〕〉》！？ー…・ゝゞ々ぁぃぅぇぉっゃゅょゎァィゥェォッャュョ,.)!?:;”’")  # 行頭禁則(小書き含む)
TAIL=set("（「『【〔〈《([“‘")                                                              # 行末禁則
PARTICLES=set("はがをにへとでもやの")                                                        # 助詞の後は自然な切れ目
def cls(ch):
    o=ord(ch)
    if 0x30A0<=o<=0x30FF: return 'kata'
    if 0x3040<=o<=0x309F: return 'hira'
    if (0x4E00<=o<=0x9FFF) or ch=='々': return 'kanji'
    if ch.isascii() and ch.isalpha(): return 'latin'
    if ch.isdigit(): return 'digit'
    return 'other'
def allowed(s,k):
    """s[k-1] と s[k] の間で改行してよいか。同種(カタカナ/英数)連続の内部は割らない。"""
    a,b=s[k-1],s[k]
    if b in HEAD or a in TAIL: return False
    ca,cb=cls(a),cls(b)
    if ca==cb and ca in ('kata','latin','digit'): return False   # カタカナ/英数の語中で割らない
    if ca=='kanji' and cb=='hira': return False                  # 送り仮名を切らない(振|り返る を防ぐ)
    return True
def tl(d,t,f): return d.textlength(t,font=f)
def greedy(d,text,f,maxw):
    out=[]
    for para in text.split("\n"):
        if para=="": continue
        n=len(para); ls=0; j=0
        while j<n:
            if tl(d,para[ls:j+1],f)>maxw and j>ls:
                bp=-1
                for k in range(j,ls,-1):
                    if allowed(para,k): bp=k; break
                if bp<=ls: bp=j                # 許容点が無ければ強制改行(極端な長語のみ)
                out.append(para[ls:bp]); ls=bp; continue
            j+=1
        out.append(para[ls:])
    return out or [text]
def bpen(s,k):
    a=s[k-1]; ca,cb=cls(a),cls(s[k])
    if a in "、。！？…": return -0.35          # 句読点直後＝最良
    if a in PARTICLES:  return -0.12          # 助詞の後
    if ca!=cb:          return -0.05          # 文字種の境目
    return 0.12                               # 同種(ひらがな/漢字)内部は避ける
def balance2(d,text,f,maxw):
    best=None
    for i in range(1,len(text)):
        if not allowed(text,i): continue
        l1,l2=text[:i],text[i:]
        if tl(d,l1,f)>maxw or tl(d,l2,f)>maxw: continue
        sc=abs(tl(d,l1,f)-tl(d,l2,f))+bpen(text,i)*maxw
        if best is None or sc<best[0]: best=(sc,[l1,l2])
    return best[1] if best else None
def fit_lines(d,text,maxw,sizes,fontname,max_lines=2):
    """最大の収まるサイズで、禁則＋バランス済みの行リストを返す。(font, lines)"""
    for s in sizes:
        f=fnt(fontname,s); lines=greedy(d,text,f,maxw)
        if len(lines)<=max_lines:
            if len(lines)==2 and "\n" not in text:
                b=balance2(d,text,f,maxw)
                if b: lines=b
            return f,lines
    f=fnt(fontname,sizes[-1]); return f,greedy(d,text,f,maxw)  # 最小でも溢れる時はそのまま
def cen(d,lines,f,cy,fill,lh=None):
    a,de=f.getmetrics(); lh=lh or int((a+de)*1.30); y=cy-lh*len(lines)//2
    for ln in lines:
        w=tl(d,ln,f); d.text((W/2-w/2,y),ln,font=f,fill=fill); y+=lh

def find_footage(cid):
    for ext in (".mp4",".mov",".MP4",".MOV",".m4v"):
        p=os.path.join(FOOT,cid+ext)
        if os.path.exists(p): return p
    return None
def fit_letterbox(frame):
    h,w=frame.shape[:2]; s=min(W/w,H/h); nw,nh=max(1,int(w*s)),max(1,int(h*s))
    r=cv2.resize(frame,(nw,nh)); c=np.zeros((H,W,3),np.uint8)
    x,y=(W-nw)//2,(H-nh)//2; c[y:y+nh,x:x+nw]=r; return c

def make_background(cut):
    sp=cut.get("special")
    img=Image.new("RGB",(W,H),BG); d=ImageDraw.Draw(img)
    if sp=="diagram":
        boxes=[("① 固定カメラ","スマホ ×3 (三脚)"),("② 映像伝送","Wi-Fi / MJPEG"),("③ Quest 3","頭の位置でカメラ選択")]
        bw,bh,gap=470,220,72; tot=bw*3+gap*2; x0=(W-tot)//2; y0=298
        for i,(t,s) in enumerate(boxes):
            x=x0+i*(bw+gap); d.rectangle([x,y0,x+bw,y0+bh],outline=(60,58,50),width=2)
            w=tl(d,t,f_dia_t); d.text((x+bw/2-w/2,y0+52),t,font=f_dia_t,fill=INK)
            w=tl(d,s,f_dia_s); d.text((x+bw/2-w/2,y0+132),s,font=f_dia_s,fill=DIM)
            if i<2:
                ax=x+bw+12; ay=y0+bh//2; d.line([(ax,ay),(ax+gap-24,ay)],fill=ACCENT,width=3)
                d.polygon([(ax+gap-24,ay-9),(ax+gap-8,ay),(ax+gap-24,ay+9)],fill=ACCENT)
        nov="操作ボタンなし — 切替トリガは「身体の移動」だけ"; by=y0+bh+78
        w=tl(d,nov,f_nov); d.rectangle([W/2-w/2-32,by-14,W/2+w/2+32,by+72],outline=ACCENT,width=2)
        d.text((W/2-w/2,by+4),nov,font=f_nov,fill=ACCENT)
        return np.array(img)[:,:,::-1].copy()
    if sp=="endcard":
        cen(d,["廻 リ 視"],f_title,372,INK)
        e="Fixed-Camera Horror in Real Space"; w=tl(d,e,f_eng); d.text((W/2-w/2,468),e,font=f_eng,fill=ACCENT)
        d.line([(700,556),(1220,556)],fill=LINE,width=1)
        kw="固定カメラの視点で、現実世界を歩く VR ホラー"; w=tl(d,kw,f_kw); d.text((W/2-w/2,596),kw,font=f_kw,fill=(210,204,190))
        ex="VR 学会での展示へ"; w=tl(d,ex,f_exhibit); d.text((W/2-w/2,690),ex,font=f_exhibit,fill=INK)
        cr="Music: ____(DOVA-SYNDROME) / SE: 効果音ラボ / Fonts: Shippori Mincho・Zen Kaku Gothic New"
        w=tl(d,cr,f_micro); d.text((W/2-w/2,848),cr,font=f_micro,fill=DIM)
        return np.array(img)[:,:,::-1].copy()
    # 仮（プレースホルダ）
    bx0,by0,bx1,by1=256,266,W-256,662
    d.rectangle([bx0,by0,bx1,by1],outline=(48,46,38),width=2)
    d.text((bx0+28,by0+22),"〔仮 / PLACEHOLDER〕",font=f_micro,fill=DIM)
    if sp=="title":
        cen(d,["廻 リ 視"],f_title,436,INK)
        s="固定視点実世界探索ホラー"; w=tl(d,s,f_titlesub); d.text((W/2-w/2,548),s,font=f_titlesub,fill=ACCENT)
    else:
        f,lines=fit_lines(d,cut.get("scene",""),bx1-bx0-90,[42,38,34],ZM,max_lines=3)
        cen(d,lines,f,(by0+by1)//2+8,(216,211,199))
    return np.array(img)[:,:,::-1].copy()

def make_overlay(cut, has_footage):
    img=Image.new("RGBA",(W,H),(0,0,0,0)); d=ImageDraw.Draw(img)
    L=70;m=60
    for (x,y,dx,dy) in [(m,m,1,1),(W-m,m,-1,1),(m,H-m,1,-1),(W-m,H-m,-1,-1)]:
        d.line([(x,y),(x+dx*L,y)],fill=ACCENT+(255,),width=2); d.line([(x,y),(x,y+dy*L)],fill=ACCENT+(255,),width=2)
    sec=cut.get("section")
    if sec: w=tl(d,sec,f_label); d.text((W/2-w/2,62),sec,font=f_label,fill=DIM+(255,))
    if cut.get("special")=="title" and has_footage:
        cen(d,["廻 リ 視"],f_title,436,INK+(255,))
        s="固定視点実世界探索ホラー"; w=tl(d,s,f_titlesub); d.text((W/2-w/2,548),s,font=f_titlesub,fill=ACCENT+(255,))
    bd=cut.get("badge")
    if bd:
        txt,col=bd; bw=tl(d,txt,f_badge); bx=W/2-bw/2
        sp=cut.get("special"); by=726 if sp not in("title","endcard","diagram") else 980
        d.rectangle([bx-22,by-8,bx+bw+22,by+50],outline=col+(255,),width=2); d.text((bx,by),txt,font=f_badge,fill=col+(255,))
    sub=cut.get("sub")
    if sub:
        d.rectangle([0,852,W,1016],fill=(4,4,4,205))
        f,lines=fit_lines(d,sub,W-260,SUB_SIZES,ZB,max_lines=2)
        cen(d,lines,f,936,INK+(255,))
    arr=np.array(img); bgr=arr[:,:,:3][:,:,::-1].copy(); alpha=(arr[:,:,3:4].astype(np.float32))/255.0
    return bgr, alpha

A="PART A ｜ 作品の概要"; B="PART B ｜ 作品の特徴・原理・プロトタイプ"
BR=("実機プロトタイプ",REAL); BI=("イメージ映像",IMAGE)
cuts=[
 dict(id="COLD",dur=6, section="COLD OPEN", cam="01", badge=None, scene="無人の薄暗い部屋 / 固定俯瞰\nテープノイズ", sub="あなたは——空間に据えられた固定カメラ越しにしか、自分を見られない。"),
 dict(id="A1",dur=10, section=A, cam="01", badge=None, special="title", sub="九〇年代のホラーゲームは、動かない固定カメラで、人を怖がらせた。", footage_from="COLD"),
 dict(id="A2",dur=11, section=A, cam="01", badge=BR, scene="固定画角に、HMD装着の体験者本人が歩いて入る（画面内に自分が映る）", sub="もし、その固定カメラの中に、あなた自身が立っていたら。"),
 dict(id="A3",dur=11, section=A, cam="01", badge=BR, scene="体験者が振り返る → スクリーン（固定カメラ映像）の中の自分も振り返る\n視点＝カメラは動かないまま / 死角に何かがよぎる", sub="振り返っても、視点は動かない。振り返るのは、画面の中の自分だけ。"),
 dict(id="A4",dur=8, section=A, cam=["01","02"], badge=("実機",REAL), scene="ゾーン移動でカメラが別画角へ自動切替（暗転＋ノイズ）", sub="角を曲がるだけで、あなたを映すカメラが、ひとりでに切り替わる。"),
 dict(id="A5",dur=8, section=A, cam="02", badge=BI, scene="見ている映像の一部が別撮り映像にすり替わり、居ないはずの人影が現れる\n（A4 と同じCCTV質感のまま）", sub="そして、そこに——居るはずのないものが、映り込む。"),
 dict(id="B0",dur=10, section=B, cam="01", badge=("実機プロトタイプ｜リアルタイム",REAL), scene="現実：三脚スマホ 3 台 ＋ Quest3 装着者 / 引きの全景", sub="この体験は、実際に動くプロトタイプです。"),
 dict(id="B1",dur=16, section=B, cam=["01","02","03"], badge=("実機・同時記録",REAL), scene="ワンショット：歩く体験者 ＋ HMD映像を映したモニタを同一画角に\nゾーン跨ぎでモニタのカメラが自動切替（テイク内 2 回）", sub="スマホが固定カメラになり、歩いて移動すると、見ているカメラが、ひとりでに切り替わる。"),
 dict(id="B2",dur=13, section=B, special="diagram", sub="スマホの映像を Wi-Fi で Quest 3 へ送り、頭の位置で、今いる場所のカメラを選ぶ。"),
 dict(id="B3",dur=8, section=B, cam="01", badge=("実機",REAL), scene="FX モンタージュ：CRT走査線・色収差・埃・エッジ", sub="映像にはリアルタイムで、監視カメラ特有の質感を重ねる。"),
 dict(id="B4",dur=7, section=B, cam="01", badge=("映像の一部を差し替え（主要演出）｜イメージ映像",IMAGE), scene="主要な恐怖演出：固定カメラ映像の一部が、事前に用意した別撮り映像にすり替わる\n（例：居なかった人影が現れる。マスクで一部だけ差し替え）", sub="見ている映像の一部が、別撮りの映像に、すり替わる。"),
 dict(id="B5",dur=10, section=B, special="endcard"),
]
total=sum(c["dur"] for c in cuts)
print(f"total {total}s = {total//60}:{total%60:02d}")

if __name__=="__main__":
    vw=cv2.VideoWriter(OUT,cv2.VideoWriter_fourcc(*"mp4v"),FPS,(W,H)); assert vw.isOpened()
    g=0; clock0=23*3600+14*60+7; used=[]
    for c in cuts:
        src=find_footage(c["id"]) or (find_footage(c["footage_from"]) if c.get("footage_from") else None)
        has=src is not None and c.get("special")!="diagram"
        used.append(f"{c['id']}:{'REAL' if has else ('SPECIAL' if c.get('special') in ('diagram','endcard') else 'placeholder')}")
        bg=None if has else make_background(c)
        ov_bgr,ov_a=make_overlay(c,has)
        cap=None; fcount=0; ffps=FPS; in_pt=0.0
        if has:
            cap=cv2.VideoCapture(src); ffps=cap.get(cv2.CAP_PROP_FPS) or FPS; fcount=cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0; in_pt=c.get("in_point",0.0)
        nfr=int(round(c["dur"]*FPS))
        for fi in range(nfr):
            if has:
                fidx=int((in_pt+fi/FPS)*ffps)
                if fcount>0: fidx%=int(fcount)
                cap.set(cv2.CAP_PROP_POS_FRAMES,fidx); ok,raw=cap.read()
                if not ok: cap.set(cv2.CAP_PROP_POS_FRAMES,0); ok,raw=cap.read()
                base=fit_letterbox(raw) if ok else np.zeros((H,W,3),np.uint8)
            else: base=bg.copy()
            fr=(base.astype(np.float32)*(1-ov_a)+ov_bgr.astype(np.float32)*ov_a).astype(np.uint8)
            fr[::4]=(fr[::4].astype(np.float32)*0.90).astype(np.uint8)
            t=g/FPS
            if int(t*2)%2==0: cv2.circle(fr,(96,100),11,(60,60,200),-1)
            cv2.putText(fr,"REC",(118,110),cv2.FONT_HERSHEY_DUPLEX,0.9,(240,250,255),1,cv2.LINE_AA)
            cam=c.get("cam","01")
            if isinstance(cam,list): cam=cam[int(t/3)%len(cam)]
            sec=clock0+int(t); ts=f"CAM {cam}   {sec//3600%24:02d}:{sec//60%60:02d}:{sec%60:02d}"
            (tw,_),_=cv2.getTextSize(ts,cv2.FONT_HERSHEY_DUPLEX,0.9,1)
            cv2.putText(fr,ts,(W-60-tw,110),cv2.FONT_HERSHEY_DUPLEX,0.9,(180,200,210),1,cv2.LINE_AA)
            px=int(W*(g/(total*FPS))); cv2.rectangle(fr,(0,H-6),(W,H),(24,24,24),-1); cv2.rectangle(fr,(0,H-6),(px,H),(173,222,255),-1)
            bx=int(W*(54/total)); cv2.rectangle(fr,(bx-1,H-12),(bx+1,H),(173,222,255),-1)
            vw.write(fr); g+=1
        if cap is not None: cap.release()
    vw.release()
    print("wrote",OUT,os.path.getsize(OUT),"bytes"); print("sources:"," ".join(used))
