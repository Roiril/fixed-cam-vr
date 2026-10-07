// Romi 手動操作の Web 画面（ESP32 の Wi-Fi アクセスポイントから配る単一ファイル）。
// http://192.168.4.1/  エンドポイント: /run /stop /state /res
#pragma once
#include <pgmspace.h>

static const char PAGE_HTML[] PROGMEM = R"HTML(<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">
<title>Romi 手動操作</title>
<style>
:root{--bg:#f4f5f7;--card:#fff;--ink:#1c1f24;--sub:#5f6773;--line:#d9dde3;--acc:#1f6feb;--accsoft:#e6efff;--stop:#d92d20;--ok:#12805c}
@media(prefers-color-scheme:dark){:root{--bg:#14171b;--card:#1d2126;--ink:#e8eaed;--sub:#9aa3ad;--line:#333a42;--acc:#5b9bff;--accsoft:#1f2f4a;--stop:#ff5a4d;--ok:#3ecf9b}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.5 system-ui,-apple-system,"Segoe UI","Hiragino Sans","Yu Gothic",sans-serif;padding-bottom:90px}
header{display:flex;align-items:center;justify-content:space-between;padding:12px 16px;background:var(--card);border-bottom:1px solid var(--line);position:sticky;top:0;z-index:5}
header b{font-size:17px}
#conn{font-size:13px;color:var(--sub)}
#conn.ok{color:var(--ok)}
#conn.ng{color:var(--stop)}
main{max-width:640px;margin:0 auto;padding:12px 16px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px;margin-bottom:12px}
.seg{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-bottom:12px}
.seg button{padding:12px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--ink);font-size:16px}
.seg button.on{background:var(--accsoft);border-color:var(--acc);color:var(--acc);font-weight:600}
.row{margin:10px 0}
.row label{display:flex;justify-content:space-between;font-size:14px;color:var(--sub)}
.row label b{color:var(--ink);font-size:17px;font-variant-numeric:tabular-nums}
input[type=range]{width:100%;height:44px;accent-color:var(--acc)}
.go{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:6px}
.go button{padding:18px 8px;border-radius:12px;border:0;background:var(--acc);color:#fff;font-size:18px;font-weight:600}
.go[hidden],textarea[hidden]{display:none}
.go button:disabled{opacity:.4}
.prog{height:6px;background:var(--line);border-radius:3px;overflow:hidden;margin-top:10px}
.prog i{display:block;height:100%;width:0;background:var(--ok)}
.hint{font-size:13px;color:var(--sub);margin:6px 0 0}
#stop{position:fixed;left:0;right:0;bottom:0;height:76px;border:0;background:var(--stop);color:#fff;font-size:22px;font-weight:700;z-index:9}
table{width:100%;border-collapse:collapse;font-size:14px}
th,td{padding:6px 4px;border-bottom:1px solid var(--line);text-align:left;vertical-align:middle}
th{color:var(--sub);font-weight:500;font-size:12px}
td input{width:100%;padding:8px;border-radius:8px;border:1px solid var(--line);background:var(--bg);color:var(--ink);font-size:16px}
td.res{width:84px}
td.memo{width:96px}
.tools{display:flex;gap:8px;margin-top:10px;flex-wrap:wrap}
.tools button{padding:10px 14px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--ink);font-size:14px}
textarea{width:100%;height:110px;margin-top:8px;font:12px/1.4 ui-monospace,Consolas,monospace;background:var(--bg);color:var(--ink);border:1px solid var(--line);border-radius:8px;padding:8px}
.chk{display:flex;gap:8px;align-items:center;font-size:14px;margin:6px 0}
.chk input{width:20px;height:20px}
</style>
</head>
<body>
<header><b>Romi 手動操作</b><span id="conn">接続を確認中…</span></header>
<main>
  <div class="seg">
    <button id="tabS" class="on">直進</button>
    <button id="tabR">回転</button>
  </div>

  <div class="card">
    <div class="row">
      <label><span>時間</span><b id="vMs">1.5 秒</b></label>
      <input id="ms" type="range" min="100" max="4000" step="100" value="1500">
    </div>
    <div class="row">
      <label><span>強さ</span><b id="vDuty">40 %</b></label>
      <input id="duty" type="range" min="10" max="68" step="1" value="40">
    </div>
    <div class="go" id="goS">
      <button data-d="1">▲ 前進</button>
      <button data-d="-1">▼ 後退</button>
    </div>
    <div class="go" id="goR" hidden>
      <button data-d="-1">↺ 左回転</button>
      <button data-d="1">↻ 右回転</button>
    </div>
    <div class="prog"><i id="bar"></i></div>
    <p class="hint" id="hint">バーで量を決めて、方向ボタンを押すと動きます。止めたいときは下の赤いボタン。</p>
  </div>

  <div class="card">
    <div class="row" id="trimRow">
      <label><span>まっすぐ補正（左右バランス）</span><b id="vTrim">0</b></label>
      <input id="trim" type="range" min="-20" max="20" step="1" value="0">
      <p class="hint">右へ曲がるときはマイナス側へ。左へ曲がるときはプラス側へ。直進のみ効きます。</p>
    </div>
    <label class="chk"><input id="flip" type="checkbox">前後を逆にする（「前進」で後ろへ進むとき）</label>
  </div>

  <div class="card">
    <b>結果を記録</b>
    <p class="hint">動かしたあとに、実際に進んだ距離（cm）か回った角度（度）を入れてください。向きが指令と逆なら、マイナスを付けます。</p>
    <table>
      <thead><tr><th>#</th><th>指令</th><th class="res">結果</th><th class="memo">メモ</th></tr></thead>
      <tbody id="log"></tbody>
    </table>
    <div class="tools">
      <button id="copy">記録をコピー</button>
      <button id="clear">記録を消す</button>
    </div>
    <textarea id="csv" hidden readonly></textarea>
  </div>
</main>
<button id="stop">■ 停止</button>

<script>
(function(){
  var $=function(id){return document.getElementById(id)};
  var KEY="romi-log-v1", store={};
  try{store=JSON.parse(localStorage.getItem(KEY)||"{}")}catch(e){store={}}
  store.rows=store.rows||[]; store.prefs=store.prefs||{};
  function save(){try{localStorage.setItem(KEY,JSON.stringify(store))}catch(e){}}

  var mode="s", busy=false, lastId=0;
  var P=store.prefs;
  if(P.ms)$("ms").value=P.ms; if(P.duty)$("duty").value=P.duty;
  if(P.trim!=null)$("trim").value=P.trim; $("flip").checked=!!P.flip;

  function fmt(){
    $("vMs").textContent=(+$("ms").value/1000).toFixed(1)+" 秒";
    $("vDuty").textContent=$("duty").value+" %";
    var t=+$("trim").value; $("vTrim").textContent=(t>0?"+":"")+t;
    P.ms=$("ms").value;P.duty=$("duty").value;P.trim=$("trim").value;P.flip=$("flip").checked;save();
  }
  ["ms","duty","trim"].forEach(function(i){$(i).addEventListener("input",fmt)});
  $("flip").addEventListener("change",fmt); fmt();

  function setMode(m){
    mode=m;
    $("tabS").className=m==="s"?"on":""; $("tabR").className=m==="r"?"on":"";
    $("goS").hidden=m!=="s"; $("goR").hidden=m!=="r"; $("trimRow").style.display=m==="s"?"":"none";
  }
  $("tabS").onclick=function(){setMode("s")}; $("tabR").onclick=function(){setMode("r")};
  setMode("s");

  function setBusy(b){busy=b;Array.prototype.forEach.call(document.querySelectorAll(".go button"),function(x){x.disabled=b})}

  function get(url,ok,ng){
    var x=new XMLHttpRequest(); x.open("GET",url,true); x.timeout=3000;
    x.onload=function(){try{ok(JSON.parse(x.responseText))}catch(e){if(ng)ng()}};
    x.onerror=x.ontimeout=function(){if(ng)ng()};
    x.send();
  }

  function label(r){
    var n=r.m==="s"?(r.d>0?"前進":"後退"):(r.d>0?"右回転":"左回転");
    return n+" "+(r.ms/1000).toFixed(1)+"秒 "+r.duty+"%"+(r.m==="s"&&r.trim?" 補正"+(r.trim>0?"+":"")+r.trim:"");
  }

  function render(){
    var tb=$("log"); tb.innerHTML="";
    store.rows.slice().reverse().forEach(function(r){
      var tr=document.createElement("tr");
      var unit=r.m==="s"?"cm":"度";
      tr.innerHTML="<td>"+r.id+"</td><td>"+label(r)+"</td>";
      var td=document.createElement("td"); td.className="res";
      var inp=document.createElement("input"); inp.type="text"; inp.setAttribute("inputmode","decimal"); inp.placeholder=unit; inp.value=r.v==null?"":r.v;
      inp.onchange=function(){r.v=inp.value.trim()===""?null:inp.value.trim();save();sendRes(r)};
      td.appendChild(inp); tr.appendChild(td);
      var tm=document.createElement("td"); tm.className="memo";
      var mi=document.createElement("input"); mi.type="text"; mi.placeholder="任意"; mi.value=r.n||"";
      mi.onchange=function(){r.n=mi.value.trim();save();if(r.v!=null)sendRes(r)};
      tm.appendChild(mi); tr.appendChild(tm);
      tb.appendChild(tr);
    });
  }
  function sendRes(r){
    get("/res?id="+r.id+"&v="+encodeURIComponent(r.v==null?"":r.v)+"&u="+(r.m==="s"?"cm":"deg")+"&n="+encodeURIComponent(r.n||""),function(){});
  }

  function run(d){
    if(busy)return;
    var q="/run?m="+mode+"&d="+d+"&duty="+$("duty").value+"&ms="+$("ms").value+"&trim="+(mode==="s"?$("trim").value:0)+"&flip="+($("flip").checked?1:0);
    setBusy(true);
    get(q,function(j){
      if(!j.ok){setBusy(false);$("hint").textContent="受け付けられませんでした（"+(j.err||"?")+"）";return}
      lastId=j.id;
      store.rows.push({id:j.id,m:mode,d:d,duty:+$("duty").value,ms:+$("ms").value,trim:mode==="s"?+$("trim").value:0,flip:$("flip").checked?1:0,v:null,n:""});
      save(); render();
      $("hint").textContent="実行中…（記録 #"+j.id+"）";
    },function(){setBusy(false);$("hint").textContent="台車につながりません。Wi-Fi を確認してください。"});
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
      else{if(busy){$("hint").textContent="完了。結果を下の記録に入れてください。"}setBusy(false);$("bar").style.width="0"}
    },function(){$("conn").textContent="切断";$("conn").className="ng"});
  }
  setInterval(poll,300); poll(); render();

  $("copy").onclick=function(){
    var lines=["id,kind,dir,duty_pct,ms,trim,flip,result,unit,memo"];
    store.rows.forEach(function(r){lines.push([r.id,r.m==="s"?"straight":"rotate",r.d,r.duty,r.ms,r.trim,r.flip,r.v==null?"":r.v,r.m==="s"?"cm":"deg",(r.n||"").replace(/,/g," ")].join(","))});
    var ta=$("csv"); ta.hidden=false; ta.value=lines.join("\n"); ta.focus(); ta.select();
    try{document.execCommand("copy")}catch(e){}
  };
  $("clear").onclick=function(){if(confirm("記録をすべて消しますか？")){store.rows=[];save();render()}};
})();
</script>
</body>
</html>
)HTML";
