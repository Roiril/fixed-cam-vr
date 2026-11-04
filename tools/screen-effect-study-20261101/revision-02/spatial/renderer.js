import {assets} from './assets.js';
import {makeGeometry,basis,field,project,unproject,add,objects} from './geometry.js';
// Geometry and the fixed display are independent passes. No processed CRT RGB is mapped to the mesh.
const vertex=`#version 300 es
precision highp float;
in vec3 position;in float surface;in vec3 bary;
uniform vec3 C,right,up,fwd;uniform vec4 lens;uniform float time,strength;
out vec3 originalCamera;out vec3 edge;flat out int sid;out float depth;
vec3 camera(vec3 p){vec3 d=p-C;return vec3(dot(d,right),dot(d,up),dot(d,fwd));}
vec3 F(vec3 p){return vec3(.025*sin(2.3*p.z+1.3*time)+.012*sin(4.*p.y-.9*time),.022*sin(2.1*p.x-1.1*time)*sin(1.5*p.z+.4),.035*sin(2.7*p.y+.8*p.x-1.2*time));}
void main(){originalCamera=camera(position);vec3 q=camera(position+strength*F(position));depth=q.z;edge=bary;sid=int(surface+.5);float n=.04,f=30.;gl_Position=vec4((lens.x*q.x+(lens.z-320.)*q.z)/320.,(lens.y*q.y+(240.-lens.w)*q.z)/240.,(f+n)/(f-n)*q.z-2.*f*n/(f-n),q.z);}`;
const fragment=`#version 300 es
precision highp float;precision highp int;
in vec3 originalCamera;in vec3 edge;flat in int sid;in float depth;
uniform sampler2D photo,owner;uniform vec4 lens;uniform int diagnostic;
out vec4 color;
vec3 palette(int id){if(id==1)return vec3(.0,.62,.45);if(id==2)return vec3(.9,.62,0);if(id==3)return vec3(.8,.4,0);if(id==9)return vec3(.8,.6,.7);if(id==10)return vec3(.34,.7,.9);return vec3(.18,.24,.3)+float(id-4)*.07;}
void main(){vec2 uv=vec2(lens.x*originalCamera.x/originalCamera.z+lens.z,lens.w-lens.y*originalCamera.y/originalCamera.z)/vec2(640,480);
 if(originalCamera.z<.04||any(lessThan(uv,vec2(0)))||any(greaterThanEqual(uv,vec2(1))))discard;
 int own=int(texture(owner,uv).r*255.+.5);if(own!=sid)discard;
 vec3 rgb=texture(photo,uv).rgb;
 if(diagnostic==1)rgb=palette(sid);
 if(diagnostic==2)rgb=vec3(clamp(depth/8.,0.,1.));
 if(diagnostic==3){vec3 width=fwidth(edge);float w=min(min(edge.x/width.x,edge.y/width.y),edge.z/width.z);rgb=mix(palette(sid)*.2,vec3(.95),1.-smoothstep(.2,1.,w));}
 if(diagnostic==4)rgb=vec3(.02);
 color=vec4(rgb,1.);}`;
function program(gl,vs,fs){function shader(type,s){let p=gl.createShader(type);gl.shaderSource(p,s);gl.compileShader(p);if(!gl.getShaderParameter(p,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(p));return p;}let p=gl.createProgram();gl.attachShader(p,shader(gl.VERTEX_SHADER,vs));gl.attachShader(p,shader(gl.FRAGMENT_SHADER,fs));gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));return p;}
export async function createSpatialScene({canvas,assetBase}={}){
 canvas??=document.createElement('canvas');canvas.width ||=720;canvas.height ||=540;
 const gl=canvas.getContext('webgl2',{preserveDrawingBuffer:true,antialias:false,alpha:true,premultipliedAlpha:false});if(!gl)throw Error('WebGL2 unavailable');
 const geometry=makeGeometry(assets.camera,assets.room),c=assets.camera,b=basis(c),p=program(gl,vertex,fragment),loc=Object.fromEntries(['C','right','up','fwd','lens','time','strength','photo','owner','diagnostic'].map(k=>[k,gl.getUniformLocation(p,k)]));
 const vao=gl.createVertexArray();gl.bindVertexArray(vao);let buf=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buf);gl.bufferData(gl.ARRAY_BUFFER,geometry.triangles,gl.STATIC_DRAW);
 for(const[name,size,offset]of [['position',3,0],['surface',1,12],['bary',3,16]]){let a=gl.getAttribLocation(p,name);gl.enableVertexAttribArray(a);gl.vertexAttribPointer(a,size,gl.FLOAT,false,28,offset);}
 function texture(unit,data,w,h,filter=gl.LINEAR){gl.activeTexture(gl.TEXTURE0+unit);const tex=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,tex);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,filter);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,filter);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);if(w)gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,w,h,0,gl.RGBA,gl.UNSIGNED_BYTE,data);else gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,data);return tex;}
 async function image(url){const i=new Image();i.src=url;await i.decode();return i;}
 const photo=await image(assets.photo),aperture=await image(assets.aperture);
 const photoTex=texture(0,photo),ownerTex=texture(1,geometry.ownership,640,480,gl.NEAREST);
 const target=texture(2,null,640,480,gl.NEAREST),frame=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,frame);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,target,0);
 const depthBuf=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,depthBuf);gl.renderbufferStorage(gl.RENDERBUFFER,gl.DEPTH_COMPONENT24,640,480);gl.framebufferRenderbuffer(gl.FRAMEBUFFER,gl.DEPTH_ATTACHMENT,gl.RENDERBUFFER,depthBuf);if(gl.checkFramebufferStatus(gl.FRAMEBUFFER)!==gl.FRAMEBUFFER_COMPLETE)throw Error('Incomplete FBO');
 const displayProgram=program(gl,`#version 300 es
 in vec2 position;out vec2 uv;void main(){uv=(position+1.)*.5;gl_Position=vec4(position,0,1);}`,`#version 300 es
 precision highp float;in vec2 uv;uniform sampler2D scene,aperture,photo;uniform int mode,display,holes;uniform vec4 motion;out vec4 color;
 void main(){vec2 q=vec2(uv.x,1.-uv.y);vec4 s=texture(scene,uv);if(mode!=0){vec2 r=q-.5-motion.zw;float a=motion.x;r=mat2(cos(a),-sin(a),sin(a),cos(a))*vec2(r.x*1.333333,r.y);r.x/=1.333333;r=r/motion.y+.5;if(any(lessThan(r,vec2(0)))||any(greaterThanEqual(r,vec2(1))))s=vec4(0);else s=texture(photo,r);}
 vec3 rgb=s.rgb;float alpha=s.a;if(holes==1&&alpha<.5){rgb=vec3(1,0,1);alpha=1.;}
 if(display==1){float a=texture(aperture,q).r;float scan=.965+.035*cos(q.y*480.*3.14159265);float vignette=1.-.18*pow(length((q-.5)*1.25),2.);rgb=rgb*scan*vignette+vec3(.018,.016,.014)*pow(max(0.,1.-length((q-vec2(.35,.27))*1.2)),8.);rgb*=a;alpha=max(alpha,1.-a);}
 color=vec4(rgb,alpha);}`);
 const quad=gl.createVertexArray();gl.bindVertexArray(quad);const qb=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,qb);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,-1,1,-1,1,1,-1,1,1]),gl.STATIC_DRAW);let a=gl.getAttribLocation(displayProgram,'position');gl.enableVertexAttribArray(a);gl.vertexAttribPointer(a,2,gl.FLOAT,false,0,0);
 const displayLoc=Object.fromEntries(['scene','aperture','photo','mode','display','holes','motion'].map(k=>[k,gl.getUniformLocation(displayProgram,k)]));const apertureTex=texture(3,aperture);
 function tracked(t){const rows=assets.track.rows;const time=Math.max(5,Math.min(rows.at(-1).t,t));let i=Math.min(rows.length-2,Math.max(0,Math.floor((time-5)*10))),a=rows[i],b=rows[i+1],f=Math.max(0,Math.min(1,(time-a.t)/(b.t-a.t)));const measured=['angle','scale','dx','dy'].map(k=>a[k]+(b[k]-a[k])*f);const ss=(a,b,x)=>{let q=Math.max(0,Math.min(1,(x-a)/(b-a)));return q*q*(3-2*q);};const w=t<4.3||t>13.28?0:Math.min(1,(t-4.3)/.7);const free=[.018*Math.sin(t*1.2)+.006*Math.sin(t*3),1.+.018*Math.sin(t*.53)+.032*ss(6,11.4,t),.0035*Math.sin(t*.83),.003*Math.sin(t*.71)];const target=[measured[0]*1.35,1+(measured[1]-1)*1.35,measured[2]*1.3,measured[3]*1.3];return free.map((v,i)=>v+(target[i]-v)*w);}
 function renderAt(t,{mode='local',strength=1,diagnostic='beauty',display=true,showHoles=diagnostic==='holes'}={}){
 if(!['local','global','none'].includes(mode))throw Error('Unknown mode');gl.bindFramebuffer(gl.FRAMEBUFFER,frame);gl.viewport(0,0,640,480);gl.clearColor(0,0,0,0);gl.clearDepth(1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);gl.depthFunc(gl.LEQUAL);gl.disable(gl.CULL_FACE);gl.disable(gl.BLEND);gl.useProgram(p);gl.bindVertexArray(vao);
 gl.uniform3f(loc.C,c.x,c.y,c.z);for(const[k,v]of [['right',b[0]],['up',b[1]],['fwd',b[2]]])gl.uniform3f(loc[k],...v);gl.uniform4f(loc.lens,c.fxPx,c.fyPx,c.cxPx,c.cyPx);gl.uniform1f(loc.time,t);gl.uniform1f(loc.strength,mode==='local'?strength:0);gl.uniform1i(loc.diagnostic,Math.max(0,['beauty','surface','depth','wire','holes'].indexOf(diagnostic)));
 for(const[unit,tex]of [[0,photoTex],[1,ownerTex]]){gl.activeTexture(gl.TEXTURE0+unit);gl.bindTexture(gl.TEXTURE_2D,tex);}gl.uniform1i(loc.photo,0);gl.uniform1i(loc.owner,1);gl.drawArrays(gl.TRIANGLES,0,geometry.triangles.length/7);
 gl.bindFramebuffer(gl.FRAMEBUFFER,null);gl.viewport(0,0,canvas.width,canvas.height);gl.disable(gl.DEPTH_TEST);gl.useProgram(displayProgram);gl.bindVertexArray(quad);for(const[unit,tex]of [[0,photoTex],[2,target],[3,apertureTex]]){gl.activeTexture(gl.TEXTURE0+unit);gl.bindTexture(gl.TEXTURE_2D,tex);}gl.uniform1i(displayLoc.scene,2);gl.uniform1i(displayLoc.photo,0);gl.uniform1i(displayLoc.aperture,3);gl.uniform1i(displayLoc.mode,mode==='global'?1:0);gl.uniform1i(displayLoc.display,+display);gl.uniform1i(displayLoc.holes,+showHoles);let m=mode==='global'?tracked(t):[0,1,0,0];m=[m[0]*strength,1+(m[1]-1)*strength,m[2]*strength,m[3]*strength];gl.uniform4f(displayLoc.motion,...m);gl.drawArrays(gl.TRIANGLES,0,6);return canvas;}
 function readRaw(){gl.bindFramebuffer(gl.FRAMEBUFFER,frame);const pixels=new Uint8Array(640*480*4);gl.readPixels(0,0,640,480,gl.RGBA,gl.UNSIGNED_BYTE,pixels);gl.bindFramebuffer(gl.FRAMEBUFFER,null);return pixels;}
 renderAt(0);return{canvas,renderAt,readRaw,ready:true,camera:c,geometry,photo,sourceManifest:assets.sources,project:p=>project(p,c),unproject:(u,v,z)=>unproject(u,v,z,c),displace:(p,t,s=1)=>add(p,field(p,t,s)),objects,assetBase};
}
