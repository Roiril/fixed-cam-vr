// Fixed camera conventions are copied from calib.js / align-model.js; Unity left handed meters.
export function basis(c){const r=Math.PI/180,x=-c.pitchDeg*r,y=c.yawDeg*r,z=c.rollDeg*r,cx=Math.cos(x),sx=Math.sin(x),cy=Math.cos(y),sy=Math.sin(y),cz=Math.cos(z),sz=Math.sin(z);return [[cy*cz+sy*sx*sz,cx*sz,-sy*cz+cy*sx*sz],[-cy*sz+sy*sx*cz,cx*cz,sy*sz+cy*sx*cz],[sy*cx,-sx,cy*cx]];}
export const dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0);
export const add=(a,b)=>a.map((v,i)=>v+b[i]);
export const mul=(a,s)=>a.map(v=>v*s);
export const sub=(a,b)=>a.map((v,i)=>v-b[i]);
export const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
export function project(p,c){const b=basis(c),d=sub(p,[c.x,c.y,c.z]),q=b.map(v=>dot(v,d));return [c.cxPx+c.fxPx*q[0]/q[2],c.cyPx-c.fyPx*q[1]/q[2],q[2]];}
export function unproject(u,v,z,c){const b=basis(c);return add([c.x,c.y,c.z],add(add(mul(b[0],(u-c.cxPx)/c.fxPx*z),mul(b[1],-(v-c.cyPx)/c.fyPx*z)),mul(b[2],z)));}
export function field(p,t,s=1){const[x,y,z]=p;return [s*(.025*Math.sin(2.3*z+1.3*t)+.012*Math.sin(4*y-.9*t)),s*.022*Math.sin(2.1*x-1.1*t)*Math.sin(1.5*z+.4),s*.035*Math.sin(2.7*y+.8*x-1.2*t)];}
export const objects=[
 {id:9,name:'金属フレーム（輪郭・奥行は推定）',depth:.72,polygon:[[371,176],[423,39],[440,30],[478,31],[488,38],[484,51],[470,65],[511,275],[530,313],[506,320],[491,294],[462,78],[445,75],[394,174],[396,218],[379,226],[371,210]]},
 {id:10,name:'棚・モニタ群（輪郭・奥行は推定）',depth:1.3,polygon:[[516,112],[640,111],[640,308],[604,310],[589,290],[564,294],[547,251],[533,221],[513,190]]}
];
export function inPoly(x,y,poly){let inside=false;for(let i=0,j=poly.length-1;i<poly.length;j=i++){const a=poly[i],b=poly[j];if(((a[1]>y)!=(b[1]>y))&&(x<(b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0]))inside=!inside;}return inside;}
export function cardDepth(o,u,v){return o.depth+.025*Math.sin(u*.027)*Math.sin(v*.021);}
export function makeGeometry(c,room){
 const planes=[{id:1,name:'床',o:[-4.5,0,-4.5],u:[9,0,0],v:[0,0,9]},
 {id:4,name:'外室奥（sim仮面）',o:[-4.5,0,4.5],u:[9,0,0],v:[0,2.7,0]},
 {id:5,name:'外室左（sim仮面）',o:[-4.5,0,-4.5],u:[0,0,9],v:[0,2.7,0]},
 {id:6,name:'外室右（sim仮面）',o:[4.5,0,-4.5],u:[0,0,9],v:[0,2.7,0]},
 {id:7,name:'外室背面（sim仮面）',o:[-4.5,0,-4.5],u:[9,0,0],v:[0,2.7,0]},
 {id:8,name:'天井（sim仮面）',o:[-4.5,2.7,-4.5],u:[9,0,0],v:[0,0,9]}];
 room.walls.forEach((w,i)=>planes.push({id:i+2,name:'カーテン '+w.id,o:[w.x1,0,w.z1],u:[w.x2-w.x1,0,w.z2-w.z1],v:[0,w.h,0]}));
 const triangles=[];function tri(a,b,d,id,bary){for(const [k,p]of [a,b,d].entries())triangles.push(...p,id,...(bary?bary[k]:[+(k===0),+(k===1),+(k===2)]));}
 function grid(fn,n,m,id){for(let j=0;j<m;j++)for(let i=0;i<n;i++){const a=fn(i/n,j/m),b=fn((i+1)/n,j/m),d=fn(i/n,(j+1)/m),e=fn((i+1)/n,(j+1)/m);tri(a,b,d,id);tri(b,e,d,id);}}
 for(const p of planes)grid((u,v)=>add(p.o,add(mul(p.u,u),mul(p.v,v))),Math.ceil(Math.hypot(...p.u)/.045),Math.ceil(Math.hypot(...p.v)/.045),p.id);
 for(const o of objects){const xs=o.polygon.map(p=>p[0]),ys=o.polygon.map(p=>p[1]),x0=Math.min(...xs),x1=Math.max(...xs),y0=Math.min(...ys),y1=Math.max(...ys);grid((a,b)=>{const u=x0+a*(x1-x0),v=y0+b*(y1-y0);return unproject(u,v,cardDepth(o,u,v),c);},Math.ceil((x1-x0)/3),Math.ceil((y1-y0)/3),o.id);}
 const ownership=new Uint8Array(640*480*4),C=[c.x,c.y,c.z];let counts={};
 for(let y=0;y<480;y++)for(let x=0;x<640;x++){const ray=sub(unproject(x+.5,y+.5,1,c),C);let best=Infinity,id=0;for(const p of planes){const n=cross(p.u,p.v),den=dot(ray,n);if(Math.abs(den)<1e-9)continue;const t=dot(sub(p.o,C),n)/den;if(t<=.04||t>=best)continue;const d=sub(add(C,mul(ray,t)),p.o),u=dot(d,p.u)/dot(p.u,p.u),v=dot(d,p.v)/dot(p.v,p.v);if(u>=0&&u<=1&&v>=0&&v<=1){best=t;id=p.id;}}
 for(const o of objects)if(inPoly(x+.5,y+.5,o.polygon)){const z=cardDepth(o,x+.5,y+.5);if(z<best){best=z;id=o.id;}}
 const i=(y*640+x)*4;ownership[i]=id;ownership[i+3]=255;counts[id]=(counts[id]||0)+1;}
 return{triangles:new Float32Array(triangles),ownership,counts,planes,objects};
}
