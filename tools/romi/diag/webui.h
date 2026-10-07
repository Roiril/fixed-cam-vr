// Romi の Web 画面（ESP32 が配る単一ファイル）。http://192.168.10.51/ または http://romi.local/
//   #ctl（既定）… 操作画面。スクロール無し。ジョイスティックと、速度・設定・停止の 3 つだけ。
//   #set        … 設定画面。条件の入力・目標指定の走行・結果の記録とコピー・ジョイスティックの速度上限・接続状態。
// エンドポイント: /run /joy /stop /state /res /cal /info /health
#pragma once
#include <pgmspace.h>

static const char PAGE_HTML[] PROGMEM = R"HTML(<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover">
<meta name="mobile-web-app-capable" content="yes">
<meta name="theme-color" content="#101418">
<link rel="icon" type="image/png" href="/icon.png">
<link rel="apple-touch-icon" href="/icon.png">
<link rel="manifest" href="/manifest.json">
<title>Romi</title>
<style>
:root{--bg:#f4f5f7;--card:#fff;--ink:#1c1f24;--sub:#5f6773;--line:#d9dde3;--acc:#1f6feb;--accsoft:#e6efff;--stop:#d92d20;--ok:#12805c;--field:#f4f5f7;--pad:#e9edf2;--padline:#c5ccd6}
@media(prefers-color-scheme:dark){:root{--bg:#101418;--card:#1b2026;--ink:#e8eaed;--sub:#9aa3ad;--line:#333a42;--acc:#5b9bff;--accsoft:#1f2f4a;--stop:#ff5a4d;--ok:#3ecf9b;--field:#101418;--pad:#1b2229;--padline:#36404a}}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{height:100%;margin:0;overflow:hidden;overscroll-behavior:none;background:var(--bg);color:var(--ink);
  font:16px/1.5 system-ui,-apple-system,"Segoe UI","Hiragino Sans","Yu Gothic",sans-serif;user-select:none;-webkit-user-select:none}
[hidden]{display:none!important}
button{font:inherit;color:inherit}

/* ---------- 操作画面 ---------- */
#ctl{height:100dvh;display:grid;grid-template-rows:auto 1fr clamp(88px,15dvh,140px);padding:env(safe-area-inset-top) 0 0}
.top{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:10px 14px}
.chip{min-width:96px;padding:10px 14px;border-radius:22px;border:1px solid var(--line);background:var(--card);font-size:15px}
#spd{font-weight:600}
#spd.s0{color:var(--ok)} #spd.s2{color:var(--stop)}
.dot{font-size:13px;color:var(--sub);display:flex;align-items:center;gap:6px}
.dot::before{content:"";width:10px;height:10px;border-radius:50%;background:var(--sub)}
.dot.ok::before{background:var(--ok)} .dot.ng::before{background:var(--stop)}
/* 持ち方（右手 .hR / 左手 .hL / 両手 .hC）。親指が届く側の下にジョイスティックを寄せ、反対側にボタンを縦に並べる */
.mid{display:grid;gap:10px;align-items:end;min-height:0;padding:0 12px 1.5dvh}
.side{grid-area:side;display:flex;flex-direction:column;gap:10px;align-self:end;min-width:0}
.side .chip{width:100%;min-width:0;min-height:48px;border-radius:14px}
.padcol{grid-area:pad;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;gap:1.5dvh;min-width:0}
.hR .mid{grid-template-columns:minmax(108px,32%) 1fr;grid-template-areas:"side pad"}
.hL .mid{grid-template-columns:1fr minmax(108px,32%);grid-template-areas:"pad side"}
.hC .mid{grid-template-columns:1fr;grid-template-rows:auto 1fr;grid-template-areas:"side" "pad"}
.hC .side{display:grid;grid-template-columns:1fr 1fr;align-self:start}
.hC #spd{grid-column:1/-1}
.hC .padcol{justify-content:center}
.hC #pad{width:min(78vw,37dvh)}
#sc{display:contents}
.scb{min-height:clamp(60px,9.5dvh,88px);border-radius:14px;border:1px solid var(--line);background:var(--card);font-size:14px;font-weight:700;line-height:1.25;padding:6px 8px;word-break:keep-all;overflow-wrap:anywhere}
.scb small{display:block;font-weight:500;color:var(--sub);font-size:11px}
.scb.armed{background:#f59e0b;border-color:#f59e0b;color:#111}
.scb.armed small{color:#111}
.scb:disabled{opacity:.4}
#read{font-size:clamp(22px,4.2dvh,34px);font-weight:700;height:1.3em;font-variant-numeric:tabular-nums}
#read small{font-size:.55em;color:var(--sub);font-weight:500;margin-left:8px}
#pad{position:relative;width:min(100%,48dvh);aspect-ratio:1;border-radius:50%;background:var(--pad);border:2px solid var(--padline);touch-action:none}
#pad::before,#pad::after{content:"";position:absolute;background:var(--padline);opacity:.6}
#pad::before{left:50%;top:9%;bottom:9%;width:2px;transform:translateX(-50%)}
#pad::after{top:50%;left:9%;right:9%;height:2px;transform:translateY(-50%)}
#knob{position:absolute;left:50%;top:50%;width:36%;height:36%;margin:-18% 0 0 -18%;border-radius:50%;background:var(--acc);
  box-shadow:0 4px 14px rgba(0,0,0,.28);transition:transform .12s ease-out;touch-action:none}
#knob.drag{transition:none}
#pad.off{opacity:.35}
#stop{border:0;background:var(--stop);color:#fff;font-size:clamp(22px,3.6dvh,30px);font-weight:800;letter-spacing:.2em}
#cut{position:fixed;inset:0;display:none;align-items:center;justify-content:center;text-align:center;background:rgba(16,20,24,.86);color:#fff;font-size:20px;font-weight:700;z-index:20;padding:24px}
#cut.on{display:flex}
@media(orientation:landscape){
  #ctl{grid-template-columns:1fr clamp(150px,26vw,260px);grid-template-rows:auto 1fr;grid-template-areas:"top top" "mid stop"}
  .top{grid-area:top} .mid{grid-area:mid;align-items:center}
  .hR .mid,.hC .mid{grid-template-columns:minmax(120px,28%) 1fr;grid-template-rows:1fr;grid-template-areas:"side pad"}
  .hL .mid{grid-template-columns:1fr minmax(120px,28%);grid-template-rows:1fr;grid-template-areas:"pad side"}
  .hC .side{display:flex;flex-direction:column;align-self:center}
  .padcol{justify-content:center}
  #pad,.hC #pad{width:min(100%,62dvh)}
  #stop{grid-area:stop;margin:0 14px 14px 0;border-radius:18px}
}

/* ---------- 設定画面 ---------- */
#set{height:100dvh;overflow:auto;-webkit-overflow-scrolling:touch;padding-bottom:40px;user-select:text;-webkit-user-select:text}
#set header{display:flex;align-items:center;gap:12px;padding:10px 14px;background:var(--card);border-bottom:1px solid var(--line);position:sticky;top:0;z-index:5}
#back{padding:10px 16px;border-radius:22px;border:1px solid var(--line);background:var(--card);font-size:15px;font-weight:600}
#set header b{font-size:17px;flex:1}
main{max-width:640px;margin:0 auto;padding:12px 16px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px;margin-bottom:12px}
.card h2{margin:0 0 8px;font-size:15px}
.seg{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-bottom:12px}
.seg button{padding:12px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-size:16px}
.seg button.on{background:var(--accsoft);border-color:var(--acc);color:var(--acc);font-weight:600}
.row{margin:10px 0}
.row label.t{display:flex;justify-content:space-between;font-size:14px;color:var(--sub)}
.row label.t b{color:var(--ink);font-size:17px;font-variant-numeric:tabular-nums}
input[type=range]{width:100%;height:44px;accent-color:var(--acc)}
input[type=text],input[type=number]{width:100%;padding:10px;border-radius:8px;border:1px solid var(--line);background:var(--field);color:var(--ink);font-size:16px}
.tgt{display:flex;gap:8px;align-items:center}
.tgt input{font-size:28px;font-weight:600;text-align:center;padding:8px}
.tgt span{font-size:18px;color:var(--sub);min-width:36px}
.chips{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}
.chips button{padding:8px 14px;border-radius:18px;border:1px solid var(--line);background:var(--card);font-size:15px}
.plan{margin:10px 0 0;font-size:14px;color:var(--sub)}
.plan b{color:var(--ink)}
.go{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:10px}
.go button{padding:18px 8px;border-radius:12px;border:0;background:var(--acc);color:#fff;font-size:18px;font-weight:600}
.go button:disabled{opacity:.4}
.prog{height:6px;background:var(--line);border-radius:3px;overflow:hidden;margin-top:10px}
.prog i{display:block;height:100%;width:0;background:var(--ok)}
.hint{font-size:13px;color:var(--sub);margin:6px 0 0}
.run{border-top:1px solid var(--line);padding:10px 0}
.run:first-child{border-top:0}
.run .h{font-size:14px;margin-bottom:6px;display:flex;justify-content:space-between;align-items:center;gap:8px}
.run .h b{font-size:15px}
.run .h small{color:var(--sub)}
.del{flex:none;padding:6px 10px;border-radius:8px;border:1px solid var(--line);background:var(--card);color:var(--stop);font-size:13px}
.f2{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.f2 label{font-size:12px;color:var(--sub);display:block}
.pm{display:flex;gap:4px}
.pm button{width:44px;border-radius:8px;border:1px solid var(--line);background:var(--card);font-size:16px}
.tools{display:flex;gap:8px;margin-top:10px;flex-wrap:wrap}
.tools button{padding:12px 16px;border-radius:10px;border:1px solid var(--line);background:var(--card);font-size:15px}
.tools button.main{background:var(--acc);border-color:var(--acc);color:#fff;font-weight:600}
textarea{width:100%;height:130px;margin-top:8px;font:12px/1.4 ui-monospace,Consolas,monospace;background:var(--field);color:var(--ink);border:1px solid var(--line);border-radius:8px;padding:8px}
details{margin-top:10px}
summary{font-size:14px;color:var(--sub)}
.chk{display:flex;gap:8px;align-items:center;font-size:14px;margin:8px 0}
.chk input{width:20px;height:20px}
.kv{display:grid;grid-template-columns:auto 1fr;gap:4px 14px;font-size:14px}
.kv span:nth-child(odd){color:var(--sub)}
.slot{border-top:1px solid var(--line);padding:12px 0}
.slot:first-child{border-top:0}
.stp{display:flex;gap:6px;align-items:center;margin:6px 0}
.stp select,.stp input{padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--field);color:var(--ink);font-size:15px;min-width:0}
.stp select{flex:1.5}
.stp input{flex:1;width:0}
.stp span{font-size:13px;color:var(--sub)}
.stp button{flex:none;width:36px;height:36px;border-radius:8px;border:1px solid var(--line);background:var(--card);color:var(--stop);font-size:16px}
.slot .tools button{padding:9px 12px;font-size:14px}
#stop2{position:fixed;left:0;right:0;bottom:0;height:64px;border:0;background:var(--stop);color:#fff;font-size:20px;font-weight:700;z-index:9}
</style>
</head>
<body>

<!-- ============ 操作画面 ============ -->
<div id="ctl" class="hR">
  <div class="top">
    <button id="hand" class="chip">右手</button>
    <span id="conn" class="dot">確認中</span>
    <button id="toSet" class="chip">設定</button>
  </div>
  <div class="mid">
    <div class="side">
      <button id="spd" class="chip s0">ゆっくり</button>
      <div id="sc"></div>
    </div>
    <div class="padcol">
      <div id="read">停止</div>
      <div id="pad"><div id="knob"></div></div>
    </div>
  </div>
  <button id="stop">■ 停止</button>
</div>
<div id="cut">台車とつながっていません<br>つながるまで操作できません</div>

<!-- ============ 設定画面 ============ -->
<div id="set" hidden>
  <header><button id="back">← 操作へ</button><b>設定</b><span id="conn2" class="dot">確認中</span></header>
  <main>

  <div class="card">
    <h2>ジョイスティックの速度</h2>
    <div class="row"><label class="t"><span>ゆっくり（上限）</span><b id="vc0">25 %</b></label><input id="cap0" type="range" min="12" max="68" value="25"></div>
    <div class="row"><label class="t"><span>ふつう（上限）</span><b id="vc1">40 %</b></label><input id="cap1" type="range" min="12" max="68" value="40"></div>
    <div class="row"><label class="t"><span>はやい（上限）</span><b id="vc2">60 %</b></label><input id="cap2" type="range" min="12" max="68" value="60"></div>
    <label class="chk"><input id="snap" type="checkbox" checked>まっすぐ・その場回転に吸い付かせる（斜めを補正）</label>
    <label class="chk"><input id="flip" type="checkbox">前後を逆にする（「前」で後ろへ進むとき）</label>
    <p class="hint">操作画面を開き直すと、速度は「ゆっくり」に戻ります。</p>
  </div>

  <div class="card">
    <h2>ショートカット</h2>
    <p class="hint">操作画面のボタンに割り当てる、一連の動きです。名前を空にするとボタンは出ません。押し間違えを防ぐため、操作画面では 2 回タップで実行します。台車に保存されるので、どの端末でも同じものが出ます。</p>
    <div id="scEdit"></div>
    <p class="hint" id="scMsg"></p>
  </div>

  <div class="card">
    <h2>いまの条件</h2>
    <div class="row"><label class="t"><span>電池の残量</span></label>
      <input id="bat" type="text" placeholder="例: 新品 / 70% / 8.1V" autocomplete="off"></div>
    <div class="row"><label class="t"><span>Romi の上に載っているもの</span></label>
      <input id="load" type="text" placeholder="例: なし / 人形 約0.9kg" autocomplete="off"></div>
    <p class="hint">変えたときだけ書き換えてください。動かすたびにこの内容が記録に付きます。</p>
  </div>

  <div class="seg">
    <button id="tabS" class="on">直進</button>
    <button id="tabR">回転</button>
  </div>

  <div class="card">
    <div class="row">
      <label class="t"><span id="tgtLabel">進む距離</span></label>
      <div class="tgt"><input id="tgt" type="number" inputmode="decimal" min="1" step="1" value="30"><span id="unit">cm</span></div>
      <div class="chips" id="chips"></div>
    </div>
    <div class="row">
      <label class="t"><span>強さ</span><b id="vDuty">40 %</b></label>
      <input id="duty" type="range" min="10" max="68" step="1" value="40">
    </div>
    <p class="plan">予定の時間 <b id="plan">—</b></p>
    <div class="go" id="goS">
      <button data-d="1">▲ 前進</button>
      <button data-d="-1">▼ 後退</button>
    </div>
    <div class="go" id="goR" hidden>
      <button data-d="-1">↺ 左回転</button>
      <button data-d="1">↻ 右回転</button>
    </div>
    <div class="prog"><i id="bar"></i></div>
    <p class="hint" id="hint">目標を決めて、方向ボタンを押すと動いて、目標に届く時間で止まります。</p>
    <details>
      <summary>詳しい設定</summary>
      <div class="row" id="trimRow">
        <label class="t"><span>まっすぐ補正（左右バランス）</span><b id="vTrim">0</b></label>
        <input id="trim" type="range" min="-20" max="20" step="1" value="0">
        <p class="hint">右へ曲がるときはマイナス側へ。左へ曲がるときはプラス側へ。直進のみ効きます。</p>
      </div>
      <label class="chk"><input id="useMs" type="checkbox">時間（秒）で直接指定する</label>
      <input id="msDirect" type="number" inputmode="decimal" step="0.1" min="0.1" max="8" value="1.0" style="display:none">
    </details>
  </div>

  <div class="card">
    <h2>止まった結果を入れる</h2>
    <p class="hint">実際に進んだ距離や回った角度と、左右のずれを入れてください。入れた内容はコピーして、そのまま渡せます。</p>
    <div id="log"></div>
    <div class="tools">
      <button id="copy" class="main">記録をコピー</button>
      <button id="clear">記録をすべて消す</button>
    </div>
    <textarea id="csv" hidden readonly></textarea>
  </div>

  <div class="card">
    <h2>台車の状態</h2>
    <div class="kv" id="info"><span>—</span><span>—</span></div>
  </div>
  </main>
  <button id="stop2">■ 停止</button>
</div>

<script>
(function(){
  var $=function(id){return document.getElementById(id)};
  var KEY="romi-log-v2", store={};
  try{store=JSON.parse(localStorage.getItem(KEY)||"{}")}catch(e){store={}}
  store.rows=store.rows||[]; store.prefs=store.prefs||{};
  function save(){try{localStorage.setItem(KEY,JSON.stringify(store))}catch(e){}}
  var P=store.prefs, mode="s", busy=false, cal=null, online=false, failN=0;
  store.rows.forEach(function(r,i){if(!r.k)r.k="old-"+i+"-"+r.id});   // 台車の再起動で #番号は振り直されるので固有キー k で区別
  P.tgtS=P.tgtS||30; P.tgtR=P.tgtR||90;
  var CAPS=[+P.cap0||25,+P.cap1||40,+P.cap2||60];
  var SPD=0;                                  // 操作画面は必ず「ゆっくり」から始める
  var SPDN=["ゆっくり","ふつう","はやい"];
  var HANDS=["R","L","C"], HN={R:"右手",L:"左手",C:"両手"};
  var hand=HN[P.hand]?P.hand:"R";
  function handRender(){$("ctl").className="h"+hand;$("hand").textContent=HN[hand]}
  $("hand").onclick=function(){hand=HANDS[(HANDS.indexOf(hand)+1)%3];P.hand=hand;save();handRender()};
  handRender();

  function get(url,ok,ng,to){
    var x=new XMLHttpRequest(); x.open("GET",url,true); x.timeout=to||3000;
    x.onload=function(){try{ok(JSON.parse(x.responseText))}catch(e){if(ng)ng()}};
    x.onerror=x.ontimeout=function(){if(ng)ng()};
    x.send(); return x;
  }

  /* ===== 画面切り替え ===== */
  function route(){var s=location.hash==="#set";$("ctl").hidden=s;$("set").hidden=!s;if(s)loadInfo();else sendStopSoon()}
  window.addEventListener("hashchange",route);
  $("toSet").onclick=function(){location.hash="#set"};
  $("back").onclick=function(){location.hash="#ctl"};

  /* ===== 接続表示 ===== */
  function setConn(on){
    online=on;
    ["conn","conn2"].forEach(function(id){$(id).textContent=on?"接続中":"切断";$(id).className="dot "+(on?"ok":"ng")});
    $("cut").className=(!on&&!$("ctl").hidden)?"on":"";
    padState();scRender();
  }
  function padState(){$("pad").className=(online&&!seqBusy)?"":"off"}

  /* ===== 操作画面: ジョイスティック ===== */
  var pad=$("pad"),knob=$("knob"),pid=null,jx=0,jy=0,rawX=0,rawY=0,zeroSends=0,inflight=null,inflightAt=0;
  function spdRender(){$("spd").textContent=SPDN[SPD];$("spd").className="chip s"+SPD}
  $("spd").onclick=function(){SPD=(SPD+1)%3;spdRender();zeroSends=2};
  spdRender();

  function shape(x,y){   // 不感帯と、まっすぐ／その場回転への吸着
    var m=Math.sqrt(x*x+y*y);
    if(m<0.12)return [0,0];
    var k=Math.min(1,(m-0.12)/0.88)/m; x*=k; y*=k;
    if($("snap").checked){
      var ax=Math.abs(x),ay=Math.abs(y);
      if(ax<0.28*ay)x=0; else if(ay<0.28*ax)y=0;
    }
    return [x,y];
  }
  function place(e){
    var r=pad.getBoundingClientRect(),cx=r.left+r.width/2,cy=r.top+r.height/2,R=r.width*0.32;
    var dx=(e.clientX-cx)/R,dy=(e.clientY-cy)/R,m=Math.sqrt(dx*dx+dy*dy);
    if(m>1){dx/=m;dy/=m}
    rawX=dx;rawY=-dy;
    knob.style.transform="translate("+(dx*R)+"px,"+(dy*R)+"px)";
    var s=shape(rawX,rawY);jx=s[0];jy=s[1];readout();
  }
  function readout(){
    var t,v=Math.round(Math.sqrt(jx*jx+jy*jy)*100);
    if(!jx&&!jy)t="停止";
    else if(!jx)t=jy>0?"前進":"後退";
    else if(!jy)t=jx>0?"右にその場回転":"左にその場回転";
    else t=(jy>0?"前":"後")+"へ"+(jx>0?"右":"左")+"カーブ";
    $("read").innerHTML=t+(v?"<small>"+Math.min(100,v)+"%</small>":"");
  }
  function release(){
    pid=null;knob.className="";knob.style.transform="translate(0,0)";
    jx=jy=rawX=rawY=0;zeroSends=4;readout();
  }
  pad.addEventListener("pointerdown",function(e){
    if(pid!==null||!online||seqBusy)return;
    e.preventDefault();pid=e.pointerId;try{pad.setPointerCapture(pid)}catch(x){}
    knob.className="drag";place(e);
  });
  pad.addEventListener("pointermove",function(e){if(e.pointerId===pid){e.preventDefault();place(e)}});
  ["pointerup","pointercancel","lostpointercapture"].forEach(function(n){
    pad.addEventListener(n,function(e){if(e.pointerId===pid)release()});
  });
  pad.addEventListener("contextmenu",function(e){e.preventDefault()});

  // 60ms ごとに送る。押している間と、離した直後の数回（確実に 0 を届ける）だけ
  setInterval(function(){
    if($("ctl").hidden)return;
    var active=pid!==null;
    if(!active&&zeroSends<=0)return;
    if(inflight&&Date.now()-inflightAt<250)return;
    if(!active)zeroSends--;
    var url="/joy?x="+jx.toFixed(3)+"&y="+jy.toFixed(3)+"&cap="+CAPS[SPD]+"&flip="+($("flip").checked?1:0);
    inflightAt=Date.now();
    inflight=get(url,function(j){inflight=null;if(!j.ok)$("read").textContent="動作中（設定画面の走行）"},function(){inflight=null},500);
  },60);

  function stopNow(e){if(e)e.preventDefault();disarm();release();get("/stop",function(){},function(){});setBusy(false)}
  function sendStopSoon(){release();get("/stop",function(){},function(){})}
  $("stop").addEventListener("touchstart",stopNow,{passive:false});
  $("stop").addEventListener("click",stopNow);
  $("stop2").addEventListener("touchstart",function(e){stopNow(e);$("hint").textContent="停止しました"},{passive:false});
  $("stop2").addEventListener("click",function(){stopNow();$("hint").textContent="停止しました"});
  document.addEventListener("visibilitychange",function(){if(document.hidden)sendStopSoon()});
  window.addEventListener("blur",function(){if(pid!==null)release()});

  /* ===== 設定画面: 速度上限・条件・目標指定 ===== */
  function capsRender(){
    for(var i=0;i<3;i++){$("cap"+i).value=CAPS[i];$("vc"+i).textContent=CAPS[i]+" %"}
  }
  for(var ci=0;ci<3;ci++)(function(i){$("cap"+i).addEventListener("input",function(){CAPS[i]=+this.value;P["cap"+i]=CAPS[i];$("vc"+i).textContent=CAPS[i]+" %";save()})})(ci);
  capsRender();
  if(P.duty)$("duty").value=P.duty; if(P.trim!=null)$("trim").value=P.trim; $("flip").checked=!!P.flip; $("snap").checked=P.snap!==false;
  $("bat").value=P.bat||""; $("load").value=P.load||"";
  function saveP(){P.duty=$("duty").value;P.trim=$("trim").value;P.flip=$("flip").checked;P.snap=$("snap").checked;P.bat=$("bat").value;P.load=$("load").value;
    if(mode==="s")P.tgtS=$("tgt").value;else P.tgtR=$("tgt").value;save()}

  var CH={s:[10,20,30,50,100],r:[45,90,180,360]};
  function planMs(){
    if($("useMs").checked){var s=+$("msDirect").value;return s>0?s*1000:null}
    if(!cal)return null;
    var d=+$("duty").value,t=+$("tgt").value;
    var r=mode==="s"?cal.sv*(d-cal.sd):cal.rv*(d-cal.rd);
    if(!(r>=0.5)||!(t>0))return null;
    return t/r*1000+(mode==="s"?cal.st:cal.rt);
  }
  function fmt(){
    $("vDuty").textContent=$("duty").value+" %";
    var t=+$("trim").value; $("vTrim").textContent=(t>0?"+":"")+t;
    $("msDirect").style.display=$("useMs").checked?"":"none";
    var p=planMs();
    $("plan").textContent=p==null?(cal?"強さが小さすぎます":"—"):(p/1000).toFixed(2)+" 秒"+(p>8000?"（長すぎます）":"");
    saveP();
  }
  ["duty","trim","tgt","bat","load","msDirect"].forEach(function(i){$(i).addEventListener("input",fmt)});
  ["flip","snap","useMs"].forEach(function(i){$(i).addEventListener("change",fmt)});
  function chips(){
    var c=$("chips"); c.innerHTML="";
    CH[mode].forEach(function(v){var b=document.createElement("button");b.textContent=v+(mode==="s"?" cm":"°");
      b.onclick=function(){$("tgt").value=v;fmt()};c.appendChild(b)});
  }
  function setMode(m,init){
    if(!init)saveP(); mode=m;
    $("tabS").className=m==="s"?"on":""; $("tabR").className=m==="r"?"on":"";
    $("goS").hidden=m!=="s"; $("goR").hidden=m!=="r"; $("trimRow").style.display=m==="s"?"":"none";
    $("tgtLabel").textContent=m==="s"?"進む距離":"回る角度"; $("unit").textContent=m==="s"?"cm":"度";
    $("tgt").value=m==="s"?P.tgtS:P.tgtR; chips(); fmt();
  }
  $("tabS").onclick=function(){setMode("s")}; $("tabR").onclick=function(){setMode("r")};
  function setBusy(b){busy=b;Array.prototype.forEach.call(document.querySelectorAll(".go button"),function(x){x.disabled=b})}
  function loadCal(){get("/cal",function(j){cal=j;fmt()},function(){setTimeout(loadCal,2000)})}
  function loadInfo(){
    get("/info",function(j){
      var kv=[["アドレス",j.ip],["電波",j.rssi+" dBm"],["稼働",Math.floor(j.up/60)+" 分"],["切断した回数",j.drops],["台車のID",j.uuid],["較正の版",j.calVer],["ファーム",j.version]];
      $("info").innerHTML=kv.map(function(r){return "<span>"+r[0]+"</span><span>"+r[1]+"</span>"}).join("");
    });
  }

  function title(r){
    var n=r.m==="s"?(r.d>0?"前進":"後退"):(r.d>0?"右回転":"左回転");
    return n+" "+(r.tgt!=null?r.tgt+(r.m==="s"?"cm":"度"):(r.pms/1000).toFixed(1)+"秒(時間指定)");
  }
  function num(v){if(v==null||String(v).trim()==="")return null;var x=parseFloat(String(v).replace(/[^0-9.\-]/g,""));return isNaN(x)?null:x}
  function field(label,row,key,unit,ph){
    var wrap=document.createElement("div");
    var l=document.createElement("label");l.textContent=label;wrap.appendChild(l);
    var box=document.createElement("div");box.className="pm";
    var inp=document.createElement("input");inp.type="text";inp.setAttribute("inputmode","decimal");inp.placeholder=ph||unit;inp.value=row[key]==null?"":row[key];
    inp.onchange=function(){var x=num(inp.value);row[key]=x;inp.value=x==null?"":x;save();sendRes(row)};
    var pm=document.createElement("button");pm.textContent="±";
    pm.onclick=function(){var x=num(inp.value);if(x==null)return;x=-x;row[key]=x;inp.value=x;save();sendRes(row)};
    box.appendChild(inp);box.appendChild(pm);wrap.appendChild(box);return wrap;
  }
  function render(){
    var el=$("log"); el.innerHTML="";
    if(!store.rows.length){el.innerHTML='<p class="hint">まだ記録がありません。</p>';return}
    store.rows.slice().reverse().forEach(function(r){
      var d=document.createElement("div");d.className="run";
      var h=document.createElement("div");h.className="h";
      h.innerHTML="<span><b>#"+r.id+" "+title(r)+"</b> <small>強さ"+r.duty+"%・予定"+(r.pms/1000).toFixed(2)+"秒"+(r.ams!=null?"・実測"+(r.ams/1000).toFixed(2)+"秒":"")+
        (r.m==="s"&&r.trim?"・補正"+(r.trim>0?"+":"")+r.trim:"")+"</small></span>";
      var del=document.createElement("button");del.textContent="この記録を消す";del.className="del";
      del.onclick=function(){
        if(!confirm("#"+r.id+" の記録を消しますか？"))return;
        store.rows=store.rows.filter(function(x){return x.k!==r.k});save();render();
      };
      h.appendChild(del);d.appendChild(h);
      var g=document.createElement("div");g.className="f2";
      g.appendChild(field(r.m==="s"?"実際に進んだ距離 (cm)":"実際に回った角度 (度)",r,"a",r.m==="s"?"cm":"度"));
      g.appendChild(field(r.m==="s"?"左右のずれ (cm・右へ＋ 左へ－)":"位置のずれ (cm・任意)",r,"lat","cm"));
      d.appendChild(g);
      var mw=document.createElement("div");mw.style.marginTop="8px";
      var mi=document.createElement("input");mi.type="text";mi.placeholder="メモ（任意）";mi.value=r.n||"";
      mi.onchange=function(){r.n=mi.value.trim();save();sendRes(r)};
      mw.appendChild(mi);d.appendChild(mw);
      el.appendChild(d);
    });
  }
  function sendRes(r){
    get("/res?id="+r.id+"&a="+(r.a==null?"":r.a)+"&lat="+(r.lat==null?"":r.lat)+"&u="+(r.m==="s"?"cm":"deg")+"&bat="+encodeURIComponent(r.bat||"")+"&load="+encodeURIComponent(r.load||"")+"&n="+encodeURIComponent(r.n||""),function(){});
  }

  function run(d){
    if(busy)return;
    var t=+$("tgt").value, useMs=$("useMs").checked;
    if(!useMs&&!(t>0)){$("hint").textContent="目標を入れてください";return}
    var q="/run?m="+mode+"&d="+d+"&duty="+$("duty").value+"&trim="+(mode==="s"?$("trim").value:0)+"&flip="+($("flip").checked?1:0)+
      "&bat="+encodeURIComponent($("bat").value)+"&load="+encodeURIComponent($("load").value)+
      (useMs?"&ms="+Math.round(+$("msDirect").value*1000):"&tgt="+t);
    setBusy(true);
    get(q,function(j){
      if(!j.ok){setBusy(false);$("hint").textContent=j.err==="weak"?"強さが小さすぎて動きません":(j.err==="busy"?"まだ動いています":"範囲外です（目標が大きすぎる／時間が長すぎる）");return}
      store.rows.push({k:Date.now()+"-"+Math.random().toString(36).slice(2,6),id:j.id,m:mode,d:d,duty:+$("duty").value,tgt:useMs?null:t,pms:j.ms,ams:null,trim:mode==="s"?+$("trim").value:0,flip:$("flip").checked?1:0,
        bat:$("bat").value,load:$("load").value,cal:cal?cal.ver:null,a:null,lat:null,n:""});
      save(); render();
      $("hint").textContent="実行中…（記録 #"+j.id+"）";
    },function(){setBusy(false);$("hint").textContent="台車につながりません。"});
  }
  Array.prototype.forEach.call(document.querySelectorAll(".go button"),function(b){
    b.addEventListener("click",function(){run(+b.getAttribute("data-d"))});
  });

  /* ===== ショートカット（一連の動き。台車が順に実行する）===== */
  var KINDS={f:["前進","cm"],b:["後退","cm"],r:["右回転","度"],l:["左回転","度"],w:["待つ","秒"]};
  var DEF_SC=[
    {n:"180°回転 → 1m前進",st:[["r",180,40],["f",100,40]]},
    {n:"1m 前進",st:[["f",100,40]]},
    {n:"180°回転",st:[["r",180,40]]},
    {n:"右に90°",st:[["r",90,40]]}];
  var SC=null, seqBusy=false, scName="", armed=-1, armTimer=null, saveT=null, ests=[];
  function post(url,body,ok,ng){
    var x=new XMLHttpRequest(); x.open("POST",url,true); x.setRequestHeader("Content-Type","text/plain"); x.timeout=3000;
    x.onload=function(){try{ok(JSON.parse(x.responseText))}catch(e){if(ng)ng()}};
    x.onerror=x.ontimeout=function(){if(ng)ng()};
    x.send(body);
  }
  function enc(st){   // 台車へ送る手順の文字列（種類:向き:量:強さ）
    return st.map(function(s){var k=s[0],v=+s[1],p=+s[2];
      if(k==="w")return "w:0:"+Math.round(v*1000)+":0";
      if(k==="f")return "s:1:"+v+":"+p; if(k==="b")return "s:-1:"+v+":"+p;
      return "r:"+(k==="r"?1:-1)+":"+v+":"+p}).join(",");
  }
  function estimate(st){   // 予定の合計秒。較正が無い／範囲外なら null
    if(!cal)return null; var ms=0,mv=0;
    for(var i=0;i<st.length;i++){var s=st[i],k=s[0],v=+s[1],p=+s[2];
      if(k==="w"){ms+=v*1000;continue}
      var r=(k==="f"||k==="b")?cal.sv*(p-cal.sd):cal.rv*(p-cal.rd);
      if(!(r>=0.5)||!(v>0))return null;
      var t=v/r*1000+((k==="f"||k==="b")?cal.st:cal.rt); if(t<50||t>8000)return null;
      ms+=t;mv++;
    }
    return (ms+mv*250)/1000;
  }
  function esc(s){return String(s).replace(/[&<>"]/g,function(c){return {"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]})}
  function normSC(a){
    var out=[];for(var i=0;i<4;i++){var s=a&&a[i]||{n:"",st:[]};out.push({n:String(s.n||""),st:(s.st||[]).filter(function(x){return KINDS[x[0]]}).slice(0,12)})}
    return out;
  }
  function scLoad(){
    get("/sc",function(j){
      SC=normSC(j&&j.sc&&j.sc.length?j.sc:(P.sc||DEF_SC)); scRender(); scEditRender();
    },function(){SC=normSC(P.sc||DEF_SC);scRender();scEditRender();setTimeout(scLoad,4000)});
  }
  function disarm(){if(armTimer)clearTimeout(armTimer);armTimer=null;if(armed!==-1){armed=-1;scRender()}}
  function scRender(){
    var c=$("sc"); if(!c||!SC)return; c.innerHTML="";
    SC.forEach(function(s,i){
      if(!s.n.trim()||!s.st.length)return;
      var b=document.createElement("button");b.className="scb"+(armed===i?" armed":"");
      b.innerHTML=esc(s.n)+"<small>"+(armed===i?"もう一度タップで実行":"タップで準備")+"</small>";
      b.disabled=!online||seqBusy;
      b.onclick=function(){scTap(i)};
      c.appendChild(b);
    });
  }
  function scTap(i){
    if(!online||seqBusy)return;
    if(armed===i){disarm();scRun(SC[i]);return}
    armed=i;scRender();
    if(armTimer)clearTimeout(armTimer);
    armTimer=setTimeout(function(){armTimer=null;armed=-1;scRender()},3000);
  }
  function scRun(s,done){
    var q="/seq?flip="+($("flip").checked?1:0)+"&trim="+$("trim").value+"&name="+encodeURIComponent(s.n);
    post(q,enc(s.st),function(j){
      if(!j.ok){$("read").textContent=j.err==="busy"?"まだ動いています":"手順が正しくありません";if(done)done(j);return}
      scName=s.n;seqBusy=true;padState();scRender();if(done)done(j);
    },function(){$("read").textContent="台車につながりません";if(done)done(null)});
  }
  function scChanged(){
    P.sc=SC;save();clearTimeout(saveT);$("scMsg").textContent="保存中…";
    saveT=setTimeout(function(){
      post("/sc",JSON.stringify({v:1,sc:SC}),function(j){$("scMsg").textContent=j.ok?"保存しました（台車に記録）":"保存できませんでした"},
        function(){$("scMsg").textContent="台車につながらないため、この端末にだけ保存しました"});
    },800);
    updateEsts();scRender();
  }
  function updateEsts(){
    ests.forEach(function(e){var t=estimate(e.slot.st);
      e.el.textContent=!e.slot.st.length?"動きがありません":(t==null?"強さや量が範囲外の動きがあります":"予定 約 "+t.toFixed(1)+" 秒")});
  }
  function scEditRender(){
    var box=$("scEdit"); if(!box||!SC)return; box.innerHTML=""; ests=[];
    SC.forEach(function(slot,i){
      var d=document.createElement("div");d.className="slot";
      var nm=document.createElement("input");nm.type="text";nm.placeholder="ボタン "+(i+1)+" の名前（空なら出さない）";nm.value=slot.n;
      nm.oninput=function(){slot.n=nm.value;scChanged()};
      d.appendChild(nm);
      slot.st.forEach(function(s,j){
        var r=document.createElement("div");r.className="stp";
        var sel=document.createElement("select");
        Object.keys(KINDS).forEach(function(k){var o=document.createElement("option");o.value=k;o.textContent=KINDS[k][0];if(k===s[0])o.selected=true;sel.appendChild(o)});
        var v=document.createElement("input");v.type="number";v.setAttribute("inputmode","decimal");v.value=s[1];
        var u=document.createElement("span");u.textContent=KINDS[s[0]][1];
        var p=document.createElement("input");p.type="number";p.setAttribute("inputmode","numeric");p.value=s[2];
        var pl=document.createElement("span");pl.textContent="%";
        function vis(){p.style.display=pl.style.display=(s[0]==="w")?"none":""}
        vis();
        sel.onchange=function(){s[0]=sel.value;u.textContent=KINDS[s[0]][1];vis();scChanged()};
        v.oninput=function(){s[1]=+v.value;scChanged()};
        p.oninput=function(){s[2]=+p.value;scChanged()};
        var x=document.createElement("button");x.textContent="×";
        x.onclick=function(){slot.st.splice(j,1);scEditRender();scChanged()};
        [sel,v,u,p,pl,x].forEach(function(n){r.appendChild(n)});
        d.appendChild(r);
      });
      var info=document.createElement("p");info.className="hint";d.appendChild(info);ests.push({slot:slot,el:info});
      var t=document.createElement("div");t.className="tools";
      var add=document.createElement("button");add.textContent="＋ 動きを足す";
      add.onclick=function(){if(slot.st.length>=12)return;slot.st.push(["f",30,40]);scEditRender();scChanged()};
      var tr=document.createElement("button");tr.textContent="▶ 試す";
      tr.onclick=function(){if(!slot.st.length)return;scRun(slot,function(j){$("scMsg").textContent=(j&&j.ok)?"実行中…（止めるときは下の停止ボタン）":"実行できませんでした"})};
      var clr=document.createElement("button");clr.textContent="空にする";
      clr.onclick=function(){if(confirm("ボタン "+(i+1)+" を空にしますか？")){slot.n="";slot.st=[];scEditRender();scChanged()}};
      [add,tr,clr].forEach(function(n){t.appendChild(n)});
      d.appendChild(t);box.appendChild(d);
    });
    updateEsts();
  }

  /* ===== 状態の取得 ===== */
  function poll(){
    get("/state",function(j){
      failN=0;setConn(true);
      var was=seqBusy;seqBusy=!!j.seq;
      if(seqBusy||was!==seqBusy){padState();scRender()}
      if(seqBusy){$("read").innerHTML=esc(scName||"ショートカット")+"<small>"+Math.min(j.si,j.sn)+"/"+j.sn+"</small>"}
      else if(was){$("read").textContent="停止"}
      if(j.busy||j.seq){setBusy(true);$("bar").style.width=j.seq?Math.min(100,100*j.si/Math.max(1,j.sn))+"%":Math.min(100,100*j.el/j.ms)+"%"}
      else{
        if(busy)$("hint").textContent="止まりました。結果を下の記録に入れてください。";
        setBusy(false);$("bar").style.width="0";
        var changed=false;
        for(var i=store.rows.length-1;i>=0;i--){   // 同じ番号が複数あっても、最新の 1 件だけに実測を入れる
          var r=store.rows[i];
          if(r.id===j.lastId){if(r.ams==null){r.ams=j.lastEl;changed=true}break}
        }
        if(changed){save();render()}
      }
    },function(){if(++failN>=2){setConn(false);release()}},900);
  }

  function q(v){v=v==null?"":String(v);return /[",\n]/.test(v)?'"'+v.replace(/"/g,'""')+'"':v}
  $("copy").onclick=function(){
    var lines=["# romi-log v2 cal="+(cal?JSON.stringify(cal):"?")];
    lines.push("id,kind,dir,target,duty_pct,planned_ms,actual_ms,trim,flip,result,lateral_cm,battery,load,memo");
    store.rows.forEach(function(r){lines.push([r.id,r.m==="s"?"straight":"rotate",r.d,r.tgt==null?"":r.tgt,r.duty,r.pms,r.ams==null?"":r.ams,r.trim,r.flip,
      r.a==null?"":r.a,r.lat==null?"":r.lat,q(r.bat),q(r.load),q(r.n)].join(","))});
    var ta=$("csv"); ta.hidden=false; ta.value=lines.join("\n"); ta.focus(); ta.select();
    var ok=false;try{ok=document.execCommand("copy")}catch(e){}
    $("hint").textContent=ok?"コピーしました。そのまま貼り付けて渡してください。":"下の枠を選択状態にしました。長押しでコピーしてください。";
  };
  $("clear").onclick=function(){if(confirm("記録をすべて消しますか？")){store.rows=[];save();render()}};

  setMode("s",true); render(); loadCal(); scLoad(); route(); setInterval(poll,350); poll();
})();
</script>
</body>
</html>
)HTML";
