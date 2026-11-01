import {createSpatialScene} from './renderer.js';
const canvas=document.querySelector('canvas');canvas.width=720;canvas.height=540;
const scene=await createSpatialScene({canvas});window.spatialStudy=scene;
const state={t:0,mode:'local',diagnostic:'beauty',strength:1,display:true,showHoles:false};let playing=false,last=0;
const $=s=>document.querySelector(s);function draw(){scene.renderAt(state.t,state);$('#time').value=state.t;$('#clock').textContent=state.t.toFixed(2)+' 秒';}
$('#play').onclick=()=>{playing=!playing;$('#play').textContent=playing?'一時停止':'再生';};$('#time').oninput=e=>{state.t=+e.target.value;playing=false;draw();};
for(const id of ['mode','diagnostic'])$('#'+id).onchange=e=>{state[id]=e.target.value;draw();};for(const id of ['display','showHoles'])$('#'+id).onchange=e=>{state[id]=e.target.checked;draw();};$('#strength').oninput=e=>{state.strength=+e.target.value;draw();};
function tick(now){if(playing){state.t=(state.t+Math.min(.1,(now-last)/1000))%15.6;draw();}last=now;requestAnimationFrame(tick);}draw();requestAnimationFrame(tick);
