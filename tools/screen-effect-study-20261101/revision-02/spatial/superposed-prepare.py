from pathlib import Path
H=Path(__file__).resolve().parent
s=(H/'combined-capture.mjs').read_text(encoding='utf-8').replace('revision-02/spatial/combined','revision-02/spatial/superposed').replace("'combined-preview.html'","'superposed-preview.html'").replace('s.combinedProject(p,t)','s.superposedProject(p,t)').replace('s.combinedProject(p,3.2)','s.superposedProject(p,3.2)').replace('for(const t of [6.5,8.5,10.5])','for(const t of [3.2,6.5,8.5,10.5])')
s=s.replace('globalPixel:s.globalProject(p,t)','globalPixel:s.globalProject(p,t),localPixel:s.project(s.displace(p,t))')
s=s.replace("if(mode!=='global')counts[k]",'counts[k]')
(H/'superposed-capture.mjs').write_bytes(s.encode('utf-8'))
b=(H/'combined-gpu-body.js').read_text(encoding='utf-8').replace('renderer-combined.js','renderer-superposed.js').replace('combined-proxy.js','superposed-motion.js').replace("loc('combined')","loc('wholeMotion')").replace("loc('rigidMotion')","loc('measuredMotion')")
(H/'superposed-gpu-calibration.mjs').write_bytes((s.split('const times=')[0]+b).encode('utf-8'))
f=(H/'combined-finalize.py').read_text(encoding='utf-8').replace('revision-02/spatial/combined','revision-02/spatial/superposed').replace('for t in [6.5,8.5,10.5]:','for t in [3.2,6.5,8.5,10.5]:')
insert="""local=np.array([p['times'][i]['localPixel'][:2] for p in m['trajectories']['points']]);x=np.column_stack([a,np.ones(len(a))]);S=np.linalg.lstsq(x,g,rcond=None)[0];removed=(b-S[2])@np.linalg.inv(S[:2]);m.setdefault('globalSRemoved',[]).append({'t':t,'rmsResidualAfterSimilarityPx':residual(a,removed),'localOnlyRmsAfterSimilarityPx':residual(a,local),'inverseSvsLocalMaxPx':float(np.max(np.linalg.norm(removed-local,axis=1)))})
 """
f=f.replace("m.setdefault('residuals',[]).append(",insert+"m.setdefault('residuals',[]).append(")
f=f.replace("'renderer-combined.js'","'renderer-combined.js','combined-proxy.js'")
f=f.replace("'residuals':m['residuals']","'residuals':m['residuals'],'globalSRemoved':m['globalSRemoved']")
(H/'superposed-finalize.py').write_bytes(f.encode('utf-8'))
