/* Deterministic composition of independent reference-derived effects and a replaceable input. */
import {createReferenceEffects} from './reference-effects.js';
const cameraTrack=await (await fetch('./motion-track.json')).json();
const referenceEffects=await createReferenceEffects();
const canvas=document.querySelector('#screen'),gl=canvas.getContext('webgl',{preserveDrawingBuffer:true,antialias:false});
if(!gl)throw Error('WebGL unavailable');
const vertex=`attribute vec2 p;varying vec2 v;void main(){v=(p+1.)*.5;gl_Position=vec4(p,0,1);}`;
const fragment=`precision highp float;
varying vec2 v;uniform sampler2D photo,foreground,caption,matte,effects;
uniform vec2 resolution;uniform vec4 camera;uniform float time,movement,invasion,dust,trackWeight;
float hash(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
float ss(float a,float b,float x){return smoothstep(a,b,x);}
float closing(float t){return ss(2.65,4.08,t)*(1.-ss(4.12,5.3,t))*.92+ss(10.9,12.65,t)*(1.-ss(13.15,13.27,t))*1.22;}
vec3 scene(vec2 uv){uv.x=(uv.x-.5)*1.073+.5;return texture2D(photo,clamp(uv,.001,.999)).rgb;}
void main(){
 float t=time;vec2 uv=vec2(v.x,1.-v.y),p=vec2((uv.x-.5)*1.625+.5,uv.y),q=p;
 float inside=step(0.,p.x)*step(p.x,1.),close=closing(t)*invasion;
 float black=ss(13.02,13.24,t)*(1.-pow(ss(13.82,14.55,t),1.3));
 float shot=ss(4.2,5.1,t),after=ss(13.83,14.4,t),coverage=t<13.28?1.42:1.;
 float zoom=mix(coverage*(1.+.018*sin(t*.53)+.032*ss(6.,11.4,t)),(1.+(camera.y-1.)*1.35)*1.42,trackWeight)+.55*ss(14.1,15.615,t);
 zoom=mix(1.,zoom,movement);
 float angle=movement*mix(.018*sin(t*1.2)+.006*sin(t*3.),camera.x*1.35,trackWeight);
 vec2 shift=movement*mix(vec2(.0035*sin(t*.83),.003*sin(t*.71)),camera.zw*1.3,trackWeight);
 vec2 r=q-.5-shift;
 // Perceptual projective adjustment from the measured affine state, not measured depth.
 float yaw=movement*trackWeight*clamp(camera.z*2.+camera.x*.38,-.085,.085);
 float pitch=movement*trackWeight*clamp(camera.w*2.-camera.x*.25,-.075,.075);
 r/=1.+yaw*r.x+pitch*r.y;r.x+=yaw*r.y*.18;r.y+=pitch*r.x*.16;
 r.x*=1.333333;r=mat2(cos(angle),-sin(angle),sin(angle),cos(angle))*r;r.x/=1.333333;q=r/zoom+.5;
 q.x+=movement*(.0007*sin(t*26.)+.00045*sin(t*63.));q.y+=movement*.001*sin(t*18.);
 float row=floor(q.y*720.),line=hash(vec2(row,floor(t*30.)));
 q.x+=(sin(q.y*480.+t*31.)*.00055+(line-.5)*.0016)*movement;
 q.x+=ss(.986,1.,hash(vec2(floor(q.y*170.),floor(t*9.))))*.005*movement*sin(t*92.);
 float mask=texture2D(matte,clamp(p,.001,.999)).r*invasion;
 float dissolve=ss(11.5,12.7,t)*invasion*(1.-step(13.28,t));
 float blur=dissolve*(1.2+3.8*mask)/540.;
 vec3 color=scene(q)*.28;
 color+=(scene(q+vec2(blur,0.))+scene(q-vec2(blur,0.))+scene(q+vec2(0.,blur))+scene(q-vec2(0.,blur)))*.12;
 color+=(scene(q+vec2(blur,blur))+scene(q-vec2(blur,blur)))*.12;
 float dimGate=invasion*(1.-step(13.28,t));
 float backgroundLight=mix(1.,.50,ss(11.5,12.4,t)*dimGate)*mix(1.,.42,ss(12.4,12.7,t)*dimGate);
 color=mix(vec3(.045),color,mix(1.,.68,dissolve))*backgroundLight;
 vec2 maskUV=vec2((p.x-.5)*1.073+.5,p.y);
 float plateAlpha=texture2D(photo,clamp(maskUV,.001,.999)).a*step(0.,maskUV.x)*step(maskUV.x,1.);
 color=mix(color,color*vec3(.82,.76,.68),shot*.36);color=mix(color,color*vec3(.78,.88,.75),after*.45);
 color=mix(color,vec3(dot(color,vec3(.2126,.7152,.0722)))*vec3(1.,.97,.91),.15);
 vec4 fg=texture2D(foreground,clamp(p,.001,.999));
 color*=1.-.025*(.5+.5*sin(q.y*resolution.y*3.14159));
 color=mix(color,vec3(.0018,.0018,.002),mask);
 color=mix(color,fg.rgb,fg.a);
 vec4 fx=texture2D(effects,clamp(p,.001,.999));color=mix(color,fx.rgb,fx.a*dust);
 vec4 cap=texture2D(caption,clamp(p,.001,.999));color=mix(color,cap.rgb,cap.a);
 color+=(hash(floor(p*resolution)+floor(t*30.))-.5)*.026*(1.-mask*.6);
 color*=1.-.13*pow(length((p-.5)*1.25),2.);color*=1.-black;
 if(t>13.82)color=mix(color,cap.rgb,cap.a);
 if(t>13.28&&t<13.82)color=vec3(0.);
 color*=plateAlpha;
 vec3 surround=vec3(0.);for(int k=0;k<5;k++){float a=float(k)-2.;surround+=scene(vec2(clamp(p.x,.24,.76)+a*.014,clamp(p.y+a*.023,.1,.9)))*.036;}
 surround=mix(surround,vec3(.027,.025,.021),clamp(close*.7,0.,1.));
 gl_FragColor=vec4(mix(surround,clamp(color,0.,1.),inside),1.);
}`;
function shader(type,source){const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s;}
const program=gl.createProgram();gl.attachShader(program,shader(gl.VERTEX_SHADER,vertex));gl.attachShader(program,shader(gl.FRAGMENT_SHADER,fragment));gl.linkProgram(program);if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(program));gl.useProgram(program);
const buf=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buf);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,-1,1,-1,1,1,-1,1,1]),gl.STATIC_DRAW);const loc=gl.getAttribLocation(program,'p');gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,2,gl.FLOAT,false,0,0);
const u=Object.fromEntries(['photo','foreground','caption','matte','effects','resolution','time','movement','invasion','dust','camera','trackWeight'].map(k=>[k,gl.getUniformLocation(program,k)]));
function makeTexture(unit){const texture=gl.createTexture();gl.activeTexture(gl.TEXTURE0+unit);gl.bindTexture(gl.TEXTURE_2D,texture);for(const mode of [gl.TEXTURE_MIN_FILTER,gl.TEXTURE_MAG_FILTER])gl.texParameteri(gl.TEXTURE_2D,mode,gl.LINEAR);for(const mode of [gl.TEXTURE_WRAP_S,gl.TEXTURE_WRAP_T])gl.texParameteri(gl.TEXTURE_2D,mode,gl.CLAMP_TO_EDGE);return texture;}
const texture=makeTexture(0);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,referenceEffects.inputs.a);let activeInput='a';
const fgtex=makeTexture(1),captex=makeTexture(2),mattex=makeTexture(3),efftex=makeTexture(4);
function trackedCamera(t){const rows=cameraTrack.rows,a=rows[Math.max(0,Math.min(rows.length-1,Math.floor((t-5)*10)))],b=rows[Math.min(rows.length-1,rows.indexOf(a)+1)],fraction=Math.max(0,Math.min(1,(t-a.t)/Math.max(.001,b.t-a.t)));return ['angle','scale','dx','dy'].map(k=>a[k]+(b[k]-a[k])*fraction);}
let current=0,playing=false,last=0;const seek=document.querySelector('#seek'),clock=document.querySelector('#clock'),play=document.querySelector('#play');
export function renderAt(t,options={}){
 current=Math.max(0,Math.min(15.615,Number(t)));const levels={};for(const [uniform,id] of [['movement','motion'],['invasion','invasion'],['dust','dust']])levels[uniform]=options[uniform]??+document.querySelector('#'+id).checked;
 const layers=referenceEffects.draw(current,{foreground:options.foreground??true,invasion:levels.invasion});
 for(const [unit,tex,key] of [[1,fgtex,'foil'],[2,captex,'caption'],[3,mattex,'matte'],[4,efftex,'particles']]){gl.activeTexture(gl.TEXTURE0+unit);gl.bindTexture(gl.TEXTURE_2D,tex);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,layers[key]);}
 gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,texture);const input=options.input??document.querySelector('#input')?.value??'a';if(input!==activeInput){gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,referenceEffects.inputs[input]);activeInput=input;}
 gl.viewport(0,0,canvas.width,canvas.height);for(const [key,unit] of [['photo',0],['foreground',1],['caption',2],['matte',3],['effects',4]])gl.uniform1i(u[key],unit);
 gl.uniform2f(u.resolution,canvas.width,canvas.height);gl.uniform4f(u.camera,...trackedCamera(current));gl.uniform1f(u.trackWeight,current<4.3?0:current>13.28?0:Math.min(1,(current-4.3)/.7));gl.uniform1f(u.time,current);for(const [key,value] of Object.entries(levels))gl.uniform1f(u[key],value);
 gl.drawArrays(gl.TRIANGLES,0,6);seek.value=current;clock.textContent=current.toFixed(3)+' 秒';return current;
}
window.motionStudy={renderAt,duration:15.615,canvas,version:'7.0.0',sourceDerivedEffects:true,provenance:referenceEffects.manifest,ready:true};
function toggle(){playing=!playing;play.textContent=playing?'一時停止':'再生';last=performance.now();}
play.onclick=toggle;seek.oninput=()=>{playing=false;play.textContent='再生';renderAt(seek.value)};document.querySelector('#ending').onclick=()=>renderAt(11.4);document.querySelector('#restart').onclick=()=>renderAt(0);
for(const id of ['motion','invasion','dust','input'])document.querySelector('#'+id).onchange=()=>renderAt(current);
function tick(now){if(playing){current+=(now-last)/1000;if(current>15.615)current=0;renderAt(current);}last=now;requestAnimationFrame(tick);}renderAt(0);requestAnimationFrame(tick);
