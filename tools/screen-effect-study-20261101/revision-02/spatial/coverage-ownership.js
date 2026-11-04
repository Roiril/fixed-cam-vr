import {objects} from './geometry.js';
const vec=p=>'vec3('+p.map(v=>Number(v).toFixed(9)).join(',')+')';
export function continuousOwnershipGLSL(room){
 const planes=[{id:1,o:[-4.5,0,-4.5],u:[9,0,0],v:[0,0,9]},
 {id:4,o:[-4.5,0,4.5],u:[9,0,0],v:[0,2.7,0]},
 {id:5,o:[-4.5,0,-4.5],u:[0,0,9],v:[0,2.7,0]},
 {id:6,o:[4.5,0,-4.5],u:[0,0,9],v:[0,2.7,0]},
 {id:7,o:[-4.5,0,-4.5],u:[9,0,0],v:[0,2.7,0]},
 {id:8,o:[-4.5,2.7,-4.5],u:[9,0,0],v:[0,0,9]},
 ...room.walls.map((w,i)=>({id:i+2,o:[w.x1,0,w.z1],u:[w.x2-w.x1,0,w.z2-w.z1],v:[0,w.h,0]}))];
 const polygon=o=>`bool inObject${o.id}(vec2 p){bool inside=false;${o.polygon.map((a,i)=>{const b=o.polygon[(i+o.polygon.length-1)%o.polygon.length];return `if(((${a[1]}.>p.y)!=(${b[1]}.>p.y))&&(p.x<(${b[0]-a[0]}. )*(p.y-${a[1]}.)/(${b[1]-a[1]}.)+${a[0]}.))inside=!inside;`;}).join('')}return inside;}`;
 const meshDepth=o=>{const xs=o.polygon.map(p=>p[0]),ys=o.polygon.map(p=>p[1]),x0=Math.min(...xs),y0=Math.min(...ys),w=Math.max(...xs)-x0,h=Math.max(...ys)-y0,n=Math.ceil(w/3),m=Math.ceil(h/3);return `float meshDepth${o.id}(vec2 p){vec2 origin=vec2(${x0}.,${y0}.),stepSize=vec2(${(w/n).toFixed(12)},${(h/m).toFixed(12)});vec2 cell=clamp(floor((p-origin)/stepSize),vec2(0),vec2(${n-1}.,${m-1}.)),f=(p-origin)/stepSize-cell,a=origin+cell*stepSize;float za=${o.depth.toFixed(9)}+.025*sin(a.x*.027)*sin(a.y*.021),zb=${o.depth.toFixed(9)}+.025*sin((a.x+stepSize.x)*.027)*sin(a.y*.021),zd=${o.depth.toFixed(9)}+.025*sin(a.x*.027)*sin((a.y+stepSize.y)*.021),ze=${o.depth.toFixed(9)}+.025*sin((a.x+stepSize.x)*.027)*sin((a.y+stepSize.y)*.021);return f.x+f.y<=1.?1./((1.-f.x-f.y)/za+f.x/zb+f.y/zd):1./((1.-f.y)/zb+(f.x+f.y-1.)/ze+(1.-f.x)/zd);}`;};
 return `uniform vec3 C,right,up,fwd;bool sourcePlaneTie=false;float sourceNearest=1e20;
 void considerPlane(vec3 ray,vec3 o,vec3 u,vec3 v,int candidate,inout float nearest,inout int winner){vec3 n=cross(u,v);float den=dot(ray,n);if(abs(den)<1e-9)return;float z=dot(o-C,n)/den;if(z<=.04)return;vec3 d=C+ray*z-o;vec2 a=vec2(dot(d,u)/dot(u,u),dot(d,v)/dot(v,v));if(all(greaterThanEqual(a,vec2(-.000001)))&&all(lessThanEqual(a,vec2(1.000001)))){if(any(lessThan(abs(a),vec2(.000001)))||any(lessThan(abs(a-1.),vec2(.000001))))sourcePlaneTie=true;if(z<nearest){nearest=z;winner=candidate;}}}
 float sourceSegmentDistance(vec2 p,vec2 a,vec2 b){vec2 d=b-a;float t=clamp(dot(p-a,d)/dot(d,d),0.,1.);return length(p-a-t*d);}
 bool sourceBoundaryTie(vec2 uv){if(sourcePlaneTie)return true;vec2 p=uv*vec2(640,480);${objects.map(o=>o.polygon.map((a,i)=>{const b=o.polygon[(i+1)%o.polygon.length];return `if(sourceSegmentDistance(p,vec2(${a[0]}.,${a[1]}.),vec2(${b[0]}.,${b[1]}.))<.001)return true;`;}).join('')).join('')}return false;}
 ${objects.map(polygon).join('\n')}
 ${objects.map(meshDepth).join('\n')}
 int continuousOwner(vec2 uv){vec2 p=uv*vec2(640,480);vec3 ray=right*((p.x-lens.z)/lens.x)+up*((lens.w-p.y)/lens.y)+fwd;float nearest=1e20;int winner=0;
 ${planes.map(p=>`considerPlane(ray,${vec(p.o)},${vec(p.u)},${vec(p.v)},${p.id},nearest,winner);`).join('\n')}
 ${objects.map(o=>`if(inObject${o.id}(p)){float z=meshDepth${o.id}(p);if(z<nearest){nearest=z;winner=${o.id};}}`).join('\n')}
 sourceNearest=nearest;return winner;}`;
}
