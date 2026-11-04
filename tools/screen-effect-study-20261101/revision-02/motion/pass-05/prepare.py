from pathlib import Path
import hashlib,json,sys
sys.stdout.reconfigure(encoding='utf-8')
H=Path(__file__).resolve().parent
BASE=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/motion');OUT=BASE/'pass-05';OUT.mkdir(exist_ok=True);P4=BASE/'pass-04'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
v=json.loads((P4/'verification.json').read_text(encoding='utf-8'));assert not v['videoRendered']
experiment={'status':'stills-only-frozen','videoRendered':False,'reason':'Parent confirmed excessive proxy stretch gone, but upper wall dotted black seams remain in none and stronger global; final video deferred for ownership boundary rendering correction.','htmlSHA256':sha(P4/'preview.html'),'next':'pass05; pass02 smoke unchanged'}
(P4/'experiment-record.json').write_text(json.dumps(experiment,ensure_ascii=False,indent=2),encoding='utf-8')
for name in ['package.py','capture.mjs','compare.py','fields.mjs','measure.py','finalize.py']:
    p=H/name;s=p.read_text(encoding='utf-8');s=s.replace('revision-02/motion/pass-04','revision-02/motion/pass-05').replace("'pass':'04'","'pass':'05'").replace('revision 02 pass04','revision 02 pass05');p.write_text(s,encoding='utf-8')
p=H/'preview.template.html';s=p.read_text(encoding='utf-8');assert '修正版4' in s;p.write_text(s.replace('修正版4','修正版5'),encoding='utf-8')
p=H/'motion-revision.js';s=p.read_text(encoding='utf-8');assert "version:'revision-02-pass-04'" in s;p.write_text(s.replace("version:'revision-02-pass-04'","version:'revision-02-pass-05'"),encoding='utf-8')
frozen=[]
for root in [H.parent/'pass-02',BASE/'pass-02',H.parent/'pass-03',BASE/'pass-03',H.parent/'pass-04',P4]:
    frozen.extend({'path':str(p),'bytes':p.stat().st_size,'sha256':sha(p)} for p in sorted(root.rglob('*')) if p.is_file())
(OUT/'prior-passes-freeze.json').write_text(json.dumps(frozen,ensure_ascii=False,indent=2),encoding='utf-8')
prediction='参照の全体運動と較正形状の局所曲がりを維持し、薄いID境界の黒い点線を減らす。\n同じ1.35倍の拡大と固定した表示を使い、none/global/local/combinedで境界を比較する。\n靄のソースと前景素材はpass02と同じSHAを保ち、退場順序は変えない。\n'
for p in [H/'prediction.md',OUT/'prediction.md']:p.write_text(prediction,encoding='utf-8')
print(json.dumps({'priorFrozenFiles':len(frozen),'stage':'prepared; await frozen seam module then parent actual confirmation before469'}))
