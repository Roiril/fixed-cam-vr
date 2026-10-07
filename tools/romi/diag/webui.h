// Romi 手動操作の Web 画面（ESP32 が配る単一ファイル）。http://romi.local/ または http://192.168.10.51/
// 流れ: 条件（電池・載せているもの）を入れる → 目標（cm / 度）を決めて動かす → 止まった結果を入れる → コピーして渡す
// エンドポイント: /run /stop /state /res /cal /info /health
#pragma once
#include <pgmspace.h>

static const char PAGE_HTML[] PROGMEM = R"HTML(<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">
<title>Romi 手動操作</title>
<style>
:root{--bg:#f4f5f7;--card:#fff;--ink:#1c1f24;--sub:#5f6773;--line:#d9dde3;--acc:#1f6feb;--accsoft:#e6efff;--stop:#d92d20;--ok:#12805c;--field:#f4f5f7}
@media(prefers-color-scheme:dark){:root{--bg:#14171b;--card:#1d2126;--ink:#e8eaed;--sub:#9aa3ad;--line:#333a42;--acc:#5b9bff;--accsoft:#1f2f4a;--stop:#ff5a4d;--ok:#3ecf9b;--field:#14171b}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.5 system-ui,-apple-system,"Segoe UI","Hiragino Sans","Yu Gothic",sans-serif;padding-bottom:96px}
header{display:flex;align-items:center;justify-content:space-between;padding:12px 16px;background:var(--card);border-bottom:1px solid var(--line);position:sticky;top:0;z-index:5}
header b{font-size:17px}
#conn{font-size:13px;color:var(--sub)}
#conn.ok{color:var(--ok)}
#conn.ng{color:var(--stop)}
main{max-width:640px;margin:0 auto;padding:12px 16px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px;margin-bottom:12px}
.card h2{margin:0 0 8px;font-size:15px}
.seg{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-bottom:12px}
.seg button{padding:12px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--ink);font-size:16px}
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
.chips button{padding:8px 14px;border-radius:18px;border:1px solid var(--line);background:var(--card);color:var(--ink);font-size:15px}
.plan{margin:10px 0 0;font-size:14px;color:var(--sub)}
.plan b{color:var(--ink)}
.go{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:10px}
.go button{padding:18px 8px;border-radius:12px;border:0;background:var(--acc);color:#fff;font-size:18px;font-weight:600}
.go[hidden],textarea[hidden]{display:none}
.go button:disabled{opacity:.4}
.prog{height:6px;background:var(--line);border-radius:3px;overflow:hidden;margin-top:10px}
.prog i{display:block;height:100%;width:0;background:var(--ok)}
.hint{font-size:13px;color:var(--sub);margin:6px 0 0}
#stop{position:fixed;left:0;right:0;bottom:0;height:76px;border:0;background:var(--stop);color:#fff;font-size:22px;font-weight:700;z-index:9}
.run{border-top:1px solid var(--line);padding:10px 0}
.run:first-child{border-top:0}
.run .h{font-size:14px;margin-bottom:6px}
.run .h b{font-size:15px}
.run .h small{color:var(--sub)}
.run .h{display:flex;justify-content:space-between;align-items:center;gap:8px}
.del{flex:none;padding:6px 10px;border-radius:8px;border:1px solid var(--line);background:var(--card);color:var(--stop);font-size:13px}
.f2{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.f2 label{font-size:12px;color:var(--sub);display:block}
.pm{display:flex;gap:4px}
.pm button{width:44px;border-radius:8px;border:1px solid var(--line);background:var(--card);color:var(--ink);font-size:16px}
.tools{display:flex;gap:8px;margin-top:10px;flex-wrap:wrap}
.tools button{padding:12px 16px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--ink);font-size:15px}
.tools button.main{background:var(--acc);border-color:var(--acc);color:#fff;font-weight:600}
textarea{width:100%;height:130px;margin-top:8px;font:12px/1.4 ui-monospace,Consolas,monospace;background:var(--field);color:var(--ink);border:1px solid var(--line);border-radius:8px;padding:8px}
details{margin-top:10px}
summary{font-size:14px;color:var(--sub)}
.chk{display:flex;gap:8px;align-items:center;font-size:14px;margin:8px 0}
.chk input{width:20px;height:20px}
</style>
</head>
<body>
<header><b>Romi 手動操作</b><span id="conn">接続を確認中…</span></header>
<main>

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
    <p class="hint" id="hint">目標を決めて、方向ボタンを押すと動いて、目標に届く時間で止まります。止めたいときは下の赤いボタン。</p>
    <details>
      <summary>詳しい設定</summary>
      <div class="row" id="trimRow">
        <label class="t"><span>まっすぐ補正（左右バランス）</span><b id="vTrim">0</b></label>
        <input id="trim" type="range" min="-20" max="20" step="1" value="0">
        <p class="hint">右へ曲がるときはマイナス側へ。左へ曲がるときはプラス側へ。直進のみ効きます。</p>
      </div>
      <label class="chk"><input id="useMs" type="checkbox">時間（秒）で直接指定する</label>
      <input id="msDirect" type="number" inputmode="decimal" step="0.1" min="0.1" max="8" value="1.0" style="display:none">
      <label class="chk"><input id="flip" type="checkbox">前後を逆にする（「前進」で後ろへ進むとき）</label>
    </details>
  </div>

  <div class="card">
    <h2>止まった結果を入れる</h2>
    <p class="hint">実際に進んだ距離や回った角度と、左右のずれを入れてください。入れた内容はコピーして、そのまま渡せます。</p>
    <div id="log"></div>
    <div class="tools">
      <button id="copy" class="main">記録をコピー</button>
      <button id="clear">記録を消す</button>
    </div>
    <textarea id="csv" hidden readonly></textarea>
  </div>
</main>
<button id="stop">■ 停止</button>

<script>
(function(){
  var $=function(id){return document.getElementById(id)};
  var KEY="romi-log-v2", store={};
  try{store=JSON.parse(localStorage.getItem(KEY)||"{}")}catch(e){store={}}
  store.rows=store.rows||[]; store.prefs=store.prefs||{};
  function save(){try{localStorage.setItem(KEY,JSON.stringify(store))}catch(e){}}
  var P=store.prefs, mode="s", busy=false, cal=null;
  // 台車が再起動すると番号 #id は 1 から振り直されるので、記録の同一性は固有キー k で持つ
  store.rows.forEach(function(r,i){if(!r.k)r.k="old-"+i+"-"+r.id});

  var CH={s:[10,20,30,50,100],r:[45,90,180,360]};
  var DEF={s:30,r:90};
  P.tgtS=P.tgtS||30; P.tgtR=P.tgtR||90;
  if(P.duty)$("duty").value=P.duty; if(P.trim!=null)$("trim").value=P.trim; $("flip").checked=!!P.flip;
  $("bat").value=P.bat||""; $("load").value=P.load||"";

  function saveP(){P.duty=$("duty").value;P.trim=$("trim").value;P.flip=$("flip").checked;P.bat=$("bat").value;P.load=$("load").value;
    if(mode==="s")P.tgtS=$("tgt").value;else P.tgtR=$("tgt").value;save()}

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
  ["flip","useMs"].forEach(function(i){$(i).addEventListener("change",fmt)});

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
  function get(url,ok,ng){
    var x=new XMLHttpRequest(); x.open("GET",url,true); x.timeout=3000;
    x.onload=function(){try{ok(JSON.parse(x.responseText))}catch(e){if(ng)ng()}};
    x.onerror=x.ontimeout=function(){if(ng)ng()};
    x.send();
  }
  function loadCal(){get("/cal",function(j){cal=j;fmt()},function(){setTimeout(loadCal,2000)})}

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
      h.appendChild(del);
      d.appendChild(h);
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
    var t=+$("tgt").value;
    var useMs=$("useMs").checked;
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
    },function(){setBusy(false);$("hint").textContent="台車につながりません。電源とネットワークを確認してください。"});
  }
  Array.prototype.forEach.call(document.querySelectorAll(".go button"),function(b){
    b.addEventListener("click",function(){run(+b.getAttribute("data-d"))});
  });

  function stop(e){if(e)e.preventDefault();get("/stop",function(){setBusy(false);$("hint").textContent="停止しました"},function(){$("hint").textContent="停止の指令が届きませんでした。電源を切ってください。"})}
  $("stop").addEventListener("touchstart",stop,{passive:false});
  $("stop").addEventListener("click",stop);

  function poll(){
    get("/state",function(j){
      $("conn").textContent="接続中"; $("conn").className="ok";
      if(j.busy){setBusy(true);$("bar").style.width=Math.min(100,100*j.el/j.ms)+"%"}
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
    },function(){$("conn").textContent="切断";$("conn").className="ng"});
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

  setMode("s",true); render(); loadCal(); setInterval(poll,300); poll();
})();
</script>
</body>
</html>
)HTML";
