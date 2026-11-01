from pathlib import Path
H=Path(__file__).resolve().parent
O=Path('G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02/spatial/coverage')
s=(O/'coverage-preview.html').read_text(encoding='utf-8')
a='if((originalCamera.z>sourceNearest+.00001||!inSourceMask)&&!sourceBoundaryTie(uv))discard;'
assert a in s
s=s.replace(a,'if(false)discard;')
s=s.replace('color=vec4(rgb,1.);','uint bits=floatBitsToUint(diagnostic==0?originalCamera.z:sourceNearest);color=vec4(float(bits&255u),float((bits>>8)&255u),float((bits>>16)&255u),float((bits>>24)&255u))/255.;if(diagnostic==2)color=vec4(originalCamera.z>sourceNearest+.00001?1.:0.,inSourceMask?1.:0.,sourceBoundaryTie(uv)?1.:0.,1.);if(diagnostic==3)color=vec4(float(sid)/255.,float(own)/255.,0.,1.);')
(O/'debug-no-ownership.html').write_bytes(s.encode('utf-8'))
p=(H/'coverage-capture.mjs').read_text(encoding='utf-8').split('const times=')[0].replace("'coverage-preview.html'","'debug-no-ownership.html'")
p+="""const result=await ev(`(()=>{const s=spatialStudy,rows=[];for(const diagnostic of ['beauty','surface','depth','wire']){s.renderAt(0,{mode:'none',display:false,diagnostic});const a=s.readRaw(),i=((479-342)*640+449)*4,bytes=Array.from(a.slice(i,i+4));rows.push({diagnostic,bytes});}return rows;})()`);for(const r of result){const b=new Uint8Array(r.bytes);r.float=new DataView(b.buffer).getFloat32(0,true);}console.log(JSON.stringify(result));ws.close();}finally{chrome.kill();}
"""
(H/'coverage-debug-render.mjs').write_bytes(p.encode('utf-8'))
