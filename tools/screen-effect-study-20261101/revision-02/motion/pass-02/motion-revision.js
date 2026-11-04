const output=document.querySelector('#screen');output.width=1170;output.height=540;
const main=output.getContext('2d',{willReadFrequently:true}),oldCanvas=document.createElement('canvas'),newCanvas=document.createElement('canvas');oldCanvas.width=newCanvas.width=720;oldCanvas.height=newCanvas.height=540;
const oldScene=await createSpatialScene({canvas:oldCanvas}),newScene=await createSpatialScene({canvas:newCanvas});
const frozenEffects=await createFrozenEffects(globalThis.__frozenForeground);
const constraint=document.createElement('p');constraint.className='note';constraint.textContent='面の境界と外周から12pxの範囲は動きを抑えています。仮の物体の輪郭は固定されます。確認表示では靄と文字を外します。';document.querySelectorAll('.wrap')[1].prepend(constraint);
const weightOption=document.createElement('option');weightOption.value='weight';weightOption.textContent='動きを抑える範囲';document.querySelector('#diagnostic').append(weightOption);
const readCanvas=document.createElement('canvas');readCanvas.width=720;readCanvas.height=540;const read=readCanvas.getContext('2d',{willReadFrequently:true});
let current=0,playing=false,last=0;
const controls=()=>({mode:document.querySelector('#mode').value,invasion:document.querySelector('#smoke').checked,effects:document.querySelector('#effects').checked,strength:+document.querySelector('#strength').value,diagnostic:document.querySelector('#diagnostic').value});
function pixels(c){read.clearRect(0,0,720,540);read.drawImage(c,0,0);return read.getImageData(0,0,720,540).data;}
export function renderAt(t,options={}){
 current=Math.max(0,Math.min(15.615,Number(t)));const opt={...controls(),...options},diagnostic=opt.diagnostic??'beauty';if(diagnostic!=='beauty'){opt.invasion=false;opt.effects=false;}const sceneOptions={mode:opt.mode,strength:opt.strength,diagnostic,display:true,showHoles:diagnostic==='holes'};
 oldScene.renderAt(current,sceneOptions);
 const old=pixels(oldCanvas);let newer=old;
 if(current>=3.54&&current<5.3){newScene.renderAt(current+1.7,sceneOptions);newer=pixels(newCanvas);}
 const smoke=drawSmokeAt(current,720,540,{enabled:opt.invasion}),ov=pixels(smoke.oldVisibilityCanvas),nv=pixels(smoke.newVisibilityCanvas),sp=pixels(smoke.edgeParticlesCanvas),data=new Uint8ClampedArray(720*540*4);
 for(let i=0;i<data.length;i+=4){const a=ov[i]/255,b=nv[i]/255,normal=Math.max(1,a+b);for(let k=0;k<3;k++)data[i+k]=(old[i+k]*a+newer[i+k]*b)/normal+(opt.invasion?sp[i+3]:0);data[i+3]=255;}
 read.putImageData(new ImageData(data,720,540),0,0);
 const fx=frozenEffects.draw(current,opt.effects);
 // Scene visibility is applied first. Reflective foil, glyphs and drifting particles are independent foreground layers.
 read.drawImage(fx.foil,0,0);read.drawImage(fx.particles,0,0);read.drawImage(fx.caption,0,0);
 main.fillStyle='black';main.fillRect(0,0,1170,540);if(!(current>=13.28&&current<13.82))main.drawImage(readCanvas,225,0);
 document.querySelector('#time').value=current;document.querySelector('#clock').textContent=current.toFixed(3)+' 秒';return{t:current,...opt};
}
window.motionRevision={renderAt,canvas:output,ready:true,duration:15.615,oldScene,newScene,version:'revision-02',displayApproximation:true};
document.querySelector('#play').onclick=()=>{playing=!playing;document.querySelector('#play').textContent=playing?'一時停止':'再生';last=performance.now();};
document.querySelector('#time').oninput=e=>{playing=false;document.querySelector('#play').textContent='再生';renderAt(e.target.value);};
for(const[id,t]of [['opening',3],['exit',4.1],['ending',11.8],['restart',0]])document.querySelector('#'+id).onclick=()=>{playing=false;renderAt(t);};
for(const id of ['mode','smoke','effects','strength','diagnostic'])document.querySelector('#'+id).oninput=()=>renderAt(current);
function tick(now){if(playing){current+=(now-last)/1000;if(current>15.615)current=0;renderAt(current);}last=now;requestAnimationFrame(tick);}renderAt(0);requestAnimationFrame(tick);
