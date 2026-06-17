# -*- coding: utf-8 -*-
"""
ivrc-final-v1.mp4(無音) を H.264+AAC に再エンコードし、音声をmuxして配布用 ivrc-final.mp4 を作る。
音声の優先順位: audio/bed.*（あなたが置いた本番音=DOVA等のミックス） > audio/_temp_bed.wav（仮スクラッチ） > 無音。
ffmpeg は imageio-ffmpeg 同梱バイナリを使用（PATH 非依存）。
"""
import os, glob, subprocess, imageio_ffmpeg
HERE=os.path.dirname(os.path.abspath(__file__))
FF=imageio_ffmpeg.get_ffmpeg_exe()
VID=os.path.join(HERE,"ivrc-final-v1.mp4")
OUT=os.path.join(HERE,"ivrc-final.mp4")
AUD=os.path.join(HERE,"audio")

def pick_audio():
    for pat in ("bed.wav","bed.mp3","bed.m4a","bed.aac","bed.flac"):
        p=os.path.join(AUD,pat)
        if os.path.exists(p): return p,"本番"
    p=os.path.join(AUD,"_temp_bed.wav")
    if os.path.exists(p): return p,"仮スクラッチ"
    return None,None

assert os.path.exists(VID), f"先に build_final.py で {VID} を作って"
audio,kind=pick_audio()
if audio:
    print(f"audio: {os.path.basename(audio)} ({kind})")
    cmd=[FF,"-y","-i",VID,"-i",audio,
         "-map","0:v:0","-map","1:a:0",
         "-c:v","libx264","-pix_fmt","yuv420p","-crf","20","-preset","medium",
         "-c:a","aac","-b:a","192k","-shortest","-movflags","+faststart",OUT]
else:
    print("audio: なし（無音でH.264再エンコードのみ）")
    cmd=[FF,"-y","-i",VID,"-c:v","libx264","-pix_fmt","yuv420p","-crf","20","-preset","medium","-movflags","+faststart",OUT]
subprocess.run(cmd,check=True)
print("wrote",OUT,os.path.getsize(OUT),"bytes")
