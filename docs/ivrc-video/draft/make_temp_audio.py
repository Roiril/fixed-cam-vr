# -*- coding: utf-8 -*-
"""
仮スクラッチ音（著作権フリー・合成）。タイミング確認用。本番は DOVA/効果音ラボの素材に差し替える。
A(0-54s): 低いドローン + テープヒス。 B(54-118s): ドローン + 時計刻みのチック。 末尾フェード。
出力: audio/_temp_bed.wav
"""
import os, wave, numpy as np
HERE=os.path.dirname(os.path.abspath(__file__)); AUD=os.path.join(HERE,"audio"); os.makedirs(AUD,exist_ok=True)
sr=44100; dur=118.0; n=int(sr*dur); t=np.arange(n)/sr
def sine(f,a,ph=0): return a*np.sin(2*np.pi*f*t+ph)
lfo=0.5+0.5*np.sin(2*np.pi*0.08*t)            # slow swell
drone = (sine(55,0.16)+sine(82.5,0.10)+sine(110,0.06))*(0.6+0.4*lfo)
rumble= sine(33,0.07)*(0.5+0.5*np.sin(2*np.pi*0.05*t+1.3))
rng=np.random.default_rng(7)
hiss = rng.standard_normal(n).astype(np.float32)*0.012        # tape hiss
# Part B clock tick (>54s): short decaying click each second
tick=np.zeros(n,np.float32)
for s in range(55,118):
    i=int(s*sr); L=int(0.05*sr)
    if i+L<n:
        env=np.exp(-np.arange(L)/(0.012*sr))
        tick[i:i+L]+=0.10*env*np.sin(2*np.pi*1800*np.arange(L)/sr)
# stings: ~50s (異物) and ~116s (終止)
def sting(at,amp,fhi):
    out=np.zeros(n,np.float32); i=int(at*sr); L=int(1.2*sr)
    if i+L<n:
        env=np.exp(-np.arange(L)/(0.35*sr))
        out[i:i+L]+=amp*env*(np.sin(2*np.pi*fhi*np.arange(L)/sr)+0.5*rng.standard_normal(L))
    return out
mix = drone+rumble+hiss+tick+sting(50,0.18,160)+sting(116,0.22,90)
# global fades
fin=int(1.0*sr); fout=int(2.5*sr)
mix[:fin]*=np.linspace(0,1,fin); mix[-fout:]*=np.linspace(1,0,fout)
mix=np.tanh(mix*1.2)                          # soft limit
pk=np.max(np.abs(mix)); mix=mix/pk*0.85
data=(mix*32767).astype(np.int16)
stereo=np.column_stack([data,data]).ravel()
with wave.open(os.path.join(AUD,"_temp_bed.wav"),"wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(sr); w.writeframes(stereo.tobytes())
print("wrote audio/_temp_bed.wav", round(dur,1),"s")
