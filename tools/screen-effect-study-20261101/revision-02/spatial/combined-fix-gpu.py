from pathlib import Path
p=Path(__file__).resolve().parent/'combined-gpu-body.js'
s=p.read_text(encoding='utf-8')
s=s.replace("const proof=await ev(`","const {referenceMotion}=await import('./combined-proxy.js');const motions=[6.5,8.5,10.5].map(t=>[t,referenceMotion(t)]);\nconst proof=await ev(`")
a="sh(g.FRAGMENT_SHADER,'#version 300 es\\nprecision highp float;out vec4 color;void main(){color=vec4(0);}');"
b="sh(g.FRAGMENT_SHADER,${JSON.stringify('#version 300 es\\nprecision highp float;out vec4 color;void main(){color=vec4(0);}')});"
assert a in s
s=s.replace(a,b).replace('for(const t of [6.5,8.5,10.5]){const m=referenceMotion(t);','for(const [t,m] of ${JSON.stringify(motions)}){').replace('expected=globalPixel(uvs[i],m)','expected=s.globalProject(s.unproject(...uvs[i],2),t)')
p.write_bytes(s.encode('utf-8'))
